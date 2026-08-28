using System;
using System.Collections.Generic;
using System.IO;

namespace Lumigram.Audio
{
    /// <summary>
    /// Wraps Opus packets in an OGG container.
    ///
    /// The mirror of <see cref="OggOpus"/>, and the harder direction: a reader can
    /// be forgiving, while anything written here has to satisfy every other
    /// Telegram client. Two details do the damage if they are wrong, and neither
    /// shows up when reading the file back with our own parser:
    ///
    ///   The checksum is OGG's own CRC-32 - the same polynomial as zlib but with no
    ///   reflection and no final inversion, so a stock CRC routine gives a
    ///   plausible, wrong answer. Our reader does not verify it; real decoders do.
    ///
    ///   Granule positions are always counted at 48 kHz, whatever rate the audio
    ///   was encoded at. Get it wrong and the file plays at the right speed but
    ///   reports the wrong duration, which is what other clients display.
    /// </summary>
    public sealed class OggWriter
    {
        private const int MaxSegments = 255;

        private readonly MemoryStream _out = new MemoryStream();
        private readonly int _serial;
        private int _sequence;

        /// <summary>
        /// <paramref name="serial"/> identifies the logical stream. Any value will
        /// do for a single-stream file; it only has to be consistent.
        /// </summary>
        public OggWriter(int serial)
        {
            _serial = serial;
        }

        /// <summary>
        /// Writes the two mandatory header packets.
        ///
        /// Each one gets a page to itself - the format requires it, and a decoder
        /// that finds them sharing a page will refuse the file.
        /// </summary>
        public void WriteHeaders(int channels, int preSkip, int inputSampleRate)
        {
            var head = new byte[19];
            head[0] = (byte)'O'; head[1] = (byte)'p'; head[2] = (byte)'u'; head[3] = (byte)'s';
            head[4] = (byte)'H'; head[5] = (byte)'e'; head[6] = (byte)'a'; head[7] = (byte)'d';
            head[8] = 1;                                   // version
            head[9] = (byte)channels;
            head[10] = (byte)(preSkip & 0xFF);
            head[11] = (byte)((preSkip >> 8) & 0xFF);
            WriteInt32(head, 12, inputSampleRate);         // informational only
            head[16] = 0; head[17] = 0;                    // output gain
            head[18] = 0;                                  // mapping family 0

            // header_type bit 1 marks the beginning of the stream.
            WritePage(new List<byte[]> { head }, 0, 0x02);

            byte[] vendor = System.Text.Encoding.UTF8.GetBytes("Lumigram");
            var tags = new byte[8 + 4 + vendor.Length + 4];
            tags[0] = (byte)'O'; tags[1] = (byte)'p'; tags[2] = (byte)'u'; tags[3] = (byte)'s';
            tags[4] = (byte)'T'; tags[5] = (byte)'a'; tags[6] = (byte)'g'; tags[7] = (byte)'s';
            WriteInt32(tags, 8, vendor.Length);
            Buffer.BlockCopy(vendor, 0, tags, 12, vendor.Length);
            WriteInt32(tags, 12 + vendor.Length, 0);       // no user comments

            WritePage(new List<byte[]> { tags }, 0, 0x00);
        }

        /// <summary>
        /// Writes audio packets.
        ///
        /// <paramref name="granuleAt48k"/> is the total number of samples, at 48 kHz
        /// and including the pre-skip, that have been decoded once the last packet
        /// on this page has been played.
        /// </summary>
        public void WriteAudio(List<byte[]> packets, long granuleAt48k, bool last)
        {
            WritePage(packets, granuleAt48k, last ? (byte)0x04 : (byte)0x00);
        }

        public byte[] ToArray()
        {
            return _out.ToArray();
        }

        private void WritePage(List<byte[]> packets, long granule, byte headerType)
        {
            var segments = new List<byte>();
            foreach (byte[] packet in packets)
            {
                int remaining = packet.Length;
                while (remaining >= 255)
                {
                    segments.Add(255);
                    remaining -= 255;
                }
                segments.Add((byte)remaining);
            }

            if (segments.Count > MaxSegments)
                throw new InvalidOperationException("too many segments for one OGG page");

            int bodyLength = 0;
            foreach (byte[] packet in packets) bodyLength += packet.Length;

            var page = new byte[27 + segments.Count + bodyLength];
            page[0] = (byte)'O'; page[1] = (byte)'g'; page[2] = (byte)'g'; page[3] = (byte)'S';
            page[4] = 0;                                   // stream structure version
            page[5] = headerType;

            for (int i = 0; i < 8; i++) page[6 + i] = (byte)((granule >> (8 * i)) & 0xFF);
            WriteInt32(page, 14, _serial);
            WriteInt32(page, 18, _sequence++);
            // 22..25 is the checksum, computed below over the page with it zeroed.
            page[26] = (byte)segments.Count;

            for (int i = 0; i < segments.Count; i++) page[27 + i] = segments[i];

            int at = 27 + segments.Count;
            foreach (byte[] packet in packets)
            {
                Buffer.BlockCopy(packet, 0, page, at, packet.Length);
                at += packet.Length;
            }

            WriteInt32(page, 22, unchecked((int)Crc(page)));

            _out.Write(page, 0, page.Length);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static uint[] _table;

        /// <summary>
        /// OGG's CRC-32.
        ///
        /// Polynomial 0x04c11db7, applied most significant bit first, starting from
        /// zero and with no final inversion. That is not the CRC-32 everyone else
        /// uses - zlib reflects the input and output and inverts both ends - so
        /// reaching for a familiar implementation produces a checksum that looks
        /// right and is rejected by every real decoder.
        /// </summary>
        private static uint Crc(byte[] data)
        {
            if (_table == null)
            {
                var table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint r = i << 24;
                    for (int bit = 0; bit < 8; bit++)
                        r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04c11db7 : r << 1;
                    table[i] = r;
                }
                _table = table;
            }

            uint crc = 0;
            for (int i = 0; i < data.Length; i++)
                crc = (crc << 8) ^ _table[((crc >> 24) & 0xFF) ^ data[i]];

            return crc;
        }
    }
}
