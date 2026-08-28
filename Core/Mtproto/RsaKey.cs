using System;
using Lumigram.Crypto;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// One of Telegram's server public keys.
    ///
    /// The "RSA" here needs no RSA library: MTProto encrypts a 255-byte block with
    /// raw textbook exponentiation, so <c>x^e mod n</c> over <see cref="BigInt"/> is
    /// the entire operation. That is also why WP8.1 having no RSA API costs us
    /// nothing.
    ///
    /// The fingerprint is derived rather than hardcoded - it is the low 64 bits of
    /// SHA1 over the TL serialisation of (modulus, exponent). Deriving it means the
    /// key material and the fingerprint cannot drift apart, and it doubles as a
    /// check that the TL writer and the hash agree with the rest of the world.
    /// </summary>
    public sealed class RsaKey
    {
        public BigInt Modulus { get; private set; }
        public BigInt Exponent { get; private set; }
        public long Fingerprint { get; private set; }

        public RsaKey(byte[] modulusBE, byte[] exponentBE, ICrypto crypto)
        {
            Modulus = BigInt.FromBytesBE(modulusBE);
            Exponent = BigInt.FromBytesBE(exponentBE);

            var w = new TlWriter(320);
            w.WriteBytes(modulusBE);
            w.WriteBytes(exponentBE);
            byte[] hash = crypto.Sha1(w.ToArray());

            long fp = 0;
            for (int i = 0; i < 8; i++) fp |= (long)hash[hash.Length - 8 + i] << (8 * i);
            Fingerprint = fp;
        }

        /// <summary>
        /// Raw RSA over a block that must be shorter than the modulus. MTProto uses
        /// exactly 255 bytes against a 2048-bit key, and expects 256 bytes back.
        /// </summary>
        public byte[] Encrypt(byte[] data)
        {
            var m = BigInt.FromBytesBE(data);
            if (BigInt.Compare(m, Modulus) >= 0)
                throw new ArgumentException("RSA block is not smaller than the modulus");
            return BigInt.ModPow(m, Exponent, Modulus).ToBytesBE(256);
        }

        /// <summary>
        /// Parses a PKCS#1 "RSA PUBLIC KEY" PEM - SEQUENCE { INTEGER n, INTEGER e }.
        /// Written by hand because the phone has no ASN.1 or RSA support at all.
        /// </summary>
        public static RsaKey FromPem(string pem, ICrypto crypto)
        {
            byte[] der = DecodePemBody(pem);
            int pos = 0;

            ReadDerHeader(der, ref pos, 0x30);          // SEQUENCE
            byte[] n = ReadDerInteger(der, ref pos);
            byte[] e = ReadDerInteger(der, ref pos);

            return new RsaKey(n, e, crypto);
        }

        private static byte[] DecodePemBody(string pem)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var rawLine in pem.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("-----")) continue;
                sb.Append(line);
            }
            return Convert.FromBase64String(sb.ToString());
        }

        private static int ReadDerHeader(byte[] der, ref int pos, byte expectedTag)
        {
            if (pos >= der.Length || der[pos] != expectedTag)
                throw new FormatException("DER: expected tag 0x" + expectedTag.ToString("x2") +
                                          " at " + pos);
            pos++;

            int len = der[pos++];
            if ((len & 0x80) != 0)
            {
                int count = len & 0x7F;
                if (count == 0 || count > 4) throw new FormatException("DER: bad length form");
                len = 0;
                for (int i = 0; i < count; i++) len = (len << 8) | der[pos++];
            }
            if (pos + len > der.Length) throw new FormatException("DER: length runs past end");
            return len;
        }

        private static byte[] ReadDerInteger(byte[] der, ref int pos)
        {
            int len = ReadDerHeader(der, ref pos, 0x02);
            int start = pos;
            pos += len;

            // DER integers are signed, so a positive value whose top bit is set
            // carries a leading zero byte. It is not part of the number.
            if (len > 1 && der[start] == 0x00) { start++; len--; }

            var r = new byte[len];
            Buffer.BlockCopy(der, start, r, 0, len);
            return r;
        }
    }
}
