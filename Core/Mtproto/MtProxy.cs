using System;
using System.Collections.Generic;
using System.Text;

namespace Lumigram.Mtproto
{
    /// <summary>The three disguises an MTProto proxy can ask for.</summary>
    public enum MtProxyMode
    {
        /// <summary>16-byte secret: the stream is encrypted, packets keep their sizes.</summary>
        Plain,

        /// <summary>"dd" + 16 bytes: the same, with 0-15 random bytes added to each packet.</summary>
        Padded,

        /// <summary>"ee" + 16 bytes + a domain: the same, dressed up as HTTPS to that domain.</summary>
        FakeTls,
    }

    /// <summary>
    /// An MTProto proxy's secret, decoded.
    ///
    /// The first byte says which disguise the proxy wants, the next sixteen are the
    /// key, and a FakeTLS secret carries the domain to pretend to be talking to
    /// after that. Rules as TDLib's ProxySecret::from_binary, which is what every
    /// proxy is written against.
    /// </summary>
    public sealed class MtProxySecret
    {
        /// <summary>The longest domain TDLib accepts after the key.</summary>
        private const int MaxDomain = 182;

        public MtProxyMode Mode;

        /// <summary>The 16 key bytes - never the mode byte or the domain.</summary>
        public byte[] Key;

        /// <summary>The domain a FakeTLS proxy poses as, or null.</summary>
        public string Domain;

        public bool IsPadded { get { return Mode != MtProxyMode.Plain; } }

        /// <summary>
        /// Reads a secret as proxy lists publish it: hex, or base64url, or plain
        /// base64. Returns null and says why when it is none of those, or decodes
        /// to something no proxy uses.
        /// </summary>
        public static MtProxySecret Parse(string text, out string error)
        {
            error = null;
            string t = Compact(text);

            if (t.Length == 0)
            {
                error = "there is no secret";
                return null;
            }

            // Hex first, as TDLib does - but only when it decodes to a usable
            // length. A base64 secret can be made of hex digits by chance, and then
            // it is the base64 reading that is meant.
            byte[] raw = HexDecode(t);
            MtProxySecret parsed = raw != null ? FromBytes(raw) : null;
            if (parsed != null) return parsed;

            raw = Base64Decode(t);
            parsed = raw != null ? FromBytes(raw) : null;
            if (parsed != null) return parsed;

            byte[] any = HexDecode(t) ?? Base64Decode(t);
            if (any == null)
                error = "the secret is neither hex nor base64";
            else if (any.Length > 17 && any[0] != 0xee)
                error = "the secret is a type this app does not know";
            else
                error = "the secret is " + any.Length + " bytes, which is not a length proxies use";

            return null;
        }

        /// <summary>16 bytes; 17 starting 0xdd; or 0xee, the key and a domain.</summary>
        public static MtProxySecret FromBytes(byte[] raw)
        {
            if (raw == null) return null;

            if (raw.Length == 16)
                return new MtProxySecret { Mode = MtProxyMode.Plain, Key = (byte[])raw.Clone() };

            if (raw.Length == 17 && raw[0] == 0xdd)
                return new MtProxySecret { Mode = MtProxyMode.Padded, Key = Slice(raw, 1, 16) };

            if (raw.Length >= 18 && raw.Length <= 17 + MaxDomain && raw[0] == 0xee)
            {
                string domain = Encoding.UTF8.GetString(raw, 17, raw.Length - 17);
                return new MtProxySecret
                {
                    Mode = MtProxyMode.FakeTls,
                    Key = Slice(raw, 1, 16),
                    Domain = domain,
                };
            }

            return null;
        }

        /// <summary>A few words for the settings page: which kind of proxy this is.</summary>
        public string Describe()
        {
            switch (Mode)
            {
                case MtProxyMode.Plain: return "plain secret - encrypted stream";
                case MtProxyMode.Padded: return "dd secret - encrypted, with random padding";
                default: return "ee secret - FakeTLS, looks like HTTPS to " + Domain;
            }
        }

        private static string Compact(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n') sb.Append(c);

            return sb.ToString();
        }

        private static byte[] HexDecode(string t)
        {
            if (t.Length == 0 || t.Length % 2 != 0) return null;

            var bytes = new byte[t.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int hi = HexValue(t[2 * i]), lo = HexValue(t[2 * i + 1]);
                if (hi < 0 || lo < 0) return null;
                bytes[i] = (byte)((hi << 4) | lo);
            }

            return bytes;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>base64url or standard base64, with or without its padding.</summary>
        private static byte[] Base64Decode(string t)
        {
            string b64 = t.Replace('-', '+').Replace('_', '/').TrimEnd('=');
            if (b64.Length % 4 == 1) return null;
            while (b64.Length % 4 != 0) b64 += "=";

            try { return Convert.FromBase64String(b64); }
            catch (FormatException) { return null; }
        }

        private static byte[] Slice(byte[] raw, int offset, int count)
        {
            var part = new byte[count];
            Buffer.BlockCopy(raw, offset, part, 0, count);
            return part;
        }
    }

