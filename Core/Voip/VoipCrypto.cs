using System;
using Lumigram.Crypto;

namespace Lumigram.Voip
{
    /// <summary>
    /// The encryption around every voice packet.
    ///
    /// Not MTProto, despite the resemblance. Voice packets go over UDP straight to a
    /// reflector, outside the MTProto session entirely, and carry their own scheme
    /// built on the call key that signalling agreed. It is close enough to MTProto
    /// 2.0 to be misleading: the same AES-IGE, the same idea of a message key
    /// derived from the plaintext, and a key derivation that looks familiar and is
    /// not the same function.
    ///
    /// Two details decide whether any of it works, and neither announces itself when
    /// wrong. The offset x is 0 for the end that placed the call and 8 for the end
    /// that answered - both ends use the same 256-byte key, and the asymmetry is
    /// what keeps the two directions from sharing a keystream. And the message key
    /// is taken from the middle of a SHA-256, not the front.
    ///
    /// Written against libtgvoip's VoIPController::SendPacket and KDF2, because a
    /// packet encrypted subtly wrong is not rejected - it is discarded in silence by
    /// a client that has no way to tell us why.
    /// </summary>
    public static class VoipCrypto
    {
        /// <summary>The key is 256 bytes, as agreed by the call's Diffie-Hellman.</summary>
        public const int KeyLength = 256;

        /// <summary>
        /// Derives the AES key and IV for one packet.
        ///
        /// <paramref name="x"/> is 0 when we placed the call and 8 when we answered.
        /// </summary>
        public static void Kdf(ICrypto crypto, byte[] key, byte[] msgKey, int x,
                               out byte[] aesKey, out byte[] aesIv)
        {
            if (key == null || key.Length < KeyLength)
                throw new ArgumentException("the call key must be 256 bytes");
            if (msgKey == null || msgKey.Length != 16)
                throw new ArgumentException("a message key is 16 bytes");

            // sA = SHA256(msgKey || key[x .. x+36])
            byte[] sA = crypto.Sha256(Concat(msgKey, Slice(key, x, 36)));

            // sB = SHA256(key[40+x .. 40+x+36] || msgKey)
            byte[] sB = crypto.Sha256(Concat(Slice(key, 40 + x, 36), msgKey));

            aesKey = Concat(Slice(sA, 0, 8), Slice(sB, 8, 16), Slice(sA, 24, 8));
            aesIv = Concat(Slice(sB, 0, 8), Slice(sA, 8, 16), Slice(sB, 24, 8));
        }

        /// <summary>
        /// Wraps a packet body for the wire: peer tag, message key, ciphertext.
        ///
        /// The length prefix is inside the encrypted part, which is what lets the
        /// receiver tell the packet from its padding. The padding is random and
        /// always at least 16 bytes, so the length of what was said is not readable
        /// from the length of what was sent.
        /// </summary>
        public static byte[] Seal(ICrypto crypto, byte[] key, byte[] peerTag,
                                  byte[] body, bool outgoing)
        {
            if (peerTag == null || peerTag.Length != 16)
                throw new ArgumentException("a peer tag is 16 bytes");

            byte[] inner = Pad(crypto, body);

            int x = outgoing ? 0 : 8;

            // The message key covers the plaintext and part of the key, so a packet
            // cannot be moved to another call or replayed in the other direction.
            byte[] large = crypto.Sha256(Concat(Slice(key, 88 + x, 32), inner));
            byte[] msgKey = Slice(large, 8, 16);

            byte[] aesKey, aesIv;
            Kdf(crypto, key, msgKey, x, out aesKey, out aesIv);

            return Concat(peerTag, msgKey, AesIge.Encrypt(inner, aesKey, aesIv));
        }

        /// <summary>
        /// Unwraps a received packet, or returns null if it is not for us.
        ///
        /// The message key is recomputed from the decrypted bytes and compared. A
        /// packet that fails is dropped rather than reported: on an open UDP port,
        /// anything at all can arrive, and most of what fails here was never meant
        /// for this call.
        ///
        /// <paramref name="outgoing"/> is our own role, and the offset used is the
        /// other end's - they encrypted it, so their side of the key is what
        /// decrypts it.
        /// </summary>
        public static byte[] Open(ICrypto crypto, byte[] key, byte[] packet, bool outgoing)
        {
            if (packet == null || packet.Length < 16 + 16 + 16) return null;

            byte[] msgKey = Slice(packet, 16, 16);

            int cipherLength = packet.Length - 32;
            if (cipherLength % 16 != 0) return null;

            int x = outgoing ? 8 : 0;

            byte[] aesKey, aesIv;
            Kdf(crypto, key, msgKey, x, out aesKey, out aesIv);

            byte[] inner = AesIge.Decrypt(Slice(packet, 32, cipherLength), aesKey, aesIv);

            byte[] large = crypto.Sha256(Concat(Slice(key, 88 + x, 32), inner));
            if (!CryptoExtensions.ConstantTimeEquals(msgKey, Slice(large, 8, 16))) return null;

            int length = inner[0] | (inner[1] << 8);
            if (length < 0 || length + 2 > inner.Length) return null;

            return Slice(inner, 2, length);
        }

        /// <summary>
        /// The plaintext as it is encrypted: a 16-bit length, the body, and enough
        /// random padding to reach a multiple of the block size.
        /// </summary>
        private static byte[] Pad(ICrypto crypto, byte[] body)
        {
            int used = 2 + body.Length;

            // At least a whole block of padding, never none: a packet whose length
            // exactly fills the blocks would leak its size precisely.
            int padding = 16 - used % 16;
            if (padding < 16) padding += 16;

            var inner = new byte[used + padding];
            inner[0] = (byte)(body.Length & 0xff);
            inner[1] = (byte)((body.Length >> 8) & 0xff);

            Buffer.BlockCopy(body, 0, inner, 2, body.Length);
            Buffer.BlockCopy(crypto.Random(padding), 0, inner, used, padding);

            return inner;
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (byte[] part in parts) total += part.Length;

            var result = new byte[total];
            int at = 0;

            foreach (byte[] part in parts)
            {
                Buffer.BlockCopy(part, 0, result, at, part.Length);
                at += part.Length;
            }

            return result;
        }
    }
}
