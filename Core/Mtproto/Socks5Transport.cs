using System;
using System.Text;
using System.Threading.Tasks;

namespace Lumigram.Mtproto
{
    /// <summary>Where a SOCKS5 proxy is, and how to log in to it.</summary>
    public sealed class ProxySettings
    {
        public string Host;
        public int Port;

        /// <summary>Empty for a proxy that takes no login.</summary>
        public string User;
        public string Password;

        public bool HasLogin { get { return !string.IsNullOrEmpty(User); } }

        public bool IsUsable
        {
            get { return !string.IsNullOrEmpty(Host) && Port > 0 && Port < 65536; }
        }
    }

    /// <summary>A proxy refused, or answered with something that is not SOCKS5.</summary>
    public sealed class Socks5Exception : Exception
    {
        public Socks5Exception(string message) : base(message) { }
    }

    /// <summary>
    /// A connection to a datacenter made through a SOCKS5 proxy.
    ///
    /// For anyone on a network that blocks Telegram: the phone connects to the
    /// proxy, asks it to connect onward to the datacenter, and from then on every
    /// byte passes straight through it. MTProto never knows - it sees an ordinary
    /// byte pipe, which is why this wraps a transport rather than changing one.
    /// The phone's socket and the desktop's both go inside it unaltered, so the
    /// handshake below is the same code on both, and the desktop harness tests the
    /// code the phone runs.
    ///
    /// The handshake is RFC 1928, with RFC 1929 username/password login. Ported from
    /// Symbigram's hand-written one, which replaced Qt's built-in proxy support.
    /// </summary>
    public sealed class Socks5Transport : ITransport
    {
        private const byte Version = 0x05;
        private const byte NoAuth = 0x00;
        private const byte UserPassword = 0x02;
        private const byte NoAcceptable = 0xFF;
        private const byte CommandConnect = 0x01;
        private const byte AddressIPv4 = 0x01;
        private const byte AddressDomain = 0x03;
        private const byte AddressIPv6 = 0x04;

        private readonly ITransport _inner;
        private readonly ProxySettings _proxy;
        private bool _ready;

        public Socks5Transport(ITransport inner, ProxySettings proxy)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            if (proxy == null || !proxy.IsUsable)
                throw new ArgumentException("a proxy needs a host and a port");

            _inner = inner;
            _proxy = proxy;
        }

        public bool IsConnected { get { return _ready && _inner.IsConnected; } }

        /// <summary>
        /// Connects to the proxy and has it connect onward to
        /// <paramref name="host"/>:<paramref name="port"/>.
        ///
        /// Only returns once the tunnel is open. Anything sent before that would be
        /// read by the proxy as a malformed handshake rather than passed on.
        /// </summary>
        public async Task ConnectAsync(string host, int port)
        {
            _ready = false;

            try
            {
                await _inner.ConnectAsync(_proxy.Host, _proxy.Port);
            }
            catch (Exception ex)
            {
                throw new Socks5Exception("could not reach the proxy at " + _proxy.Host + ":" +
                                          _proxy.Port + " - " + ex.Message);
            }

            try
            {
                await HandshakeAsync(host, port);
            }
            catch (Exception)
            {
                // The socket to the proxy is open and of no further use; holding on
                // to it until the transport is collected would leave the proxy with
                // a dangling connection for every failed attempt.
                _inner.Dispose();
                throw;
            }

            _ready = true;
        }

        private async Task HandshakeAsync(string host, int port)
        {
            await _inner.SendAsync(Greeting(_proxy.HasLogin));

            byte[] choice = await _inner.ReceiveExactAsync(2);
            if (choice[0] != Version)
                throw new Socks5Exception("that is not a SOCKS5 proxy");

            if (choice[1] == UserPassword)
            {
                if (!_proxy.HasLogin)
                    throw new Socks5Exception("the proxy wants a username and password");

                await _inner.SendAsync(Login(_proxy.User, _proxy.Password));

                byte[] status = await _inner.ReceiveExactAsync(2);
                if (status[1] != 0)
                    throw new Socks5Exception(
                        "the proxy refused the login - check the username and password");
            }
            else if (choice[1] == NoAcceptable)
            {
                throw new Socks5Exception(_proxy.HasLogin
                    ? "the proxy accepts none of the ways this app can log in"
                    : "the proxy wants a username and password");
            }
            else if (choice[1] != NoAuth)
            {
                throw new Socks5Exception("the proxy asked for a login method this app does not have");
            }

            await _inner.SendAsync(Connect(host, port));
            await ReadReplyAsync();
        }