    /// <summary>Where an MTProto proxy is, and the secret it wants.</summary>
    public sealed class MtProxySettings
    {
        public string Host;
        public int Port;
        public MtProxySecret Secret;

        public bool IsUsable
        {
            get { return !string.IsNullOrEmpty(Host) && Port > 0 && Port < 65536 && Secret != null; }
        }
    }

    /// <summary>
    /// A proxy as shared in a link: tg://proxy?server=...&amp;port=...&amp;secret=...,
    /// the same on https://t.me/proxy, and the SOCKS5 forms tg://socks and
    /// t.me/socks with user and pass instead of a secret.
    /// </summary>
    public sealed class ProxyLink
    {
        public bool IsMtproto;
        public string Server;
        public int Port;

        /// <summary>The secret as the link wrote it - kept in that form for the settings page.</summary>
        public string SecretText;
        public MtProxySecret Secret;

        public string User;
        public string Password;

        /// <summary>
        /// True when the address is a proxy link at all - whether or not it is a
        /// good one. Used to keep proxy links away from the browser and from the
        /// username lookup, which can do nothing useful with them.
        /// </summary>
        public static bool IsProxyLink(string url)
        {
            string path = PathOf(url);
            return path != null &&
                   (path.Equals("proxy", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("socks", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Reads a proxy link, or returns null and says what is wrong with it.</summary>
        public static ProxyLink Parse(string url, out string error)
        {
            error = null;

            string path = PathOf(url);
            if (path == null || !IsProxyLink(url))
            {
                error = "That is not a proxy link.";
                return null;
            }

            Dictionary<string, string> query = Query(url);

            string server, portText, secret;
            query.TryGetValue("server", out server);
            query.TryGetValue("port", out portText);
            query.TryGetValue("secret", out secret);

            int port;
            if (string.IsNullOrEmpty(server) || !int.TryParse(portText, out port) ||
                port <= 0 || port > 65535)
            {
                error = "The proxy link is missing the server or the port.";
                return null;
            }

            var link = new ProxyLink { Server = server.Trim(), Port = port };

            // A secret makes it an MTProto proxy whatever the path says; t.me/proxy
            // without one is how some lists share a SOCKS5 server.
            if (!string.IsNullOrEmpty(secret))
            {
                string why;
                link.Secret = MtProxySecret.Parse(secret, out why);
                if (link.Secret == null)
                {
                    error = "The proxy link's secret is not usable: " + why + ".";
                    return null;
                }

                link.IsMtproto = true;
                link.SecretText = secret.Trim();
                return link;
            }

            string user, pass;
            query.TryGetValue("user", out user);
            query.TryGetValue("pass", out pass);
            link.User = user ?? "";
            link.Password = pass ?? "";
            return link;
        }

        /// <summary>
        /// "proxy" or "socks" from any of the link's spellings, or null when the
        /// address is not one of Telegram's.
        /// </summary>
        private static string PathOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;

            string v = url.Trim();
            int q = v.IndexOf('?');
            string head = q >= 0 ? v.Substring(0, q) : v;

            if (head.StartsWith("tg://", StringComparison.OrdinalIgnoreCase))
                return head.Substring(5).TrimEnd('/');

            int scheme = head.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) head = head.Substring(scheme + 3);
            if (head.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) head = head.Substring(4);

            foreach (string host in new[] { "t.me/", "telegram.me/", "telegram.dog/" })
                if (head.StartsWith(host, StringComparison.OrdinalIgnoreCase))
                    return head.Substring(host.Length).TrimEnd('/');

            return null;
        }

        /// <summary>
        /// The query's values, percent-decoded. A "+" is left alone rather than
        /// read as a space: in a base64 secret it is a digit.
        /// </summary>
        private static Dictionary<string, string> Query(string url)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int q = url.IndexOf('?');
            if (q < 0) return values;

            string query = url.Substring(q + 1);
            int hash = query.IndexOf('#');
            if (hash >= 0) query = query.Substring(0, hash);

            foreach (string pair in query.Split('&'))
            {
                // A link copied out of a page sometimes keeps its HTML escaping.
                string p = pair.StartsWith("amp;", StringComparison.Ordinal) ? pair.Substring(4) : pair;
                int eq = p.IndexOf('=');
                if (eq <= 0) continue;

                string key = p.Substring(0, eq);
                string value;
                try { value = Uri.UnescapeDataString(p.Substring(eq + 1)); }
                catch (Exception) { value = p.Substring(eq + 1); }

                if (!values.ContainsKey(key)) values[key] = value;
            }

            return values;
        }
    }
}
