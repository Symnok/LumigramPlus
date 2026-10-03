using System;

namespace Lumigram.Audio
{
    /// <summary>
    /// Where in time each packet of an Opus stream falls, and where to start
    /// decoding to be heard from a given moment.
    ///
    /// A voice message is played by decoding it as it plays, one packet at a time,
    /// rather than all at once before the first sound. That is what makes playback
    /// start the moment the file is here - and what makes seeking a question of its
    /// own, because "two seconds in" has to be turned into "packet 100, minus a few
    /// samples" before anything can be decoded.
    ///
    /// Kept in Core, away from the player, because this is the part that can be
    /// wrong quietly: an off-by-one here is a voice message that starts with a click,
    /// or a slider that drifts from what is being heard.
    /// </summary>
    public sealed class OpusTimeline
    {
        /// <summary>Opus always counts in 48 kHz samples, whatever it was encoded at.</summary>
        public const int Rate = 48000;

        /// <summary>
        /// The longest a single packet can be: 120 ms. Anything claiming more is
        /// damaged, and is treated as holding nothing rather than trusted.
        /// </summary>
        public const int MaxPacketSamples = Rate / 1000 * 120;

        private readonly long[] _start;
        private readonly int[] _length;

        /// <summary>Samples to throw away from the front - see OpusStream.PreSkip.</summary>
        public readonly int PreSkip;

        /// <summary>Every packet's samples added up, pre-skip included.</summary>
        public readonly long TotalSamples;

        public int Count { get { return _start.Length; } }

        public OpusTimeline(OpusStream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");

            PreSkip = Math.Max(0, stream.PreSkip);

            int n = stream.Packets.Count;
            _start = new long[n];
            _length = new int[n];

            long at = 0;
            for (int i = 0; i < n; i++)
            {
                _start[i] = at;
                _length[i] = PacketSamples(stream.Packets[i]);
                at += _length[i];
            }

            TotalSamples = at;
        }

        /// <summary>How long the message is, as heard - the pre-skip is not.</summary>
        public TimeSpan Duration
        {
            get { return ToTime(Math.Max(0, TotalSamples - PreSkip)); }
        }

        public int LengthOf(int packet) { return _length[packet]; }

        /// <summary>
        /// Where to begin decoding so that what is heard starts at
        /// <paramref name="at"/>.
        ///
        /// <paramref name="preroll"/> packets before the right one are decoded too
        /// and thrown away. Opus carries state from packet to packet, and a decoder
        /// started cold in the middle of a stream produces a brief burst of noise;
        /// a few packets of run-up settle it before anything is heard.
        ///
        /// <paramref name="skip"/> is how many samples of what that first packet
        /// decodes to are thrown away - the pre-roll, the part of the target packet
        /// before the moment asked for, and at the very start the encoder's own
        /// pre-skip.
        /// </summary>
        public void Locate(TimeSpan at, int preroll, out int packet, out long skip)
        {
            packet = 0;
            skip = PreSkip;
            if (_start.Length == 0) return;

            long target = PreSkip + at.Ticks * Rate / TimeSpan.TicksPerSecond;
            if (target < PreSkip) target = PreSkip;
            if (target >= TotalSamples) target = Math.Max(0, TotalSamples - 1);

            // The packet the target sample falls in. Binary search: a ten-minute
            // message is thirty thousand packets, and this runs on every drag of the
            // slider.
            int lo = 0, hi = _start.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_start[mid] <= target) lo = mid;
                else hi = mid - 1;
            }

            packet = Math.Max(0, lo - Math.Max(0, preroll));
            skip = target - _start[packet];
        }

        /// <summary>Converts a count of 48 kHz samples to time.</summary>
        public static TimeSpan ToTime(long samples)
        {
            return TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / Rate);
        }

        /// <summary>
        /// How many 48 kHz samples one Opus packet decodes to, read from its first
        /// byte without decoding it (RFC 6716, section 3.1).
        ///
        /// The table-of-contents byte names a configuration - which fixes the frame
        /// length - and how many frames follow. Returns 0 for a packet that is empty
        /// or claims to be longer than Opus allows.
        /// </summary>
        public static int PacketSamples(byte[] packet)
        {
            if (packet == null || packet.Length == 0) return 0;

            int toc = packet[0];
            int config = toc >> 3;

            // Frame length in tenths of a millisecond, by configuration range.
            int tenths;
            if (config < 12)
            {
                // SILK: 10, 20, 40, 60 ms.
                int[] silk = { 100, 200, 400, 600 };
                tenths = silk[config & 3];
            }
            else if (config < 16)
            {
                // Hybrid: 10, 20 ms.
                tenths = (config & 1) == 0 ? 100 : 200;
            }
            else
            {
                // CELT: 2.5, 5, 10, 20 ms.
                int[] celt = { 25, 50, 100, 200 };
                tenths = celt[config & 3];
            }

            int frames;
            switch (toc & 3)
            {
                case 0: frames = 1; break;
                case 1:
                case 2: frames = 2; break;
                default:
                    // An arbitrary number of frames, given in the next byte.
                    if (packet.Length < 2) return 0;
                    frames = packet[1] & 0x3F;
                    break;
            }

            int samples = frames * tenths * (Rate / 1000) / 10;
            return samples > MaxPacketSamples ? 0 : samples;
        }
    }
}
