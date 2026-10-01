using System;

namespace Lumigram.Tl
{
    /// <summary>
    /// A link that points back into Telegram rather than out at the web.
    ///
    /// Either a peer named by username, or a peer named by internal id - the second
    /// is what a t.me/c/ link is, and it can only be followed by somebody already in
    /// the chat, because the id alone is not enough to address it.
    /// </summary>
    public sealed class TelegramLink
    {
        /// <summary>The name without its at-sign, or null for a t.me/c/ link.</summary>
        public string Username;

        /// <summary>The channel's own id, or 0 when the link names a username.</summary>
        public long ChannelId;

        /// <summary>The message to open at, or 0 to open at the newest.</summary>
        public int MessageId;

        /// <summary>The forum topic the message is in, or 0 when it is not in one.</summary>
        public int TopicId;

        public bool NamesAMessage { get { return MessageId != 0; } }

        /// <summary>True for a link that can only be followed from inside the chat.</summary>
        public bool IsPrivate { get { return Username == null; } }
    }

    /// <summary>
    /// Recognising the links that should open in the app rather than the browser.
    ///
    /// Three shapes, which is what people actually paste:
    ///
    ///     @name                     a mention
    ///     https://t.me/name         a public chat, channel or person
    ///     https://t.me/name/391     one message in it
    ///     https://t.me/c/4398.../391  one message in a chat with no username
    ///
    /// Deliberately strict about what counts. Everything t.me serves that is *not* a
    /// peer - invite links, sticker sets, proxies, share dialogs - is left alone and
    /// handed to the browser, because opening one of those as though it were a
    /// username resolves to nothing and loses the user the link.
    /// </summary>
    public static class TelegramLinks
    {
        /// <summary>
        /// The hosts that address Telegram itself.
        ///
        /// telegram.me is the older spelling and is still handed out; both redirect
        /// to the same place, so a client that only knows one of them sends people
        /// to a browser for no reason.
        /// </summary>
        private static readonly string[] Hosts = { "t.me", "telegram.me", "telegram.dog" };

        /// <summary>
        /// First path segments that are a feature of t.me rather than somebody's
        /// name. Following one of these as a username looks up a user who does not
        /// exist - and "joinchat" in particular would be a confusing thing to be
        /// told there is no account for.
        /// </summary>
        private static readonly string[] Reserved =
        {
            "joinchat", "addstickers", "addemoji", "addtheme", "addlist", "share",
            "proxy", "socks", "login", "setlanguage", "confirmphone", "bg",
            "invoice", "giftcode", "boost", "contact", "m", "iv",
        };

        /// <summary>
        /// Reads a link, or returns null when it does not point into Telegram.
        ///
        /// Accepts what Links.Split hands back, which includes addresses written
        /// without a scheme, and bare mentions.
        /// </summary>
        public static TelegramLink Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            string value = text.Trim();

            // A mention is the whole link: there is no path to walk.
            if (value[0] == '@')
            {
                string name = value.Substring(1);
                return IsUsername(name) ? new TelegramLink { Username = name } : null;
            }

            string rest = StripScheme(value);
            rest = StripHost(rest);
            if (rest == null) return null;

            // Anything after a ? or # describes how to show the link, not what it
            // points at - ?single on a grouped photo being the common one.
            rest = Cut(Cut(rest, '?'), '#');

            string[] parts = rest.Split('/');
            if (parts.Length == 0 || parts[0].Length == 0) return null;

            if (string.Equals(parts[0], "c", StringComparison.OrdinalIgnoreCase))
                return ParsePrivate(parts);

            if (IsReserved(parts[0])) return null;

            // A "+" first segment is an invite link in the newer spelling, and the
            // characters after it are a hash rather than a name.
            if (parts[0][0] == '+') return null;

            if (!IsUsername(parts[0])) return null;

            var link = new TelegramLink { Username = parts[0] };
            ReadMessageIds(parts, 1, link);
            return link;
        }

        /// <summary>
        /// t.me/c/&lt;channel&gt;/&lt;message&gt;, and the forum form with the topic
        /// in between.
        ///
        /// The id here is the channel's own, without the -100 that bot APIs put in
        /// front of it - which is the same number this client uses everywhere else,
        /// so it needs no adjusting.
        /// </summary>
        private static TelegramLink ParsePrivate(string[] parts)
        {
            if (parts.Length < 2) return null;

            long channelId;
            if (!TryParseLong(parts[1], out channelId) || channelId <= 0) return null;

            var link = new TelegramLink { ChannelId = channelId };
            ReadMessageIds(parts, 2, link);

            // Without a message this is a link to a chat that cannot be named any
            // other way, and opening it at the newest message is right.
            return link;
        }

