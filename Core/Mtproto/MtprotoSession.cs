using System;
using Lumigram.Crypto;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// Encrypts and decrypts messages under an established authorisation key -
    /// MTProto 2.0's message layer.
    ///
    ///     msg_key = middle 128 bits of SHA256(auth_key[88+x .. +32] || plaintext)
    ///     aes_key / aes_iv derived from msg_key and the auth key by SHA256
    ///     payload encrypted with AES-256 IGE
    ///
    /// x is 0 for messages we send and 8 for messages we receive, so the two
    /// directions never share key material.
    ///
    /// This is the 2.0 construction specifically - version 1.0 derived msg_key from
    /// SHA1 over the plaintext alone and is long dead server-side.
    ///
    /// Decryption re-derives msg_key from the *decrypted* plaintext and compares.
    /// Because IGE propagates any change through the rest of the message, that check
    /// is what makes tampering detectable, so it is not optional.
    /// </summary>
    public sealed class MtprotoSession
    {
        private readonly ICrypto _crypto;
        private readonly AuthKey _authKey;

        private long _sessionId;
        private long _lastMsgId;
        private int _seqNo;

        public long ServerSalt { get; set; }
        public int TimeOffset { get; set; }

        public MtprotoSession(ICrypto crypto, AuthKey authKey)
        {
            _crypto = crypto;
            _authKey = authKey;
            ServerSalt = authKey.ServerSalt;
            TimeOffset = authKey.TimeOffset;

            var sid = crypto.Random(8);
            _sessionId = ToInt64LE(sid, 0);
        }

        public long SessionId { get { return _sessionId; } }

        /// <summary>
        /// Re-bases message ids on the server's clock.
        ///
        /// Every message id carries a timestamp, and the server refuses any that
        /// falls outside a window around its own time - bad_msg_notification 16 or
        /// 17. The device clock is the one input here nobody controls, so when it
        /// disagrees the offset is learned from the server's own message id rather
        /// than trusted from the phone.
        ///
        /// The last id is cleared as well: when the clock was running ahead the new
        /// ids are lower, and holding on to the old high-water mark would keep
        /// producing exactly the ids the server just refused.
        /// </summary>
        /// <summary>
        /// How far the server's clock is ahead of the one this session is using,
        /// in seconds. Zero once the two agree, whatever the device clock says.
        /// </summary>
        public int DriftFrom(int serverUnixTime)
        {
            return serverUnixTime - (UnixNow() + TimeOffset);
        }

        public void SyncTime(int serverUnixTime)
        {
            TimeOffset = serverUnixTime - UnixNow();
            _lastMsgId = 0;
        }

        /// <summary>
        /// Starts a fresh session, keeping the authorisation key.
        ///
        /// The answer to bad_msg_notification 32 and 33: our sequence numbering and
        /// the server's have diverged, and only a new session puts them back in
        /// step. The expensive part - the key - is untouched.
        /// </summary>
        public void Renew()
        {
            _sessionId = ToInt64LE(_crypto.Random(8), 0);
            _lastMsgId = 0;
            _seqNo = 0;
        }

        /// <summary>
        /// Wraps a TL body in an encrypted message.
        ///
        /// <paramref name="contentRelated"/> controls the sequence number: real API
        /// calls consume one and must be acknowledged, bare service messages like
        /// acks do not.
        /// </summary>
        public byte[] Encrypt(byte[] body, bool contentRelated, out long msgId)
        {
            msgId = NextMessageId();
            int seq = NextSeqNo(contentRelated);

            var plain = new TlWriter(body.Length + 64);
            plain.WriteLong(ServerSalt)
                 .WriteLong(_sessionId)
                 .WriteLong(msgId)
                 .WriteInt(seq)
                 .WriteInt(body.Length)
                 .WriteRaw(body);

            // 12..1024 bytes of padding, total length a multiple of 16.
            int unpadded = plain.Length;
            int pad = 16 - (unpadded % 16);
            if (pad < 12) pad += 16;
            plain.WriteRaw(_crypto.Random(pad));

            byte[] plaintext = plain.ToArray();
            byte[] msgKey = ComputeMsgKey(plaintext, 0);

            byte[] aesKey, aesIv;
            DeriveKeys(msgKey, 0, out aesKey, out aesIv);

            byte[] encrypted = AesIge.Encrypt(plaintext, aesKey, aesIv);

            var packet = new TlWriter(24 + encrypted.Length);
            packet.WriteLong(_authKey.KeyId)
                  .WriteRaw(msgKey)
                  .WriteRaw(encrypted);
            return packet.ToArray();
        }

        /// <summary>
        /// Unwraps an encrypted message. Throws if the key id is not ours, if the
        /// re-derived msg_key disagrees, or if the declared body length is
        /// inconsistent - all of which mean the data is not what the server sent.
        /// </summary>
        public byte[] Decrypt(byte[] packet, out long msgId, out int seqNo)
        {
            if (packet.Length < 24)
                throw new MtprotoException("encrypted message shorter than its header");

            var r = new TlReader(packet);
            long keyId = r.ReadLong();
            if (keyId != _authKey.KeyId)
                throw new MtprotoException("message encrypted to key " + keyId.ToString("x16") +
                                           ", ours is " + _authKey.KeyId.ToString("x16"));

            byte[] msgKey = r.ReadRaw(16);
            byte[] encrypted = r.ReadRaw(packet.Length - 24);

            if (encrypted.Length % 16 != 0)
                throw new MtprotoException("encrypted payload is not a whole number of blocks");

            byte[] aesKey, aesIv;
            DeriveKeys(msgKey, 8, out aesKey, out aesIv);        // x = 8 for server messages

            byte[] plaintext = AesIge.Decrypt(encrypted, aesKey, aesIv);

            byte[] expected = ComputeMsgKey(plaintext, 8);
            if (!CryptoExtensions.ConstantTimeEquals(msgKey, expected))
                throw new MtprotoException("msg_key mismatch - message was corrupted or forged");

            var pr = new TlReader(plaintext);
            pr.ReadLong();                                       // salt
            long session = pr.ReadLong();
            if (session != _sessionId)
                throw new MtprotoException("message belongs to a different session");

            msgId = pr.ReadLong();
            seqNo = pr.ReadInt();
            int length = pr.ReadInt();

            if (length < 0 || length > plaintext.Length - 32)
                throw new MtprotoException("declared body length " + length + " does not fit the message");

            return pr.ReadRaw(length);
        }

        /// <summary>
        /// msg_key is the middle 128 bits of SHA256 over a 32-byte slice of the auth
        /// key followed by the plaintext.
        /// </summary>
        private byte[] ComputeMsgKey(byte[] plaintext, int x)
        {
            byte[] full = _crypto.Sha256(CryptoExtensions.Concat(
                _authKey.Key.Slice(88 + x, 32), plaintext));
            return full.Slice(8, 16);
        }

        /// <summary>
        /// The MTProto 2.0 key derivation. The interleaving of the two digests is
        /// arbitrary but exact; a swapped slice yields a key that fails silently as
        /// undecryptable noise.
        /// </summary>
        private void DeriveKeys(byte[] msgKey, int x, out byte[] aesKey, out byte[] aesIv)
        {
            byte[] a = _crypto.Sha256(CryptoExtensions.Concat(msgKey, _authKey.Key.Slice(x, 36)));
            byte[] b = _crypto.Sha256(CryptoExtensions.Concat(_authKey.Key.Slice(40 + x, 36), msgKey));

            aesKey = CryptoExtensions.Concat(a.Slice(0, 8), b.Slice(8, 16), a.Slice(24, 8));
            aesIv = CryptoExtensions.Concat(b.Slice(0, 8), a.Slice(8, 16), b.Slice(24, 8));
        }

        private long NextMessageId()
        {
            long now = UnixNow() + TimeOffset;
            long id = (now << 32) | ((long)_crypto.Random(2)[0] << 8 & 0xFFFC);
            if (id <= _lastMsgId) id = _lastMsgId + 4;
            _lastMsgId = id;
            return id;
        }

        private int NextSeqNo(bool contentRelated)
        {
            int seq = _seqNo * 2 + (contentRelated ? 1 : 0);
            if (contentRelated) _seqNo++;
            return seq;
        }

        private static int UnixNow()
        {
            return (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static long ToInt64LE(byte[] b, int offset)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v |= (long)b[offset + i] << (8 * i);
            return v;
        }
    }
}