        /// <summary>
        /// Reads the proxy's answer to CONNECT, all of it.
        ///
        /// The reply carries the address the proxy bound for us, and its length
        /// depends on the kind of address. Leaving any of it unread would hand the
        /// last few bytes of the handshake to MTProto as the start of its first
        /// packet, which then fails in a way that looks nothing like a proxy problem.
        /// </summary>
        private async Task ReadReplyAsync()
        {
            byte[] head = await _inner.ReceiveExactAsync(4);
            if (head[0] != Version) throw new Socks5Exception("the proxy sent a malformed reply");

            int rest;
            switch (head[3])
            {
                case AddressIPv4: rest = 4; break;
                case AddressIPv6: rest = 16; break;
                case AddressDomain:
                    byte[] length = await _inner.ReceiveExactAsync(1);
                    rest = length[0];
                    break;
                default:
                    throw new Socks5Exception("the proxy sent a malformed reply");
            }

            await _inner.ReceiveExactAsync(rest + 2);         // the address, then the port

            if (head[1] != 0) throw new Socks5Exception(Refusal(head[1]));
        }

        public Task SendAsync(byte[] data)
        {
            return _inner.SendAsync(data);
        }

        public Task<byte[]> ReceiveExactAsync(int count)
        {
            return _inner.ReceiveExactAsync(count);
        }

        public void Dispose()
        {
            _ready = false;
            _inner.Dispose();
        }

        // ---- the messages, public so they can be checked byte for byte --------

        /// <summary>
        /// The opening offer of ways to log in.
        ///
        /// "No login" is always offered, and username/password as well when there
        /// is one to give. Offering only the password method would fail against a
        /// proxy that takes no login - a common setup - for no reason.
        /// </summary>
        public static byte[] Greeting(bool withLogin)
        {
            return withLogin
                ? new byte[] { Version, 2, NoAuth, UserPassword }
                : new byte[] { Version, 1, NoAuth };
        }

        /// <summary>
        /// The username/password login (RFC 1929). Each is limited to 255 bytes of
        /// UTF-8 by the one-byte length in front of it.
        /// </summary>
        public static byte[] Login(string user, string password)
        {
            byte[] u = Limit(Encoding.UTF8.GetBytes(user ?? ""));
            byte[] p = Limit(Encoding.UTF8.GetBytes(password ?? ""));

            var message = new byte[3 + u.Length + p.Length];
            message[0] = 0x01;                                  // login protocol version
            message[1] = (byte)u.Length;
            Buffer.BlockCopy(u, 0, message, 2, u.Length);
            message[2 + u.Length] = (byte)p.Length;
            Buffer.BlockCopy(p, 0, message, 3 + u.Length, p.Length);

            return message;
        }

        /// <summary>
        /// Asks the proxy to connect onward.
        ///
        /// An IPv4 address goes as four bytes; anything else as a name for the proxy
        /// to look up. That second case matters on a filtered network: the phone
        /// never asks its own, possibly poisoned, DNS where Telegram is.
        /// </summary>
        public static byte[] Connect(string host, int port)
        {
            byte[] address;
            byte kind;

            byte[] ip = ParseIPv4(host);
            if (ip != null)
            {
                kind = AddressIPv4;
                address = ip;
            }
            else
            {
                byte[] name = Limit(Encoding.UTF8.GetBytes(host ?? ""));
                kind = AddressDomain;
                address = new byte[1 + name.Length];
                address[0] = (byte)name.Length;
                Buffer.BlockCopy(name, 0, address, 1, name.Length);
            }

            var message = new byte[4 + address.Length + 2];
            message[0] = Version;
            message[1] = CommandConnect;
            message[2] = 0x00;                                  // reserved
            message[3] = kind;
            Buffer.BlockCopy(address, 0, message, 4, address.Length);
            message[4 + address.Length] = (byte)(port >> 8);    // port, big-endian
            message[5 + address.Length] = (byte)port;

            return message;
        }

        /// <summary>The reply codes of RFC 1928, said in words.</summary>
        public static string Refusal(int code)
        {
            switch (code)
            {
                case 1: return "the proxy failed (general failure)";
                case 2: return "the proxy's rules do not allow this connection";
                case 3: return "the proxy cannot reach Telegram's network";
                case 4: return "the proxy cannot reach Telegram's server";
                case 5: return "Telegram's server refused the proxy's connection";
                case 6: return "the proxy's connection to Telegram timed out";
                case 7: return "the proxy does not support connecting onward";
                case 8: return "the proxy does not support this kind of address";
                default: return "the proxy refused the connection (code " + code + ")";
            }
        }

        private static byte[] Limit(byte[] bytes)
        {
            if (bytes.Length <= 255) return bytes;

            var cut = new byte[255];
            Buffer.BlockCopy(bytes, 0, cut, 0, 255);
            return cut;
        }

        /// <summary>Four bytes for a dotted IPv4 address, or null for anything else.</summary>
        private static byte[] ParseIPv4(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;

            string[] parts = host.Split('.');
            if (parts.Length != 4) return null;

            var bytes = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                int value;
                if (parts[i].Length == 0 || parts[i].Length > 3 ||
                    !int.TryParse(parts[i], out value) || value < 0 || value > 255)
                    return null;

                foreach (char c in parts[i])
                    if (c < '0' || c > '9') return null;

                bytes[i] = (byte)value;
            }

            return bytes;
        }
    }
}