        /// <summary>
        /// Reads the numbers after the peer.
        ///
        /// One number is a message. Two is a forum: the topic first, then the
        /// message inside it - which matters, because opening the message without
        /// the topic shows it detached from the thread it belongs to.
        /// </summary>
        private static void ReadMessageIds(string[] parts, int from, TelegramLink link)
        {
            int first = 0, second = 0;

            if (parts.Length > from) TryParseInt(parts[from], out first);
            if (parts.Length > from + 1) TryParseInt(parts[from + 1], out second);

            if (second > 0)
            {
                link.TopicId = first;
                link.MessageId = second;
            }
            else
            {
                link.MessageId = first;
            }
        }

        /// <summary>
        /// The t.me address for one message, or null when there is nothing to build
        /// one from.
        ///
        /// A chat with a username gets the readable form, which anybody can open. A
        /// chat without one gets the /c/ form, which only works for people already
        /// in it - that is a property of the chat rather than a shortcoming of the
        /// link, and it is still the link that chat's own members would share.
        ///
        /// A one-to-one conversation gets nothing: its messages have no address, and
        /// offering a link that silently is not one is worse than offering none.
        /// </summary>
        public static string ForMessage(string username, long channelId, int messageId,
                                        int topicId = 0)
        {
            if (messageId <= 0) return null;

            string thread = topicId > 0 ? topicId + "/" : "";

            if (!string.IsNullOrEmpty(username))
                return "https://t.me/" + username + "/" + thread + messageId;

            if (channelId > 0)
                return "https://t.me/c/" + channelId + "/" + thread + messageId;

            return null;
        }

        private static string StripScheme(string value)
        {
            int at = value.IndexOf("://", StringComparison.Ordinal);
            return at >= 0 ? value.Substring(at + 3) : value;
        }

        /// <summary>
        /// Removes the host, or returns null if it is not one of Telegram's.
        ///
        /// A leading "www." is dropped first: t.me does not use it, but people paste
        /// what their browser showed them.
        /// </summary>
        private static string StripHost(string value)
        {
            if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(4);

            foreach (string host in Hosts)
            {
                if (value.Length <= host.Length) continue;
                if (value[host.Length] != '/') continue;

                if (string.Compare(value, 0, host, 0, host.Length,
                                   StringComparison.OrdinalIgnoreCase) == 0)
                    return value.Substring(host.Length + 1);
            }

            return null;
        }

        private static string Cut(string value, char at)
        {
            int i = value.IndexOf(at);
            return i >= 0 ? value.Substring(0, i) : value;
        }

        private static bool IsReserved(string segment)
        {
            foreach (string word in Reserved)
                if (string.Equals(segment, word, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// Whether a word can be a Telegram username.
        ///
        /// Letters, digits and underscores, starting with a letter. The leading
        /// letter is the useful half of the rule: it is what stops a bare number
        /// after an at-sign, and most of what stops ordinary punctuation runs, from
        /// being offered as somebody to open.
        /// </summary>
        public static bool IsUsername(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value.Length < 2 || value.Length > 32) return false;
            if (!IsLetter(value[0])) return false;

            foreach (char c in value)
                if (!IsLetter(c) && !IsDigit(c) && c != '_') return false;

            return true;
        }

        private static bool IsLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        // Parsed by hand rather than with int.TryParse and a culture: the ids are
        // plain ASCII digits and nothing else is acceptable, where TryParse would
        // accept a sign, spaces and group separators.
        private static bool TryParseInt(string value, out int result)
        {
            long wide;
            result = 0;

            if (!TryParseLong(value, out wide) || wide > int.MaxValue) return false;

            result = (int)wide;
            return true;
        }

        private static bool TryParseLong(string value, out long result)
        {
            result = 0;
            if (string.IsNullOrEmpty(value) || value.Length > 19) return false;

            long total = 0;
            foreach (char c in value)
            {
                if (!IsDigit(c)) return false;
                total = total * 10 + (c - '0');
            }

            result = total;
            return true;
        }
    }
}
