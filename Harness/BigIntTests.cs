using System;
using System.Diagnostics;
using System.Numerics;
using Lumigram.Crypto;

namespace Lumigram.Harness
{
    /// <summary>
    /// Differential tests: every BigInt operation is run against System.Numerics.BigInteger
    /// on the same random inputs and the results must agree byte for byte.
    ///
    /// This is worth more than hand-picked vectors. The parts of a big-integer
    /// implementation that break are the carry/borrow edges and the qhat correction
    /// in division - conditions that are hit by volume of random cases, not by the
    /// tidy examples a human thinks to write down.
    ///
    /// The seed is fixed so a failure is reproducible.
    /// </summary>
    internal static class BigIntTests
    {
        private static int _checks;
        private static int _failures;

        public static bool RunAll()
        {
            var rng = new Random(20260823);

            Section("round-trip");
            for (int i = 0; i < 500; i++)
            {
                var b = RandomBytes(rng, 1 + rng.Next(300));
                var mine = BigInt.FromBytesBE(b);
                Check("bytes", ToRef(b), mine);
            }

            Section("add / sub / mul");
            for (int i = 0; i < 2000; i++)
            {
                byte[] ab = RandomBytes(rng, 1 + rng.Next(140));
                byte[] bb = RandomBytes(rng, 1 + rng.Next(140));
                BigInteger ra = ToRef(ab), rb = ToRef(bb);
                BigInt ma = BigInt.FromBytesBE(ab), mb = BigInt.FromBytesBE(bb);

                Check("add", ra + rb, BigInt.Add(ma, mb));
                Check("mul", ra * rb, BigInt.Mul(ma, mb));

                // Sub is unsigned-only; order the operands so it is defined.
                if (ra < rb) { var t = ra; ra = rb; rb = t; var u = ma; ma = mb; mb = u; }
                Check("sub", ra - rb, BigInt.Sub(ma, mb));
            }

            Section("divmod / mod");
            for (int i = 0; i < 2000; i++)
            {
                byte[] ab = RandomBytes(rng, 1 + rng.Next(200));
                byte[] bb = RandomBytes(rng, 1 + rng.Next(80));
                BigInteger ra = ToRef(ab), rb = ToRef(bb);
                if (rb.IsZero) continue;

                BigInt q, r;
                BigInt.DivMod(BigInt.FromBytesBE(ab), BigInt.FromBytesBE(bb), out q, out r);
                Check("div", ra / rb, q);
                Check("rem", ra % rb, r);
            }

            Section("modpow");
            for (int i = 0; i < 60; i++)
            {
                byte[] bb = RandomBytes(rng, 1 + rng.Next(64));
                byte[] eb = RandomBytes(rng, 1 + rng.Next(8));
                byte[] mb = RandomBytes(rng, 1 + rng.Next(64));
                BigInteger rm = ToRef(mb);
                if (rm.IsZero) continue;

                Check("modpow", BigInteger.ModPow(ToRef(bb), ToRef(eb), rm),
                      BigInt.ModPow(BigInt.FromBytesBE(bb), BigInt.FromBytesBE(eb), BigInt.FromBytesBE(mb)));
            }

            Section("mtproto shapes");
            {
                // The RSA step: x^65537 mod n over a 255-byte block, 2048-bit modulus.
                byte[] xb = RandomBytes(rng, 255);
                byte[] nb = RandomBytes(rng, 256);
                nb[0] |= 0x80;                       // a real modulus has its top bit set
                var e = BigInt.FromUInt(65537);
                Check("rsa 65537", BigInteger.ModPow(ToRef(xb), 65537, ToRef(nb)),
                      BigInt.ModPow(BigInt.FromBytesBE(xb), e, BigInt.FromBytesBE(nb)));

                // Fixed-width output: DH values go on the wire as exactly 256 bytes.
                var small = BigInt.FromUInt(0x1234);
                var padded = small.ToBytesBE(256);
                if (padded.Length != 256 || padded[254] != 0x12 || padded[255] != 0x34)
                    Fail("pad", "256-byte left-pad wrong");
                else _checks++;
            }

            Section("timing (2048-bit DH exponentiation)");
            {
                byte[] pb = RandomBytes(rng, 256); pb[0] |= 0x80; pb[255] |= 1;
                byte[] gb = { 3 };
                byte[] ab = RandomBytes(rng, 256);
                var p = BigInt.FromBytesBE(pb);
                var g = BigInt.FromBytesBE(gb);
                var a = BigInt.FromBytesBE(ab);

                var sw = Stopwatch.StartNew();
                var r = BigInt.ModPow(g, a, p);
                sw.Stop();
                Check("dh matches", BigInteger.ModPow(ToRef(gb), ToRef(ab), ToRef(pb)), r);
                Console.WriteLine("    full 2048-bit ModPow: {0} ms on desktop", sw.ElapsedMilliseconds);
                Console.WriteLine("    (the handshake needs two of these; a 2013 ARM phone");
                Console.WriteLine("     will be roughly 10-20x slower - budget accordingly)");
            }

            Console.WriteLine();
            Console.WriteLine("{0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("  [{0}]", name);
        }

        private static byte[] RandomBytes(Random rng, int n)
        {
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        /// <summary>BigInteger from an unsigned big-endian string.</summary>
        private static BigInteger ToRef(byte[] be)
        {
            var le = new byte[be.Length + 1];         // trailing 0 keeps it positive
            for (int i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
            return new BigInteger(le);
        }

        private static byte[] RefToBE(BigInteger v)
        {
            if (v.IsZero) return new byte[] { 0 };
            var le = v.ToByteArray();
            int n = le.Length;
            while (n > 1 && le[n - 1] == 0) n--;      // drop the sign byte
            var be = new byte[n];
            for (int i = 0; i < n; i++) be[i] = le[n - 1 - i];
            return be;
        }

        private static void Check(string what, BigInteger expected, BigInt actual)
        {
            _checks++;
            var e = RefToBE(expected);
            var a = actual.ToBytesBE();
            if (e.Length == 1 && e[0] == 0 && actual.IsZero) return;
            if (!Same(e, a)) Fail(what, Hex(e) + " != " + Hex(a));
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Fail(string what, string detail)
        {
            _failures++;
            if (_failures <= 5) Console.WriteLine("    FAIL {0}: {1}", what, detail);
            else if (_failures == 6) Console.WriteLine("    ... further failures suppressed");
        }

        private static string Hex(byte[] b)
        {
            var sb = new System.Text.StringBuilder();
            int n = Math.Min(b.Length, 24);
            for (int i = 0; i < n; i++) sb.Append(b[i].ToString("x2"));
            if (b.Length > n) sb.Append("...");
            return sb.Length == 0 ? "(empty)" : sb.ToString();
        }
    }
}
