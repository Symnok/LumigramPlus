using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Mtproto;

namespace Lumigram.Harness
{
    /// <summary>
    /// The SOCKS5 handshake, against a scripted proxy.
    ///
    /// Every byte the app sends is checked, and every way a proxy can say no. The
    /// failure worth most attention is the quiet one: a handshake that leaves part
    /// of the proxy's reply unread hands it to MTProto as the start of the first
    /// packet, and the error that follows names MTProto, not the proxy.
    /// </summary>
    internal static class Socks5Tests
    {
        private static int _checks;
        private static int _failures;

        private const string Dc = "149.154.167.51";

        public static bool RunAll()
        {
            _checks = 0;
            _failures = 0;

            var open = new ProxySettings { Host = "10.0.0.5", Port = 1080 };
            var login = new ProxySettings { Host = "proxy.example", Port = 9050,
                                            User = "u", Password = "pw" };

            // A proxy that takes no login.
            {
                var wire = new FakeWire(new byte[] { 5, 0 },
                                        new byte[] { 5, 0, 0, 1, 1, 2, 3, 4, 0x01, 0xBB });
                Run(new Socks5Transport(wire, open), Dc, 443, null);

                Eq("open: dialled the proxy", "10.0.0.5:1080", wire.Dialled);
                Same("open: greeting offers no-login only", new byte[] { 5, 1, 0 }, wire.Sent[0]);
                Same("open: connect to the DC by IPv4",
                     new byte[] { 5, 1, 0, 1, 149, 154, 167, 51, 0x01, 0xBB }, wire.Sent[1]);
                Eq("open: two messages", 2, wire.Sent.Count);
            }

            // A proxy with a login: both methods offered, then the RFC 1929 login.
            {
                var wire = new FakeWire(new byte[] { 5, 2 }, new byte[] { 1, 0 },
                                        new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                Run(new Socks5Transport(wire, login), Dc, 443, null);

                Eq("login: dialled the proxy", "proxy.example:9050", wire.Dialled);
                Same("login: greeting offers both", new byte[] { 5, 2, 0, 2 }, wire.Sent[0]);
                Same("login: credentials", new byte[] { 1, 1, (byte)'u', 2, (byte)'p', (byte)'w' },
                     wire.Sent[1]);
            }

            // A login offered but not asked for: the proxy picked no-login.
            {
                var wire = new FakeWire(new byte[] { 5, 0 },
                                        new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                Run(new Socks5Transport(wire, login), Dc, 443, null);
                Eq("login not needed: no credentials sent", 2, wire.Sent.Count);
            }

            // Refusals, each said in words rather than as a code.
            Refused("wrong password", login, "refused the login",
                    new byte[] { 5, 2 }, new byte[] { 1, 1 });
            Refused("login wanted, none set", open, "wants a username and password",
                    new byte[] { 5, 2 });
            Refused("no acceptable method", open, "wants a username and password",
                    new byte[] { 5, 0xFF });
            Refused("not SOCKS5", open, "not a SOCKS5 proxy",
                    new byte[] { 4, 0x5A });
            Refused("server refused", open, "refused the proxy's connection",
                    new byte[] { 5, 0 }, new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 });
            Refused("not allowed", open, "do not allow",
                    new byte[] { 5, 0 }, new byte[] { 5, 2, 0, 1, 0, 0, 0, 0, 0, 0 });
            Refused("garbled reply", open, "malformed reply",
                    new byte[] { 5, 0 }, new byte[] { 5, 0, 0, 9 });

            // A reply carrying a name rather than an address is longer, and must be
            // read to its last byte: what follows is MTProto's.
            {
                var wire = new FakeWire(new byte[] { 5, 0 },
                                        new byte[] { 5, 0, 0, 3, 4, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 0x01, 0xBB },
                                        new byte[] { 0xDE, 0xAD });
                var tunnel = new Socks5Transport(wire, open);
                Run(tunnel, Dc, 443, null);

                byte[] first = tunnel.ReceiveExactAsync(2).GetAwaiter().GetResult();
                Same("name reply: nothing left over", new byte[] { 0xDE, 0xAD }, first);
                Eq("name reply: connected", true, tunnel.IsConnected);
            }

            // ...and an IPv6 one, sixteen bytes of address.
            {
                var reply = new byte[4 + 16 + 2];
                reply[0] = 5; reply[3] = 4;
                var wire = new FakeWire(new byte[] { 5, 0 }, reply, new byte[] { 0x42 });
                var tunnel = new Socks5Transport(wire, open);
                Run(tunnel, Dc, 443, null);

                Same("ipv6 reply: nothing left over", new byte[] { 0x42 },
                     tunnel.ReceiveExactAsync(1).GetAwaiter().GetResult());
            }

            // A name is passed on for the proxy to look up, so the phone never asks
            // its own, possibly filtered, DNS.
            Same("connect by name", new byte[] { 5, 1, 0, 3, 11,
                     (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
                     (byte)'.', (byte)'o', (byte)'r', (byte)'g', 0x1F, 0x90 },
                 Socks5Transport.Connect("example.org", 8080));

            // Things that look a little like addresses are names.
            Eq("three parts is a name", 3, (int)Socks5Transport.Connect("1.2.3", 80)[3]);
            Eq("out of range is a name", 3, (int)Socks5Transport.Connect("1.2.3.256", 80)[3]);
            Eq("signs are a name", 3, (int)Socks5Transport.Connect("1.2.3.+4", 80)[3]);

            // Over-long logins are cut to the 255 bytes the length byte can say.
            {
                byte[] message = Socks5Transport.Login(new string('x', 300), "p");
                Eq("long user cut", 255, (int)message[1]);
                Eq("long user message length", 3 + 255 + 1, message.Length);
            }

            Console.WriteLine("  {0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        private static void Run(Socks5Transport tunnel, string host, int port, string expectError)
        {
            try
            {
                tunnel.ConnectAsync(host, port).GetAwaiter().GetResult();
                if (expectError != null) Fail("expected a refusal: " + expectError);
            }
            catch (Socks5Exception ex)
            {
                if (expectError == null) Fail("unexpected refusal: " + ex.Message);
            }
        }

        private static void Refused(string label, ProxySettings proxy, string expected,
                                    params byte[][] replies)
        {
            _checks++;

            var wire = new FakeWire(replies);
            try
            {
                new Socks5Transport(wire, proxy).ConnectAsync(Dc, 443).GetAwaiter().GetResult();
                Fail(label + ": was accepted");
            }
            catch (Socks5Exception ex)
            {
                if (ex.Message.IndexOf(expected, StringComparison.Ordinal) < 0)
                    Fail(label + ": said \"" + ex.Message + "\"");
            }
            catch (Exception ex)
            {
                Fail(label + ": failed the wrong way - " + ex.GetType().Name + " " + ex.Message);
            }
        }

        private static void Eq(string what, object expected, object actual)
        {
            _checks++;
            if (!Equals(expected, actual)) Fail(what + ": " + expected + " != " + actual);
        }

        private static void Same(string what, byte[] expected, byte[] actual)
        {
            _checks++;
            if (expected.Length != actual.Length)
            {
                Fail(what + ": length " + expected.Length + " != " + actual.Length);
                return;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] == actual[i]) continue;
                Fail(what + ": byte " + i + " is " + actual[i] + ", expected " + expected[i]);
                return;
            }
        }

        private static void Fail(string message)
        {
            _failures++;
            Console.WriteLine("  FAIL " + message);
        }

        /// <summary>
        /// A proxy that answers from a script: each read takes bytes from the
        /// replies in order, and running out is an error, like a closed socket.
        /// </summary>
        private sealed class FakeWire : ITransport
        {
            private readonly Queue<byte> _incoming = new Queue<byte>();

            public readonly List<byte[]> Sent = new List<byte[]>();
            public string Dialled;
            private bool _open;

            public FakeWire(params byte[][] replies)
            {
                foreach (byte[] reply in replies)
                    foreach (byte b in reply) _incoming.Enqueue(b);
            }

            public bool IsConnected { get { return _open; } }

            public Task ConnectAsync(string host, int port)
            {
                Dialled = host + ":" + port;
                _open = true;
                return Task.FromResult(0);
            }

            public Task SendAsync(byte[] data)
            {
                Sent.Add((byte[])data.Clone());
                return Task.FromResult(0);
            }

            public Task<byte[]> ReceiveExactAsync(int count)
            {
                if (_incoming.Count < count)
                    throw new System.IO.EndOfStreamException("the script ran out");

                var bytes = new byte[count];
                for (int i = 0; i < count; i++) bytes[i] = _incoming.Dequeue();
                return Task.FromResult(bytes);
            }

            public void Dispose() { _open = false; }
        }
    }
}
