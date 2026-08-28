using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Concentus.Enums;
using Concentus.Structs;

namespace LumigramPlus.App
{
    /// <summary>
    /// Takes speech off the microphone and turns it into Opus frames.
    ///
    /// Windows Phone 8.1 has no low-latency capture API that managed code can reach.
    /// AudioGraph is Windows 10; WASAPI needs C++. What is left is MediaCapture
    /// recording to a stream, and reading that stream from behind while it is still
    /// being written - which is a strange shape for a live call and is nonetheless
    /// the only shape available without a native component.
    ///
    /// The consequence to watch is delay. Everything here is downstream of however
    /// much the platform's recording pipeline buffers before it flushes, and that is
    /// not ours to set. If it turns out to be too much, the answer is a small C++
    /// media sink and nothing else about this changes.
    ///
    /// The recording is restarted periodically because the stream it writes into
    /// only grows. Half a minute of audio is several megabytes, and a call is not
    /// bounded; the cost is a brief gap each time, which is a poor trade made
    /// knowingly rather than an oversight.
    /// </summary>
    internal sealed class VoiceRecorder : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 1;
        private const int BitsPerSample = 16;

        /// <summary>Whatever we declare to the far end, so the two cannot diverge.</summary>
        private const int FrameMs = Lumigram.Voip.VoipPackets.OutgoingFrameMs;
        private const int FrameSamples = SampleRate / 1000 * FrameMs;

        /// <summary>
        /// Enough of the file to be sure the header has been written.
        ///
        /// The header is not a fixed 44 bytes. That is the canonical minimum, and
        /// Media Foundation writes what it likes - an extensible format description
        /// is longer, and extra chunks can sit between it and the audio. Assuming 44
        /// and being wrong by an odd number of bytes swaps every sample's halves,
        /// which is not silence or a glitch but continuous noise.
        /// </summary>
        private const int HeaderSearchBytes = 512;

        /// <summary>
        /// How much to let the sink write before starting again.
        ///
        /// The stream it writes into only grows, so a call has to restart it
        /// eventually - and each restart loses the audio in between. Sixteen
        /// megabytes is about three minutes, which turns a gap every twenty seconds
        /// into one that most calls never reach.
        /// </summary>
        private const ulong RestartAfterBytes = 16 * 1024 * 1024;

        private MediaCapture _capture;
        private InMemoryRandomAccessStream _stream;
        private CancellationTokenSource _stop;

