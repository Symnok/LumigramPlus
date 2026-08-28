using System;
using System.IO;
using System.Text;
using Lumigram.Qr;

namespace Lumigram.Harness
{
    /// <summary>
    /// Dumps QR matrices so they can be compared against a reference encoder.
    ///
    /// A QR code that is subtly wrong does not look wrong - it simply fails to
    /// scan, and there is nothing to inspect. So the matrix is compared module by
    /// module against segno (a mature Python implementation) rather than trusted
    /// because it renders nicely.
    /// </summary>
    internal static class QrTests
    {
        public static int Dump(string[] args)
        {
            string outPath = args.Length > 1 ? args[1] : "qr-dump.txt";

            string[] samples =
            {
                "A",
                "HELLO WORLD",
                "tg://login?token=AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA",
                "tg://login?token=" + new string('X', 60),
                new string('m', 150),
            };

            using (var w = new StreamWriter(outPath, false, Encoding.ASCII))
            {
                foreach (string s in samples)
                {
                  {
                    bool[,] m = QrCode.Encode(s);
                    int size = m.GetLength(0);

                    w.WriteLine("### 0 " + s.Length + " " + s);
                    w.WriteLine(size.ToString());
                    for (int r = 0; r < size; r++)
                    {
                        var sb = new StringBuilder(size);
                        for (int c = 0; c < size; c++) sb.Append(m[r, c] ? '1' : '0');
                        w.WriteLine(sb.ToString());
                    }
                  }
                }
            }

            foreach (string s2 in samples)
            {
                int version;
                byte[] msg = QrCode.DebugMessage(s2, out version);
                byte[] cw = QrCode.DebugCodewords(s2);
                Console.Write("v{0} data:", version);
                foreach (byte b in cw) Console.Write(" " + b.ToString("x2"));
                Console.WriteLine();
                Console.Write("v{0} full:", version);
                foreach (byte b in msg) Console.Write(" " + b.ToString("x2"));
                Console.WriteLine();
            }

            Console.WriteLine("wrote {0} matrices to {1}", samples.Length, outPath);
            return 0;
        }

        /// <summary>Prints a QR to the console, scannable from the screen.</summary>
        public static void Render(bool[,] m)
        {
            int size = m.GetLength(0);
            const int quiet = 2;

            // Two half-height blocks per character cell keeps the aspect roughly
            // square in a terminal, where cells are about twice as tall as wide.
            for (int r = -quiet; r < size + quiet; r += 2)
            {
                var sb = new StringBuilder();
                for (int c = -quiet; c < size + quiet; c++)
                {
                    bool top = Get(m, size, r, c);
                    bool bottom = Get(m, size, r + 1, c);

                    if (top && bottom) sb.Append((char)0x2588);
                    else if (top) sb.Append((char)0x2580);
                    else if (bottom) sb.Append((char)0x2584);
                    else sb.Append(' ');
                }
                Console.WriteLine(sb.ToString());
            }
        }

        /// <summary>
        /// Writes the QR as a BMP.
        ///
        /// Block characters in a terminal look right but survive neither output
        /// redirection nor most console fonts, and a 21x21 symbol is too small to
        /// scan from a screen anyway. A scaled image file is what a phone camera can
        /// actually read. BMP because it needs no encoder - just a header and rows.
        /// </summary>
        public static void SaveBmp(bool[,] m, string path, int scale, int quiet)
        {
            int size = m.GetLength(0);
            int pixels = (size + 2 * quiet) * scale;

            // BMP rows are padded to a 4-byte boundary, bottom-up.
            int rowBytes = pixels * 3;
            int padding = (4 - (rowBytes % 4)) % 4;
            int imageBytes = (rowBytes + padding) * pixels;

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((byte)'B'); w.Write((byte)'M');
                w.Write(54 + imageBytes);
                w.Write(0);
                w.Write(54);                       // pixel data offset

                w.Write(40);                       // DIB header size
                w.Write(pixels);
                w.Write(pixels);
                w.Write((short)1);                 // planes
                w.Write((short)24);                // bits per pixel
                w.Write(0);                        // no compression
                w.Write(imageBytes);
                w.Write(2835); w.Write(2835);      // ~72 dpi
                w.Write(0); w.Write(0);

                var pad = new byte[padding];
                for (int py = pixels - 1; py >= 0; py--)   // bottom-up
                {
                    for (int px = 0; px < pixels; px++)
                    {
                        int r = py / scale - quiet;
                        int c = px / scale - quiet;
                        bool dark = Get(m, size, r, c);
                        byte v = dark ? (byte)0 : (byte)255;
                        w.Write(v); w.Write(v); w.Write(v);
                    }
                    if (padding > 0) w.Write(pad);
                }
            }
        }

        private static bool Get(bool[,] m, int size, int r, int c)
        {
            if (r < 0 || c < 0 || r >= size || c >= size) return false;
            return m[r, c];
        }
    }
}
