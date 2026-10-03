using System;

namespace Lumigram.Audio
{
    /// <summary>
    /// Walks an Opus stream for playback: which packet to decode next, how much of
    /// what it decodes to keep, and what time each kept stretch belongs at.
    ///
    /// The decoder itself is handed in rather than named, because Core takes no
    /// outside libraries and the decoder is one. That is also what lets the desktop
    /// tests run exactly this code against a real encoder and decoder: the phone
    /// passes the same Concentus calls the tests do.
    ///
    /// Not thread-safe on its own. The phone's player asks for audio on one thread
    /// and seeks on another, and the caller - VoiceFilePlayer - holds the lock.
    /// </summary>
    public sealed class OpusPlayback
    {
        /// <summary>Decodes one packet into pcm and returns samples per channel.</summary>
        public delegate int DecodeFunc(byte[] packet, short[] pcm, int maxSamples);

        /// <summary>
        /// Packets decoded and thrown away before the one a seek lands in. Opus
        /// carries state between packets, and eighty milliseconds of run-up stops a
        /// cold decoder making a burst of noise where the seek lands.
        /// </summary>
        public const int Preroll = 4;

        private readonly OpusStream _stream;
        private readonly OpusTimeline _timeline;
        private readonly DecodeFunc _decode;
        private readonly Action _reset;
        private readonly short[] _pcm;

        private int _next;
        private long _skip;
        private TimeSpan _position;

        public readonly int Channels;

        public OpusPlayback(OpusStream stream, int channels, DecodeFunc decode, Action reset)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (decode == null) throw new ArgumentNullException("decode");

            _stream = stream;
            _timeline = new OpusTimeline(stream);
            _decode = decode;
            _reset = reset;

            Channels = channels == 2 ? 2 : 1;
            _pcm = new short[OpusTimeline.MaxPacketSamples * Channels];

            Seek(TimeSpan.Zero);
        }

        public TimeSpan Duration { get { return _timeline.Duration; } }

        /// <summary>Where the next stretch handed out will start.</summary>
        public TimeSpan Position { get { return _position; } }

        /// <summary>The decoded samples the last call to Next pointed into.</summary>
        public short[] Pcm { get { return _pcm; } }

        /// <summary>Moves to <paramref name="at"/>, clamped to the message.</summary>
        public void Seek(TimeSpan at)
        {
            if (at < TimeSpan.Zero) at = TimeSpan.Zero;
            if (at > _timeline.Duration) at = _timeline.Duration;

            int packet;
            long skip;
            _timeline.Locate(at, Preroll, out packet, out skip);

            _next = packet;
            _skip = skip;
            _position = at;

            // A clean start, so the run-up builds the decoder's state from nothing
            // rather than on top of whatever came before the jump.
            if (_reset != null) _reset();
        }

        /// <summary>
        /// Decodes up to the next stretch that is meant to be heard.
        ///
        /// On true, Pcm holds it from sample <paramref name="from"/> for
        /// <paramref name="count"/> samples per channel, and it belongs at
        /// <paramref name="at"/>. False means the message is over.
        ///
        /// Packets that are entirely run-up or pre-skip are decoded and dropped
        /// without being handed out: only audio meant to be heard leaves here.
        /// </summary>
        public bool Next(out int from, out int count, out TimeSpan at)
        {
            from = 0;
            count = 0;
            at = _position;

            while (_next < _timeline.Count)
            {
                byte[] packet = _stream.Packets[_next];
                int expected = _timeline.LengthOf(_next);
                _next++;

                int samples;
                try
                {
                    samples = _decode(packet, _pcm, OpusTimeline.MaxPacketSamples);
                }
                catch (Exception)
                {
                    // A damaged packet becomes silence of the length it should have
                    // been, so everything after it still plays at the right time.
                    samples = expected;
                    Array.Clear(_pcm, 0, samples * Channels);
                }

                if (samples <= 0) continue;

                int skipHere = 0;
                if (_skip > 0)
                {
                    if (_skip >= samples)
                    {
                        _skip -= samples;
                        continue;
                    }

                    skipHere = (int)_skip;
                    _skip = 0;
                }

                from = skipHere;
                count = samples - skipHere;
                at = _position;

                _position += OpusTimeline.ToTime(count);
                return true;
            }

            return false;
        }
    }
}
