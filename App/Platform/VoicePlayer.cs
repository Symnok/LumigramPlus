using System;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Concentus.Structs;
using Lumigram.Voip;

namespace LumigramPlus.App
{
    /// <summary>
    /// Turns arriving Opus frames into sound.
    ///
    /// A MediaStreamSource rather than any of the simpler playback paths, because a
    /// call needs samples handed over as they arrive rather than a file played from
    /// start to finish. It asks for one sample at a time and we answer from the
    /// jitter buffer, which is the shape this problem actually has.
    ///
    /// The awkward part is that playback pulls rather than being pushed: the source
    /// asks for the next twenty milliseconds whenever it is ready for them, and the
    /// answer has to be immediate. There is no waiting for a late frame - the
    /// speaker cone is going to move either way, and the only question is what it
    /// moves to.
    ///
    /// So a gap is concealed rather than filled with silence. Opus can invent a
    /// plausible continuation of what it just decoded, which sounds like a rough
    /// edge; digital silence in the middle of a word sounds like a click, which the
    /// ear notices far more.
    /// </summary>
    internal sealed class VoicePlayer : IDisposable
    {
        /// <summary>Opus at its native rate, which is what the far end sends.</summary>
        private const int SampleRate = 48000;

        private const int Channels = 1;
        private const int BitsPerSample = 16;

        /// <summary>
        /// Room for the longest frame Opus can carry, not the one we expect.
        ///
        /// 120 ms. The far end chooses its own frame duration and libtgvoip's
        /// default is 60 ms rather than the 20 this first assumed - which is not a
        /// negotiation failure but an ordinary choice, and decoding it into a buffer
        /// sized for 20 fails with nothing more helpful than "buffer too small".
        /// </summary>
        private const int MaxFrameSamples = SampleRate / 1000 * 120;

        private readonly JitterBuffer _buffer = new JitterBuffer();
        private readonly OpusDecoder _decoder = new OpusDecoder(SampleRate, Channels);
        private readonly short[] _pcm = new short[MaxFrameSamples];

        /// <summary>
        /// How many samples the last frame held.
        ///
        /// Concealment has to invent audio of some length, and the right length is
        /// whatever the far end has been sending.
        /// </summary>
        private int _lastSamples = SampleRate / 1000 * JitterBuffer.DefaultFrameDuration;

        /// <summary>
        /// How much silence to invent at a time while waiting for audio.
        ///
        /// Deliberately shorter than a frame. The player pulls as fast as it can
        /// until its own buffer is full, and every concealed sample advances the
        /// presentation clock - so concealing a whole 60 ms frame per request during
        /// a stall buries the real audio behind a wall of invented silence.
        /// </summary>
        private const int ConcealSamples = SampleRate / 1000 * 20;

        private MediaStreamSource _source;
        private TimeSpan _position;
        private bool _stopped;

        /// <summary>
        /// How many times the player has asked for audio.
        ///
        /// The single most useful number here. If this stops climbing, playback has
        /// given up and nothing about the network or the buffer matters; if it keeps
        /// climbing while nothing is heard, the fault is on this side of it.
        /// </summary>
        public int Requests;

        /// <summary>Frames played, and gaps concealed, for a page that shows its work.</summary>
        public int Played;
        public int Concealed { get { return _buffer.Missing; } }
        public int Late { get { return _buffer.Late; } }
        public int Waiting { get { return _buffer.Count; } }
        public int DelayMs { get { return _buffer.DelayMs; } }
        public string LastError;

