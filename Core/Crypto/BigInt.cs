using System;

namespace Lumigram.Crypto
{
    /// <summary>
    /// Minimal unsigned big integer - only what MTProto needs.
    ///
    /// This exists because WP8.1 Silverlight has no System.Numerics. It is not a
    /// general-purpose numeric type: values are unsigned, immutable, and the API
    /// stops at the handful of operations the handshake uses (ModPow for
    /// Diffie-Hellman, and x^65537 mod n for the RSA step).
    ///
    /// Limbs are uint, little-endian: _d[0] is least significant. Callers deal in
    /// big-endian byte strings, because that is how MTProto carries integers.
    /// </summary>
    public sealed class BigInt
    {
        private readonly uint[] _d;   // little-endian limbs, no leading zeros

        public static readonly BigInt Zero = new BigInt(new uint[0]);
        public static readonly BigInt One = FromUInt(1);

        private BigInt(uint[] limbs) { _d = Trim(limbs); }

        private static uint[] Trim(uint[] a)
        {
            int n = a.Length;
            while (n > 0 && a[n - 1] == 0) n--;
            if (n == a.Length) return a;
            var r = new uint[n];
            Array.Copy(a, r, n);
            return r;
        }

        public static BigInt FromUInt(uint v)
        {
            return v == 0 ? Zero : new BigInt(new[] { v });
        }

        public static BigInt FromUInt64(ulong v)
        {
            if (v == 0) return Zero;
            return new BigInt(new[] { (uint)v, (uint)(v >> 32) });
        }

        /// <summary>Parses a big-endian byte string, as MTProto transmits integers.</summary>
        public static BigInt FromBytesBE(byte[] b)
        {
            if (b == null || b.Length == 0) return Zero;
            int limbs = (b.Length + 3) / 4;
            var d = new uint[limbs];
            for (int i = 0; i < b.Length; i++)
            {
                int be = b.Length - 1 - i;           // byte index counted from the low end
                d[be >> 2] |= (uint)b[i] << (8 * (be & 3));
            }
            return new BigInt(d);
        }

        /// <summary>
        /// Big-endian bytes. When <paramref name="size"/> is positive the result is
        /// left-padded or trimmed to exactly that length, which is what the protocol
        /// wants: DH values are always sent as 256 bytes regardless of magnitude.
        /// </summary>
        public byte[] ToBytesBE(int size)
        {
            int len = _d.Length * 4;
            if (len == 0)
            {
                return size < 0 ? new byte[1] : new byte[size];
            }

            var full = new byte[len];
            for (int i = 0; i < len; i++)
                full[len - 1 - i] = (byte)(_d[i >> 2] >> (8 * (i & 3)));

            int first = 0;
            while (first < len - 1 && full[first] == 0) first++;
            int sig = len - first;

            if (size < 0)
            {
                var r = new byte[sig];
                Array.Copy(full, first, r, 0, sig);
                return r;
            }

            var p = new byte[size];
            int copy = Math.Min(sig, size);
            Array.Copy(full, first + (sig - copy), p, size - copy, copy);
            return p;
        }

        public byte[] ToBytesBE() { return ToBytesBE(-1); }

        public bool IsZero { get { return _d.Length == 0; } }

        public int BitLength
        {
            get
            {
                if (_d.Length == 0) return 0;
                uint hi = _d[_d.Length - 1];
                int bits = 0;
                while (hi != 0) { bits++; hi >>= 1; }
                return (_d.Length - 1) * 32 + bits;
            }
        }

        public bool TestBit(int i)
        {
            int limb = i >> 5;
            return limb < _d.Length && ((_d[limb] >> (i & 31)) & 1) != 0;
        }

        public static int Compare(BigInt a, BigInt b)
        {
            if (a._d.Length != b._d.Length) return a._d.Length < b._d.Length ? -1 : 1;
            for (int i = a._d.Length - 1; i >= 0; i--)
                if (a._d[i] != b._d[i]) return a._d[i] < b._d[i] ? -1 : 1;
            return 0;
        }

        public static BigInt Add(BigInt a, BigInt b)
        {
            int n = Math.Max(a._d.Length, b._d.Length);
            var r = new uint[n + 1];
            ulong carry = 0;
            for (int i = 0; i < n; i++)
            {
                ulong s = carry;
                if (i < a._d.Length) s += a._d[i];
                if (i < b._d.Length) s += b._d[i];
                r[i] = (uint)s;
                carry = s >> 32;
            }
            r[n] = (uint)carry;
            return new BigInt(r);
        }

        /// <summary>a - b, requiring a >= b (unsigned type: negatives cannot be represented).</summary>
        public static BigInt Sub(BigInt a, BigInt b)
        {
            if (Compare(a, b) < 0) throw new ArgumentException("BigInt.Sub would go negative");
            var r = new uint[a._d.Length];
            long borrow = 0;
            for (int i = 0; i < a._d.Length; i++)
            {
                long s = (long)a._d[i] - borrow - (i < b._d.Length ? b._d[i] : 0);
                if (s < 0) { s += 1L << 32; borrow = 1; } else borrow = 0;
                r[i] = (uint)s;
            }
            return new BigInt(r);
        }

        public static BigInt Mul(BigInt a, BigInt b)
        {
            if (a.IsZero || b.IsZero) return Zero;
            var r = new uint[a._d.Length + b._d.Length];
            for (int i = 0; i < a._d.Length; i++)
            {
                ulong carry = 0, ai = a._d[i];
                if (ai == 0) continue;
                for (int j = 0; j < b._d.Length; j++)
                {
                    ulong t = ai * b._d[j] + r[i + j] + carry;
                    r[i + j] = (uint)t;
                    carry = t >> 32;
                }
                int k = i + b._d.Length;
                while (carry != 0) { ulong t = r[k] + carry; r[k] = (uint)t; carry = t >> 32; k++; }
            }
            return new BigInt(r);
        }