        private readonly OpusEncoder _encoder =
            new OpusEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);

        /// <summary>
        /// Frames encoded but not yet sent.
        ///
        /// The recorder writes in bursts - the platform's pipeline flushes when it
        /// chooses, not every twenty milliseconds - so a read can yield ten frames
        /// at once and then nothing for a while. Sending them the moment they are
        /// encoded passes that shape straight to the far end, which hears it as
        /// speech arriving in clumps.
        ///
        /// So capture and sending are separated by this queue: bursty in, steady
        /// out, which is what the audio actually is.
        /// </summary>
        private readonly Queue<byte[]> _outgoing = new Queue<byte[]>();
        private readonly Queue<int> _stamps = new Queue<int>();
        private readonly object _queueGate = new object();

        /// <summary>
        /// The most frames to hold before dropping the oldest.
        ///
        /// A queue that grows is delay that grows. Half a second is already more lag
        /// than a conversation tolerates, and beyond it the right thing is to lose
        /// audio rather than to fall further behind.
        /// </summary>
        private const int MaxQueued = 25;

        /// <summary>Exactly one frame: it is emptied the moment it is full.</summary>
        private readonly short[] _pending = new short[FrameSamples];
        private int _pendingCount;

        private byte _oddByte;
        private bool _hasOddByte;
        private readonly byte[] _packet = new byte[1275];

        private ulong _read;
        private bool _skippedHeader;
        private int _timestamp;

        /// <summary>What the recorder is actually producing, as it says itself.</summary>
        public string Format = "unknown";

        /// <summary>Which capture mode this call is using.</summary>
        public string Processing = "";

        /// <summary>
        /// The loudest sample in the last second, as a percentage of full scale.
        ///
        /// Every other counter here says how much audio moved, and none of them says
        /// whether it was audio. Silence, a gate chopping speech into pieces, and a
        /// microphone that is not really open all produce identical byte counts.
        /// </summary>
        public int Level;

        /// <summary>Frames quiet enough to be nothing at all.</summary>
        public int SilentFrames;

        /// <summary>
        /// Whether to stop sending what the microphone hears.
        ///
        /// Capture and encoding carry on regardless, and only the queueing stops.
        /// The timestamps count encoded frames, so letting them keep counting is
        /// what makes speech resume in the right place - stopping the encoder would
        /// leave the far end's clock behind by however long the mute lasted.
        /// </summary>
        public bool Muted;

        private int _peak;
        private int _framesSincePeak;

        /// <summary>An encoded frame, ready to send.</summary>
        public event Action<byte[], int> Frame;

        /// <summary>Counters for a page that has to show whether this works.</summary>
        public int Frames;
        public int Sent;
        public int Dropped;
        public int Restarts;

        /// <summary>How many frames are waiting to go out.</summary>
        public int Queued { get { lock (_queueGate) return _outgoing.Count; } }
        public long BytesCaptured;
        public string LastError;
        public bool Running;

        public async Task StartAsync()
        {
            _capture = new MediaCapture();

            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,

                // Communications puts capture through the platform's voice
                // pipeline - echo cancellation, noise suppression, gain control -
                // which is what a call wants and what managed code cannot do for
                // itself at fifty frames a second.
                //
                // There was briefly a switch to take the microphone raw instead.
                // Raw capture produced complete silence on this hardware, so the
                // setting was only ever a way to break a call.
                MediaCategory = MediaCategory.Communications,
                AudioProcessing = Windows.Media.AudioProcessing.Default,
            };

            Processing = "platform voice processing";

            await _capture.InitializeAsync(settings);

            // What the benchmark said this phone can afford, rather than the
            // library's defaults: speech at 20 kbit/s, and the cheapest search that
            // still sounds like a voice. Encoding runs fifty times a second and has
            // to leave room for everything else.
            _encoder.Bitrate = 20000;
            _encoder.Complexity = 0;
            _encoder.UseVBR = true;

            _stop = new CancellationTokenSource();

            await BeginAsync();

            Running = true;

            var ignored = ReadLoopAsync(_stop.Token);
            var alsoIgnored = SendLoopAsync(_stop.Token);
        }

        /// <summary>Starts a fresh recording, and forgets where the last one got to.</summary>
        private async Task BeginAsync()
        {
            _stream = new InMemoryRandomAccessStream();
            _read = 0;
            _skippedHeader = false;

            MediaEncodingProfile profile = MediaEncodingProfile.CreateWav(
                AudioEncodingQuality.Auto);

            // Overridden rather than accepted: Opus works at 48 kHz and a capture at
            // some other rate would need resampling, which is both work and a way to
            // sound wrong.
            profile.Audio = AudioEncodingProperties.CreatePcm(
                SampleRate, Channels, BitsPerSample);

            await _capture.StartRecordToStreamAsync(profile, _stream);
        }

        /// <summary>
        /// Reads whatever the recorder has written since last time.
        ///
        /// Polled rather than pushed, because nothing here offers a notification -
        /// the stream simply grows. Twenty milliseconds is one frame's worth, so
        /// this wakes about as often as it has something to do.
        /// </summary>
        private async Task ReadLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(FrameMs, token);
                    await DrainAsync();

                    if (_stream != null && _stream.Size > RestartAfterBytes)
                    {
                        Restarts++;

                        await _capture.StopRecordAsync();
                        await BeginAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Hung up.
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Running = false;
            }
        }

        private async Task DrainAsync()
        {
            if (_stream == null) return;

            ulong size = _stream.Size;

            // The header is written before any audio and must not be encoded as
            // though it were. Where the audio starts is read from the file rather
            // than assumed.
            if (!_skippedHeader)
            {
                if (size < HeaderSearchBytes) return;

                int start = await FindAudioAsync();
                if (start <= 0) return;

                _read = (ulong)start;
                _skippedHeader = true;
            }

            if (size <= _read) return;

            uint available = (uint)Math.Min(size - _read, 64 * 1024);

            var buffer = new Windows.Storage.Streams.Buffer(available);

            using (IInputStream input = _stream.GetInputStreamAt(_read))
            {
                IBuffer filled = await input.ReadAsync(
                    buffer, available, InputStreamOptions.None);

                if (filled.Length == 0) return;

                _read += filled.Length;
                BytesCaptured += filled.Length;

                Encode(filled);
            }
        }

        /// <summary>
        /// Walks the RIFF chunks to find where the audio actually begins, and
        /// reports the format the recorder chose.
        ///
        /// Worth doing rather than assuming, twice over: the offset has to be exact
        /// or every sample is byte-swapped, and the format tells us whether the
        /// profile we asked for is the one we got. A capture at some other rate
        /// would encode into frames of the wrong length and play back at the wrong
        /// speed, and nothing else in the pipeline would notice.
        /// </summary>
        private async Task<int> FindAudioAsync()
        {
            var buffer = new Windows.Storage.Streams.Buffer(HeaderSearchBytes);

            using (IInputStream input = _stream.GetInputStreamAt(0))
            {
                IBuffer filled = await input.ReadAsync(
                    buffer, HeaderSearchBytes, InputStreamOptions.None);

                var bytes = new byte[filled.Length];
                DataReader.FromBuffer(filled).ReadBytes(bytes);

                if (bytes.Length < 12) return 0;
                if (Tag(bytes, 0) != "RIFF" || Tag(bytes, 8) != "WAVE") return 0;

                int at = 12;

                while (at + 8 <= bytes.Length)
                {
                    string chunk = Tag(bytes, at);
                    int length = Int(bytes, at + 4);

                    if (chunk == "fmt " && at + 8 + 16 <= bytes.Length)
                    {
                        int channels = bytes[at + 10] | (bytes[at + 11] << 8);
                        int rate = Int(bytes, at + 12);
                        int bits = bytes[at + 22] | (bytes[at + 23] << 8);

                        Format = rate + " Hz, " + channels +
                                 (channels == 1 ? " channel, " : " channels, ") +
                                 bits + "-bit";

                        if (rate != SampleRate || channels != Channels)
                            LastError = "unexpected capture format: " + Format;
                    }

                    if (chunk == "data") return at + 8;

                    // Chunks are padded to even lengths, and a length that does not
                    // make sense means this is not a file we can read at all.
                    if (length <= 0) return 0;

                    at += 8 + length + (length & 1);
                }
            }

            return 0;
        }

        private static string Tag(byte[] data, int at)
        {
            return "" + (char)data[at] + (char)data[at + 1] +
                        (char)data[at + 2] + (char)data[at + 3];
        }

        private static int Int(byte[] data, int at)
        {
            return data[at] | (data[at + 1] << 8) |
                   (data[at + 2] << 16) | (data[at + 3] << 24);
        }

        /// <summary>
        /// Turns raw bytes into whole frames, however many arrive at once.
        ///
        /// The previous version filled a fixed buffer and stopped, dropping the rest
        /// of the read on the floor. That is fine while reads are small and quietly
        /// destructive when they are not: the recorder flushes in bursts, a burst can
        /// carry half a second of audio, and everything past the buffer's end was
        /// discarded. The timestamps knew nothing about it - they count encoded
        /// frames - so the far end received speech with pieces cut out of it and no
        /// indication that anything was missing.
        ///
        /// Two things carry across calls, and both have to: a partial frame, and a
        /// single byte when a read ends halfway through a sample. Losing that byte
        /// swaps the halves of every sample after it, which is not a gap but a
        /// permanent rasp.
        /// </summary>
        private void Encode(IBuffer data)
        {
            var bytes = new byte[data.Length];
            DataReader.FromBuffer(data).ReadBytes(bytes);

            int at = 0;

            // A byte left over from last time pairs with the first byte of this read.
            if (_hasOddByte && bytes.Length > 0)
            {
                Add((short)(_oddByte | (bytes[0] << 8)));
                _hasOddByte = false;
                at = 1;
            }

            while (at + 1 < bytes.Length)
            {
                Add((short)(bytes[at] | (bytes[at + 1] << 8)));
                at += 2;
            }

            if (at < bytes.Length)
            {
                _oddByte = bytes[at];
                _hasOddByte = true;
            }
        }

        /// <summary>
        /// Adds one sample, and encodes as soon as there are enough for a frame.
        /// </summary>
        private void Add(short sample)
        {
            _pending[_pendingCount++] = sample;

            if (_pendingCount < FrameSamples) return;

            _pendingCount = 0;

            Measure();

            try
            {
                int length = _encoder.Encode(_pending, 0, FrameSamples,
                                             _packet, 0, _packet.Length);

                if (length > 0 && !Muted)
                {
                    var frame = new byte[length];
                    System.Buffer.BlockCopy(_packet, 0, frame, 0, length);

                    lock (_queueGate)
                    {
                        _outgoing.Enqueue(frame);
                        _stamps.Enqueue(_timestamp);

                        while (_outgoing.Count > MaxQueued)
                        {
                            _outgoing.Dequeue();
                            _stamps.Dequeue();
                            Dropped++;
                        }
                    }

                    Frames++;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            _timestamp += FrameMs;
        }

        /// <summary>
        /// Hands frames to the network at the rate they were spoken.
        ///
        /// Paced against a clock rather than by sleeping between frames. Task.Delay
        /// on this platform rounds up to the system timer, so asking for 20 ms gets
        /// about 31 - a sender built on that ships thirty-two frames a second while
        /// the microphone produces fifty, and the missing third is heard as speech
        /// hacked to pieces.
        ///
        /// So the loop asks how many frames should have left by now and sends until
        /// it has caught up. Sleeping imprecisely then costs nothing: the error is
        /// in when frames go, never in how many.
        /// </summary>
        private async Task SendLoopAsync(CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            long sent = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(10, token);

                    long due = clock.ElapsedMilliseconds / FrameMs;

                    // Bounded, so a long stall does not empty the whole queue in one
                    // burst - which would put back exactly the clumping this exists
                    // to remove.
                    int allowance = 5;

                    while (sent < due && allowance-- > 0)
                    {
                        byte[] frame;
                        int stamp;

                        lock (_queueGate)
                        {
                            if (_outgoing.Count == 0) break;

                            frame = _outgoing.Dequeue();
                            stamp = _stamps.Dequeue();
                        }

                        Action<byte[], int> handler = Frame;
                        if (handler != null) handler(frame, stamp);

                        sent++;
                        Sent++;
                    }

                    // Nothing waiting means silence, not lateness. Without this the
                    // loop would owe the queue every frame it could not send during
                    // a pause and fire them all at once when speech resumed.
                    lock (_queueGate)
                    {
                        if (_outgoing.Count == 0) sent = due;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Hung up.
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        /// <summary>
        /// Looks at the frame about to be encoded.
        ///
        /// Held over a second rather than reported per frame, because a peak that
        /// updates fifty times a second is unreadable on a screen and speech is
        /// mostly gaps anyway.
        /// </summary>
        private void Measure()
        {
            int peak = 0;

            for (int i = 0; i < FrameSamples; i++)
            {
                int value = _pending[i];
                if (value < 0) value = -value;
                if (value > peak) peak = value;
            }

            // A hundredth of full scale. Below that a frame carries nothing anyone
            // would hear, whether because nobody spoke or because something ate it.
            if (peak < 328) SilentFrames++;

            if (peak > _peak) _peak = peak;

            if (++_framesSincePeak >= 1000 / FrameMs)
            {
                Level = _peak * 100 / short.MaxValue;
                _peak = 0;
                _framesSincePeak = 0;
            }
        }

        public void Dispose()
        {
            Running = false;

            if (_stop != null)
            {
                _stop.Cancel();
                _stop = null;
            }

            if (_capture != null)
            {
                try
                {
                    var ignored = _capture.StopRecordAsync();
                }
                catch (Exception) { }

                try { _capture.Dispose(); }
                catch (Exception) { }

                _capture = null;
            }

            if (_stream != null)
            {
                try { _stream.Dispose(); }
                catch (Exception) { }
                _stream = null;
            }
        }
    }
}
