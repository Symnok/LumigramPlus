using System;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// Factors the server's <c>pq</c> challenge into its two prime factors.
    ///
    /// pq is a semiprime below 2^63 whose factors are each around 2^31, so this is a
    /// proof-of-work step rather than a real cryptographic barrier - but it must
    /// still be fast enough not to stall login on a 2013 phone. Brent's variant of
    /// Pollard's rho finds factors of that size in milliseconds.
    ///
    /// Everything is 64-bit, which is why <see cref="MulMod"/> exists: a plain
    /// <c>a * b % n</c> overflows for n near 2^63, silently producing a wrong
    /// result rather than an error.
    /// </summary>
    public static class PqFactorization
    {
        /// <summary>
        /// Splits <paramref name="pq"/> into p and q with p &lt; q, as the protocol
        /// requires them to be ordered.
        /// </summary>
        public static void Factor(ulong pq, out ulong p, out ulong q)
        {
            ulong f = FindFactor(pq);
            ulong other = pq / f;

            p = Math.Min(f, other);
            q = Math.Max(f, other);

            if (p * q != pq)
                throw new MtprotoException("failed to factor pq = " + pq);
        }

        private static ulong FindFactor(ulong n)
        {
            if (n % 2 == 0) return 2;

            // Brent's cycle detection. The multiplier c and starting point are
            // varied on retry because a single choice can fail to separate the
            // factors for particular n.
            for (ulong c = 1; c < 20; c++)
            {
                ulong x = 2, y = 2, d = 1;
                ulong r = 1, qAcc = 1;
                ulong ys = 0;

                while (d == 1)
                {
                    x = y;
                    for (ulong i = 0; i < r; i++) y = Step(y, c, n);

                    ulong k = 0;
                    while (k < r && d == 1)
                    {
                        ys = y;
                        ulong limit = Math.Min(128, r - k);
                        for (ulong i = 0; i < limit; i++)
                        {
                            y = Step(y, c, n);
                            qAcc = MulMod(qAcc, Diff(x, y), n);
                        }
                        d = Gcd(qAcc, n);
                        k += limit;
                    }
                    r *= 2;

                    if (r > (1UL << 40)) break;      // give up on this c
                }

                if (d == n)
                {
                    // Backtrack one step at a time to recover the factor the batched
                    // gcd lost.
                    d = 1;
                    ulong y2 = ys;
                    while (d == 1)
                    {
                        y2 = Step(y2, c, n);
                        d = Gcd(Diff(x, y2), n);
                    }
                }

                if (d != 1 && d != n) return d;
            }

            throw new MtprotoException("could not factor pq = " + n);
        }

        private static ulong Step(ulong y, ulong c, ulong n)
        {
            ulong v = MulMod(y, y, n) + c;
            return v >= n ? v - n : v;
        }

        private static ulong Diff(ulong a, ulong b)
        {
            return a > b ? a - b : b - a;
        }

        /// <summary>
        /// (a * b) mod n without overflowing 64 bits, by shift-and-add. Slower than a
        /// 128-bit multiply, but neither WP8.1 nor plain C# offers one.
        /// </summary>
        public static ulong MulMod(ulong a, ulong b, ulong n)
        {
            ulong result = 0;
            a %= n;
            while (b > 0)
            {
                if ((b & 1) != 0)
                {
                    result += a;
                    if (result >= n || result < a) result -= n;   // second test catches wraparound
                }
                ulong t = a;
                a += a;
                if (a >= n || a < t) a -= n;
                b >>= 1;
            }
            return result;
        }

        public static ulong Gcd(ulong a, ulong b)
        {
            while (b != 0)
            {
                ulong t = a % b;
                a = b;
                b = t;
            }
            return a;
        }
    }
}
