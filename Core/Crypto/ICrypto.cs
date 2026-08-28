using System;

namespace Lumigram.Crypto
{
    /// <summary>
    /// The only platform-specific surface the protocol core needs: hashing and
    /// randomness.
    ///
    /// Core cannot reference System.Security.Cryptography (absent on WP8.1
    /// Silverlight) or WinRT (absent on the desktop), so both heads implement this
    /// and everything above it stays portable.
    ///
    /// AES is deliberately *not* here. MTProto uses IGE mode, which no platform
    /// offers, and IGE chains block to block - routing it through a platform API
    /// would mean one interop call per 16 bytes. <see cref="Aes256"/> implements the
    /// block cipher in managed code instead, so IGE runs without crossing the
    /// boundary at all. Hashing stays on the platform because it is called once or
    /// twice per message over a whole buffer, where interop costs nothing.
    /// </summary>
    public interface ICrypto
    {
        byte[] Sha1(byte[] data);
        byte[] Sha256(byte[] data);

        /// <summary>Needed only by the two-step-verification KDF.</summary>
        byte[] Sha512(byte[] data);

        /// <summary>
        /// HMAC-SHA512, the primitive PBKDF2 iterates for two-step verification.
        /// Exposed rather than the whole of PBKDF2 because the iteration loop is
        /// identical on both platforms - see <see cref="Pbkdf2"/> - while the MAC
        /// itself is not available in portable code.
        /// </summary>
        byte[] HmacSha512(byte[] key, byte[] data);

        /// <summary>
        /// Cryptographically secure random bytes. Used for nonces and the DH secret,
        /// so a predictable source here breaks the session outright.
        /// </summary>
        void RandomBytes(byte[] buffer);
    }

    public static class CryptoExtensions
    {
        public static byte[] Random(this ICrypto crypto, int count)
        {
            var b = new byte[count];
            crypto.RandomBytes(b);
            return b;
        }

        public static byte[] Sha1(this ICrypto crypto, params byte[][] parts)
        {
            return crypto.Sha1(Concat(parts));
        }

        public static byte[] Sha256(this ICrypto crypto, params byte[][] parts)
        {
            return crypto.Sha256(Concat(parts));
        }

        public static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            for (int i = 0; i < parts.Length; i++) n += parts[i].Length;
            var r = new byte[n];
            int o = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                Buffer.BlockCopy(parts[i], 0, r, o, parts[i].Length);
                o += parts[i].Length;
            }
            return r;
        }

        public static byte[] Slice(this byte[] source, int offset, int count)
        {
            var r = new byte[count];
            Buffer.BlockCopy(source, offset, r, 0, count);
            return r;
        }

        /// <summary>Length-independent comparison, for anything an attacker can probe.</summary>
        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static string ToHex(this byte[] b)
        {
            if (b == null) return "(null)";
            var sb = new System.Text.StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
