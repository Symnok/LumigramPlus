using System;
using Lumigram.Crypto;

namespace Lumigram.Harness
{
    /// <summary>
    /// Tests for the managed AES-256 and for IGE mode.
    ///
    /// Two independent kinds of check, because they catch different mistakes:
    ///
    ///   1. The FIPS-197 known-answer vector. Anchors the cipher to the standard,
    ///      so a self-consistent-but-wrong implementation cannot pass.
    ///   2. Differential tests against the framework's AES on random inputs -
    ///      including IGE itself, re-implemented here over platform ECB. If Core's
    ///      IGE loop and this one disagree, one of them has the chaining wrong.
    /// </summary>
    internal static class CryptoTests
    {
        private static int _checks;
        private static int _failures;

        public static bool RunAll()
        {
            var rng = new Random(20260824);

            Section("AES-256 known-answer (FIPS-197 C.3)");
            {
                var key = new byte[32];
                for (int i = 0; i < 32; i++) key[i] = (byte)i;
                var plain = new byte[16];
                for (int i = 0; i < 16; i++) plain[i] = (byte)(i * 0x11);

                byte[] expected = FromHex("8ea2b7ca516745bfeafc49904b496089");

                var got = new byte[16];
                new Aes256(key).EncryptBlock(plain, 0, got, 0);
                Expect("encrypt", expected, got);

                var back = new byte[16];
                new Aes256(key).DecryptBlock(expected, 0, back, 0);
                Expect("decrypt", plain, back);
            }

            Section("AES-256 vs framework (random)");
            for (int i = 0; i < 400; i++)
            {
                byte[] key = Rand(rng, 32), block = Rand(rng, 16);
                var aes = new Aes256(key);

                var mine = new byte[16];
                aes.EncryptBlock(block, 0, mine, 0);
                Expect("enc", DesktopCrypto.ReferenceEcbEncrypt(key, block), mine);

                var back = new byte[16];
                aes.DecryptBlock(mine, 0, back, 0);
                Expect("dec", block, back);
            }

            Section("IGE round-trip");
            for (int i = 0; i < 200; i++)
            {
                byte[] key = Rand(rng, 32), iv = Rand(rng, 32);
                byte[] data = Rand(rng, 16 * (1 + rng.Next(20)));

                byte[] enc = AesIge.Encrypt(data, key, iv);
                byte[] dec = AesIge.Decrypt(enc, key, iv);
                Expect("roundtrip", data, dec);

                if (data.Length >= 16 && Same(enc, data)) Fail("roundtrip", "ciphertext == plaintext");
            }

            Section("IGE vs independent implementation");
            for (int i = 0; i < 200; i++)
            {
                byte[] key = Rand(rng, 32), iv = Rand(rng, 32);
                byte[] data = Rand(rng, 16 * (1 + rng.Next(12)));

                Expect("ige-enc", ReferenceIgeEncrypt(data, key, iv), AesIge.Encrypt(data, key, iv));
                Expect("ige-dec", ReferenceIgeDecrypt(data, key, iv), AesIge.Decrypt(data, key, iv));
            }

            Section("IGE error propagation");
            {
                // A single flipped bit must change everything downstream. This is the
                // property MTProto relies on to detect tampering, so it is worth
                // asserting rather than assuming.
                byte[] key = Rand(rng, 32), iv = Rand(rng, 32);
                byte[] data = Rand(rng, 16 * 8);
                byte[] enc = AesIge.Encrypt(data, key, iv);

                enc[3] ^= 0x01;
                byte[] dec = AesIge.Decrypt(enc, key, iv);

                int sameBlocks = 0;
                for (int b = 0; b < data.Length; b += 16)
                    if (Same(data.Slice(b, 16), dec.Slice(b, 16))) sameBlocks++;

                _checks++;
                if (sameBlocks != 0)
                    Fail("propagation", sameBlocks + " block(s) survived a corrupted ciphertext");
            }

            Section("SHA-512 vs framework");
            {
                // Known-answer first, so a self-consistent-but-wrong implementation
                // cannot pass, then random differential cases.
                Expect("sha512(abc)", FromHex(
                    "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a" +
                    "2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f"),
                    Sha512.ComputeHash(System.Text.Encoding.ASCII.GetBytes("abc")));

                using (var reference = System.Security.Cryptography.SHA512.Create())
                {
                    // Sizes around the 128-byte block boundary and the padding edge,
                    // where length handling goes wrong if it is going to.
                    int[] sizes = { 0, 1, 55, 63, 64, 111, 112, 113, 127, 128, 129, 255, 256, 1000 };
                    foreach (int n in sizes)
                    {
                        byte[] data = Rand(rng, n);
                        Expect("sha512/" + n, reference.ComputeHash(data), Sha512.ComputeHash(data));
                    }
                    for (int i = 0; i < 200; i++)
                    {
                        byte[] data = Rand(rng, rng.Next(500));
                        Expect("sha512-rand", reference.ComputeHash(data), Sha512.ComputeHash(data));
                    }
                }
            }

            Section("HMAC-SHA512 and PBKDF2 vs framework");
            {
                for (int i = 0; i < 100; i++)
                {
                    byte[] key = Rand(rng, 1 + rng.Next(200));    // spans the 128-byte key limit
                    byte[] data = Rand(rng, rng.Next(300));

                    using (var reference = new System.Security.Cryptography.HMACSHA512(key))
                        Expect("hmac", reference.ComputeHash(data), Sha512.ComputeHmac(key, data));
                }

                // The reusable key schedule must agree with the one-shot form.
                byte[] k = Rand(rng, 64);
                var reusable = new Sha512.Hmac(k);
                for (int i = 0; i < 20; i++)
                {
                    byte[] data = Rand(rng, rng.Next(200));
                    Expect("hmac-reused", Sha512.ComputeHmac(k, data), reusable.Compute(data));
                }

                // PBKDF2 against RFC 6070-style expectations is awkward for SHA-512,
                // so check it against an independent implementation of the same loop.
                byte[] pw = System.Text.Encoding.UTF8.GetBytes("password");
                byte[] salt = System.Text.Encoding.UTF8.GetBytes("salt");
                Expect("pbkdf2", ReferencePbkdf2(pw, salt, 100, 64),
                       Pbkdf2.DeriveSha512(new DesktopCrypto(), pw, salt, 100, 64));
            }

            Section("shim");
            {
                var crypto = new DesktopCrypto();
                Expect("sha1(abc)", FromHex("a9993e364706816aba3e25717850c26c9cd0d89d"),
                       crypto.Sha1(System.Text.Encoding.ASCII.GetBytes("abc")));
                Expect("sha256(abc)", FromHex("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
                       crypto.Sha256(System.Text.Encoding.ASCII.GetBytes("abc")));

                var a = crypto.Random(32);
                var b = crypto.Random(32);
                _checks++;
                if (Same(a, b)) Fail("rng", "two draws were identical");
            }

            Console.WriteLine();
            Console.WriteLine("{0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        /// <summary>
        /// IGE written directly over the framework's AES, deliberately structured
        /// differently from Core's version so a shared misreading is less likely.
        /// </summary>
        private static byte[] ReferenceIgeEncrypt(byte[] data, byte[] key, byte[] iv)
        {
            var outBuf = new byte[data.Length];
            byte[] cPrev = iv.Slice(0, 16), mPrev = iv.Slice(16, 16);
            for (int o = 0; o < data.Length; o += 16)
            {
                byte[] m = data.Slice(o, 16);
                byte[] c = DesktopCrypto.ReferenceEcbEncrypt(key, Xor(m, cPrev));
                c = Xor(c, mPrev);
                Buffer.BlockCopy(c, 0, outBuf, o, 16);
                cPrev = c;
                mPrev = m;
            }
            return outBuf;
        }

        private static byte[] ReferenceIgeDecrypt(byte[] data, byte[] key, byte[] iv)
        {
            var outBuf = new byte[data.Length];
            byte[] cPrev = iv.Slice(0, 16), mPrev = iv.Slice(16, 16);
            for (int o = 0; o < data.Length; o += 16)
            {
                byte[] c = data.Slice(o, 16);
                byte[] m = DesktopCrypto.ReferenceEcbDecrypt(key, Xor(c, mPrev));
                m = Xor(m, cPrev);
                Buffer.BlockCopy(m, 0, outBuf, o, 16);
                cPrev = c;
                mPrev = m;
            }
            return outBuf;
        }

        /// <summary>PBKDF2 written directly over the framework's HMAC, for comparison.</summary>
        private static byte[] ReferencePbkdf2(byte[] password, byte[] salt, int iterations, int length)
        {
            using (var mac = new System.Security.Cryptography.HMACSHA512(password))
            {
                var seed = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, seed, 0, salt.Length);
                seed[salt.Length + 3] = 1;

                byte[] u = mac.ComputeHash(seed);
                var acc = (byte[])u.Clone();
                for (int i = 1; i < iterations; i++)
                {
                    u = mac.ComputeHash(u);
                    for (int j = 0; j < acc.Length; j++) acc[j] ^= u[j];
                }
                var result = new byte[length];
                Buffer.BlockCopy(acc, 0, result, 0, Math.Min(length, acc.Length));
                return result;
            }
        }

        private static byte[] Xor(byte[] a, byte[] b)
        {
            var r = new byte[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
            return r;
        }

        private static byte[] Rand(Random rng, int n)
        {
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        private static byte[] FromHex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("  [{0}]", name);
        }

        private static void Expect(string what, byte[] expected, byte[] actual)
        {
            _checks++;
            if (!Same(expected, actual))
                Fail(what, expected.ToHex() + " != " + actual.ToHex());
        }

        private static void Fail(string what, string detail)
        {
            _failures++;
            if (_failures <= 5) Console.WriteLine("    FAIL {0}: {1}", what, detail);
            else if (_failures == 6) Console.WriteLine("    ... further failures suppressed");
        }
    }
}
