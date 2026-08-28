using System;
using System.Collections.Generic;

namespace Lumigram.Qr
{
    /// <summary>
    /// A QR Code encoder - byte mode, error correction level M, versions 1 to 15.
    ///
    /// Written here because neither WP8.1 nor the desktop framework ships one, and
    /// QR login needs to *display* a code rather than read one. Only encoding is
    /// implemented; nothing in this app ever scans.
    ///
    /// Level M (about 15% recoverable) is a deliberate middle: enough tolerance for
    /// a phone camera pointed at a screen, without inflating the version and making
    /// the modules smaller on a 480x800 display.
    ///
    /// Versions above 15 are not supported. A Telegram login URL is around 100
    /// characters, which fits comfortably in version 6, so the extra tables would be
    /// dead weight on a phone.
    /// </summary>
    public static class QrCode
    {
        /// <summary>
        /// Per version (index 1..15) at EC level M:
        /// { ec codewords per block, group1 blocks, group1 data codewords,
        ///   group2 blocks, group2 data codewords }
        /// </summary>
        private static readonly int[][] EcTableM =
        {
            null,                               // version 0 does not exist
            new[] { 10, 1, 16, 0, 0 },
            new[] { 16, 1, 28, 0, 0 },
            new[] { 26, 1, 44, 0, 0 },
            new[] { 18, 2, 32, 0, 0 },
            new[] { 24, 2, 43, 0, 0 },
            new[] { 16, 4, 27, 0, 0 },
            new[] { 18, 4, 31, 0, 0 },
            new[] { 22, 2, 38, 2, 39 },
            new[] { 22, 3, 36, 2, 37 },
            new[] { 26, 4, 43, 1, 44 },
            new[] { 30, 1, 50, 4, 51 },
            new[] { 22, 6, 36, 2, 37 },
            new[] { 22, 8, 37, 1, 38 },
            new[] { 24, 4, 40, 5, 41 },
            new[] { 24, 5, 41, 5, 42 },
        };

        private static readonly int[][] AlignmentPositions =
        {
            null,
            new int[0],
            new[] { 6, 18 },
            new[] { 6, 22 },
            new[] { 6, 26 },
            new[] { 6, 30 },
            new[] { 6, 34 },
            new[] { 6, 22, 38 },
            new[] { 6, 24, 42 },
            new[] { 6, 26, 46 },
            new[] { 6, 28, 50 },
            new[] { 6, 30, 54 },
            new[] { 6, 32, 58 },
            new[] { 6, 34, 62 },
            new[] { 6, 26, 46, 66 },
            new[] { 6, 26, 48, 70 },
        };

        /// <summary>
        /// Encodes text as a QR matrix. true means a dark module.
        ///
        /// Indexing is [row, column] with the origin at the top left, which is the
        /// orientation a renderer wants.
        /// </summary>
        public static bool[,] Encode(string text)
        {
            return Encode(text, -1);
        }

        /// <summary>
        /// Encodes with a forced mask. Used only by tests, to compare against a
        /// reference encoder one mask at a time - otherwise a difference in mask
        /// *selection* is indistinguishable from a difference in encoding.
        /// </summary>
        public static bool[,] Encode(string text, int forcedMask)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
            int version = ChooseVersion(data.Length);

            byte[] codewords = BuildCodewords(data, version);
            byte[] finalMessage = InterleaveWithEcc(codewords, version);

            int size = 17 + version * 4;
            var modules = new bool[size, size];
            var reserved = new bool[size, size];

            DrawFunctionPatterns(modules, reserved, version, size);
            DrawData(modules, reserved, finalMessage, size);

            int bestMask = forcedMask >= 0 ? forcedMask
                                           : ChooseMask(modules, reserved, version, size);
            ApplyMask(modules, reserved, bestMask, size);
            DrawFormatInfo(modules, bestMask, size);
            if (version >= 7) DrawVersionInfo(modules, version, size);

            return modules;
        }

