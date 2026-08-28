using System;
using System.Diagnostics;
using System.IO;
using Concentus.Structs;
using Lumigram.Audio;

namespace Lumigram.Harness
{
    /// <summary>
    /// Checks the OGG demuxer against a real Opus file.
    ///
    /// The container parsing is ours and the codec is not, which is exactly the
    /// split that needs a test: a demuxer that hands the decoder subtly wrong packet
    /// boundaries produces noise, not an error, and noise is hard to notice in a
    /// voice message from someone you cannot understand anyway.
    ///
    /// Deliberately run against a file produced by the reference encoder rather than
    /// against anything this project writes. Comparing our own output with our own
    /// input is the mistake that cost days on the QR encoder - see Tools/verify-qr.py.
    /// </summary>
    internal static class VoiceCommand
    {
        private const int SampleRate = 48000;
        private const int MaxFrameSamples = 5760;

        /// <summary>
        /// Encodes a synthetic recording and writes it out, so the muxer can be
        /// checked by something other than our own reader.
        ///
        /// The signal is deliberately speech-shaped - a swept tone that goes quiet
        /// in gaps - because a constant tone hides exactly the bugs that matter: a
        /// waveform that is all one height, and frame boundaries that never vary in
        /// size.
        /// </summary>
        public static int Make(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: Lumigram.Harness voicemake <out.opus> [seconds]");
                return 2;
            }

            string path = args[1];
            double seconds = 3.0;
            if (args.Length > 2) double.TryParse(args[2], out seconds);

            const int rate = 16000;
            int count = (int)(rate * seconds);
            var pcm = new short[count];

            for (int i = 0; i < count; i++)
            {
                double t = i / (double)rate;

                // Syllables: roughly three a second, with silence between them.
                double envelope = Math.Max(0.0, Math.Sin(t * Math.PI * 3.0));
                envelope *= envelope;

                double tone = Math.Sin(2 * Math.PI * (180 + 120 * Math.Sin(t * 2.0)) * t);
                double buzz = Math.Sin(2 * Math.PI * 1400 * t) * 0.15;

                pcm[i] = (short)(12000 * envelope * (tone + buzz));
            }

            var sw = Stopwatch.StartNew();
            EncodedVoice voice = VoiceEncoder.Encode(pcm, count, rate);
            sw.Stop();

            File.WriteAllBytes(path, voice.File);

            Console.WriteLine("{0}", path);
            Console.WriteLine("  encoded in : {0} ms  ({1:F1}x real time)",
                              sw.ElapsedMilliseconds,
                              seconds / Math.Max(0.001, sw.ElapsedMilliseconds / 1000.0));
            Console.WriteLine("  file       : {0:N0} bytes", voice.File.Length);
            Console.WriteLine("  duration   : {0} s", voice.DurationSeconds);
            Console.WriteLine("  waveform   : {0} bytes", voice.Waveform.Length);

            // A waveform that is flat means the peak search is wrong, and it is the
            // sort of thing nobody notices until the bars look wrong on a phone.
            int low = 31, high = 0;
            for (int bar = 0; bar < 100; bar++)
            {
                int bit = bar * 5;
                int value = voice.Waveform[bit / 8] >> (bit % 8);
                int spare = 8 - bit % 8;
                if (spare < 5) value |= voice.Waveform[bit / 8 + 1] << spare;
                value &= 31;
                if (value < low) low = value;
                if (value > high) high = value;
            }
            Console.WriteLine("  bars       : {0}..{1}", low, high);

            if (high != 31) { Console.WriteLine("FAILED: loudest bar should be 31"); return 1; }
            if (low == high) { Console.WriteLine("FAILED: waveform is flat"); return 1; }

            Console.WriteLine();
            Console.WriteLine("PASS - now check it with Tools/verify-ogg.py and 'voice'");
            return 0;
        }

        public static int Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: Lumigram.Harness voice <file.opus> [expected-seconds]");
                return 2;
            }

            string path = args[1];
            if (!File.Exists(path))
            {
                Console.WriteLine("not found: {0}", path);
                return 1;
            }

            byte[] file = File.ReadAllBytes(path);
            Console.WriteLine("{0}  {1:N0} bytes", Path.GetFileName(path), file.Length);

            var sw = Stopwatch.StartNew();
            OpusStream stream = OggOpus.Read(file);
            sw.Stop();

            Console.WriteLine();
            Console.WriteLine("demuxed in {0} ms", sw.ElapsedMilliseconds);
            Console.WriteLine("  channels  : {0}", stream.Channels);
            Console.WriteLine("  pre-skip  : {0} samples", stream.PreSkip);
            Console.WriteLine("  packets   : {0}", stream.Packets.Count);

            if (stream.Packets.Count == 0)
            {
                Console.WriteLine("FAILED: no audio packets");
                return 1;
            }

            var decoder = new OpusDecoder(SampleRate, stream.Channels);
            var pcm = new short[MaxFrameSamples * stream.Channels];

            long samples = 0;
            int failed = 0;

            sw = Stopwatch.StartNew();
            foreach (byte[] packet in stream.Packets)
            {
                try
                {
                    int got = decoder.Decode(packet, 0, packet.Length, pcm, 0,
                                             MaxFrameSamples, false);
                    if (got > 0) samples += got; else failed++;
                }
                catch (Exception)
                {
                    failed++;
                }
            }
            sw.Stop();

            double seconds = (samples - stream.PreSkip) / (double)SampleRate;

            Console.WriteLine();
            Console.WriteLine("decoded in {0} ms", sw.ElapsedMilliseconds);
            Console.WriteLine("  samples   : {0:N0}", samples);
            Console.WriteLine("  duration  : {0:F2} s", seconds);
            Console.WriteLine("  refused   : {0} packet(s)", failed);
            Console.WriteLine("  speed     : {0:F1}x real time",
                              seconds / Math.Max(0.001, sw.ElapsedMilliseconds / 1000.0));

            bool ok = failed == 0;

            if (args.Length > 2)
            {
                double expected;
                if (double.TryParse(args[2], out expected))
                {
                    // A demuxer that drops or merges packets still decodes cleanly;
                    // the length is what gives it away.
                    double drift = Math.Abs(seconds - expected);
                    Console.WriteLine("  expected  : {0:F2} s  (out by {1:F2} s)", expected, drift);
                    if (drift > 0.5) ok = false;
                }
            }

            Console.WriteLine();
            Console.WriteLine(ok ? "PASS" : "FAILED");
            return ok ? 0 : 1;
        }
    }
}
