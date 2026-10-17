using System;

namespace Lumigram.Crypto
{
    /// <summary>
    /// AES-256 in counter mode, as a running keystream.
    ///
    /// Needed by MTProto proxies only: their obfuscation encrypts the whole TCP
    /// stream as one continuous keystream in each direction, so this keeps its
    /// place between calls - bytes can be fed in any number of pieces of any size
    /// and come out exactly as if they had been fed in one go.
    ///
    /// The counter is the whole 16-byte block, incremented big-endian, which is
    /// what OpenSSL's CTR does and so what TDLib and the proxies do.
    /// </summary>
    public sealed class AesCtr
    {
        private readonly Aes256 _aes;
        private readonly byte[] _counter = new byte[16];
        private readonly byte[] _stream = new byte[16];
        private int _used = 16;                 // nothing of the current block left

        public AesCtr(byte[] key32, byte[] iv16)
        {
            if (iv16 == null || iv16.Length != 16)
                throw new ArgumentException("AES-CTR needs a 16-byte IV");

            _aes = new Aes256(key32);
            Buffer.BlockCopy(iv16, 0, _counter, 0, 16);
        }

        /// <summary>Encrypts or decrypts in place - in CTR they are the same thing.</summary>
        public void Apply(byte[] data)
        {
            if (data != null) Apply(data, 0, data.Length);
        }

        public void Apply(byte[] data, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                if (_used == 16)
                {
                    _aes.EncryptBlock(_counter, 0, _stream, 0);
                    Increment();
                    _used = 0;
                }

                data[i] ^= _stream[_used++];
            }
        }

        private void Increment()
        {
            for (int i = 15; i >= 0; i--)
                if (++_counter[i] != 0) break;
        }
    }
}
