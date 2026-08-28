using System;

namespace Lumigram.Tl
{
    /// <summary>
    /// DEFLATE decompression (RFC 1951) and the gzip wrapper around it (RFC 1952).
    ///
    /// Telegram compresses any sizeable response and hands it back as gzip_packed,
    /// so this is not optional - account.getPassword alone arrives compressed.
    ///
    /// Implemented in managed code because WP8.1 Silverlight has no
    /// System.IO.Compression in any form. Decompression only: the client never needs
    /// to compress anything it sends.
    /// </summary>
    public static class Inflate
    {
        private const int MaxBits = 15;

        /// <summary>Strips the gzip header and trailer, then inflates the contents.</summary>
        public static byte[] Gunzip(byte[] data)
        {
            if (data == null || data.Length < 18)
                throw new TlParseException("gzip stream too short");
            if (data[0] != 0x1f || data[1] != 0x8b)
                throw new TlParseException("not a gzip stream");
            if (data[2] != 8)
                throw new TlParseException("unsupported gzip compression method " + data[2]);

            int flags = data[3];
            int pos = 10;                                   // magic, method, flags, mtime, xfl, os

            if ((flags & 0x04) != 0)                        // FEXTRA
            {
                int extra = data[pos] | (data[pos + 1] << 8);
                pos += 2 + extra;
            }
            if ((flags & 0x08) != 0) pos = SkipZeroTerminated(data, pos);   // FNAME
            if ((flags & 0x10) != 0) pos = SkipZeroTerminated(data, pos);   // FCOMMENT
            if ((flags & 0x02) != 0) pos += 2;                              // FHCRC

            if (pos >= data.Length)
                throw new TlParseException("gzip header runs past the end of the data");

            // The trailer carries CRC32 and the uncompressed size; the size is a
            // useful sanity check on what we produce.
            int expectedSize = data[data.Length - 4]
                             | (data[data.Length - 3] << 8)
                             | (data[data.Length - 2] << 16)
                             | (data[data.Length - 1] << 24);

            byte[] result = InflateRaw(data, pos);

            if (expectedSize >= 0 && result.Length != expectedSize)
                throw new TlParseException("gzip size mismatch: got " + result.Length +
                                           ", trailer says " + expectedSize);
            return result;
        }

        private static int SkipZeroTerminated(byte[] data, int pos)
        {
            while (pos < data.Length && data[pos] != 0) pos++;
            return pos + 1;
        }

        public static byte[] InflateRaw(byte[] data, int offset)
        {
            var s = new State(data, offset);
            var outBuf = new Output();

            bool final;
            do
            {
                final = s.ReadBits(1) == 1;
                int type = s.ReadBits(2);

                switch (type)
                {
                    case 0: Stored(s, outBuf); break;
                    case 1: Compressed(s, outBuf, FixedLengthCodes, FixedDistanceCodes); break;
                    case 2: Dynamic(s, outBuf); break;
                    default: throw new TlParseException("invalid deflate block type 3");
                }
            }
            while (!final);

            return outBuf.ToArray();
        }

        private static void Stored(State s, Output outBuf)
        {
            s.AlignToByte();
            int len = s.ReadByte() | (s.ReadByte() << 8);
            int nlen = s.ReadByte() | (s.ReadByte() << 8);

            if ((len & 0xFFFF) != ((~nlen) & 0xFFFF))
                throw new TlParseException("stored block length check failed");

            for (int i = 0; i < len; i++) outBuf.Write((byte)s.ReadByte());
        }

        private static readonly int[] LengthBase = {
            3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258 };
        private static readonly int[] LengthExtra = {
            0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,3,3,3,3,4,4,4,4,5,5,5,5,0 };
        private static readonly int[] DistBase = {
            1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,
            4097,6145,8193,12289,16385,24577 };
        private static readonly int[] DistExtra = {
            0,0,0,0,1,1,2,2,3,3,4,4,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,13 };

        private static void Compressed(State s, Output outBuf, Huffman lengths, Huffman distances)
        {
            while (true)
            {
                int sym = Decode(s, lengths);

                if (sym < 256)
                {
                    outBuf.Write((byte)sym);
                }
                else if (sym == 256)
                {
                    return;                                  // end of block
                }
                else
                {
                    sym -= 257;
                    if (sym >= LengthBase.Length)
                        throw new TlParseException("invalid length symbol");

                    int len = LengthBase[sym] + s.ReadBits(LengthExtra[sym]);

                    int dsym = Decode(s, distances);
                    if (dsym >= DistBase.Length)
                        throw new TlParseException("invalid distance symbol");

                    int dist = DistBase[dsym] + s.ReadBits(DistExtra[dsym]);
                    outBuf.Copy(dist, len);
                }
            }
        }

        private static readonly int[] CodeLengthOrder = {
            16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15 };

