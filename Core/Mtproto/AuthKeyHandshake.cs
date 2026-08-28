using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>The result of a successful handshake - everything a session needs.</summary>
    public sealed class AuthKey
    {
        public byte[] Key { get; set; }          // 256 bytes
        public long KeyId { get; set; }
        public long ServerSalt { get; set; }
        public int TimeOffset { get; set; }      // server time minus ours, in seconds
    }

    /// <summary>
    /// Creates a permanent authorisation key by Diffie-Hellman, per MTProto 2.0.
    ///
    /// Three round trips: req_pq, req_DH_params, set_client_DH_params. The client
    /// proves it did the work by factoring pq, the server's DH parameters arrive
    /// inside an RSA-encrypted envelope, and both sides confirm they derived the
    /// same key by exchanging hashes of it.
    ///
    /// The key produced here is permanent and is what every later message is
    /// encrypted with, so this runs once and the result is persisted. On a phone
    /// that matters: the two 2048-bit exponentiations below are the slowest thing
    /// the app ever does.
    /// </summary>
    public sealed class AuthKeyHandshake
    {
        private readonly ICrypto _crypto;
        private readonly MtprotoFraming _framing;
        private readonly Action<string> _log;

        private long _lastMsgId;

        /// <summary>
        /// Takes the connection's framing rather than the transport, because the
        /// intermediate-mode tag is sent once per connection. The handshake and the
        /// session that follows it must share one framing instance - giving each its
        /// own re-sends the tag mid-stream and the server drops the connection.
        /// </summary>
        public AuthKeyHandshake(ICrypto crypto, MtprotoFraming framing, Action<string> log = null)
        {
            _crypto = crypto;
            _framing = framing;
            _log = log ?? delegate { };
        }

        public AuthKeyHandshake(ICrypto crypto, ITransport transport, Action<string> log = null)
            : this(crypto, new MtprotoFraming(transport), log)
        {
        }

        public async Task<AuthKey> RunAsync()
        {
            // ---- 1. req_pq -------------------------------------------------
            byte[] nonce = _crypto.Random(16);

            var w = new TlWriter();
            w.WriteConstructor(TlConstructors.ReqPQ).WriteRaw(nonce);
            _log("-> req_pq");

            TlReader r = await ExchangeAsync(w.ToArray());

            r.Expect(TlConstructors.ResPQ, "resPQ");
            byte[] echoNonce = r.ReadRaw(16);
            byte[] serverNonce = r.ReadRaw(16);
            byte[] pqBytes = r.ReadBytes();
            long[] fingerprints = r.ReadVectorOfLong();

            if (!CryptoExtensions.ConstantTimeEquals(nonce, echoNonce))
                throw new MtprotoException("resPQ echoed a different nonce");

            ulong pq = ToUInt64BE(pqBytes);
            _log("<- resPQ  pq=" + pq + "  fingerprints=" + fingerprints.Length);

            // ---- 2. factor pq and build p_q_inner_data ---------------------
            ulong p, q;
            PqFactorization.Factor(pq, out p, out q);
            _log("   pq = " + p + " * " + q);

            var keys = TelegramServers.LoadPublicKeys(_crypto);
            RsaKey rsaKey = TelegramServers.FindByFingerprint(keys, fingerprints);
            if (rsaKey == null)
                throw new MtprotoException("server offered no public key we recognise");

            byte[] newNonce = _crypto.Random(32);
            byte[] pBytes = FromUInt32BE((uint)p);
            byte[] qBytes = FromUInt32BE((uint)q);

            var inner = new TlWriter();
            inner.WriteConstructor(TlConstructors.PQInnerData)
                 .WriteBytes(pqBytes)
                 .WriteBytes(pBytes)
                 .WriteBytes(qBytes)
                 .WriteRaw(nonce)
                 .WriteRaw(serverNonce)
                 .WriteRaw(newNonce);
            byte[] innerData = inner.ToArray();

            // SHA1 + data + random padding, to exactly 255 bytes so the value stays
            // below the 2048-bit modulus.
            byte[] hashed = CryptoExtensions.Concat(_crypto.Sha1(innerData), innerData);
            if (hashed.Length > 255) throw new MtprotoException("p_q_inner_data too large");
            byte[] padded = CryptoExtensions.Concat(hashed, _crypto.Random(255 - hashed.Length));

            byte[] encryptedInner = rsaKey.Encrypt(padded);

            // ---- 3. req_DH_params ------------------------------------------
            w = new TlWriter();
            w.WriteConstructor(TlConstructors.ReqDHParams)
             .WriteRaw(nonce)
             .WriteRaw(serverNonce)
             .WriteBytes(pBytes)
             .WriteBytes(qBytes)
             .WriteLong(rsaKey.Fingerprint)
             .WriteBytes(encryptedInner);
            _log("-> req_DH_params  key=" + rsaKey.Fingerprint.ToString("x16"));

            r = await ExchangeAsync(w.ToArray());

            uint answerType = r.ReadConstructor();
            if (answerType == TlConstructors.ServerDHParamsFail)
                throw new MtprotoException("server rejected DH params (server_DH_params_fail)");
            if (answerType != TlConstructors.ServerDHParamsOk)
                throw new MtprotoException("unexpected reply 0x" + answerType.ToString("x8"));

            r.ReadRaw(16);                       // nonce
            byte[] serverNonceEcho = r.ReadRaw(16);
            byte[] encryptedAnswer = r.ReadBytes();

            if (!CryptoExtensions.ConstantTimeEquals(serverNonce, serverNonceEcho))
                throw new MtprotoException("server_DH_params_ok echoed a different server_nonce");
            _log("<- server_DH_params_ok  " + encryptedAnswer.Length + " bytes");

            // ---- 4. decrypt the DH parameters ------------------------------
            byte[] tmpKey, tmpIv;
            DeriveTempKeys(newNonce, serverNonce, out tmpKey, out tmpIv);

            byte[] answerWithHash = AesIge.Decrypt(encryptedAnswer, tmpKey, tmpIv);
            var ar = new TlReader(answerWithHash, 20);       // skip the leading SHA1

            ar.Expect(TlConstructors.ServerDHInnerData, "server_DH_inner_data");
            ar.ReadRaw(16);                                  // nonce
            ar.ReadRaw(16);                                  // server_nonce
            int g = ar.ReadInt();
            byte[] dhPrimeBytes = ar.ReadBytes();
            byte[] gaBytes = ar.ReadBytes();
            int serverTime = ar.ReadInt();

            // The hash covers only the message, not the padding after it.
            int answerLength = ar.Position - 20;
            byte[] expectedHash = answerWithHash.Slice(0, 20);
            byte[] actualHash = _crypto.Sha1(answerWithHash.Slice(20, answerLength));
            if (!CryptoExtensions.ConstantTimeEquals(expectedHash, actualHash))
                throw new MtprotoException("server_DH_inner_data failed its hash check");

            int timeOffset = serverTime - UnixNow();
            _log("<- server_DH_inner_data  g=" + g +
                 " prime=" + (dhPrimeBytes.Length * 8) + " bits" +
                 " time offset=" + timeOffset + "s");

            var dhPrime = BigInt.FromBytesBE(dhPrimeBytes);
            var ga = BigInt.FromBytesBE(gaBytes);

            DhValidation.ValidateParameters(g, dhPrime, ga, _crypto, _log);

            // ---- 5. our half of the exchange -------------------------------
            byte[] bBytes = _crypto.Random(256);
            var b = BigInt.FromBytesBE(bBytes);
            var gb = BigInt.ModPow(BigInt.FromUInt((uint)g), b, dhPrime);

            DhValidation.ValidatePublicValue(gb, dhPrime, "g_b");

            var clientInner = new TlWriter();
            clientInner.WriteConstructor(TlConstructors.ClientDHInnerData)
                       .WriteRaw(nonce)
                       .WriteRaw(serverNonce)
                       .WriteLong(0)                     // retry_id
                       .WriteBytes(gb.ToBytesBE(256));
            byte[] clientInnerData = clientInner.ToArray();

            byte[] clientHashed = CryptoExtensions.Concat(_crypto.Sha1(clientInnerData), clientInnerData);
            int padLen = (16 - (clientHashed.Length % 16)) % 16;
            byte[] clientPadded = CryptoExtensions.Concat(clientHashed, _crypto.Random(padLen));

            byte[] encryptedClient = AesIge.Encrypt(clientPadded, tmpKey, tmpIv);

            w = new TlWriter();
            w.WriteConstructor(TlConstructors.SetClientDHParams)
             .WriteRaw(nonce)
             .WriteRaw(serverNonce)
             .WriteBytes(encryptedClient);
            _log("-> set_client_DH_params");

            r = await ExchangeAsync(w.ToArray());

            // ---- 6. confirm both sides derived the same key ----------------
            uint genResult = r.ReadConstructor();
            r.ReadRaw(16);                                   // nonce
            r.ReadRaw(16);                                   // server_nonce
            byte[] newNonceHash = r.ReadRaw(16);

            if (genResult == TlConstructors.DhGenRetry)
                throw new MtprotoException("server asked to retry DH generation");
            if (genResult == TlConstructors.DhGenFail)
                throw new MtprotoException("server reported dh_gen_fail");
            if (genResult != TlConstructors.DhGenOk)
                throw new MtprotoException("unexpected DH result 0x" + genResult.ToString("x8"));

            byte[] authKey = BigInt.ModPow(ga, b, dhPrime).ToBytesBE(256);
            byte[] authKeySha = _crypto.Sha1(authKey);
            byte[] auxHash = authKeySha.Slice(0, 8);

            byte[] expectedNonceHash = _crypto.Sha1(
                CryptoExtensions.Concat(newNonce, new byte[] { 1 }, auxHash)).Slice(4, 16);

            if (!CryptoExtensions.ConstantTimeEquals(newNonceHash, expectedNonceHash))
                throw new MtprotoException("new_nonce_hash1 mismatch - the server derived a different key");

            long keyId = ToInt64LE(authKeySha, 12);
            long salt = ToInt64LE(newNonce, 0) ^ ToInt64LE(serverNonce, 0);

            _log("<- dh_gen_ok  auth_key_id=" + keyId.ToString("x16"));

            return new AuthKey
            {
                Key = authKey,
                KeyId = keyId,
                ServerSalt = salt,
                TimeOffset = timeOffset,
            };
        }

        /// <summary>
        /// tmp_aes_key and tmp_aes_iv, built from overlapping slices of three SHA1
        /// digests. The layout is arbitrary but exact - an off-by-one here decrypts
        /// to noise with no other symptom.
        /// </summary>
        private void DeriveTempKeys(byte[] newNonce, byte[] serverNonce, out byte[] key, out byte[] iv)
        {
            byte[] nsHash = _crypto.Sha1(CryptoExtensions.Concat(newNonce, serverNonce));
            byte[] snHash = _crypto.Sha1(CryptoExtensions.Concat(serverNonce, newNonce));
            byte[] nnHash = _crypto.Sha1(CryptoExtensions.Concat(newNonce, newNonce));

            key = CryptoExtensions.Concat(nsHash, snHash.Slice(0, 12));
            iv = CryptoExtensions.Concat(snHash.Slice(12, 8), nnHash, newNonce.Slice(0, 4));
        }

        /// <summary>
        /// Sends one unencrypted message and reads the reply. Handshake traffic uses
        /// auth_key_id = 0, since the key being negotiated does not exist yet.
        /// </summary>
        private async Task<TlReader> ExchangeAsync(byte[] body)
        {
            var packet = new TlWriter(body.Length + 20);
            packet.WriteLong(0);                       // auth_key_id: unencrypted
            packet.WriteLong(NextMessageId());
            packet.WriteInt(body.Length);
            packet.WriteRaw(body);

            await _framing.SendPacketAsync(packet.ToArray());

            byte[] response = await _framing.ReceivePacketAsync();
            var r = new TlReader(response);

            long keyId = r.ReadLong();
            if (keyId != 0)
                throw new MtprotoException("expected an unencrypted reply, got key id " + keyId);

            r.ReadLong();                              // message id
            int length = r.ReadInt();
            if (length < 0 || length > r.Remaining)
                throw new MtprotoException("declared body length " + length + " exceeds the packet");

            return new TlReader(response, r.Position);
        }

        /// <summary>
        /// Message ids are the unix time in the high 32 bits and a counter in the
        /// low ones, must be divisible by 4 for client messages, and must strictly
        /// increase within a session.
        /// </summary>
        private long NextMessageId()
        {
            long id = ((long)UnixNow() << 32) | ((long)_crypto.Random(4)[0] << 8) & 0xFFFC;
            if (id <= _lastMsgId) id = _lastMsgId + 4;
            _lastMsgId = id;
            return id;
        }

        private static int UnixNow()
        {
            return (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static ulong ToUInt64BE(byte[] b)
        {
            ulong v = 0;
            for (int i = 0; i < b.Length; i++) v = (v << 8) | b[i];
            return v;
        }

        private static byte[] FromUInt32BE(uint v)
        {
            return new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        }

        private static long ToInt64LE(byte[] b, int offset)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v |= (long)b[offset + i] << (8 * i);
            return v;
        }
    }
}
