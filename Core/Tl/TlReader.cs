using System;

namespace Lumigram.Tl
{
    public class TlParseException : Exception
    {
        public TlParseException(string message) : base(message) { }
    }

    /// <summary>
    /// Reads TL binary. Mirrors <see cref="TlWriter"/>, including the byte-string
    /// padding rules.
    ///
    /// Every read is bounds-checked and throws <see cref="TlParseException"/> rather
    /// than returning junk. The data here arrives from the network before it has
    /// been authenticated, so a malformed length must fail loudly instead of walking
    /// off the end of the buffer.
    /// </summary>
    public sealed class TlReader
    {
        private readonly byte[] _buf;
        private int _pos;

        public TlReader(byte[] data, int offset = 0)
        {
            _buf = data;
            _pos = offset;
        }

        public int Position { get { return _pos; } set { _pos = value; } }
        public int Remaining { get { return _buf.Length - _pos; } }

        private void Need(int count)
        {
            if (count < 0 || _pos + count > _buf.Length)
                throw new TlParseException("TL read past end: wanted " + count +
                                           " at " + _pos + " of " + _buf.Length);
        }

        public byte ReadByte()
        {
            Need(1);
            return _buf[_pos++];
        }

        public int ReadInt()
        {
            Need(4);
            int v = _buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16) | (_buf[_pos + 3] << 24);
            _pos += 4;
            return v;
        }

        public uint ReadUInt() { return unchecked((uint)ReadInt()); }

        public uint ReadConstructor() { return ReadUInt(); }

        public long ReadLong()
        {
            Need(8);
            long v = 0;
            for (int i = 0; i < 8; i++) v |= (long)_buf[_pos + i] << (8 * i);
            _pos += 8;
            return v;
        }

        public double ReadDouble() { return BitConverter.Int64BitsToDouble(ReadLong()); }

        public byte[] ReadRaw(int count)
        {
            Need(count);
            var r = new byte[count];
            Buffer.BlockCopy(_buf, _pos, r, 0, count);
            _pos += count;
            return r;
        }

        public byte[] ReadBytes()
        {
            int start = _pos;
            int len = ReadByte();

            if (len == 0xFE)
            {
                Need(3);
                len = _buf[_pos] | (_buf[_pos + 1] << 8) | (_buf[_pos + 2] << 16);
                _pos += 3;
            }
            else if (len > 0xFE)
            {
                throw new TlParseException("invalid TL length prefix 0x" + len.ToString("x2"));
            }

            var data = ReadRaw(len);

            int pad = (4 - ((_pos - start) & 3)) & 3;
            Need(pad);
            _pos += pad;

            return data;
        }

        public string ReadString()
        {
            var b = ReadBytes();
            return System.Text.Encoding.UTF8.GetString(b, 0, b.Length);
        }

        public bool ReadBool()
        {
            uint c = ReadConstructor();
            if (c == TlConstructors.BoolTrue) return true;
            if (c == TlConstructors.BoolFalse) return false;
            throw new TlParseException("expected Bool, got 0x" + c.ToString("x8"));
        }

        public long[] ReadVectorOfLong()
        {
            uint c = ReadConstructor();
            if (c != TlConstructors.Vector)
                throw new TlParseException("expected vector, got 0x" + c.ToString("x8"));

            int count = ReadInt();
            if (count < 0 || count > Remaining / 8)
                throw new TlParseException("implausible vector count " + count);

            var r = new long[count];
            for (int i = 0; i < count; i++) r[i] = ReadLong();
            return r;
        }

        /// <summary>Reads a constructor and throws unless it is the expected one.</summary>
        public void Expect(uint constructor, string what)
        {
            uint c = ReadConstructor();
            if (c != constructor)
                throw new TlParseException("expected " + what + " (0x" + constructor.ToString("x8") +
                                           "), got 0x" + c.ToString("x8"));
        }
    }
}
