using System;

namespace Lumigram.Voip
{
    /// <summary>What a voice packet is for.</summary>
    public static class VoipPacketType
    {
        public const byte Init = 1;
        public const byte InitAck = 2;
        public const byte StreamState = 3;
        public const byte StreamData = 4;
        public const byte Ping = 6;
        public const byte Pong = 7;
        public const byte Nop = 14;
    }

    /// <summary>A packet taken off the wire.</summary>
    public sealed class VoipPacket
    {
        public byte Type;

        /// <summary>Their sequence number, and the highest of ours they have seen.</summary>
        public uint Sequence;
        public uint AckSequence;

        /// <summary>A bitmask of the 32 packets before <see cref="AckSequence"/>.</summary>
        public uint AckMask;

        /// <summary>Whatever follows the header, unread.</summary>
        public byte[] Body;

        /// <summary>The Opus frame, for a stream data packet.</summary>
        public byte[] Audio;

        /// <summary>Its timestamp in milliseconds, which is what orders playback.</summary>
        public int Timestamp;
    }

    /// <summary>
    /// The voice protocol's own framing, inside the encryption.
    ///
    /// This is libtgvoip's format at protocol version 9, which is what both ends
    /// agree to when the call advertises layer 92. The older shapes it can also
    /// speak are not implemented: there is no point being compatible with a client
    /// too old to be running.
    ///
    /// Everything is little-endian, and the reader is deliberately strict. A packet
    /// that does not make sense is one from a different call or a different version,
    /// and guessing at it would put noise into someone's ear.
    /// </summary>
    public static class VoipPackets
    {
        /// <summary>libtgvoip's version, and the oldest it will talk to.</summary>
        public const int ProtocolVersion = 9;
        public const int MinProtocolVersion = 3;

        /// <summary>FOURCC('O','P','U','S') - the only codec this client offers.</summary>
        public const int CodecOpus = 0x4F505553;

        /// <summary>Audio is stream 1. Video would be 2, and there is none.</summary>
        private const byte AudioStream = 1;

        /// <summary>
        /// How long our own frames are, in milliseconds.
        ///
        /// Sixty, which is libtgvoip's own default rather than an arbitrary choice.
        /// It is declared in the init acknowledgement and has to match what is
        /// actually sent - a peer told one number and fed another sorts the frames
        /// against a clock that ticks at the wrong rate, and discards most of them
        /// as duplicates or as arriving out of order.
        ///
        /// Matching its default also means a peer that somehow misses the
        /// declaration still lands on the right answer.
        /// </summary>
        public const int OutgoingFrameMs = 60;

        /// <summary>Set when the length that follows is 16 bits rather than 8.</summary>
        private const byte Length16 = 0x40;

        /// <summary>
        /// Writes the header every packet starts with.
        ///
        /// The acknowledgements ride along in the header rather than in packets of
        /// their own, which is why every packet needs to know what has been heard.
        /// </summary>
        public static void WriteHeader(Writer w, byte type, uint sequence,
                                       uint theirLastSequence, uint ackMask)
        {
            w.Byte(type);
            w.Int((int)theirLastSequence);
            w.Int((int)sequence);
            w.Int((int)ackMask);
            w.Byte(0);              // no extras, no receive timestamps
        }

        /// <summary>
        /// The init packet: what we are and what we can decode.
        ///
        /// Sent repeatedly until the other end acknowledges it. Until that exchange
        /// completes neither side sends audio, which is why a call with working
        /// signalling can still sit forever saying "connecting".
        /// </summary>
        public static byte[] Init(uint sequence, uint theirLastSequence, uint ackMask)
        {
            var w = new Writer(64);

            WriteHeader(w, VoipPacketType.Init, sequence, theirLastSequence, ackMask);

            w.Int(ProtocolVersion);
            w.Int(MinProtocolVersion);
            w.Int(0);               // no data saving, no video, no group calls

            w.Byte(1);              // one audio codec
            w.Int(CodecOpus);
            w.Byte(0);              // no video decoders
            w.Byte(0);              // no video resolution

            return w.ToArray();
        }

        /// <summary>The answer to an init, saying the same about us.</summary>
        public static byte[] InitAck(uint sequence, uint theirLastSequence, uint ackMask)
        {
            var w = new Writer(64);

            WriteHeader(w, VoipPacketType.InitAck, sequence, theirLastSequence, ackMask);

            w.Int(ProtocolVersion);
            w.Int(MinProtocolVersion);

            w.Byte(1);              // one audio stream
            w.Byte(AudioStream);
            w.Byte(1);              // stream type: audio
            w.Int(CodecOpus);
            w.Short((short)OutgoingFrameMs);
            w.Byte(1);              // enabled

            w.Byte(0);              // no video streams

            return w.ToArray();
        }

