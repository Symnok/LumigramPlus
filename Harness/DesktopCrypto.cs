using System;
using System.Security.Cryptography;
using Lumigram.Crypto;

namespace Lumigram.Harness
{
    /// <summary>
    /// Desktop implementation of the platform shim. The phone gets an equivalent
    /// built on WinRT's HashAlgorithmProvider and CryptographicBuffer.
    /// </summary>
    internal sealed class DesktopCrypto : ICrypto
    {
        private readonly SHA1 _sha1 = SHA1.Create();
        private readonly SHA256 _sha256 = SHA256.Create();
        private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();

        public byte[] Sha1(byte[] data)
        {
            lock (_sha1) return _sha1.ComputeHash(data);
        }

        public byte[] Sha256(byte[] data)
        {
            lock (_sha256) return _sha256.ComputeHash(data);
        }

        public byte[] Sha512(byte[] data)
        {
            using (var h = SHA512.Create()) return h.ComputeHash(data);
        }

        public byte[] HmacSha512(byte[] key, byte[] data)
        {
            using (var h = new HMACSHA512(key)) return h.ComputeHash(data);
        }

        public void RandomBytes(byte[] buffer)
        {
            _rng.GetBytes(buffer);
        }

        /// <summary>
        /// Test-only AES-256 ECB via the framework, used to check Core's managed
        /// implementation. Not part of ICrypto - the protocol never calls this.
        /// </summary>
        public static byte[] ReferenceEcbEncrypt(byte[] key32, byte[] block)
        {
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.KeySize = 256;
                aes.Key = key32;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using (var enc = aes.CreateEncryptor())
                    return enc.TransformFinalBlock(block, 0, block.Length);
            }
        }

        public static byte[] ReferenceEcbDecrypt(byte[] key32, byte[] block)
        {
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.KeySize = 256;
                aes.Key = key32;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using (var dec = aes.CreateDecryptor())
                    return dec.TransformFinalBlock(block, 0, block.Length);
            }
        }
    }
}