        /// <summary>
        /// The source to hand a MediaElement.
        ///
        /// Built here rather than in XAML because the format has to match what the
        /// decoder produces exactly - a mismatch is not an error, it is a call that
        /// sounds like a chipmunk or like a drone.
        /// </summary>
        public MediaStreamSource Source
        {
            get
            {
                if (_source != null) return _source;

                var properties = AudioEncodingProperties.CreatePcm(
                    SampleRate, Channels, BitsPerSample);

                var descriptor = new AudioStreamDescriptor(properties);

                _source = new MediaStreamSource(descriptor);

                // No duration is set at all. A live call has no length, and
                // declaring one - even zero - gives the player an end to reach,
                // after which it stops asking for samples and the call goes quiet
                // with everything else still working.
                _source.CanSeek = false;

                // How much to gather before starting. Kept small deliberately: the
                // jitter buffer is already holding audio back for the same reason,
                // and paying the delay twice is what makes a call feel like a
                // satellite link.
                _source.BufferTime = TimeSpan.FromMilliseconds(40);

                _source.SampleRequested += OnSampleRequested;

                // A live stream starts where it starts. Without answering this the
                // player waits for a position that will never be agreed.
                _source.Starting += delegate (MediaStreamSource s,
                                              MediaStreamSourceStartingEventArgs a)
                {
                    a.Request.SetActualStartPosition(TimeSpan.Zero);
                };

                // Playback ending on its own is worth knowing about: it looks
                // exactly like the far end going quiet.
                _source.Closed += delegate (MediaStreamSource s,
                                            MediaStreamSourceClosedEventArgs a)
                {
                    LastError = "playback closed: " + a.Request.Reason;
                };

                return _source;
            }
        }

        /// <summary>Takes a frame off the network.</summary>
        public void Receive(byte[] opus, int timestamp)
        {
            if (_stopped) return;

            _buffer.Put(timestamp, opus);
        }

        /// <summary>
        /// Answers the player's request for the next twenty milliseconds.
        ///
        /// Always answers, and always with exactly one frame's worth. Returning
        /// nothing would end the stream, and returning a different length would
        /// drift the clock the player is keeping.
        /// </summary>
        private void OnSampleRequested(MediaStreamSource sender,
                                       MediaStreamSourceSampleRequestedEventArgs args)
        {
            Requests++;

            try
            {
                byte[] frame = _buffer.Get();
                int samples;

                if (frame != null)
                {
                    // The capacity is passed, not the expected size: Opus decodes
                    // whatever the packet actually holds and says how much that was.
                    samples = _decoder.Decode(frame, 0, frame.Length,
                                              _pcm, 0, MaxFrameSamples, false);
                    _lastSamples = samples;
                    Played++;
                }
                else
                {
                    // Nothing to play. The last argument asks Opus to invent a
                    // continuation of what it decoded before, which is what makes a
                    // dropout sound like a rough edge rather than a click.
                    samples = _decoder.Decode(null, 0, 0, _pcm, 0, ConcealSamples, true);
                }

                if (samples <= 0) samples = ConcealSamples;

                Emit(args, samples);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;

                // Leaving the sample unset ends playback, and a call that goes
                // permanently silent on one bad frame is worse than one that clicks.
                Array.Clear(_pcm, 0, ConcealSamples);
                Emit(args, ConcealSamples);
            }
        }

        /// <summary>
        /// Hands over the decoded samples with the duration they actually represent.
        ///
        /// The player keeps its own clock from these durations, so a sample whose
        /// declared length disagrees with its contents drifts playback against the
        /// sender - slowly, and audibly.
        /// </summary>
        private void Emit(MediaStreamSourceSampleRequestedEventArgs args, int samples)
        {
            MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(
                Pack(_pcm, samples), _position);

            sample.Duration = TimeSpan.FromTicks(
                TimeSpan.TicksPerSecond * samples / SampleRate);

            args.Request.Sample = sample;
            _position += sample.Duration;
        }

        /// <summary>Sixteen-bit samples, little-endian, as the format declares.</summary>
        private static IBuffer Pack(short[] samples, int count)
        {
            var bytes = new byte[count * 2];

            for (int i = 0; i < count; i++)
            {
                bytes[i * 2] = (byte)samples[i];
                bytes[i * 2 + 1] = (byte)(samples[i] >> 8);
            }

            var writer = new DataWriter();
            writer.WriteBytes(bytes);
            return writer.DetachBuffer();
        }

        public void Dispose()
        {
            _stopped = true;

            if (_source != null)
            {
                _source.SampleRequested -= OnSampleRequested;
                _source = null;
            }

            _buffer.Reset();
        }
    }
}
