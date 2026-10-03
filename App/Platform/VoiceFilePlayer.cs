using System;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Concentus.Structs;
using Lumigram.Audio;

namespace LumigramPlus.App
{
    /// <summary>
    /// Plays a downloaded voice message, decoding it as it plays.
    ///
    /// Voice messages are Opus in OGG, which the phone's own player cannot open.
    /// The call player already solved the same problem for audio arriving live -
    /// a MediaStreamSource that asks for the next stretch of sound and is answered
    /// by decoding one Opus packet - and this is that machinery fed from a file.
    /// Decoding as it goes means the first sound comes at once, rather than after
    /// the whole message has been turned into a WAV, and nothing extra is stored.
    ///
    /// Only the phone-side plumbing is here. Which packet comes next, how much of it
    /// to keep and when it plays is OpusPlayback, in Core, where the desktop tests
    /// run it against a real encoder and decoder.
    /// </summary>
    internal sealed class VoiceFilePlayer : IDisposable
    {
        private const int BitsPerSample = 16;

        private readonly OpusPlayback _playback;
        private readonly OpusDecoder _decoder;

        /// <summary>
        /// Guards the playback position. The player asks for samples on one thread
        /// and announces seeks on another, and a seek landing halfway through
        /// answering a request would hand back audio from the wrong place.
        /// </summary>
        private readonly object _gate = new object();

        private MediaStreamSource _source;

        /// <summary>Reads the whole file up front - voice messages are kilobytes.</summary>
        public VoiceFilePlayer(byte[] ogg)
        {
            OpusStream stream = OggOpus.Read(ogg);
            int channels = stream.Channels == 2 ? 2 : 1;

            _decoder = new OpusDecoder(OpusTimeline.Rate, channels);

            _playback = new OpusPlayback(stream, channels,
                delegate (byte[] packet, short[] pcm, int max)
                {
                    return _decoder.Decode(packet, 0, packet.Length, pcm, 0, max, false);
                },
                delegate { _decoder.ResetState(); });
        }

        public TimeSpan Duration { get { return _playback.Duration; } }

        /// <summary>
        /// The source to hand the MediaElement.
        ///
        /// The format has to match what the decoder produces exactly; a mismatch is
        /// not an error, it is a voice that sounds like a chipmunk or a drone.
        /// </summary>
        public MediaStreamSource Source
        {
            get
            {
                if (_source != null) return _source;

                var properties = AudioEncodingProperties.CreatePcm(
                    (uint)OpusTimeline.Rate, (uint)_playback.Channels, BitsPerSample);

                _source = new MediaStreamSource(new AudioStreamDescriptor(properties));

                // A file, unlike a call, has a length - which is what gives the
                // slider an end and lets the player say when it is done.
                _source.Duration = _playback.Duration;
                _source.CanSeek = true;
                _source.BufferTime = TimeSpan.FromMilliseconds(250);

                _source.SampleRequested += OnSampleRequested;
                _source.Starting += OnStarting;

                return _source;
            }
        }

        /// <summary>
        /// The player starting, or moving: from the beginning, after a drag of the
        /// slider, or again after reaching the end. Answered with where playback
        /// really starts; leaving it unanswered stalls the player.
        /// </summary>
        private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            lock (_gate)
            {
                TimeSpan? at = args.Request.StartPosition;
                if (at.HasValue) _playback.Seek(at.Value);

                args.Request.SetActualStartPosition(_playback.Position);
            }
        }

        /// <summary>
        /// Answers the player's request with the next stretch of sound. Past the end
        /// the request is left unanswered, which is how a MediaStreamSource says the
        /// stream is over.
        /// </summary>
        private void OnSampleRequested(MediaStreamSource sender,
                                       MediaStreamSourceSampleRequestedEventArgs args)
        {
            lock (_gate)
            {
                int from, count;
                TimeSpan at;
                if (!_playback.Next(out from, out count, out at)) return;

                MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(
                    Pack(_playback.Pcm, from, count, _playback.Channels), at);

                // The player keeps its clock - and the slider - from these.
                sample.Duration = OpusTimeline.ToTime(count);
                args.Request.Sample = sample;
            }
        }

        /// <summary>Sixteen-bit samples, little-endian, as the format declares.</summary>
        private static IBuffer Pack(short[] pcm, int from, int count, int channels)
        {
            var bytes = new byte[count * channels * 2];

            int start = from * channels;
            int values = count * channels;
            for (int i = 0; i < values; i++)
            {
                short v = pcm[start + i];
                bytes[i * 2] = (byte)v;
                bytes[i * 2 + 1] = (byte)(v >> 8);
            }

            var writer = new DataWriter();
            writer.WriteBytes(bytes);
            return writer.DetachBuffer();
        }

        public void Dispose()
        {
            if (_source == null) return;

            _source.SampleRequested -= OnSampleRequested;
            _source.Starting -= OnStarting;
            _source = null;
        }
    }
}
