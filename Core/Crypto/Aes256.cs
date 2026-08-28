using System;

namespace Lumigram.Crypto
{
    /// <summary>
    /// AES-256 block cipher (FIPS-197), encrypt and decrypt of a single 16-byte block.
    ///
    /// Managed rather than platform-provided because MTProto needs IGE mode, which
    /// chains one block into the next; see <see cref="ICrypto"/> for why that rules
    /// out an interop-based block primitive.
    ///
    /// The key schedule is computed once per instance, so callers should keep the
    /// instance for as long as they keep the key.
    ///
    /// This is a straightforward table-driven implementation. It is not hardened
    /// against cache-timing attacks - the S-box lookups are data-dependent, as in
    /// every table-driven AES. That is an accepted trade here: the attacker model
    /// for a phone client does not include a local process measuring our cache
    /// lines, and the alternative (bitsliced AES) would be far slower on this
    /// hardware.
    /// </summary>
    public sealed class Aes256
    {
        private const int Nb = 4;      // block size in 32-bit words
        private const int Nk = 8;      // key size in words (256 bits)
        private const int Nr = 14;     // rounds for AES-256

        private readonly uint[] _encKey;   // expanded key, (Nr+1)*Nb words
        private readonly uint[] _decKey;

        private static readonly byte[] SBox = new byte[256];
        private static readonly byte[] InvSBox = new byte[256];
        private static readonly uint[] Te0 = new uint[256], Te1 = new uint[256], Te2 = new uint[256], Te3 = new uint[256];
        private static readonly uint[] Td0 = new uint[256], Td1 = new uint[256], Td2 = new uint[256], Td3 = new uint[256];
        private static readonly uint[] Rcon = { 0x01000000, 0x02000000, 0x04000000, 0x08000000, 0x10000000,
                                                0x20000000, 0x40000000, 0x80000000, 0x1B000000, 0x36000000,
                                                0x6C000000, 0xD8000000, 0xAB000000, 0x4D000000 };

        static Aes256()
        {
            BuildSBox();
            BuildTables();
        }

        private static void BuildSBox()
        {
            // Multiplicative inverse in GF(2^8) followed by the affine transform,
            // computed rather than tabulated so there is no long constant to mistype.
            var pow = new byte[256];
            var log = new byte[256];
            byte x = 1;
            for (int i = 0; i < 256; i++)
            {
                pow[i] = x;
                log[x] = (byte)i;
                x = (byte)(x ^ XTime(x));           // multiply by 3
            }

            SBox[0] = 0x63;
            for (int i = 1; i < 256; i++)
            {
                byte inv = pow[255 - log[i]];
                byte s = inv;
                byte r = inv;
                for (int t = 0; t < 4; t++)
                {
                    r = (byte)((r << 1) | (r >> 7));
                    s ^= r;
                }
                s ^= 0x63;
                SBox[i] = s;
            }
            for (int i = 0; i < 256; i++) InvSBox[SBox[i]] = (byte)i;
        }

        private static byte XTime(byte a)
        {
            return (byte)((a << 1) ^ ((a & 0x80) != 0 ? 0x1B : 0));
        }

        private static byte Mul(byte a, byte b)
        {
            byte r = 0;
            while (b != 0)
            {
                if ((b & 1) != 0) r ^= a;
                a = XTime(a);
                b >>= 1;
            }
            return r;
        }

        private static void BuildTables()
        {
            for (int i = 0; i < 256; i++)
            {
                byte s = SBox[i];
                Te0[i] = (uint)((Mul(s, 2) << 24) | (s << 16) | (s << 8) | Mul(s, 3));
                Te1[i] = (Te0[i] >> 8) | (Te0[i] << 24);
                Te2[i] = (Te0[i] >> 16) | (Te0[i] << 16);
                Te3[i] = (Te0[i] >> 24) | (Te0[i] << 8);

                byte d = InvSBox[i];
                Td0[i] = (uint)((Mul(d, 14) << 24) | (Mul(d, 9) << 16) | (Mul(d, 13) << 8) | Mul(d, 11));
                Td1[i] = (Td0[i] >> 8) | (Td0[i] << 24);
                Td2[i] = (Td0[i] >> 16) | (Td0[i] << 16);
                Td3[i] = (Td0[i] >> 24) | (Td0[i] << 8);
            }
        }

        public Aes256(byte[] key32)
        {
            if (key32 == null || key32.Length != 32)
                throw new ArgumentException("AES-256 requires a 32-byte key");

            _encKey = new uint[Nb * (Nr + 1)];
            for (int i = 0; i < Nk; i++) _encKey[i] = LoadBE(key32, i * 4);

            for (int i = Nk; i < Nb * (Nr + 1); i++)
            {
                uint t = _encKey[i - 1];
                if (i % Nk == 0)
                {
                    t = (t << 8) | (t >> 24);            // RotWord
                    t = SubWord(t) ^ Rcon[i / Nk - 1];
                }
                else if (i % Nk == 4)
                {
                    t = SubWord(t);                      // extra SubWord for 256-bit keys
                }
                _encKey[i] = _encKey[i - Nk] ^ t;
            }

            // Decryption key schedule: same words, order reversed, with InvMixColumns
            // applied to the interior round keys so the equivalent inverse cipher works.
            _decKey = new uint[_encKey.Length];
            for (int i = 0; i < Nb * (Nr + 1); i += 4)
            {
                int j = _encKey.Length - 4 - i;
                for (int k = 0; k < 4; k++) _decKey[i + k] = _encKey[j + k];
            }
            for (int i = 4; i < _decKey.Length - 4; i++)
            {
                uint w = _decKey[i];
                _decKey[i] = Td0[SBox[(w >> 24) & 0xFF]] ^ Td1[SBox[(w >> 16) & 0xFF]]
                           ^ Td2[SBox[(w >> 8) & 0xFF]] ^ Td3[SBox[w & 0xFF]];
            }
        }

