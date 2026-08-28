using System;
using System.Collections.Generic;

namespace Lumigram.Audio
{
    /// <summary>
    /// One Opus stream lifted out of an OGG file.
    /// </summary>
    public sealed class OpusStream
    {
        public int Channels;

        /// <summary>
        /// Samples the encoder wants discarded from the front, at 48 kHz.
        ///
        /// Opus needs a moment of audio before its output is correct, so every
        /// encoder prepends some and says how much to throw away. Keeping it is
        /// heard as a click at the start of every voice message.
        /// </summary>
        public int PreSkip;

        /// <summary>The audio packets, in order, headers already removed.</summary>
        public List<byte[]> Packets = new List<byte[]>();
    }

    /// <summary>
    /// Reads Opus packets out of an OGG container.
    ///
    /// Telegram voice messages are Opus in OGG, which is two problems: the codec,
    /// and the container it arrives in. This is the container half, and it is the
    /// half worth writing ourselves - OGG paging is simple, well specified, and
    /// small, where the codec is neither.
    ///
    /// Only what a voice message needs is implemented: one logical stream, and the
    /// two mandatory header packets. Chained or multiplexed streams do not occur
    /// here, and pretending to support them would mean claiming behaviour nothing
    /// exercises.
    /// </summary>
    public static class OggOpus
    {
        private const int HeaderBytes = 27;          // up to and including page_segments

        /// <summary>
        /// Parses a whole OGG file.
        ///
        /// Throws when the bytes are not an Opus stream - a wrong file type is worth
        /// reporting rather than playing as silence.
        /// </summary>
        public static OpusStream Read(byte[] data)
        {
            if (data == null) throw new ArgumentNullException("data");

            var stream = new OpusStream();
            var packet = new List<byte>();
            bool headSeen = false;
            bool tagsSeen = false;
            int position = 0;

            while (position + HeaderBytes <= data.Length)
            {
                if (data[position] != (byte)'O' || data[position + 1] != (byte)'g' ||
                    data[position + 2] != (byte)'g' || data[position + 3] != (byte)'S')
                {
                    // Resync rather than give up: a stray byte between pages should
                    // not cost the rest of the message.
                    position++;
                    continue;
                }

                int segmentCount = data[position + 26];
                int tableAt = position + HeaderBytes;
                int bodyAt = tableAt + segmentCount;
                if (bodyAt > data.Length) break;

                // header_type bit 0: this page continues a packet from the last one.
                bool continued = (data[position + 5] & 0x01) != 0;
                if (!continued) packet.Clear();

                int bodyLength = 0;
                for (int i = 0; i < segmentCount; i++) bodyLength += data[tableAt + i];
                if (bodyAt + bodyLength > data.Length) break;

                int at = bodyAt;
                for (int i = 0; i < segmentCount; i++)
                {
                    int length = data[tableAt + i];
                    for (int b = 0; b < length; b++) packet.Add(data[at + b]);
                    at += length;

                    // A segment shorter than 255 is the last of its packet; a run of
                    // 255s means the packet spills into the next segment, and on into
                    // the next page if the run reaches the end of this one.
                    if (length == 255) continue;

                    byte[] complete = packet.ToArray();
                    packet.Clear();

                    if (!headSeen)
                    {
                        ReadHead(complete, stream);
                        headSeen = true;
                    }
                    else if (!tagsSeen)
                    {
                        tagsSeen = true;                 // OpusTags: nothing needed
                    }
                    else if (complete.Length > 0)
                    {
                        stream.Packets.Add(complete);
                    }
                }

                position = bodyAt + bodyLength;
            }

            if (!headSeen) throw new FormatException("not an Opus stream: no OpusHead");
            return stream;
        }

        private static void ReadHead(byte[] head, OpusStream stream)
        {
            if (head.Length < 19 ||
                head[0] != (byte)'O' || head[1] != (byte)'p' || head[2] != (byte)'u' ||
                head[3] != (byte)'s' || head[4] != (byte)'H' || head[5] != (byte)'e' ||
                head[6] != (byte)'a' || head[7] != (byte)'d')
            {
                throw new FormatException("not an Opus stream: first packet is not OpusHead");
            }

            stream.Channels = head[9];
            stream.PreSkip = head[10] | (head[11] << 8);

            if (stream.Channels < 1 || stream.Channels > 2)
                throw new FormatException("unsupported channel count: " + stream.Channels);
        }
    }
}
