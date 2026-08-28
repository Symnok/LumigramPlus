using System;
using System.Collections.Generic;
using Concentus.Enums;
using Concentus.Structs;

namespace Lumigram.Audio
{
    /// <summary>A recording, encoded and ready to send.</summary>
    public sealed class EncodedVoice
    {
        /// <summary>The complete .ogg file.</summary>
        public byte[] File;

        public int DurationSeconds;

        /// <summary>The bar chart other clients draw behind the message.</summary>
        public byte[] Waveform;
    }

    /// <summary>
    /// Turns recorded PCM into a Telegram voice message.
    ///
    /// Encoding is the direction with no room to be forgiving: the result has to be
    /// accepted by every other Telegram client, and the only feedback available is
    /// that it either plays elsewhere or does not. The settings below are chosen to
    /// match what other clients send rather than to be interesting - 16 kHz mono in
    /// Opus's voice mode, which is what speech at this bitrate wants.
    ///
    /// Lives outside Core so the protocol library keeps its one distinguishing
    /// property: no dependencies at all. The desktop harness links this file
    /// directly, which is how the output gets checked against a second, independent
    /// implementation before any of it reaches a phone.
    /// </summary>
    public static class VoiceEncoder
    {
        /// <summary>
        /// Granule positions and pre-skip are always counted at 48 kHz, whatever
        /// rate the audio was actually encoded at.
        /// </summary>
        private const int GranuleRate = 48000;

        /// <summary>20 ms frames: what Opus is tuned for, and what every client uses.</summary>
        private const int FrameMilliseconds = 20;

        /// <summary>Plenty for one frame of speech; the encoder writes far less.</summary>
        private const int MaxPacketBytes = 1275;

        /// <summary>Bars in the waveform. Telegram's own clients send 100.</summary>
        private const int WaveformBars = 100;

        /// <summary>Each bar is five bits, so the largest value is 31.</summary>
        private const int WaveformMax = 31;

        public static EncodedVoice Encode(short[] pcm, int sampleCount,
                                          int sampleRate, int bitrate = 20000)
        {
            if (pcm == null) throw new ArgumentNullException("pcm");

            int frameSamples = sampleRate / 1000 * FrameMilliseconds;
            if (frameSamples <= 0) throw new ArgumentException("unusable sample rate");

            var encoder = new OpusEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = bitrate;

            // The phone doing the encoding is the slowest machine in the chain, and
            // speech at this bitrate gains little from the exhaustive search.
            encoder.Complexity = 5;

            var writer = new OggWriter(unchecked((int)0x4C554D49));   // "LUMI"

            // Opus needs a little audio before its output settles. The convention is
            // to declare the encoder's own delay and let the decoder discard it.
            int preSkip = 312 * GranuleRate / 48000;
            writer.WriteHeaders(1, preSkip, sampleRate);

            var packet = new byte[MaxPacketBytes];
            var page = new List<byte[]>();
            long granule = preSkip;
            int at = 0;

            while (at < sampleCount)
            {
                // The final frame is padded rather than dropped: Opus only encodes
                // whole frames, and truncating loses the end of the message.
                var frame = new short[frameSamples];
                int available = Math.Min(frameSamples, sampleCount - at);
                Buffer.BlockCopy(pcm, at * sizeof(short), frame, 0, available * sizeof(short));
                at += frameSamples;

                int length;
                try
                {
                    length = encoder.Encode(frame, 0, frameSamples, packet, 0, MaxPacketBytes);
                }
                catch (Exception)
                {
                    continue;
                }

                if (length <= 0) continue;

                var copy = new byte[length];
                Buffer.BlockCopy(packet, 0, copy, 0, length);
                page.Add(copy);

                granule += frameSamples * (long)GranuleRate / sampleRate;

                // A page holds at most 255 segments, and one packet can need several.
                // Fifty small frames is a comfortable page and keeps the count well
                // under the limit.
                if (page.Count >= 50)
                {
                    writer.WriteAudio(page, granule, false);
                    page = new List<byte[]>();
                }
            }

            // The last page must carry the end-of-stream flag, so it is written even
            // when it would otherwise be empty.
            writer.WriteAudio(page, granule, true);

            return new EncodedVoice
            {
                File = writer.ToArray(),
                DurationSeconds = (int)Math.Round(sampleCount / (double)sampleRate),
                Waveform = Waveform(pcm, sampleCount),
            };
        }

        /// <summary>
        /// The waveform Telegram draws behind a voice message.
        ///
        /// One hundred bars, five bits each, packed low bits first and running
        /// across byte boundaries. Peak rather than average per bar: speech is
        /// mostly quiet, and averaging produces a flat line that tells the reader
        /// nothing about where the words are.
        /// </summary>
        public static byte[] Waveform(short[] pcm, int sampleCount)
        {
            var bars = new int[WaveformBars];
            if (sampleCount > 0)
            {
                int peak = 1;
                for (int bar = 0; bar < WaveformBars; bar++)
                {
                    int from = (int)((long)bar * sampleCount / WaveformBars);
                    int to = (int)((long)(bar + 1) * sampleCount / WaveformBars);

                    int loudest = 0;
                    for (int i = from; i < to && i < sampleCount; i++)
                    {
                        int level = pcm[i] < 0 ? -pcm[i] : pcm[i];
                        if (level > loudest) loudest = level;
                    }

                    bars[bar] = loudest;
                    if (loudest > peak) peak = loudest;
                }

                for (int bar = 0; bar < WaveformBars; bar++)
                    bars[bar] = bars[bar] * WaveformMax / peak;
            }

            var packed = new byte[(WaveformBars * 5 + 7) / 8];
            for (int bar = 0; bar < WaveformBars; bar++)
            {
                int bit = bar * 5;
                int value = bars[bar] & WaveformMax;

                packed[bit / 8] |= (byte)(value << (bit % 8));

                // A value straddling two bytes continues in the next one.
                int spare = 8 - bit % 8;
                if (spare < 5) packed[bit / 8 + 1] |= (byte)(value >> spare);
            }

            return packed;
        }
    }
}
