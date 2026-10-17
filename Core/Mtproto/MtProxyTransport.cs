using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Crypto;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// A connection to Telegram through an MTProto proxy.
    ///
    /// Unlike SOCKS5 there is no tunnel to ask for: the phone connects to the proxy
    /// itself and names the datacenter it wants inside the opening 64 bytes, and
    /// from then on the whole stream is AES-256-CTR in both directions
    /// ("obfuscated2"), so nothing on the wire looks like MTProto. The disguise on
    /// top depends on the secret - see MtProxyMode.
    ///
    /// Wraps any other transport, so the phone's socket and the desktop's go
    /// inside it unaltered, and the harness tests the code the phone runs. The
    /// framing above (MtprotoFraming) learns through IFramingOptions that the
    /// transport tag has already gone in the opening bytes and whether to pad.
    ///
    /// Per core.telegram.org/mtproto/mtproto-transports and TDLib's
    /// ObfuscatedTransport (td/mtproto/TcpTransport.cpp). Ported from Symbigram.
    /// </summary>
    public sealed class MtProxyTransport : ITransport, IFramingOptions
    {
        private readonly ITransport _inner;
        private readonly MtProxySettings _proxy;
        private readonly ICrypto _crypto;
        private readonly int _timeOffset;

        private AesCtr _enc;
        private AesCtr _dec;
        private byte[] _header;          // the opening 64 bytes, until they go out with the first send
        private bool _firstRecord;       // FakeTLS: our ChangeCipherSpec has not gone yet
        private bool _ready;

        // FakeTLS: decrypted stream bytes not yet asked for, out of records already read.
        private readonly List<byte> _plain = new List<byte>();

        /// <param name="timeOffset">
        /// Seconds to add to the clock to get real time - the session's measured
        /// offset. FakeTLS stamps its hello with the time and the proxy refuses one
        /// more than a few minutes out, so a phone with a drifted clock would fail
        /// every such proxy without it.
        /// </param>
        public MtProxyTransport(ITransport inner, MtProxySettings proxy, ICrypto crypto,
                                int timeOffset = 0)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            if (proxy == null || !proxy.IsUsable)
                throw new ArgumentException("an MTProto proxy needs a host, a port and a secret");

            _inner = inner;
            _proxy = proxy;
            _crypto = crypto;
            _timeOffset = timeOffset;
        }

        /// <summary>The datacenter named in the last connection's opening bytes.</summary>
        public int DcId { get; private set; }

        public bool IsConnected { get { return _ready && _inner.IsConnected; } }

        // ---- IFramingOptions --------------------------------------------------

        public bool TagSent { get { return true; } }

        public bool Padded { get { return _proxy.Secret.IsPadded; } }

        /// <summary>0-15 random bytes, for padded framing.</summary>
        public byte[] NewPadding()
        {
            byte[] one = _crypto.Random(1);
            return _crypto.Random(one[0] % 16);
        }

        // ---- connecting -------------------------------------------------------

        /// <summary>
        /// Connects to the proxy and prepares the stream for
        /// <paramref name="host"/>, which is only used to say which datacenter
        /// is wanted - the proxy decides how to reach it.
        /// </summary>
        public async Task ConnectAsync(string host, int port)
        {
            _ready = false;
            _plain.Clear();
            DcId = TelegramServers.DcFor(host);

            try
            {
                await _inner.ConnectAsync(_proxy.Host, _proxy.Port);
            }
            catch (Socks5Exception)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MtprotoException("could not reach the proxy at " + _proxy.Host + ":" +
                                           _proxy.Port + " - " + ex.Message);
            }

            try
            {
                if (_proxy.Secret.Mode == MtProxyMode.FakeTls) await TlsHandshakeAsync();
                _header = OpeningBytes();
                _firstRecord = true;
            }
            catch (Exception)
            {
                // The socket is open and of no further use; holding on to it would
                // leave the proxy a dangling connection for every failed attempt.
                _inner.Dispose();
                throw;
            }

            _ready = true;
        }

        /// <summary>
        /// The fake TLS handshake: our hello, then the server's reply, read exactly
        /// as TDLib reads it - the ServerHello record, then a ChangeCipherSpec glued
        /// to one application-data record.
        /// </summary>
        private async Task TlsHandshakeAsync()
        {
            MtProxySecret secret = _proxy.Secret;
            int now = UnixNow() + _timeOffset;

            byte[] digest;
            byte[] hello = FakeTls.ClientHello(_crypto, secret.Key, secret.Domain, now, out digest);
            await _inner.SendAsync(hello);

            // A proxy that does not like the hello says nothing: it hangs up, or
            // passes the connection on to the real website it is posing as.
            byte[] head;
            try
            {
                head = await _inner.ReceiveExactAsync(5);
            }
            catch (Exception)
            {
                throw new MtprotoException(
                    "the proxy hung up on the TLS handshake - wrong secret, or the phone's clock is off");
            }

            if (head[0] != FakeTls.Handshake || head[1] != 0x03 || head[2] != 0x03)
                throw new MtprotoException(
                    "the proxy did not answer with TLS - wrong secret, or not a FakeTLS proxy");

            byte[] serverHello = await _inner.ReceiveExactAsync((head[3] << 8) | head[4]);

            byte[] rest = await _inner.ReceiveExactAsync(FakeTls.ServerRestPrefix.Length + 2);
            for (int i = 0; i < FakeTls.ServerRestPrefix.Length; i++)
                if (rest[i] != FakeTls.ServerRestPrefix[i])
                    throw new MtprotoException(
                        "the proxy's TLS answer is not the type MTProto proxies send - wrong secret?");

            int fillerLength = (rest[rest.Length - 2] << 8) | rest[rest.Length - 1];
            byte[] filler = fillerLength > 0 ? await _inner.ReceiveExactAsync(fillerLength) : new byte[0];

            byte[] response = Concat(head, serverHello, rest, filler);
            if (!FakeTls.VerifyServerHello(_crypto, secret.Key, digest, response))
                throw new MtprotoException(
                    "the proxy failed its TLS check - wrong secret, or the phone's clock is off");

            // The application-data record that ended the reply is filler: TDLib
            // discards it with the rest of the handshake, and so do we.
        }

        /// <summary>
        /// The opening 64 bytes (obfuscated2), and the keys for both directions.
        ///
        /// Random bytes whose middle doubles as the AES-CTR keys: 8..40 key and
        /// 40..56 IV for what we send, the same offsets of the reversed bytes for
        /// what the proxy sends. Each key is hashed together with the secret's 16
        /// key bytes, which is what ties the stream to this proxy. Then the framing
        /// tag at 56..60 and the datacenter at 60..62, and the whole thing
        /// encrypted - but only its last 8 bytes are sent encrypted, since the proxy
        /// needs the first 56 as they are to derive the same keys.
        /// </summary>
        private byte[] OpeningBytes()
        {
            byte[] init = null;
            for (int attempt = 0; attempt < 64 && init == null; attempt++)
            {
                byte[] candidate = _crypto.Random(64);
                if (!ForbiddenStart(candidate)) init = candidate;
            }
            if (init == null) throw new MtprotoException("could not make the proxy's opening bytes");

            var reversed = new byte[64];
            for (int i = 0; i < 64; i++) reversed[i] = init[63 - i];

            byte[] key = _proxy.Secret.Key;
            _enc = new AesCtr(_crypto.Sha256(Slice(init, 8, 32), key), Slice(init, 40, 16));
            _dec = new AesCtr(_crypto.Sha256(Slice(reversed, 8, 32), key), Slice(reversed, 40, 16));

            byte tag = Padded ? (byte)0xdd : (byte)0xee;
            for (int i = 56; i < 60; i++) init[i] = tag;

            short dc = (short)DcId;
            init[60] = (byte)dc;
            init[61] = (byte)(dc >> 8);
            init[62] = 0;
            init[63] = 0;

            byte[] encrypted = (byte[])init.Clone();
            _enc.Apply(encrypted);                     // also moves the keystream past these 64

            Buffer.BlockCopy(encrypted, 56, init, 56, 8);
            return init;
        }

        /// <summary>
        /// Opening bytes that would read as some other protocol to anyone watching -
        /// the other transports' tags, HTTP verbs - are thrown away and drawn again.
        /// From the transport documentation and TDLib.
        /// </summary>
        public static bool ForbiddenStart(byte[] b)
        {
            if (b[0] == 0xef) return true;                         // the abridged tag

            uint first = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            if (first == 0x44414548 || first == 0x54534f50 || first == 0x20544547 ||
                first == 0x4954504f || first == 0xdddddddd || first == 0xeeeeeeee ||
                first == 0x02010316)
                return true;                                       // HEAD POST GET OPTI, tags, TLS

            return b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0;
        }

        // ---- the stream -------------------------------------------------------

        public async Task SendAsync(byte[] data)
        {
            if (!_ready) throw new MtprotoException("not connected to the proxy");

            byte[] cipher = (byte[])data.Clone();
            _enc.Apply(cipher);

            // The opening bytes go out with the first packet, as TDLib sends them.
            byte[] payload = cipher;
            if (_header != null)
            {
                payload = Concat(_header, cipher);
                _header = null;
            }

            if (_proxy.Secret.Mode == MtProxyMode.FakeTls)
            {
                payload = FakeTls.Wrap(payload, _firstRecord);
                _firstRecord = false;
            }

            await _inner.SendAsync(payload);
        }

        public async Task<byte[]> ReceiveExactAsync(int count)
        {
            if (_proxy.Secret.Mode != MtProxyMode.FakeTls)
            {
                byte[] bytes = await _inner.ReceiveExactAsync(count);
                _dec.Apply(bytes);
                return bytes;
            }

            // FakeTLS: the keystream runs on underneath the records and knows
            // nothing of where one ends, so whole records are read, decrypted in
            // arrival order, and handed out in whatever pieces are asked for.
            while (_plain.Count < count)
            {
                byte[] head = await _inner.ReceiveExactAsync(5);
                int length = (head[3] << 8) | head[4];

                if (head[0] != FakeTls.ApplicationData || head[1] != 0x03 || head[2] != 0x03)
                    throw new MtprotoException("the proxy sent a malformed TLS record");

                byte[] body = length > 0 ? await _inner.ReceiveExactAsync(length) : new byte[0];
                _dec.Apply(body);
                _plain.AddRange(body);
            }

            byte[] result = new byte[count];
            _plain.CopyTo(0, result, 0, count);
            _plain.RemoveRange(0, count);
            return result;
        }

        public void Dispose()
        {
            _ready = false;
            _inner.Dispose();
        }

        // ---- helpers ----------------------------------------------------------

        private static int UnixNow()
        {
            return (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static byte[] Slice(byte[] data, int offset, int count)
        {
            var part = new byte[count];
            Buffer.BlockCopy(data, offset, part, 0, count);
            return part;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (byte[] p in parts) total += p.Length;

            var all = new byte[total];
            int at = 0;
            foreach (byte[] p in parts)
            {
                Buffer.BlockCopy(p, 0, all, at, p.Length);
                at += p.Length;
            }

            return all;
        }
    }
}
