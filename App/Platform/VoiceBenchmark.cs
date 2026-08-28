using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Concentus.Enums;
using Concentus.Structs;

namespace LumigramPlus.App
{
    /// <summary>
    /// Whether this phone can run the Opus codec in managed code fast enough for a
    /// live call.
    ///
    /// This exists to settle one question before any voice work is designed around
    /// the answer. Concentus is a C# port of Opus; it is fast enough to encode a
    /// voice message, where nothing is waiting on it, but a call is a different
    /// contract. Every 20 milliseconds the phone must encode one frame of your
    /// speech and decode one of theirs, and still have time left for the network,
    /// the encryption and the jitter buffer.
    ///
    /// If it does not fit, the codec has to be native - which decides the shape of
    /// everything else, so it is worth an hour to know now rather than a fortnight
    /// to discover.
    ///
    /// The numbers are a floor, not a verdict: this measures one core with nothing
    /// else happening, and a real call also has a radio, a screen and a UI thread.
    /// </summary>
    internal static class VoiceBenchmark
    {
        /// <summary>What a call has per frame, in milliseconds.</summary>
        private const double Budget = 20.0;

        private const int Warmup = 25;
        private const int Frames = 250;

        public sealed class Result
        {
            public int SampleRate;
            public int Complexity;
            public double EncodeMs;
            public double DecodeMs;
            public int AverageBytes;

            /// <summary>Share of the 20 ms frame that encode and decode together take.</summary>
            public double Load { get { return (EncodeMs + DecodeMs) / Budget; } }

            public override string ToString()
            {
                return string.Format(
                    "{0} kHz c{1}: enc {2:0.00} + dec {3:0.00} = {4:0.0}% of frame, {5} B",
                    SampleRate / 1000, Complexity, EncodeMs, DecodeMs, Load * 100,
                    AverageBytes);
            }
        }

        /// <summary>
        /// Runs the settings a call might plausibly use, cheapest last.
        ///
        /// More than one, because the answer is rarely yes or no. Wideband at
        /// complexity 5 is what a call would like; 16 kHz at complexity 0 is what it
        /// could fall back to, and knowing the gap between them is the useful part.
        /// </summary>
        public static async Task<Result[]> RunAsync()
        {
            return await Task.Run(() => new Result[]
            {
                Measure(48000, 5),
                Measure(48000, 0),
                Measure(16000, 5),
                Measure(16000, 0),
            });
        }

        private static Result Measure(int sampleRate, int complexity)
        {
            int frameSamples = sampleRate / 1000 * 20;

            var encoder = new OpusEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 20000;
            encoder.Complexity = complexity;

            var decoder = new OpusDecoder(sampleRate, 1);

            short[][] input = Speech(sampleRate, frameSamples, Warmup + Frames);

            var packet = new byte[1275];
            var pcm = new short[frameSamples];

            // Discarded. The first frames pay for JIT and for the encoder settling,
            // and counting them would blame the codec for a cost paid once.
            for (int i = 0; i < Warmup; i++)
            {
                int n = encoder.Encode(input[i], 0, frameSamples, packet, 0, packet.Length);
                decoder.Decode(packet, 0, n, pcm, 0, frameSamples, false);
            }

            var lengths = new int[Frames];
            var encoded = new byte[Frames][];

            var watch = Stopwatch.StartNew();

            for (int i = 0; i < Frames; i++)
            {
                lengths[i] = encoder.Encode(input[Warmup + i], 0, frameSamples,
                                            packet, 0, packet.Length);

                encoded[i] = new byte[lengths[i]];
                Buffer.BlockCopy(packet, 0, encoded[i], 0, lengths[i]);
            }

            watch.Stop();
            double encodeMs = watch.Elapsed.TotalMilliseconds / Frames;

            watch.Restart();

            for (int i = 0; i < Frames; i++)
                decoder.Decode(encoded[i], 0, lengths[i], pcm, 0, frameSamples, false);

            watch.Stop();

            long total = 0;
            foreach (int length in lengths) total += length;

            return new Result
            {
                SampleRate = sampleRate,
                Complexity = complexity,
                EncodeMs = encodeMs,
                DecodeMs = watch.Elapsed.TotalMilliseconds / Frames,
                AverageBytes = (int)(total / Frames),
            };
        }

        /// <summary>
        /// Something speech-like to encode.
        ///
        /// Silence and pure tones compress to almost nothing and encode far faster
        /// than real speech, which would make the whole measurement flattering. This
        /// is a moving fundamental with harmonics, amplitude modulated at syllable
        /// rate, over a noise floor - not speech, but the same order of difficulty.
        ///
        /// Deterministic, so two runs on the same phone are comparable.
        /// </summary>
        private static short[][] Speech(int sampleRate, int frameSamples, int frames)
        {
            var random = new Random(20260827);
            var output = new short[frames][];

            double phase = 0;
            long sample = 0;

            for (int f = 0; f < frames; f++)
            {
                var frame = new short[frameSamples];

                for (int i = 0; i < frameSamples; i++, sample++)
                {
                    double seconds = sample / (double)sampleRate;

                    // A pitch that wanders, the way a voice does.
                    double fundamental = 120 + 40 * Math.Sin(seconds * 2.1);
                    phase += 2 * Math.PI * fundamental / sampleRate;

                    double value = Math.Sin(phase)
                                 + 0.5 * Math.Sin(2 * phase)
                                 + 0.3 * Math.Sin(3 * phase)
                                 + 0.2 * Math.Sin(5 * phase);

                    // Syllables, roughly four a second, never quite to silence.
                    double envelope = 0.35 + 0.65 * Math.Abs(Math.Sin(seconds * Math.PI * 4));

                    value = value * envelope * 0.22 + (random.NextDouble() - 0.5) * 0.03;

                    if (value > 1) value = 1;
                    if (value < -1) value = -1;

                    frame[i] = (short)(value * short.MaxValue);
                }

                output[f] = frame;
            }

            return output;
        }
    }
}
