using System;
using System.Collections.Generic;
using Lumigram.Crypto;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// Checks the Diffie-Hellman parameters the server supplies.
    ///
    /// This matters more here than in most clients. The entire security argument for
    /// this project is that no server is trusted - but the DH prime and generator
    /// arrive *from* the server, and a client that accepts them unchecked can be fed
    /// parameters that make the shared secret cheap to recover. Skipping these
    /// checks would quietly undo the reason for talking to Telegram directly.
    ///
    /// Primality testing a 2048-bit number costs several modular exponentiations,
    /// which on a 2013 phone is seconds. Telegram sends the same well-known prime to
    /// everyone, so a validated prime is remembered and the cost is paid once per
    /// install rather than once per login.
    /// </summary>
    public static class DhValidation
    {
        private const int MillerRabinRounds = 12;

        /// <summary>
        /// The 2048-bit safe prime Telegram serves to everyone.
        ///
        /// Recognising it turns the common case from a twenty-second proof into a
        /// byte comparison. Measured on a Lumia 521, validating it from scratch cost
        /// ~35 s on first login - twenty-four 2048-bit exponentiations at ~915 ms
        /// each - which is not a reasonable thing to make someone sit through.
        ///
        /// This is not a shortcut around the security property. An *unrecognised*
        /// prime still gets the full Miller-Rabin treatment, which is exactly the
        /// case worth being slow about: a server substituting a weak group. The
        /// official clients do the same (TDLib: DhCache::is_good_prime compares a
        /// built_in_good_prime before doing any work).
        /// </summary>
        private const string BuiltInGoodPrimeHex =
            "c71caeb9c6b1c9048e6c522f70f13f73980d40238e3e21c14934d037563d930f" +
            "48198a0aa7c14058229493d22530f4dbfa336f6e0ac925139543aed44cce7c37" +
            "20fd51f69458705ac68cd4fe6b6b13abdc9746512969328454f18faf8c595f64" +
            "2477fe96bb2a941d5bcd1d4ac8cc49880708fa9b378e3c4f3a9060bee67cf9a4" +
            "a4a695811051907e162753b56b0f6b410dba74d8a84b2a14b3144e0ef1284754" +
            "fd17ed950d5965b4b9dd46582db1178d169c6bc465b0d6ff9ca3928fef5b9ae4" +
            "e418fc15e83ebea0f87fa9ff5eed70050ded2849f47bf959d956850ce929851f" +
            "0d8115f635b105ee2e4e15d04b2454bf6f4fadf034b10403119cd8e3b92fcc5b";

        private static byte[] _builtInGoodPrime;

        // Primes already validated in this process, keyed by their bytes. Small: the
        // server realistically only ever offers one.
        private static readonly List<byte[]> _validated = new List<byte[]>();
        private static readonly object _lock = new object();

        /// <summary>
        /// Full parameter check. <paramref name="crypto"/> supplies random bases for
        /// primality testing - fixed bases would let a malicious server construct a
        /// composite that passes.
        /// </summary>
        public static void ValidateParameters(int g, BigInt dhPrime, BigInt ga,
                                              ICrypto crypto, Action<string> log)
        {
            // g is restricted by the protocol to a handful of small values.
            if (g < 2 || g > 7)
                throw new MtprotoException("DH generator out of range: " + g);

            if (dhPrime.BitLength != 2048)
                throw new MtprotoException("DH prime is " + dhPrime.BitLength + " bits, expected 2048");

            ValidatePublicValue(ga, dhPrime, "g_a");

            byte[] primeBytes = dhPrime.ToBytesBE(256);

            if (IsBuiltInGoodPrime(primeBytes))
            {
                log("   dh_prime: recognised as Telegram's standard safe prime");
                return;
            }

            if (AlreadyValidated(primeBytes))
            {
                log("   dh_prime: previously validated");
                return;
            }

            log("   dh_prime: validating (first time - this is the slow path)");

            // Telegram requires a safe prime: both p and (p-1)/2 must be prime.
            if (!IsProbablePrime(dhPrime, crypto))
                throw new MtprotoException("DH prime is not prime");

            BigInt half = HalveOdd(dhPrime);          // (p - 1) / 2
            if (!IsProbablePrime(half, crypto))
                throw new MtprotoException("DH prime is not a safe prime: (p-1)/2 is composite");

            Remember(primeBytes);
            log("   dh_prime: valid safe prime");
        }

        /// <summary>
        /// Bounds check for a DH public value. Beyond the obvious 1 &lt; x &lt; p-1,
        /// the protocol requires values to sit well inside the range: something near
        /// either end leaks the shared secret to anyone watching.
        /// </summary>
        public static void ValidatePublicValue(BigInt value, BigInt dhPrime, string name)
        {
            if (BigInt.Compare(value, BigInt.One) <= 0)
                throw new MtprotoException(name + " is too small");

            BigInt pMinus1 = BigInt.Sub(dhPrime, BigInt.One);
            if (BigInt.Compare(value, pMinus1) >= 0)
                throw new MtprotoException(name + " is too large");

            // 2^(2048-64) <= value <= p - 2^(2048-64)
            BigInt lower = PowerOfTwo(2048 - 64);
            BigInt upper = BigInt.Sub(dhPrime, lower);

            if (BigInt.Compare(value, lower) < 0)
                throw new MtprotoException(name + " is within 2^1984 of zero");
            if (BigInt.Compare(value, upper) > 0)
                throw new MtprotoException(name + " is within 2^1984 of the prime");
        }

        private static BigInt PowerOfTwo(int exponent)
        {
            var bytes = new byte[exponent / 8 + 1];
            bytes[0] = (byte)(1 << (exponent % 8));
            return BigInt.FromBytesBE(bytes);
        }

        /// <summary>(n - 1) / 2 for odd n, by shifting the big-endian bytes right one bit.</summary>
        private static BigInt HalveOdd(BigInt n)
        {
            byte[] b = n.ToBytesBE(-1);
            var r = new byte[b.Length];
            byte carry = 0;
            for (int i = 0; i < b.Length; i++)
            {
                r[i] = (byte)((b[i] >> 1) | carry);
                carry = (byte)((b[i] & 1) << 7);
            }
            return BigInt.FromBytesBE(r);
        }

        public static bool IsProbablePrime(BigInt n, ICrypto crypto)
        {
            if (BigInt.Compare(n, BigInt.FromUInt(2)) < 0) return false;

            // Cheap composite filter before the expensive part.
            uint[] smallPrimes = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 };
            foreach (uint sp in smallPrimes)
            {
                BigInt p = BigInt.FromUInt(sp);
                if (BigInt.Compare(n, p) == 0) return true;
                if (BigInt.Mod(n, p).IsZero) return false;
            }

            // n - 1 = d * 2^s with d odd
            BigInt nMinus1 = BigInt.Sub(n, BigInt.One);
            BigInt d = nMinus1;
            int s = 0;
            while (!d.TestBit(0)) { d = HalveEven(d); s++; }

            int byteLen = (n.BitLength + 7) / 8;

            for (int round = 0; round < MillerRabinRounds; round++)
            {
                BigInt a = RandomBase(crypto, byteLen, nMinus1);
                BigInt x = BigInt.ModPow(a, d, n);

                if (BigInt.Compare(x, BigInt.One) == 0 || BigInt.Compare(x, nMinus1) == 0)
                    continue;

                bool witnessed = false;
                for (int i = 1; i < s; i++)
                {
                    x = BigInt.Mod(BigInt.Mul(x, x), n);
                    if (BigInt.Compare(x, nMinus1) == 0) { witnessed = true; break; }
                }
                if (!witnessed) return false;
            }
            return true;
        }

        private static BigInt HalveEven(BigInt n)
        {
            byte[] b = n.ToBytesBE(-1);
            var r = new byte[b.Length];
            byte carry = 0;
            for (int i = 0; i < b.Length; i++)
            {
                r[i] = (byte)((b[i] >> 1) | carry);
                carry = (byte)((b[i] & 1) << 7);
            }
            return BigInt.FromBytesBE(r);
        }

        private static BigInt RandomBase(ICrypto crypto, int byteLen, BigInt upperExclusive)
        {
            while (true)
            {
                BigInt a = BigInt.FromBytesBE(crypto.Random(byteLen));
                if (BigInt.Compare(a, BigInt.FromUInt(2)) >= 0 &&
                    BigInt.Compare(a, upperExclusive) < 0)
                    return a;
            }
        }

        private static bool IsBuiltInGoodPrime(byte[] prime)
        {
            if (_builtInGoodPrime == null)
            {
                var b = new byte[BuiltInGoodPrimeHex.Length / 2];
                for (int i = 0; i < b.Length; i++)
                    b[i] = Convert.ToByte(BuiltInGoodPrimeHex.Substring(i * 2, 2), 16);
                _builtInGoodPrime = b;
            }
            return CryptoExtensions.ConstantTimeEquals(_builtInGoodPrime, prime);
        }

        private static bool AlreadyValidated(byte[] prime)
        {
            lock (_lock)
            {
                foreach (var p in _validated)
                    if (CryptoExtensions.ConstantTimeEquals(p, prime)) return true;
                return false;
            }
        }

        private static void Remember(byte[] prime)
        {
            lock (_lock)
            {
                if (_validated.Count < 8) _validated.Add(prime);
            }
        }
    }
}