        public static BigInt Mod(BigInt a, BigInt m)
        {
            BigInt q, rem;
            DivMod(a, m, out q, out rem);
            return rem;
        }

        /// <summary>
        /// Knuth algorithm D. Plain shift-and-subtract reduction would be far simpler,
        /// but it costs O(bits) subtractions per reduction - roughly 800M limb
        /// operations for one 2048-bit ModPow, which is tens of seconds on a phone.
        /// This keeps the handshake near a second.
        /// </summary>
        public static void DivMod(BigInt a, BigInt b, out BigInt quotient, out BigInt remainder)
        {
            if (b.IsZero) throw new DivideByZeroException();
            if (Compare(a, b) < 0) { quotient = Zero; remainder = a; return; }

            if (b._d.Length == 1)
            {
                ulong d = b._d[0], r0 = 0;
                var q1 = new uint[a._d.Length];
                for (int i = a._d.Length - 1; i >= 0; i--)
                {
                    ulong cur = (r0 << 32) | a._d[i];
                    q1[i] = (uint)(cur / d);
                    r0 = cur % d;
                }
                quotient = new BigInt(q1);
                remainder = FromUInt((uint)r0);
                return;
            }

            // Normalise so the divisor's top limb has its high bit set - algorithm D
            // requires this for the qhat estimate to be within one of the truth.
            int shift = 0;
            uint hi = b._d[b._d.Length - 1];
            while ((hi & 0x80000000u) == 0) { hi <<= 1; shift++; }

            uint[] u = ShiftLeft(a._d, shift, a._d.Length + 1);
            uint[] v = ShiftLeft(b._d, shift, b._d.Length);

            int n = v.Length, m = u.Length - n - 1;
            var q = new uint[m + 1];
            ulong vHigh = v[n - 1], vNext = v[n - 2];

            for (int j = m; j >= 0; j--)
            {
                ulong num = ((ulong)u[j + n] << 32) | u[j + n - 1];
                ulong qhat = num / vHigh, rhat = num % vHigh;
                while (qhat > 0xFFFFFFFFul ||
                       qhat * vNext > ((rhat << 32) | u[j + n - 2]))
                {
                    qhat--;
                    rhat += vHigh;
                    if (rhat > 0xFFFFFFFFul) break;
                }

                long borrow = 0;
                ulong carry = 0;
                for (int i = 0; i < n; i++)
                {
                    ulong p = qhat * v[i] + carry;
                    carry = p >> 32;
                    long t = (long)u[i + j] - (long)(uint)p - borrow;
                    if (t < 0) { t += 1L << 32; borrow = 1; } else borrow = 0;
                    u[i + j] = (uint)t;
                }
                long tn = (long)u[j + n] - (long)carry - borrow;
                if (tn < 0)
                {
                    // qhat was one too large: give the digit back and add the divisor in.
                    tn += 1L << 32;
                    u[j + n] = (uint)tn;
                    qhat--;
                    ulong c2 = 0;
                    for (int i = 0; i < n; i++)
                    {
                        ulong s = (ulong)u[i + j] + v[i] + c2;
                        u[i + j] = (uint)s;
                        c2 = s >> 32;
                    }
                    u[j + n] = (uint)(u[j + n] + c2);
                }
                else u[j + n] = (uint)tn;

                q[j] = (uint)qhat;
            }

            quotient = new BigInt(q);
            remainder = new BigInt(ShiftRight(u, shift, n));
        }

        private static uint[] ShiftLeft(uint[] a, int shift, int size)
        {
            var r = new uint[size];
            if (shift == 0)
            {
                Array.Copy(a, r, Math.Min(a.Length, size));
                return r;
            }
            uint carry = 0;
            int lim = Math.Min(a.Length, size);
            for (int i = 0; i < lim; i++)
            {
                r[i] = (a[i] << shift) | carry;
                carry = a[i] >> (32 - shift);
            }
            if (a.Length < size) r[a.Length] = carry;
            return r;
        }

        private static uint[] ShiftRight(uint[] a, int shift, int len)
        {
            var r = new uint[len];
            if (shift == 0)
            {
                Array.Copy(a, r, Math.Min(a.Length, len));
                return r;
            }
            for (int i = 0; i < len; i++)
            {
                uint lo = a[i] >> shift;
                uint hi = (i + 1 < a.Length) ? (a[i + 1] << (32 - shift)) : 0;
                r[i] = lo | hi;
            }
            return r;
        }

        /// <summary>
        /// Left-to-right square-and-multiply. Deliberately not constant time: the
        /// exponents here are ephemeral DH secrets on a single-user device, and a
        /// side-channel-hardened ladder is not worth the cost on a 2013 ARM chip.
        /// </summary>
        public static BigInt ModPow(BigInt b, BigInt e, BigInt m)
        {
            if (m.IsZero) throw new DivideByZeroException();
            if (e.IsZero) return One;
            BigInt result = One, bas = Mod(b, m);
            for (int i = e.BitLength - 1; i >= 0; i--)
            {
                result = Mod(Mul(result, result), m);
                if (e.TestBit(i)) result = Mod(Mul(result, bas), m);
            }
            return result;
        }

        public override string ToString()
        {
            if (IsZero) return "0";
            var b = ToBytesBE();
            var sb = new System.Text.StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return "0x" + sb;
        }
    }
}
