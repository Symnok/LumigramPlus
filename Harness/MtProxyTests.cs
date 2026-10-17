using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// MTProto proxies, offline: the ciphers against published vectors and .NET's
    /// own implementations, secrets and links as proxy lists publish them, padded
    /// framing, the FakeTLS hello and reply, and the opening bytes as a proxy would
    /// decrypt them.
    ///
    /// The live half - a real handshake through a relaying proxy written separately
    /// in Python - is the "mtproxy" command.
    /// </summary>
    internal static class MtProxyTests
    {
        private static int _checks;
        private static int _failures;

        public static bool RunAll()
        {
            _checks = 0;
            _failures = 0;

            var crypto = new DesktopCrypto();

            Ctr();
            Hmac(crypto);
            Secrets();
            ProxyLinks();
            HiddenLinks();
            Padding();
            Hello(crypto);
            OpeningBytes(crypto);

            Eq("dc for DC2", 2, TelegramServers.DcFor(TelegramServers.ProductionDc2Host));
            Eq("dc for DC4", 4, TelegramServers.DcFor("149.154.167.91"));
            Eq("dc for the test DC", 10002, TelegramServers.DcFor(TelegramServers.TestDc2Host));

            Console.WriteLine("  mtproto proxy: {0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        // ---- AES-CTR ----------------------------------------------------------

        private static void Ctr()
        {
            // NIST SP 800-38A, F.5.5 CTR-AES256.Encrypt.
            byte[] key = Hex("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
            byte[] iv = Hex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
            byte[] plain = Hex("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51" +
                               "30c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");
            string cipher = "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5" +
                            "2b0930daa23de94ce87017ba2d84988ddfc9c58db67aada613c2dd08457941a6";

            byte[] whole = (byte[])plain.Clone();
            new AesCtr(key, iv).Apply(whole);
            Eq("ctr: NIST F.5.5", cipher, ToHex(whole));

            // The same in awkward pieces: the keystream must carry across calls.
            byte[] pieces = (byte[])plain.Clone();
            var ctr = new AesCtr(key, iv);
            int at = 0;
            foreach (int n in new[] { 1, 15, 3, 29, 0, 16 })
            {
                ctr.Apply(pieces, at, n);
                at += n;
            }
            ctr.Apply(pieces, at, pieces.Length - at);
            Eq("ctr: in pieces", cipher, ToHex(pieces));

            // A counter that wraps all 128 bits, against .NET's AES as the reference.
            byte[] wrapIv = Hex("fffffffffffffffffffffffffffffffe");
            byte[] data = new byte[64];
            new AesCtr(key, wrapIv).Apply(data);
            Eq("ctr: 128-bit carry", ToHex(ReferenceCtr(key, wrapIv, 64)), ToHex(data));
        }

        /// <summary>CTR keystream from .NET's AES in ECB, counter incremented big-endian.</summary>
        private static byte[] ReferenceCtr(byte[] key, byte[] iv, int length)
        {
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.Key = key;

                var counter = (byte[])iv.Clone();
                var output = new byte[length];
                using (ICryptoTransform e = aes.CreateEncryptor())
                {
                    for (int off = 0; off < length; off += 16)
                    {
                        byte[] block = e.TransformFinalBlock(counter, 0, 16);
                        Buffer.BlockCopy(block, 0, output, off, Math.Min(16, length - off));
                        for (int i = 15; i >= 0; i--) if (++counter[i] != 0) break;
                    }
                }

                return output;
            }
        }

        // ---- HMAC-SHA256 ------------------------------------------------------

        private static void Hmac(ICrypto crypto)
        {
            // RFC 4231 test cases 1, 2 and 6 (a key longer than a block).
            Eq("hmac: RFC 4231 #1",
               "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7",
               ToHex(HmacSha256.Compute(crypto, Repeat(0x0b, 20), Encoding.ASCII.GetBytes("Hi There"))));
            Eq("hmac: RFC 4231 #2",
               "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843",
               ToHex(HmacSha256.Compute(crypto, Encoding.ASCII.GetBytes("Jefe"),
                                        Encoding.ASCII.GetBytes("what do ya want for nothing?"))));
            Eq("hmac: RFC 4231 #6",
               "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54",
               ToHex(HmacSha256.Compute(crypto, Repeat(0xaa, 131),
                     Encoding.ASCII.GetBytes("Test Using Larger Than Block-Size Key - Hash Key First"))));

            var rng = new Random(7);
            for (int i = 0; i < 20; i++)
            {
                byte[] key = new byte[rng.Next(1, 100)];
                byte[] msg = new byte[rng.Next(0, 700)];
                rng.NextBytes(key);
                rng.NextBytes(msg);

                using (var reference = new HMACSHA256(key))
                    Eq("hmac: vs .NET #" + i, ToHex(reference.ComputeHash(msg)),
                       ToHex(HmacSha256.Compute(crypto, key, msg)));
            }
        }

        // ---- secrets and links ------------------------------------------------

        private const string Key16 = "00112233445566778899aabbccddeeff";

        private static void Secrets()
        {
            string error;

            MtProxySecret plain = MtProxySecret.Parse(Key16, out error);
            Eq("secret: plain hex", MtProxyMode.Plain, plain == null ? (object)error : plain.Mode);

            MtProxySecret dd = MtProxySecret.Parse("dd" + Key16.ToUpperInvariant(), out error);
            Eq("secret: dd hex", MtProxyMode.Padded, dd == null ? (object)error : dd.Mode);
            Eq("secret: dd key", Key16, dd == null ? null : ToHex(dd.Key));

            string eeHex = "ee" + Key16 + ToHex(Encoding.ASCII.GetBytes("www.example.com"));
            MtProxySecret ee = MtProxySecret.Parse(eeHex, out error);
            Eq("secret: ee hex", MtProxyMode.FakeTls, ee == null ? (object)error : ee.Mode);
            Eq("secret: ee domain", "www.example.com", ee == null ? null : ee.Domain);
            Eq("secret: ee key excludes mode byte and domain", Key16, ee == null ? null : ToHex(ee.Key));

            // base64url of the same ee secret - how most FakeTLS proxies are shared.
            // A real ee secret's base64 starts with "7"; one starting "ee" is not one.
            string ee64 = Convert.ToBase64String(Hex(eeHex)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Eq("secret: ee base64 starts with 7", '7', ee64[0]);
            MtProxySecret ee2 = MtProxySecret.Parse(ee64, out error);
            Eq("secret: ee base64url", "www.example.com", ee2 == null ? error : ee2.Domain);

            // The one from the Symbigram session: base64url for 16 bytes, plain.
            MtProxySecret looksEe = MtProxySecret.Parse("eeNEgYdJvXrFGRMCIMJdCQ", out error);
            Eq("secret: 'ee...' base64 is a plain secret", MtProxyMode.Plain,
               looksEe == null ? (object)error : looksEe.Mode);

            Eq("secret: spaces ignored", MtProxyMode.Plain,
               Mode(MtProxySecret.Parse(" 0011 2233 4455 6677 8899 aabb ccdd eeff ", out error)));

            Eq("secret: empty", null, MtProxySecret.Parse("", out error));
            Eq("secret: 15 bytes", null, MtProxySecret.Parse(Key16.Substring(2), out error));
            Eq("secret: 15 bytes says length", true, error != null && error.Contains("15 bytes"));
            Eq("secret: 17 bytes not dd", null, MtProxySecret.Parse("ab" + Key16, out error));
            Eq("secret: ee without domain", null, MtProxySecret.Parse("ee" + Key16, out error));
            Eq("secret: garbage", null, MtProxySecret.Parse("not a secret!", out error));
        }

        private static object Mode(MtProxySecret s) { return s == null ? (object)null : s.Mode; }

        private static void ProxyLinks()
        {
            string error;

            ProxyLink a = ProxyLink.Parse(
                "https://t.me/proxy?server=hadaf.example.co.uk&port=2053&secret=eeNEgYdJvXrFGRMCIMJdCQ", out error);
            Eq("link: t.me parses", true, a != null);
            if (a != null)
            {
                Eq("link: server", "hadaf.example.co.uk", a.Server);
                Eq("link: port", 2053, a.Port);
                Eq("link: mtproto", true, a.IsMtproto);
                Eq("link: secret text kept", "eeNEgYdJvXrFGRMCIMJdCQ", a.SecretText);
            }

            ProxyLink b = ProxyLink.Parse("tg://proxy?server=1.2.3.4&port=443&secret=dd" + Key16, out error);
            Eq("link: tg://proxy dd", MtProxyMode.Padded, b == null ? (object)error : b.Secret.Mode);

            ProxyLink c = ProxyLink.Parse("https://www.t.me/proxy?server=x.org&amp;port=8443&amp;secret=" + Key16, out error);
            Eq("link: www and &amp;", 8443, c == null ? (object)error : c.Port);

            ProxyLink d = ProxyLink.Parse("https://t.me/socks?server=s.org&port=1080&user=bob&pass=p%40ss", out error);
            Eq("link: socks", false, d == null ? (object)error : d.IsMtproto);
            Eq("link: socks password decoded", "p@ss", d == null ? null : d.Password);

            // A base64 secret with a + in it: the + is a digit, not a space.
            byte[] raw = Hex("ee" + Key16 + ToHex(Encoding.ASCII.GetBytes("a.b")));
            raw[1] = 0xfb;                       // makes a "+" or "/" likely in base64
            string std = Convert.ToBase64String(raw);
            ProxyLink e = ProxyLink.Parse("tg://proxy?server=h&port=1&secret=" + std, out error);
            Eq("link: standard base64 secret", MtProxyMode.FakeTls, e == null ? (object)error : e.Secret.Mode);

            Eq("link: missing port", null, ProxyLink.Parse("https://t.me/proxy?server=h&secret=" + Key16, out error));
            Eq("link: bad secret", null, ProxyLink.Parse("https://t.me/proxy?server=h&port=1&secret=zz", out error));
            Eq("link: bad secret says why", true, error != null && error.Contains("secret"));

            Eq("is proxy: t.me/proxy", true, ProxyLink.IsProxyLink("https://t.me/proxy?server=a&port=1"));
            Eq("is proxy: telegram.me/socks", true, ProxyLink.IsProxyLink("telegram.me/socks?server=a&port=1"));
            Eq("is proxy: tg://proxy", true, ProxyLink.IsProxyLink("tg://proxy?server=a&port=1"));
            Eq("is proxy: a username starting proxy", false, ProxyLink.IsProxyLink("https://t.me/proxyfan"));
            Eq("is proxy: a channel post", false, ProxyLink.IsProxyLink("https://t.me/somechannel/12"));
            Eq("is proxy: elsewhere", false, ProxyLink.IsProxyLink("https://example.com/proxy?server=a"));

            // Not treated as a username, which TelegramLinks has always refused.
            Eq("telegram link: proxy is not a peer", null,
               TelegramLinks.Parse("https://t.me/proxy?server=a&port=1&secret=" + Key16));
        }

        private static void HiddenLinks()
        {
            // Farsi words with a link behind them, after an ampersand - offsets are
            // UTF-16 positions in the text as it arrived.
            string text = "A & B \u0627\u062a\u0635\u0627\u0644 now, see https://example.com";
            var hidden = new List<TextLink>
            {
                new TextLink { Offset = 6, Length = 5, Url = "https://t.me/proxy?server=h&port=1&secret=" + Key16 },
                new TextLink { Offset = 500, Length = 3, Url = "https://bad" },        // out of range
            };

            List<TextPart> parts = Links.Split(text, hidden);
            var links = new List<string>();
            foreach (TextPart p in parts) if (p.IsLink) links.Add(p.Text + "=" + p.Url);

            Eq("hidden: two links", 2, links.Count);
            Eq("hidden: the Farsi word carries the proxy link",
               "\u0627\u062a\u0635\u0627\u0644=https://t.me/proxy?server=h&port=1&secret=" + Key16,
               links.Count > 0 ? links[0] : null);
            Eq("hidden: visible link still found", "https://example.com=https://example.com",
               links.Count > 1 ? links[1] : null);

            var joined = new StringBuilder();
            foreach (TextPart p in parts) joined.Append(p.Text);
            Eq("hidden: text unchanged", text, joined.ToString());

            List<TextPart> tg = Links.Split("join tg://proxy?server=a&port=1&secret=" + Key16 + " now");
            Eq("text: tg://proxy is a link", true, tg.Count == 3 && tg[1].IsLink &&
               tg[1].Url.StartsWith("tg://proxy?"));

            // Read off a message's entity vector exactly as the server sends it.
            var w = new TlWriter(128);
            w.WriteConstructor(TlConstructors.MessageEntityTextUrl).WriteInt(6).WriteInt(5).WriteString("tg://proxy?x");
            TlObject entity = TlSchema.ReadObject(new TlReader(w.ToArray()));
            Eq("entity: ctor known to the schema", TlConstructors.MessageEntityTextUrl, entity.Ctor);
            Eq("entity: url", "tg://proxy?x", entity.Has("url") ? entity.Str("url") : null);
        }

        // ---- padded framing ---------------------------------------------------

        private static void Padding()
        {
            // Unencrypted (auth_key_id 0): its own length says where it ends.
            var plain = new byte[20 + 40 + 13];
            plain[16] = 40;
            Eq("unpad: unencrypted", 60, MtprotoFraming.UnpaddedLength(plain));

            // Encrypted: 24 + whole blocks.
            var enc = new byte[24 + 32 + 13];
            enc[0] = 1;
            Eq("unpad: encrypted", 56, MtprotoFraming.UnpaddedLength(enc));

            var exact = new byte[24 + 48];
            exact[3] = 9;
            Eq("unpad: encrypted, no padding", 72, MtprotoFraming.UnpaddedLength(exact));

            // A transport error code, padded.
            var err = new byte[4 + 7];
            err[0] = 0x6c; err[1] = 0xfe; err[2] = 0xff; err[3] = 0xff;
            Eq("unpad: error code", 4, MtprotoFraming.UnpaddedLength(err));

            // Through the framing: sizes stop being multiples of four, and come back exact.
            var wire = new LoopWire();
            var framing = new MtprotoFraming(wire);
            bool sawOdd = false;
            for (int i = 0; i < 40; i++)
            {
                var payload = new byte[24 + 16 * (1 + i % 3)];
                payload[0] = (byte)(i + 1);
                framing.SendPacketAsync(payload).GetAwaiter().GetResult();
                if (wire.LastFrameLength % 4 != 0) sawOdd = true;

                byte[] back = framing.ReceivePacketAsync().GetAwaiter().GetResult();
                if (back.Length != payload.Length || back[0] != payload[0])
                {
                    Fail("padded round trip " + i + ": " + back.Length + " bytes back for " + payload.Length);
                    break;
                }
            }
            Eq("padded: some frames not a multiple of 4", true, sawOdd);
            Eq("padded: no plain tag sent", false, wire.SawTag);
        }

        /// <summary>A transport whose sends come back as receives, with padding switched on.</summary>
        private sealed class LoopWire : ITransport, IFramingOptions
        {
            private readonly Queue<byte> _bytes = new Queue<byte>();
            private readonly Random _rng = new Random(3);

            public int LastFrameLength;
            public bool SawTag;

            public bool TagSent { get { return true; } }
            public bool Padded { get { return true; } }
            public byte[] NewPadding() { return new byte[_rng.Next(16)]; }

            public bool IsConnected { get { return true; } }
            public Task ConnectAsync(string host, int port) { return Task.FromResult(0); }

            public Task SendAsync(byte[] data)
            {
                if (data.Length >= 4 && data[0] == 0xee && data[1] == 0xee) SawTag = true;
                LastFrameLength = data[0] | (data[1] << 8);
                foreach (byte b in data) _bytes.Enqueue(b);
                return Task.FromResult(0);
            }

            public Task<byte[]> ReceiveExactAsync(int count)
            {
                var b = new byte[count];
                for (int i = 0; i < count; i++) b[i] = _bytes.Dequeue();
                return Task.FromResult(b);
            }

            public void Dispose() { }
        }

        // ---- FakeTLS ----------------------------------------------------------

        private static void Hello(ICrypto crypto)
        {
            byte[] key = Hex(Key16);
            int now = 1790000000;
            byte[] digest;
            byte[] hello = FakeTls.ClientHello(crypto, key, "www.example.com", now, out digest);

            Eq("hello: 517 bytes", 517, hello.Length);
            Eq("hello: handshake record, TLS 1.0 on the record", "160301", ToHex(Slice(hello, 0, 3)));
            Eq("hello: record length", hello.Length - 5, (hello[3] << 8) | hello[4]);
            Eq("hello: client_hello", 1, (int)hello[5]);
            Eq("hello: SNI present", true, Contains(hello, Encoding.ASCII.GetBytes("www.example.com")));

            // The digest as a proxy checks it, with .NET's HMAC.
            byte[] zeroed = (byte[])hello.Clone();
            for (int i = 0; i < 32; i++) zeroed[11 + i] = 0;
            byte[] expected;
            using (var h = new HMACSHA256(key)) expected = h.ComputeHash(zeroed);
            int stamp = 0;
            for (int i = 0; i < 4; i++) stamp |= (hello[11 + 28 + i] ^ expected[28 + i]) << (8 * i);
            Eq("hello: digest bytes 0..28", ToHex(Slice(expected, 0, 28)), ToHex(Slice(hello, 11, 28)));
            Eq("hello: timestamp recovered", now, stamp);
            Eq("hello: digest returned", ToHex(Slice(hello, 11, 32)), ToHex(digest));

            // Every extension length adds up - a hello a parser would choke on is
            // one a censor notices.
            Eq("hello: extensions parse to the end", true, ExtensionsWellFormed(hello));

            // The server's reply, built the way a proxy builds it.
            byte[] hs = new byte[] { 0x02, 0, 0, 38, 0x03, 0x03 };
            var reply = new List<byte>();
            reply.AddRange(new byte[] { 0x16, 0x03, 0x03, 0, (byte)(hs.Length + 32 + 2) });
            reply.AddRange(hs);
            reply.AddRange(new byte[32]);
            reply.AddRange(new byte[] { 0x13, 0x01 });
            reply.AddRange(FakeTls.ServerRestPrefix);
            reply.AddRange(new byte[] { 0x00, 0x05, 1, 2, 3, 4, 5 });
            byte[] r = reply.ToArray();
            byte[] signed = new byte[32 + r.Length];
            Buffer.BlockCopy(digest, 0, signed, 0, 32);
            Buffer.BlockCopy(r, 0, signed, 32, r.Length);
            byte[] serverDigest;
            using (var h = new HMACSHA256(key)) serverDigest = h.ComputeHash(signed);
            Buffer.BlockCopy(serverDigest, 0, r, 11, 32);

            Eq("server hello: verifies", true, FakeTls.VerifyServerHello(crypto, key, digest, r));
            r[r.Length - 1] ^= 1;
            Eq("server hello: tampered filler fails", false, FakeTls.VerifyServerHello(crypto, key, digest, r));
            r[r.Length - 1] ^= 1;
            Eq("server hello: wrong key fails", false,
               FakeTls.VerifyServerHello(crypto, Hex("ff" + Key16.Substring(2)), digest, r));

            // Records: cut at 2878, ChangeCipherSpec only in front of the first.
            byte[] wrapped = FakeTls.Wrap(new byte[6000], true);
            Eq("wrap: CCS first", "140303000101", ToHex(Slice(wrapped, 0, 6)));
            Eq("wrap: first record 2878", 2878, (wrapped[9] << 8) | wrapped[10]);
            Eq("wrap: total", 6 + 3 * 5 + 6000, wrapped.Length);
            Eq("wrap: later sends have no CCS", 0x17, (int)FakeTls.Wrap(new byte[10], false)[0]);
        }

        private static bool ExtensionsWellFormed(byte[] hello)
        {
            int p = 5 + 4 + 2 + 32;                         // record, handshake header, version, random
            p += 1 + hello[p];                              // session id
            p += 2 + ((hello[p] << 8) | hello[p + 1]);      // cipher suites
            p += 1 + hello[p];                              // compression
            int extEnd = p + 2 + ((hello[p] << 8) | hello[p + 1]);
            p += 2;
            while (p < extEnd)
            {
                if (p + 4 > hello.Length) return false;
                p += 4 + ((hello[p + 2] << 8) | hello[p + 3]);
            }
            return p == extEnd && extEnd == hello.Length;
        }

        // ---- the opening bytes ------------------------------------------------

        private static void OpeningBytes(ICrypto crypto)
        {
            foreach (string secretHex in new[] { Key16, "dd" + Key16 })
            {
                string error;
                var proxy = new MtProxySettings
                {
                    Host = "proxy.example",
                    Port = 443,
                    Secret = MtProxySecret.Parse(secretHex, out error),
                };

                var wire = new CaptureWire();
                var t = new MtProxyTransport(wire, proxy, crypto);
                t.ConnectAsync("149.154.167.91", 443).GetAwaiter().GetResult();      // DC 4
                byte[] packet = Encoding.ASCII.GetBytes("twelve bytes");
                t.SendAsync(packet).GetAwaiter().GetResult();

                string label = "opening (" + (secretHex.Length == 32 ? "plain" : "dd") + "): ";
                Eq(label + "dialled the proxy", "proxy.example:443", wire.Dialled);
                Eq(label + "one send: 64 + packet", 64 + packet.Length, wire.Sent.Count == 1 ? wire.Sent[0].Length : -1);
                if (wire.Sent.Count != 1) continue;

                // Decrypt as the proxy would, with .NET's AES.
                byte[] sent = wire.Sent[0];
                byte[] init = Slice(sent, 0, 64);
                byte[] k = crypto.Sha256(Concat(Slice(init, 8, 32), Hex(Key16)));
                byte[] stream = ReferenceCtr(k, Slice(init, 40, 16), sent.Length);
                for (int i = 0; i < sent.Length; i++) stream[i] ^= sent[i];

                string tag = secretHex.Length == 32 ? "eeeeeeee" : "dddddddd";
                Eq(label + "tag", tag, ToHex(Slice(stream, 56, 4)));
                Eq(label + "dc", 4, stream[60] | (stream[61] << 8));
                Eq(label + "packet follows", "twelve bytes", Encoding.ASCII.GetString(stream, 64, 12));
                Eq(label + "start is not a forbidden one", false, MtProxyTransport.ForbiddenStart(init));
                Eq(label + "padded as the secret says", secretHex.Length != 32, t.Padded);
            }
        }

        private sealed class CaptureWire : ITransport
        {
            public readonly List<byte[]> Sent = new List<byte[]>();
            public string Dialled;

            public bool IsConnected { get { return Dialled != null; } }

            public Task ConnectAsync(string host, int port)
            {
                Dialled = host + ":" + port;
                return Task.FromResult(0);
            }

            public Task SendAsync(byte[] data)
            {
                Sent.Add((byte[])data.Clone());
                return Task.FromResult(0);
            }

            public Task<byte[]> ReceiveExactAsync(int count)
            {
                throw new System.IO.EndOfStreamException("nothing scripted");
            }

            public void Dispose() { }
        }

        // ---- helpers ----------------------------------------------------------

        private static byte[] Hex(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
            return b;
        }

        private static string ToHex(byte[] b)
        {
            if (b == null) return null;
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static byte[] Repeat(byte value, int count)
        {
            var b = new byte[count];
            for (int i = 0; i < count; i++) b[i] = value;
            return b;
        }

        private static byte[] Slice(byte[] b, int offset, int count)
        {
            var part = new byte[count];
            Buffer.BlockCopy(b, offset, part, 0, count);
            return part;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var all = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, all, 0, a.Length);
            Buffer.BlockCopy(b, 0, all, a.Length, b.Length);
            return all;
        }

        private static bool Contains(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        private static void Eq(string what, object expected, object actual)
        {
            _checks++;
            if (!Equals(expected, actual))
                Fail(what + ": got " + (actual ?? "(null)") + ", expected " + (expected ?? "(null)"));
        }

        private static void Fail(string message)
        {
            _failures++;
            Console.WriteLine("  FAIL " + message);
        }
    }
}
