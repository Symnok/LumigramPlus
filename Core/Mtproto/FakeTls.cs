using System;
using System.Collections.Generic;
using System.Text;
using Lumigram.Crypto;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// FakeTLS, the "ee" MTProto proxy mode: the connection is dressed up as an
    /// ordinary HTTPS session, so a censor that lets through only what looks like
    /// TLS lets it through.
    ///
    /// None of the TLS is real - no certificate, no key exchange, no AEAD. The proxy
    /// checks one HMAC to see that we know the secret, we check one to see that it
    /// does, and after that the obfuscated stream (MtProxyTransport) travels inside
    /// TLS application-data records.
    ///
    /// Byte layout from TDLib's td/mtproto/TlsInit.cpp and TcpTransport.cpp, which
    /// is what the proxies are written against:
    ///   - the ClientHello's 32-byte random sits at offset 11 and starts zeroed;
    ///   - digest = HMAC-SHA256(key, the whole hello), written back at offset 11,
    ///     with the unix time XORed into its last four bytes, little-endian;
    ///   - the server answers with a ServerHello record, then a ChangeCipherSpec
    ///     and one application-data record. Its own 32 bytes at offset 11 of that
    ///     whole reply are HMAC-SHA256(key, our digest + the reply with them zeroed).
    ///     The application-data record is filler and is thrown away;
    ///   - our first record afterwards is preceded by a ChangeCipherSpec of our own,
    ///     and our records are cut at 2878 bytes.
    ///
    /// Ported from Symbigram's faketls.cpp, with the last two points corrected to
    /// what TDLib actually does.
    /// </summary>
    public static class FakeTls
    {
        public const byte Handshake = 0x16;
        public const byte ChangeCipherSpec = 0x14;
        public const byte ApplicationData = 0x17;

        /// <summary>TDLib's record size - TcpTransport.h, MAX_TLS_PACKET_LENGTH.</summary>
        public const int MaxRecord = 2878;

        public const int DigestOffset = 11;
        public const int DigestSize = 32;

        /// <summary>Sent once, in front of the first application-data record.</summary>
        public static readonly byte[] ClientChangeCipherSpec = { 0x14, 0x03, 0x03, 0x00, 0x01, 0x01 };

        /// <summary>
        /// What follows the ServerHello record: a ChangeCipherSpec, then the header
        /// of an application-data record (its 2-byte length comes next).
        /// </summary>
        public static readonly byte[] ServerRestPrefix =
            { 0x14, 0x03, 0x03, 0x00, 0x01, 0x01, 0x17, 0x03, 0x03 };

        /// <summary>
        /// A TLS 1.3 ClientHello carrying the proxy digest.
        ///
        /// <paramref name="digest"/> receives the 32 bytes written at offset 11;
        /// checking the server's answer needs them. <paramref name="unixTime"/>
        /// should be real time - the phone's clock corrected by what the session
        /// has measured - because the proxy refuses a hello more than a few minutes
        /// off.
        /// </summary>
        public static byte[] ClientHello(ICrypto crypto, byte[] key16, string domain, int unixTime,
                                         out byte[] digest)
        {
            byte[] g = GreaseValues(crypto, 7);
            byte[] host = Encoding.UTF8.GetBytes(domain ?? "");

            // ---- extensions --------------------------------------------------------
            var exts = new Bytes();

            Ext(exts, (g[0] << 8) | g[1], new byte[0]);                         // GREASE

            var sni = new Bytes();                                              // server_name
            sni.U16(host.Length + 3).U8(0).U16(host.Length).Add(host);
            Ext(exts, 0x0000, sni.ToArray());

            Ext(exts, 0x0017, new byte[0]);                                     // extended_master_secret
            Ext(exts, 0xff01, new byte[] { 0 });                                // renegotiation_info

            var groups = new Bytes();                                           // supported_groups
            groups.U16(8).Add(g, 2, 2).U16(0x001d).U16(0x0017).U16(0x0018);
            Ext(exts, 0x000a, groups.ToArray());

            Ext(exts, 0x000b, new byte[] { 1, 0 });                             // ec_point_formats
            Ext(exts, 0x0023, new byte[0]);                                     // session_ticket

            var alpn = new Bytes();                                             // ALPN
            alpn.U16(12).U8(2).Add(Encoding.UTF8.GetBytes("h2"))
                .U8(8).Add(Encoding.UTF8.GetBytes("http/1.1"));
            Ext(exts, 0x0010, alpn.ToArray());

            Ext(exts, 0x0005, new byte[] { 1, 0, 0, 0, 0 });                    // status_request

            int[] algs = { 0x0403, 0x0804, 0x0401, 0x0503, 0x0805, 0x0501, 0x0806, 0x0601 };
            var sigalgs = new Bytes();                                          // signature_algorithms
            sigalgs.U16(algs.Length * 2);
            foreach (int a in algs) sigalgs.U16(a);
            Ext(exts, 0x000d, sigalgs.ToArray());

            Ext(exts, 0x0012, new byte[0]);                                     // signed_certificate_timestamp

            // key_share: 32 random bytes where an x25519 public key would be. Nobody
            // does a key exchange with it, and a real key looks exactly like this.
            var shares = new Bytes();
            shares.Add(g, 4, 2).U16(1).U8(0).U16(0x001d).U16(32).Add(crypto.Random(32));
            var keyShare = new Bytes();
            keyShare.U16(shares.Length).Add(shares.ToArray());
            Ext(exts, 0x0033, keyShare.ToArray());

            Ext(exts, 0x002d, new byte[] { 1, 1 });                             // psk_key_exchange_modes

            var versions = new Bytes();                                         // supported_versions
            versions.U8(6).Add(g, 6, 2).U16(0x0304).U16(0x0303);
            Ext(exts, 0x002b, versions.ToArray());

            Ext(exts, 0x001b, new byte[] { 2, 0, 2 });                          // compress_certificate

            Ext(exts, (g[8] << 8) | g[9], new byte[] { 0 });                    // GREASE

            // ---- body --------------------------------------------------------------
            var body = new Bytes();
            body.U16(0x0303);                                                   // legacy_version
            body.Add(new byte[DigestSize]);                                     // random: becomes the digest
            body.U8(32).Add(crypto.Random(32));                                 // legacy_session_id

            int[] ciphers = { 0x1301, 0x1302, 0x1303, 0xc02b, 0xc02f, 0xc02c, 0xc030,
                              0xcca9, 0xcca8, 0xc013, 0xc014, 0x009c, 0x009d, 0x002f, 0x0035 };
            var suites = new Bytes();
            suites.Add(g, 10, 2);
            foreach (int c in ciphers) suites.U16(c);
            body.U16(suites.Length).Add(suites.ToArray());

            body.U8(1).U8(0);                                                   // compression: null

            // Padded to 517 bytes in all, the size a current browser's hello is; a
            // distinctive length would undo the point of the disguise.
            int framed = 5 + 4 + body.Length + 2 + exts.Length;
            int padding = Math.Max(0, 517 - framed - 4);
            Ext(exts, 0x0015, new byte[padding]);

            body.U16(exts.Length).Add(exts.ToArray());

            // ---- record ------------------------------------------------------------
            var hello = new Bytes();
            hello.U8(Handshake).U16(0x0301)                                     // the record says TLS 1.0, as browsers' do
                 .U16(body.Length + 4)
                 .U8(0x01).U8(0).U16(body.Length)                               // client_hello, 24-bit length
                 .Add(body.ToArray());

            byte[] result = hello.ToArray();

            // ---- the digest --------------------------------------------------------
            byte[] mac = HmacSha256.Compute(crypto, key16, result);
            for (int i = 0; i < 4; i++) mac[28 + i] ^= (byte)(unixTime >> (8 * i));
            Buffer.BlockCopy(mac, 0, result, DigestOffset, DigestSize);

            digest = mac;
            return result;
        }

        /// <summary>
        /// True when the proxy proved it knows the secret.
        ///
        /// <paramref name="response"/> is the whole reply as received: the
        /// ServerHello record, the ChangeCipherSpec and the application-data record.
        /// </summary>
        public static bool VerifyServerHello(ICrypto crypto, byte[] key16, byte[] clientDigest,
                                             byte[] response)
        {
            if (response == null || response.Length < DigestOffset + DigestSize) return false;

            var theirs = new byte[DigestSize];
            Buffer.BlockCopy(response, DigestOffset, theirs, 0, DigestSize);

            var signed = new byte[clientDigest.Length + response.Length];
            Buffer.BlockCopy(clientDigest, 0, signed, 0, clientDigest.Length);
            Buffer.BlockCopy(response, 0, signed, clientDigest.Length, response.Length);
            for (int i = 0; i < DigestSize; i++) signed[clientDigest.Length + DigestOffset + i] = 0;

            byte[] mine = HmacSha256.Compute(crypto, key16, signed);
            return HmacSha256.SameBytes(mine, theirs);
        }

        /// <summary>
        /// Cuts a payload into application-data records. The ChangeCipherSpec goes
        /// in front of the very first one sent on the connection, as TDLib does.
        /// </summary>
        public static byte[] Wrap(byte[] payload, bool first)
        {
            var output = new Bytes();
            if (first) output.Add(ClientChangeCipherSpec);

            int offset = 0;
            do
            {
                int take = Math.Min(MaxRecord, payload.Length - offset);
                output.U8(ApplicationData).U16(0x0303).U16(take).Add(payload, offset, take);
                offset += take;
            }
            while (offset < payload.Length);

            return output.ToArray();
        }

        /// <summary>
        /// GREASE values: random codepoints of the form 0x?A?A, which real browsers
        /// sprinkle through a hello so middleboxes learn to ignore unknown ones. A
        /// hello without them stands out.
        /// </summary>
        private static byte[] GreaseValues(ICrypto crypto, int count)
        {
            byte[] raw = crypto.Random(count);
            var output = new byte[count * 2];

            for (int i = 0; i < count; i++)
            {
                byte v = (byte)((raw[i] & 0xf0) | 0x0a);
                output[i * 2] = v;
                output[i * 2 + 1] = v;
            }

            // The two GREASE extensions must differ, or the hello is malformed.
            if (output[0] == output[8])
            {
                output[8] ^= 0x10;
                output[9] = output[8];
            }

            return output;
        }

        private static void Ext(Bytes b, int type, byte[] body)
        {
            b.U16(type).U16(body.Length).Add(body);
        }

        /// <summary>A small growable byte buffer with big-endian writers.</summary>
        private sealed class Bytes
        {
            private readonly List<byte> _b = new List<byte>(600);

            public int Length { get { return _b.Count; } }

            public Bytes U8(int v) { _b.Add((byte)v); return this; }

            public Bytes U16(int v) { _b.Add((byte)(v >> 8)); _b.Add((byte)v); return this; }

            public Bytes Add(byte[] data) { _b.AddRange(data); return this; }

            public Bytes Add(byte[] data, int offset, int count)
            {
                for (int i = 0; i < count; i++) _b.Add(data[offset + i]);
                return this;
            }

            public byte[] ToArray() { return _b.ToArray(); }
        }
    }
}
