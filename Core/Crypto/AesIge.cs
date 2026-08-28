using System;

namespace Lumigram.Crypto
{
    /// <summary>
    /// AES-256 in IGE (Infinite Garble Extension) mode - the mode MTProto encrypts
    /// every message with, and one that no platform crypto library provides.
    ///
    ///     encrypt:  c[i] = E(m[i] xor c[i-1]) xor m[i-1]
    ///     decrypt:  m[i] = D(c[i] xor m[i-1]) xor c[i-1]
    ///
    /// The 32-byte IV supplies the two "previous" blocks the first iteration needs:
    /// bytes 0..16 stand in for c[-1] and bytes 16..32 for m[-1].
    ///
    /// Unlike CBC, IGE propagates a change in any block through the whole remainder
    /// of the message in both directions, which is what lets MTProto detect
    /// tampering by re-deriving msg_key from the decrypted plaintext.
    /// </summary>
    public static class AesIge
    {
        public const int BlockSize = 16;
        public const int IvSize = 32;

        public static byte[] Encrypt(byte[] data, byte[] key32, byte[] iv32)
        {
            Validate(data, key32, iv32);

            var aes = new Aes256(key32);
            var output = new byte[data.Length];

            var prevCipher = iv32.Slice(0, BlockSize);    // c[-1]
            var prevPlain = iv32.Slice(BlockSize, BlockSize); // m[-1]
            var tmp = new byte[BlockSize];

            for (int off = 0; off < data.Length; off += BlockSize)
            {
                for (int i = 0; i < BlockSize; i++)
                    tmp[i] = (byte)(data[off + i] ^ prevCipher[i]);

                aes.EncryptBlock(tmp, 0, output, off);

                for (int i = 0; i < BlockSize; i++)
                    output[off + i] ^= prevPlain[i];

                Buffer.BlockCopy(output, off, prevCipher, 0, BlockSize);
                Buffer.BlockCopy(data, off, prevPlain, 0, BlockSize);
            }
            return output;
        }

        public static byte[] Decrypt(byte[] data, byte[] key32, byte[] iv32)
        {
            Validate(data, key32, iv32);

            var aes = new Aes256(key32);
            var output = new byte[data.Length];

            var prevCipher = iv32.Slice(0, BlockSize);
            var prevPlain = iv32.Slice(BlockSize, BlockSize);
            var tmp = new byte[BlockSize];

            for (int off = 0; off < data.Length; off += BlockSize)
            {
                for (int i = 0; i < BlockSize; i++)
                    tmp[i] = (byte)(data[off + i] ^ prevPlain[i]);

                aes.DecryptBlock(tmp, 0, output, off);

                for (int i = 0; i < BlockSize; i++)
                    output[off + i] ^= prevCipher[i];

                Buffer.BlockCopy(data, off, prevCipher, 0, BlockSize);
                Buffer.BlockCopy(output, off, prevPlain, 0, BlockSize);
            }
            return output;
        }

        private static void Validate(byte[] data, byte[] key32, byte[] iv32)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (key32 == null || key32.Length != 32) throw new ArgumentException("key must be 32 bytes");
            if (iv32 == null || iv32.Length != IvSize) throw new ArgumentException("iv must be 32 bytes");
            if (data.Length % BlockSize != 0)
                throw new ArgumentException("IGE operates on whole 16-byte blocks; got " + data.Length);
        }
    }
}
