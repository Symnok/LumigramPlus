using System;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;
using Lumigram.Crypto;

namespace Lumigram.Phone
{
    /// <summary>
    /// The platform shim on Windows Phone 8.1, over WinRT.
    ///
    /// WP8.1 Silverlight has no System.Security.Cryptography, but it can call the
    /// WinRT crypto APIs, which is where these come from. Providers are opened once
    /// and cached: OpenAlgorithm is not free, and the handshake hashes repeatedly.
    ///
    /// Note what is absent - AES. IGE chains block to block, so routing it through
    /// WinRT would mean an interop call per 16 bytes; Core implements the cipher in
    /// managed code instead. The same concern applies to PBKDF2, which runs 100,000
    /// HMAC iterations for two-step verification: see the remark on HmacSha512.
    /// </summary>
    internal sealed class PhoneCrypto : ICrypto
    {
        private static readonly HashAlgorithmProvider Sha1Provider =
            HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha1);

        private static readonly HashAlgorithmProvider Sha256Provider =
            HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);

        private static readonly HashAlgorithmProvider Sha512Provider =
            HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha512);

        private static readonly MacAlgorithmProvider HmacSha512Provider =
            MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha512);

        public byte[] Sha1(byte[] data) { return Hash(Sha1Provider, data); }
        public byte[] Sha256(byte[] data) { return Hash(Sha256Provider, data); }
        public byte[] Sha512(byte[] data) { return Hash(Sha512Provider, data); }

        private static byte[] Hash(HashAlgorithmProvider provider, byte[] data)
        {
            IBuffer input = CryptographicBuffer.CreateFromByteArray(data);
            IBuffer digest = provider.HashData(input);

            byte[] result;
            CryptographicBuffer.CopyToByteArray(digest, out result);
            return result;
        }

        /// <summary>
        /// One HMAC per call, each crossing into WinRT and allocating two IBuffers.
        /// That is fine for the handful the protocol uses directly, and it is the
        /// thing to watch for two-step verification, where PBKDF2 calls this 100,000
        /// times in a row. If that proves too slow on real hardware, the answer is a
        /// managed SHA-512 in Core, not a faster shim.
        /// </summary>
        public byte[] HmacSha512(byte[] key, byte[] data)
        {
            CryptographicKey macKey = HmacSha512Provider.CreateKey(
                CryptographicBuffer.CreateFromByteArray(key));

            IBuffer signature = CryptographicEngine.Sign(
                macKey, CryptographicBuffer.CreateFromByteArray(data));

            byte[] result;
            CryptographicBuffer.CopyToByteArray(signature, out result);
            return result;
        }

        public void RandomBytes(byte[] buffer)
        {
            IBuffer random = CryptographicBuffer.GenerateRandom((uint)buffer.Length);
            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(random, out bytes);
            System.Buffer.BlockCopy(bytes, 0, buffer, 0, buffer.Length);
        }
    }
}