        private static uint SubWord(uint w)
        {
            return (uint)((SBox[(w >> 24) & 0xFF] << 24) | (SBox[(w >> 16) & 0xFF] << 16)
                        | (SBox[(w >> 8) & 0xFF] << 8) | SBox[w & 0xFF]);
        }

        private static uint LoadBE(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        private static void StoreBE(uint v, byte[] b, int o)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        public void EncryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            uint s0 = LoadBE(input, inOffset) ^ _encKey[0];
            uint s1 = LoadBE(input, inOffset + 4) ^ _encKey[1];
            uint s2 = LoadBE(input, inOffset + 8) ^ _encKey[2];
            uint s3 = LoadBE(input, inOffset + 12) ^ _encKey[3];

            uint t0, t1, t2, t3;
            int k = 4;
            for (int round = 1; round < Nr; round++)
            {
                t0 = Te0[(s0 >> 24) & 0xFF] ^ Te1[(s1 >> 16) & 0xFF] ^ Te2[(s2 >> 8) & 0xFF] ^ Te3[s3 & 0xFF] ^ _encKey[k++];
                t1 = Te0[(s1 >> 24) & 0xFF] ^ Te1[(s2 >> 16) & 0xFF] ^ Te2[(s3 >> 8) & 0xFF] ^ Te3[s0 & 0xFF] ^ _encKey[k++];
                t2 = Te0[(s2 >> 24) & 0xFF] ^ Te1[(s3 >> 16) & 0xFF] ^ Te2[(s0 >> 8) & 0xFF] ^ Te3[s1 & 0xFF] ^ _encKey[k++];
                t3 = Te0[(s3 >> 24) & 0xFF] ^ Te1[(s0 >> 16) & 0xFF] ^ Te2[(s1 >> 8) & 0xFF] ^ Te3[s2 & 0xFF] ^ _encKey[k++];
                s0 = t0; s1 = t1; s2 = t2; s3 = t3;
            }

            // Final round has no MixColumns.
            StoreBE(FinalEnc(s0, s1, s2, s3) ^ _encKey[k++], output, outOffset);
            StoreBE(FinalEnc(s1, s2, s3, s0) ^ _encKey[k++], output, outOffset + 4);
            StoreBE(FinalEnc(s2, s3, s0, s1) ^ _encKey[k++], output, outOffset + 8);
            StoreBE(FinalEnc(s3, s0, s1, s2) ^ _encKey[k], output, outOffset + 12);
        }

        private static uint FinalEnc(uint a, uint b, uint c, uint d)
        {
            return (uint)((SBox[(a >> 24) & 0xFF] << 24) | (SBox[(b >> 16) & 0xFF] << 16)
                        | (SBox[(c >> 8) & 0xFF] << 8) | SBox[d & 0xFF]);
        }

        public void DecryptBlock(byte[] input, int inOffset, byte[] output, int outOffset)
        {
            uint s0 = LoadBE(input, inOffset) ^ _decKey[0];
            uint s1 = LoadBE(input, inOffset + 4) ^ _decKey[1];
            uint s2 = LoadBE(input, inOffset + 8) ^ _decKey[2];
            uint s3 = LoadBE(input, inOffset + 12) ^ _decKey[3];

            uint t0, t1, t2, t3;
            int k = 4;
            for (int round = 1; round < Nr; round++)
            {
                t0 = Td0[(s0 >> 24) & 0xFF] ^ Td1[(s3 >> 16) & 0xFF] ^ Td2[(s2 >> 8) & 0xFF] ^ Td3[s1 & 0xFF] ^ _decKey[k++];
                t1 = Td0[(s1 >> 24) & 0xFF] ^ Td1[(s0 >> 16) & 0xFF] ^ Td2[(s3 >> 8) & 0xFF] ^ Td3[s2 & 0xFF] ^ _decKey[k++];
                t2 = Td0[(s2 >> 24) & 0xFF] ^ Td1[(s1 >> 16) & 0xFF] ^ Td2[(s0 >> 8) & 0xFF] ^ Td3[s3 & 0xFF] ^ _decKey[k++];
                t3 = Td0[(s3 >> 24) & 0xFF] ^ Td1[(s2 >> 16) & 0xFF] ^ Td2[(s1 >> 8) & 0xFF] ^ Td3[s0 & 0xFF] ^ _decKey[k++];
                s0 = t0; s1 = t1; s2 = t2; s3 = t3;
            }

            StoreBE(FinalDec(s0, s3, s2, s1) ^ _decKey[k++], output, outOffset);
            StoreBE(FinalDec(s1, s0, s3, s2) ^ _decKey[k++], output, outOffset + 4);
            StoreBE(FinalDec(s2, s1, s0, s3) ^ _decKey[k++], output, outOffset + 8);
            StoreBE(FinalDec(s3, s2, s1, s0) ^ _decKey[k], output, outOffset + 12);
        }

        private static uint FinalDec(uint a, uint b, uint c, uint d)
        {
            return (uint)((InvSBox[(a >> 24) & 0xFF] << 24) | (InvSBox[(b >> 16) & 0xFF] << 16)
                        | (InvSBox[(c >> 8) & 0xFF] << 8) | InvSBox[d & 0xFF]);
        }
    }
}