        private static void Dynamic(State s, Output outBuf)
        {
            int hlit = s.ReadBits(5) + 257;
            int hdist = s.ReadBits(5) + 1;
            int hclen = s.ReadBits(4) + 4;

            var clens = new int[19];
            for (int i = 0; i < hclen; i++) clens[CodeLengthOrder[i]] = s.ReadBits(3);

            Huffman codeLengths = BuildHuffman(clens, 19);

            var lengths = new int[hlit + hdist];
            int pos = 0;
            while (pos < lengths.Length)
            {
                int sym = Decode(s, codeLengths);
                if (sym < 16)
                {
                    lengths[pos++] = sym;
                }
                else if (sym == 16)
                {
                    if (pos == 0) throw new TlParseException("repeat with no previous length");
                    int prev = lengths[pos - 1];
                    int repeat = 3 + s.ReadBits(2);
                    while (repeat-- > 0 && pos < lengths.Length) lengths[pos++] = prev;
                }
                else if (sym == 17)
                {
                    int repeat = 3 + s.ReadBits(3);
                    while (repeat-- > 0 && pos < lengths.Length) lengths[pos++] = 0;
                }
                else
                {
                    int repeat = 11 + s.ReadBits(7);
                    while (repeat-- > 0 && pos < lengths.Length) lengths[pos++] = 0;
                }
            }

            var litLengths = new int[hlit];
            var distLengths = new int[hdist];
            Array.Copy(lengths, 0, litLengths, 0, hlit);
            Array.Copy(lengths, hlit, distLengths, 0, hdist);

            Compressed(s, outBuf, BuildHuffman(litLengths, hlit), BuildHuffman(distLengths, hdist));
        }

        /// <summary>
        /// Canonical Huffman table: counts per bit length, plus symbols ordered by
        /// (length, symbol). Decoding walks bit by bit, which is slower than a lookup
        /// table but far shorter and quite fast enough for message-sized payloads.
        /// </summary>
        private sealed class Huffman
        {
            public int[] Count;
            public int[] Symbol;
        }

        private static Huffman BuildHuffman(int[] lengths, int n)
        {
            var h = new Huffman { Count = new int[MaxBits + 1], Symbol = new int[n] };

            for (int i = 0; i < n; i++) h.Count[lengths[i]]++;
            h.Count[0] = 0;

            var offs = new int[MaxBits + 1];
            for (int i = 1; i <= MaxBits; i++) offs[i] = offs[i - 1] + h.Count[i - 1];

            for (int i = 0; i < n; i++)
                if (lengths[i] != 0) h.Symbol[offs[lengths[i]]++] = i;

            return h;
        }

        private static int Decode(State s, Huffman h)
        {
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= MaxBits; len++)
            {
                code |= s.ReadBits(1);
                int count = h.Count[len];
                if (code - first < count) return h.Symbol[index + (code - first)];
                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }
            throw new TlParseException("invalid Huffman code");
        }

        private static readonly Huffman FixedLengthCodes = BuildFixedLengths();
        private static readonly Huffman FixedDistanceCodes = BuildFixedDistances();

        private static Huffman BuildFixedLengths()
        {
            var lengths = new int[288];
            for (int i = 0; i < 144; i++) lengths[i] = 8;
            for (int i = 144; i < 256; i++) lengths[i] = 9;
            for (int i = 256; i < 280; i++) lengths[i] = 7;
            for (int i = 280; i < 288; i++) lengths[i] = 8;
            return BuildHuffman(lengths, 288);
        }

        private static Huffman BuildFixedDistances()
        {
            var lengths = new int[30];
            for (int i = 0; i < 30; i++) lengths[i] = 5;
            return BuildHuffman(lengths, 30);
        }

        private sealed class State
        {
            private readonly byte[] _data;
            private int _pos;
            private int _bitBuffer;
            private int _bitCount;

            public State(byte[] data, int offset)
            {
                _data = data;
                _pos = offset;
            }

            public int ReadByte()
            {
                if (_pos >= _data.Length) throw new TlParseException("deflate stream ended early");
                return _data[_pos++];
            }

            public int ReadBits(int need)
            {
                while (_bitCount < need)
                {
                    _bitBuffer |= ReadByte() << _bitCount;
                    _bitCount += 8;
                }
                int value = _bitBuffer & ((1 << need) - 1);
                _bitBuffer >>= need;
                _bitCount -= need;
                return value;
            }

            public void AlignToByte()
            {
                _bitBuffer = 0;
                _bitCount = 0;
            }
        }

        private sealed class Output
        {
            private byte[] _buf = new byte[4096];
            private int _len;

            public void Write(byte b)
            {
                Ensure(1);
                _buf[_len++] = b;
            }

            /// <summary>
            /// Back-reference copy. It must be byte by byte: the run may overlap
            /// itself (distance 1, length 100 repeats one byte), which a block copy
            /// would get wrong.
            /// </summary>
            public void Copy(int distance, int length)
            {
                if (distance > _len)
                    throw new TlParseException("back-reference points before the start of the output");

                Ensure(length);
                int from = _len - distance;
                for (int i = 0; i < length; i++) _buf[_len++] = _buf[from + i];
            }

            private void Ensure(int extra)
            {
                if (_len + extra <= _buf.Length) return;
                int cap = _buf.Length * 2;
                while (cap < _len + extra) cap *= 2;
                var b = new byte[cap];
                Buffer.BlockCopy(_buf, 0, b, 0, _len);
                _buf = b;
            }

            public byte[] ToArray()
            {
                var r = new byte[_len];
                Buffer.BlockCopy(_buf, 0, r, 0, _len);
                return r;
            }
        }
    }
}