        /// <summary>Test hook: the final interleaved message, before placement.</summary>
        public static byte[] DebugMessage(string text, out int version)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
            version = ChooseVersion(data.Length);
            return InterleaveWithEcc(BuildCodewords(data, version), version);
        }

        /// <summary>Test hook: data codewords only, before error correction.</summary>
        public static byte[] DebugCodewords(string text)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
            return BuildCodewords(data, ChooseVersion(data.Length));
        }

        private static int ChooseVersion(int byteCount)
        {
            for (int v = 1; v < EcTableM.Length; v++)
            {
                int capacity = DataCodewords(v) - (v < 10 ? 2 : 3);   // mode + length header
                if (byteCount <= capacity) return v;
            }
            throw new ArgumentException(
                "text is too long for this encoder (max version 15 at EC level M)");
        }

        private static int DataCodewords(int version)
        {
            int[] t = EcTableM[version];
            return t[1] * t[2] + t[3] * t[4];
        }

        private static byte[] BuildCodewords(byte[] data, int version)
        {
            int total = DataCodewords(version);
            var bits = new BitBuffer();

            bits.Append(0x4, 4);                                  // byte mode
            bits.Append(data.Length, version < 10 ? 8 : 16);
            foreach (byte b in data) bits.Append(b, 8);

            // Terminator, then pad to a whole codeword.
            int capacityBits = total * 8;
            int terminator = Math.Min(4, capacityBits - bits.Length);
            bits.Append(0, terminator);
            while (bits.Length % 8 != 0) bits.Append(0, 1);

            byte[] result = bits.ToBytes();
            var padded = new byte[total];
            Array.Copy(result, padded, Math.Min(result.Length, total));

            // The spec's alternating pad bytes.
            for (int i = result.Length; i < total; i++)
                padded[i] = ((i - result.Length) % 2 == 0) ? (byte)0xEC : (byte)0x11;

            return padded;
        }

        /// <summary>
        /// Splits into blocks, appends Reed-Solomon codewords, then interleaves.
        ///
        /// Interleaving is what makes the error correction resist a *localised*
        /// smudge: consecutive codewords end up far apart in the final matrix.
        /// </summary>
        private static byte[] InterleaveWithEcc(byte[] data, int version)
        {
            int[] t = EcTableM[version];
            int ecPerBlock = t[0], g1 = t[1], g1Len = t[2], g2 = t[3], g2Len = t[4];

            var blocks = new List<byte[]>();
            var eccs = new List<byte[]>();

            int offset = 0;
            for (int i = 0; i < g1 + g2; i++)
            {
                int len = i < g1 ? g1Len : g2Len;
                var block = new byte[len];
                Array.Copy(data, offset, block, 0, len);
                offset += len;

                blocks.Add(block);
                eccs.Add(ReedSolomon.Encode(block, ecPerBlock));
            }

            var output = new List<byte>();

            int maxData = Math.Max(g1Len, g2Len);
            for (int i = 0; i < maxData; i++)
                foreach (byte[] block in blocks)
                    if (i < block.Length) output.Add(block[i]);

            for (int i = 0; i < ecPerBlock; i++)
                foreach (byte[] ecc in eccs)
                    output.Add(ecc[i]);

            return output.ToArray();
        }

        private static void DrawFunctionPatterns(bool[,] m, bool[,] reserved, int version, int size)
        {
            DrawFinder(m, reserved, 0, 0, size);
            DrawFinder(m, reserved, size - 7, 0, size);
            DrawFinder(m, reserved, 0, size - 7, size);

            // Timing patterns
            for (int i = 8; i < size - 8; i++)
            {
                bool dark = i % 2 == 0;
                m[6, i] = dark; reserved[6, i] = true;
                m[i, 6] = dark; reserved[i, 6] = true;
            }

            // Alignment patterns, skipping the three finder corners.
            int[] positions = AlignmentPositions[version];
            foreach (int r in positions)
            {
                foreach (int c in positions)
                {
                    if ((r == 6 && c == 6) ||
                        (r == 6 && c == size - 7) ||
                        (r == size - 7 && c == 6)) continue;
                    DrawAlignment(m, reserved, r, c);
                }
            }

            // The single always-dark module.
            m[size - 8, 8] = true;
            reserved[size - 8, 8] = true;

            // Reserve the format information areas.
            for (int i = 0; i < 9; i++)
            {
                if (!reserved[8, i]) { reserved[8, i] = true; }
                if (!reserved[i, 8]) { reserved[i, 8] = true; }
            }
            for (int i = 0; i < 8; i++)
            {
                reserved[8, size - 1 - i] = true;
                reserved[size - 1 - i, 8] = true;
            }

            if (version >= 7)
            {
                for (int i = 0; i < 6; i++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        reserved[i, size - 11 + j] = true;
                        reserved[size - 11 + j, i] = true;
                    }
                }
            }
        }

        private static void DrawFinder(bool[,] m, bool[,] reserved, int row, int col, int size)
        {
            // 7x7 pattern plus a one-module separator, clipped at the edges.
            for (int r = -1; r <= 7; r++)
            {
                for (int c = -1; c <= 7; c++)
                {
                    int rr = row + r, cc = col + c;
                    if (rr < 0 || rr >= size || cc < 0 || cc >= size) continue;

                    bool dark = (r >= 0 && r <= 6 && (c == 0 || c == 6)) ||
                                (c >= 0 && c <= 6 && (r == 0 || r == 6)) ||
                                (r >= 2 && r <= 4 && c >= 2 && c <= 4);

                    m[rr, cc] = dark;
                    reserved[rr, cc] = true;
                }
            }
        }

        private static void DrawAlignment(bool[,] m, bool[,] reserved, int row, int col)
        {
            for (int r = -2; r <= 2; r++)
            {
                for (int c = -2; c <= 2; c++)
                {
                    bool dark = Math.Max(Math.Abs(r), Math.Abs(c)) != 1;
                    m[row + r, col + c] = dark;
                    reserved[row + r, col + c] = true;
                }
            }
        }

        /// <summary>Places the message in the zigzag order the spec defines.</summary>
        private static void DrawData(bool[,] m, bool[,] reserved, byte[] message, int size)
        {
            int bitIndex = 0;
            bool upward = true;

            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;              // the vertical timing column is skipped

                for (int i = 0; i < size; i++)
                {
                    int row = upward ? size - 1 - i : i;

                    for (int c = 0; c < 2; c++)
                    {
                        int col = right - c;
                        if (reserved[row, col]) continue;

                        bool bit = false;
                        if (bitIndex < message.Length * 8)
                        {
                            byte b = message[bitIndex / 8];
                            bit = ((b >> (7 - (bitIndex % 8))) & 1) != 0;
                        }
                        m[row, col] = bit;
                        bitIndex++;
                    }
                }
                upward = !upward;
            }
        }

        private static bool MaskAt(int mask, int row, int col)
        {
            switch (mask)
            {
                case 0: return (row + col) % 2 == 0;
                case 1: return row % 2 == 0;
                case 2: return col % 3 == 0;
                case 3: return (row + col) % 3 == 0;
                case 4: return ((row / 2) + (col / 3)) % 2 == 0;
                case 5: return (row * col) % 2 + (row * col) % 3 == 0;
                case 6: return ((row * col) % 2 + (row * col) % 3) % 2 == 0;
                case 7: return ((row + col) % 2 + (row * col) % 3) % 2 == 0;
                default: return false;
            }
        }

        private static void ApplyMask(bool[,] m, bool[,] reserved, int mask, int size)
        {
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (!reserved[r, c] && MaskAt(mask, r, c))
                        m[r, c] = !m[r, c];
        }

        /// <summary>
        /// Tries all eight masks and keeps the one with the lowest penalty, which is
        /// how the spec avoids patterns a scanner might confuse with finder marks.
        /// </summary>
        private static int ChooseMask(bool[,] m, bool[,] reserved, int version, int size)
        {
            int best = 0, bestScore = int.MaxValue;

            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(m, reserved, mask, size);
                DrawFormatInfo(m, mask, size);

                int score = Penalty(m, size);
                if (score < bestScore) { bestScore = score; best = mask; }

                ApplyMask(m, reserved, mask, size);     // masking is its own inverse
            }
            return best;
        }

        private static int Penalty(bool[,] m, int size)
        {
            int penalty = 0;

            // Rule 1: runs of five or more identical modules.
            for (int r = 0; r < size; r++)
            {
                penalty += RunPenalty(m, size, r, true);
                penalty += RunPenalty(m, size, r, false);
            }

            // Rule 2: 2x2 blocks of one colour.
            for (int r = 0; r < size - 1; r++)
                for (int c = 0; c < size - 1; c++)
                    if (m[r, c] == m[r, c + 1] && m[r, c] == m[r + 1, c] && m[r, c] == m[r + 1, c + 1])
                        penalty += 3;

            // Rule 3: finder-like 1:1:3:1:1 sequences.
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size - 6; c++)
                {
                    if (MatchesFinderRun(m, r, c, true, size)) penalty += 40;
                    if (MatchesFinderRun(m, c, r, false, size)) penalty += 40;
                }

            // Rule 4: overall dark/light imbalance.
            int dark = 0;
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (m[r, c]) dark++;

            int percent = dark * 100 / (size * size);
            penalty += Math.Abs(percent - 50) / 5 * 10;

            return penalty;
        }

        private static int RunPenalty(bool[,] m, int size, int line, bool horizontal)
        {
            int penalty = 0, run = 1;
            bool previous = horizontal ? m[line, 0] : m[0, line];

            for (int i = 1; i < size; i++)
            {
                bool current = horizontal ? m[line, i] : m[i, line];
                if (current == previous) run++;
                else
                {
                    if (run >= 5) penalty += 3 + (run - 5);
                    run = 1;
                    previous = current;
                }
            }
            if (run >= 5) penalty += 3 + (run - 5);
            return penalty;
        }

        private static readonly bool[] FinderRun =
            { true, false, true, true, true, false, true };

        private static bool MatchesFinderRun(bool[,] m, int a, int b, bool horizontal, int size)
        {
            for (int i = 0; i < 7; i++)
            {
                int r = horizontal ? a : b + i;
                int c = horizontal ? b + i : a;
                if (r >= size || c >= size) return false;
                if (m[r, c] != FinderRun[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Format info: two bits of EC level plus three of mask, protected by a
        /// BCH(15,5) code and XORed with a fixed mask. Computed rather than tabulated
        /// so there is no long constant to mistype.
        /// </summary>
        private static void DrawFormatInfo(bool[,] m, int mask, int size)
        {
            const int EcLevelM = 0x00;                  // level M is 00
            int data = (EcLevelM << 3) | mask;

            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);

            int bits = ((data << 10) | rem) ^ 0x5412;

            // Bit 14 (most significant) goes first, at (8,0). Placing these
            // least-significant-first produces a symbol that looks perfectly well
            // formed and cannot be scanned - verified against a reference encoder
            // rather than reasoned about, because there is nothing to see.
            for (int i = 0; i < 15; i++)
            {
                bool bit = ((bits >> (14 - i)) & 1) != 0;

                // First copy: along row 8, then up column 8, skipping the timing
                // module at (8,6) and the one at (6,8).
                if (i < 6) m[8, i] = bit;
                else if (i == 6) m[8, 7] = bit;
                else if (i == 7) m[8, 8] = bit;
                else if (i == 8) m[7, 8] = bit;
                else m[14 - i, 8] = bit;

                // Second copy: up column 8 from the bottom, then along row 8 at the
                // right edge.
                if (i < 7) m[size - 1 - i, 8] = bit;
                else m[8, size - 15 + i] = bit;
            }
        }

        /// <summary>Version information, present from version 7: BCH(18,6).</summary>
        private static void DrawVersionInfo(bool[,] m, int version, int size)
        {
            int rem = version;
            for (int i = 0; i < 12; i++)
                rem = (rem << 1) ^ (((rem >> 11) & 1) * 0x1F25);

            int bits = (version << 12) | rem;

            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int r = i / 3, c = size - 11 + (i % 3);
                m[r, c] = bit;
                m[c, r] = bit;
            }
        }

        private sealed class BitBuffer
        {
            private readonly List<byte> _bytes = new List<byte>();
            private int _bitLength;

            public int Length { get { return _bitLength; } }

            public void Append(int value, int bits)
            {
                for (int i = bits - 1; i >= 0; i--)
                {
                    if (_bitLength % 8 == 0) _bytes.Add(0);
                    if (((value >> i) & 1) != 0)
                        _bytes[_bitLength / 8] |= (byte)(1 << (7 - (_bitLength % 8)));
                    _bitLength++;
                }
            }

            public byte[] ToBytes() { return _bytes.ToArray(); }
        }
    }

    /// <summary>Reed-Solomon over GF(256) with the QR primitive polynomial 0x11D.</summary>
    internal static class ReedSolomon
    {
        private static readonly byte[] Exp = new byte[512];
        private static readonly byte[] Log = new byte[256];

        static ReedSolomon()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                Exp[i] = (byte)x;
                Log[x] = (byte)i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
            for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        }

        private static byte Multiply(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return Exp[Log[a] + Log[b]];
        }

        public static byte[] Encode(byte[] data, int ecCount)
        {
            byte[] generator = BuildGenerator(ecCount);
            var remainder = new byte[ecCount];

            foreach (byte b in data)
            {
                byte factor = (byte)(b ^ remainder[0]);
                Array.Copy(remainder, 1, remainder, 0, ecCount - 1);
                remainder[ecCount - 1] = 0;

                for (int i = 0; i < ecCount; i++)
                    remainder[i] ^= Multiply(generator[i], factor);
            }
            return remainder;
        }

        private static byte[] BuildGenerator(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;

            byte root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = Multiply(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = Multiply(root, 2);
            }
            return result;
        }
    }
}
