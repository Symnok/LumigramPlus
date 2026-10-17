using System;

namespace Lumigram.Crypto
{
    /// <summary>
    /// HMAC-SHA256 (RFC 2104), built on the platform's SHA-256.
    ///
    /// Only FakeTLS proxies need it, and it is two hashes and two XORs, so it is
    /// written once here rather than added to both platforms' ICrypto.
    /// </summary>
    public static class HmacSha256
    {
        private const int BlockSize = 64;

        public static byte[] Compute(ICrypto crypto, byte[] key, byte[] data)
        {
            if (key.Length > BlockSize) key = crypto.Sha256(key);

            var inner = new byte[BlockSize + data.Length];
            var outer = new byte[BlockSize + 32];

            for (int i = 0; i < BlockSize; i++)
            {
                byte k = i < key.Length ? key[i] : (byte)0;
                inner[i] = (byte)(k ^ 0x36);
                outer[i] = (byte)(k ^ 0x5c);
            }

            Buffer.BlockCopy(data, 0, inner, BlockSize, data.Length);
            byte[] innerHash = crypto.Sha256(inner);
            Buffer.BlockCopy(innerHash, 0, outer, BlockSize, 32);

            return crypto.Sha256(outer);
        }

        /// <summary>Compares without stopping at the first difference.</summary>
        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
