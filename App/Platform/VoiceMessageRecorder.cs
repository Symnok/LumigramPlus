using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace LumigramPlus.App
{
    /// <summary>
    /// Records a voice message.
    ///
    /// Separate from the recorder a call uses, and deliberately so. A call needs
    /// frames the instant they exist and cannot wait; a message is finished before
    /// anyone hears it, so this one simply accumulates and hands over the whole
    /// thing at the end. Trying to serve both from one class would mean the harder
    /// constraint everywhere for the benefit of neither.
    ///
    /// It still reads the stream as it grows rather than only at the end, for one
    /// reason: the level meter. A recording panel that shows nothing moving is
    /// indistinguishable from a microphone that never opened.
    /// </summary>
    internal sealed class VoiceMessageRecorder : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 1;
        private const int BitsPerSample = 16;

        /// <summary>Enough of the file to be sure the header is complete.</summary>
        private const int HeaderSearchBytes = 512;

        /// <summary>
        /// The longest message this will record.
        ///
        /// Two minutes. Everything is held in memory until it is sent, and a
        /// recording nobody stopped should not be the thing that runs the phone out
        /// of it.
        /// </summary>
        public const int MaxSeconds = 120;

        private MediaCapture _capture;
        private InMemoryRandomAccessStream _stream;
        private CancellationTokenSource _stop;

        private readonly List<short> _samples = new List<short>();
        private readonly object _gate = new object();

        private ulong _read;
        private bool _skippedHeader;
        private byte _oddByte;
        private bool _hasOddByte;
        private int _peak;

        public bool Running { get; private set; }
        public string LastError;

        /// <summary>How long has been recorded so far.</summary>
        public int Seconds
        {
            get { lock (_gate) return _samples.Count / SampleRate; }
        }

        /// <summary>
        /// The loudest sample since this was last read, as a percentage.
        ///
        /// Reading it clears it, so the meter falls back when the room goes quiet
        /// instead of holding the highest note of the whole recording.
        /// </summary>
        public int TakeLevel()
        {
            lock (_gate)
            {
                int level = _peak * 100 / short.MaxValue;
                _peak = 0;
                return level;
            }
        }

        public async Task StartAsync()
        {
            _capture = new MediaCapture();

            await _capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
                MediaCategory = MediaCategory.Communications,
                AudioProcessing = Windows.Media.AudioProcessing.Default,
            });

            _stream = new InMemoryRandomAccessStream();

            MediaEncodingProfile profile = MediaEncodingProfile.CreateWav(
                AudioEncodingQuality.Auto);

            profile.Audio = AudioEncodingProperties.CreatePcm(
                SampleRate, Channels, BitsPerSample);

            await _capture.StartRecordToStreamAsync(profile, _stream);

            _stop = new CancellationTokenSource();
            Running = true;

            var ignored = ReadLoopAsync(_stop.Token);
        }

        /// <summary>
        /// Stops, and returns everything recorded.
        ///
        /// The stream is drained once more first: the sink writes when it chooses,
        /// and the last flush usually lands after the stop was asked for. Skipping
        /// it loses the end of the message, which is where people say goodbye.
        /// </summary>
        public async Task<short[]> StopAsync()
        {
            Running = false;

            if (_stop != null)
            {
                _stop.Cancel();
                _stop = null;
            }

            try
            {
                if (_capture != null) await _capture.StopRecordAsync();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            await DrainAsync();

            lock (_gate) return _samples.ToArray();
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(100, token);
                    await DrainAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        private async Task DrainAsync()
        {
            if (_stream == null) return;

            ulong size = _stream.Size;

            if (!_skippedHeader)
            {
                if (size < HeaderSearchBytes) return;

                int start = await FindAudioAsync();
                if (start <= 0) return;

                _read = (ulong)start;
                _skippedHeader = true;
            }

            while (size > _read)
            {
                uint available = (uint)Math.Min(size - _read, 64 * 1024);
                var buffer = new Windows.Storage.Streams.Buffer(available);

                using (IInputStream input = _stream.GetInputStreamAt(_read))
                {
                    IBuffer filled = await input.ReadAsync(
                        buffer, available, InputStreamOptions.None);

                    if (filled.Length == 0) return;

                    _read += filled.Length;
                    Append(filled);
                }
            }
        }

        private void Append(IBuffer data)
        {
            var bytes = new byte[data.Length];
            DataReader.FromBuffer(data).ReadBytes(bytes);

            lock (_gate)
            {
                int at = 0;

                // A read can end halfway through a sample; losing that byte swaps
                // the halves of every sample after it.
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
        }

        private void Add(short sample)
        {
            if (_samples.Count >= SampleRate * MaxSeconds) return;

            _samples.Add(sample);

            int level = sample < 0 ? -sample : sample;
            if (level > _peak) _peak = level;
        }

        /// <summary>Walks the RIFF chunks to find where the audio starts.</summary>
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

                    if (chunk == "data") return at + 8;
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

        /// <summary>What the encoder needs to know about what was captured.</summary>
        public static int Rate { get { return SampleRate; } }

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