        /// <summary>
        /// One frame of speech.
        ///
        /// The timestamp is the stream's own clock in milliseconds, not the wall
        /// clock: it advances by the frame duration whether or not packets are late,
        /// and it is what the far end's jitter buffer sorts on.
        /// </summary>
        public static byte[] StreamData(uint sequence, uint theirLastSequence, uint ackMask,
                                        byte[] opus, int timestamp)
        {
            var w = new Writer(opus.Length + 32);

            WriteHeader(w, VoipPacketType.StreamData, sequence, theirLastSequence, ackMask);

            bool long16 = opus.Length > 255;

            w.Byte((byte)(AudioStream | (long16 ? Length16 : 0)));

            if (long16) w.Short((short)opus.Length);
            else w.Byte((byte)opus.Length);

            w.Int(timestamp);
            w.Raw(opus);

            return w.ToArray();
        }

        /// <summary>
        /// Reads a decrypted packet, or returns null if it is not one we understand.
        /// </summary>
        public static VoipPacket Read(byte[] data)
        {
            if (data == null || data.Length < 14) return null;

            var r = new Reader(data);

            var packet = new VoipPacket
            {
                Type = r.Byte(),
                AckSequence = (uint)r.Int(),
                Sequence = (uint)r.Int(),
                AckMask = (uint)r.Int(),
            };

            byte flags = r.Byte();

            // Extras are a list of small typed blobs riding in the header. None of
            // them is needed here, but they have to be stepped over or everything
            // after them reads as rubbish.
            if ((flags & 1) != 0)
            {
                int count = r.Byte();
                for (int i = 0; i < count; i++)
                {
                    int length = r.Byte();
                    r.Skip(length);
                }
            }

            if ((flags & 2) != 0) r.Skip(4);       // a receive timestamp, for video

            if (r.Failed) return null;

            if (packet.Type == VoipPacketType.StreamData)
            {
                byte stream = r.Byte();
                int length = (stream & Length16) != 0 ? (ushort)r.Short() : r.Byte();

                packet.Timestamp = r.Int();
                packet.Audio = r.Raw(length);
            }
            else
            {
                packet.Body = r.Rest();
            }

            return r.Failed ? null : packet;
        }

        /// <summary>A little-endian writer, kept here so the format reads in one place.</summary>
        public sealed class Writer
        {
            private byte[] _buffer;
            private int _at;

            public Writer(int capacity) { _buffer = new byte[Math.Max(16, capacity)]; }

            private void Need(int count)
            {
                if (_at + count <= _buffer.Length) return;

                var grown = new byte[Math.Max(_buffer.Length * 2, _at + count)];
                Buffer.BlockCopy(_buffer, 0, grown, 0, _at);
                _buffer = grown;
            }

            public void Byte(byte value)
            {
                Need(1);
                _buffer[_at++] = value;
            }

            public void Short(short value)
            {
                Need(2);
                _buffer[_at++] = (byte)value;
                _buffer[_at++] = (byte)(value >> 8);
            }

            public void Int(int value)
            {
                Need(4);
                _buffer[_at++] = (byte)value;
                _buffer[_at++] = (byte)(value >> 8);
                _buffer[_at++] = (byte)(value >> 16);
                _buffer[_at++] = (byte)(value >> 24);
            }

            public void Raw(byte[] value)
            {
                Need(value.Length);
                Buffer.BlockCopy(value, 0, _buffer, _at, value.Length);
                _at += value.Length;
            }

            public byte[] ToArray()
            {
                var result = new byte[_at];
                Buffer.BlockCopy(_buffer, 0, result, 0, _at);
                return result;
            }
        }

        /// <summary>
        /// The matching reader.
        ///
        /// Running off the end sets a flag rather than throwing: a truncated packet
        /// is an ordinary thing to receive on a UDP port, and every read site would
        /// otherwise need its own guard.
        /// </summary>
        public sealed class Reader
        {
            private readonly byte[] _data;
            private int _at;

            public bool Failed;

            public Reader(byte[] data) { _data = data; }

            private bool Have(int count)
            {
                if (_at + count <= _data.Length) return true;

                Failed = true;
                return false;
            }

            public byte Byte()
            {
                if (!Have(1)) return 0;
                return _data[_at++];
            }

            public short Short()
            {
                if (!Have(2)) return 0;

                short value = (short)(_data[_at] | (_data[_at + 1] << 8));
                _at += 2;
                return value;
            }

            public int Int()
            {
                if (!Have(4)) return 0;

                int value = _data[_at] | (_data[_at + 1] << 8) |
                            (_data[_at + 2] << 16) | (_data[_at + 3] << 24);
                _at += 4;
                return value;
            }

            public byte[] Raw(int count)
            {
                if (!Have(count)) return new byte[0];

                var value = new byte[count];
                Buffer.BlockCopy(_data, _at, value, 0, count);
                _at += count;
                return value;
            }

            public void Skip(int count)
            {
                if (Have(count)) _at += count;
            }

            public byte[] Rest() { return Raw(_data.Length - _at); }
        }
    }
}
