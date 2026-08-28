using System;

namespace Lumigram.Crypto
{
    /// <summary>
    /// PBKDF2 over HMAC-SHA512 (RFC 2898).
    ///
    /// Written here rather than taken from either platform: WP8.1 Silverlight has no
    /// PBKDF2 at all, and the desktop's Rfc2898DeriveBytes is hard-wired to SHA1 in
    /// .NET 4.5. Telegram's two-step verification uses 100,000 iterations of
    /// HMAC-SHA512, so neither would do.
    ///
    /// The MAC is the managed <see cref="Sha512.Hmac"/> rather than the platform's.
    /// Measured on a Lumia 521, routing this through WinRT cost ~20 ms per call -
    /// half an hour for one 2FA login - because every iteration crossed the interop
    /// boundary and rebuilt the MAC key. Here the key schedule is built once and
    /// the loop stays in managed code.
    /// </summary>
    public static class Pbkdf2
    {
        public static byte[] DeriveSha512(ICrypto crypto, byte[] password, byte[] salt,
                                          int iterations, int outputLength)
        {
            // crypto is accepted for symmetry with the rest of Core but deliberately
            // unused: the platform MAC is far too slow for this loop.
            if (iterations < 1) throw new ArgumentException("iterations must be positive");
            if (outputLength < 1) throw new ArgumentException("outputLength must be positive");

            const int HashLength = 64;                 // SHA-512
            var hmac = new Sha512.Hmac(password);      // key schedule built once
            int blocks = (outputLength + HashLength - 1) / HashLength;
            var result = new byte[blocks * HashLength];

            for (int block = 1; block <= blocks; block++)
            {
                // U1 = HMAC(password, salt || INT32_BE(block))
                var seed = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, seed, 0, salt.Length);
                seed[salt.Length] = (byte)(block >> 24);
                seed[salt.Length + 1] = (byte)(block >> 16);
                seed[salt.Length + 2] = (byte)(block >> 8);
                seed[salt.Length + 3] = (byte)block;

                byte[] u = hmac.Compute(seed);
                var acc = new byte[HashLength];
                Buffer.BlockCopy(u, 0, acc, 0, HashLength);

                for (int i = 1; i < iterations; i++)
                {
                    u = hmac.Compute(u);
                    for (int j = 0; j < HashLength; j++) acc[j] ^= u[j];
                }

                Buffer.BlockCopy(acc, 0, result, (block - 1) * HashLength, HashLength);
            }

            if (result.Length == outputLength) return result;
            var trimmed = new byte[outputLength];
            Buffer.BlockCopy(result, 0, trimmed, 0, outputLength);
            return trimmed;
        }
    }
}
