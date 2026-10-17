using System;
using System.Collections.Generic;

namespace Lumigram.Tl
{
    /// <summary>One stretch of a message: either plain text, or a link.</summary>
    public sealed class TextPart
    {
        public string Text;

        /// <summary>The address to open, or null when this is ordinary text.</summary>
        public string Url;

        public bool IsLink { get { return Url != null; } }
    }

    /// <summary>
    /// A link the sender put behind some of the message's words
    /// (messageEntityTextUrl): the words show, the address does not. Offsets count
    /// UTF-16 code units, the same units C# strings index by.
    /// </summary>
    public sealed class TextLink
    {
        public int Offset;
        public int Length;
        public string Url;
    }

    /// <summary>
    /// Finds the links in a message.
    ///
    /// Telegram does send message entities describing exactly where the links are,
    /// which would be the better source - but only for messages that carry them,
    /// and plenty arrive as bare text with a URL in the middle. Scanning the text
    /// covers both, and covers what other clients linkify by the same rule.
    ///
    /// Deliberately conservative. A false positive turns ordinary words into
    /// something that looks tappable and goes nowhere, which is worse than missing
    /// an unusual URL: only well-formed http and https addresses, www. and t.me
    /// prefixes, and @mentions are recognised.
    ///
    /// A mention is a link in exactly the sense that matters here - it names a peer
    /// and tapping it should open that peer - so it is given the t.me address it is
    /// shorthand for, and everything above this layer can treat the two alike.
    /// </summary>
    public static class Links
    {
        /// <summary>
        /// Trailing characters that are almost always punctuation around a link
        /// rather than part of it. A closing bracket is kept when the link opened
        /// one, so a URL containing brackets survives.
        /// </summary>
        private const string TrailingPunctuation = ".,;:!?'\"";

        /// <summary>Splits text into runs, marking the ones that are links.</summary>
        public static List<TextPart> Split(string text)
        {
            var parts = new List<TextPart>();
            if (string.IsNullOrEmpty(text))
            {
                return parts;
            }

            int at = 0;
            while (at < text.Length)
            {
                int start, length;
                if (!FindNext(text, at, out start, out length))
                {
                    parts.Add(new TextPart { Text = text.Substring(at) });
                    break;
                }

                if (start > at)
                    parts.Add(new TextPart { Text = text.Substring(at, start - at) });

                string found = text.Substring(start, length);
                parts.Add(new TextPart { Text = found, Url = Absolute(found) });


                at = start + length;
            }

            return parts;
        }

        /// <summary>
        /// Splits text into runs, taking the links hidden behind words into account
        /// as well as the ones written out.
        ///
        /// Channels publish links this way all the time - proxy lists especially,
        /// whose posts are words in Farsi or Russian with the addresses only in the
        /// metadata - and text scanning alone cannot see them. A hidden link that
        /// does not fit the text (a malformed entity, or text edited since) is
        /// ignored rather than trusted.
        /// </summary>
        public static List<TextPart> Split(string text, IList<TextLink> hidden)
        {
            if (hidden == null || hidden.Count == 0 || string.IsNullOrEmpty(text))
                return Split(text);

            var sorted = new List<TextLink>(hidden);
            sorted.Sort((a, c) => a.Offset.CompareTo(c.Offset));

            var parts = new List<TextPart>();
            int at = 0;

            foreach (TextLink link in sorted)
            {
                if (link == null || string.IsNullOrEmpty(link.Url)) continue;
                if (link.Offset < at || link.Length <= 0 || link.Offset + link.Length > text.Length)
                    continue;

                if (link.Offset > at) parts.AddRange(Split(text.Substring(at, link.Offset - at)));

                parts.Add(new TextPart
                {
                    Text = text.Substring(link.Offset, link.Length),
                    Url = link.Url.Trim(),
                });

                at = link.Offset + link.Length;
            }

            if (at < text.Length) parts.AddRange(Split(text.Substring(at)));
            return parts;
        }

        /// <summary>Just the addresses, in the order they appear.</summary>
        public static List<string> Find(string text)
        {
            var urls = new List<string>();
            foreach (TextPart part in Split(text))
                if (part.IsLink) urls.Add(part.Url);

            return urls;
        }

        private static bool FindNext(string text, int from, out int start, out int length)
        {
            start = -1;
            length = 0;

            for (int i = from; i < text.Length; i++)
            {
                if (!AtBoundary(text, i)) continue;

                int matched = MatchAt(text, i);
                if (matched <= 0) continue;

                start = i;
                length = matched;
                return true;
            }

            return false;
        }

        /// <summary>
        /// A link has to start at the beginning or after a space or bracket.
        /// Without this, "note:http" inside a word would be treated as a link.
        /// </summary>
        private static bool AtBoundary(string text, int i)
        {
            if (i == 0) return true;

            char before = text[i - 1];
            return char.IsWhiteSpace(before) || before == '(' || before == '[' ||
                   before == '<' || before == '"';
        }

        private static int MatchAt(string text, int i)
        {
            if (text[i] == '@') return MatchMention(text, i);

            int length = 0;

            // tg:// only for proxies: they are the tg:// links people post as text,
            // and the only ones this app can do something with.
            if (Starts(text, i, "tg://proxy?")) length = 11;
            else if (Starts(text, i, "tg://socks?")) length = 11;
            else if (Starts(text, i, "http://")) length = 7;
            else if (Starts(text, i, "https://")) length = 8;
            else if (Starts(text, i, "www.")) length = 4;
            else if (Starts(text, i, "t.me/")) length = 5;
            else return 0;

            int end = i + length;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;

            // Trim punctuation that ends the sentence rather than the address.
            while (end > i + length && TrailingPunctuation.IndexOf(text[end - 1]) >= 0) end--;

            // A closing bracket belongs to the link only if it opened one.
            while (end > i + length && text[end - 1] == ')' &&
                   Count(text, i, end, '(') < Count(text, i, end, ')')) end--;

            int total = end - i;

            // A bare scheme with nothing after it is not a link.
            if (total <= length) return 0;

            return total;
        }

        /// <summary>
        /// An @mention, and only when the word after the at-sign could really be a
        /// username.
        ///
        /// The check matters more here than anywhere else in this file. An at-sign
        /// turns up in prose, in handles for other services and in the middle of
        /// e-mail addresses, and the boundary rule only covers the last of those -
        /// "name@example.com" is safe because the at-sign is mid-word, but "@ 5pm"
        /// and "@2026" would both be offered as people to open without this.
        /// </summary>
        private static int MatchMention(string text, int i)
        {
            int end = i + 1;
            while (end < text.Length && IsUsernameChar(text[end])) end++;

            int length = end - i;
            if (length < 2) return 0;

            return TelegramLinks.IsUsername(text.Substring(i + 1, length - 1)) ? length : 0;
        }

        private static bool IsUsernameChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                   (c >= '0' && c <= '9') || c == '_';
        }

        private static int Count(string text, int from, int to, char c)
        {
            int n = 0;
            for (int i = from; i < to; i++) if (text[i] == c) n++;
            return n;
        }

        private static bool Starts(string text, int at, string prefix)
        {
            if (at + prefix.Length > text.Length) return false;

            for (int i = 0; i < prefix.Length; i++)
                if (char.ToLowerInvariant(text[at + i]) != prefix[i]) return false;

            return true;
        }

        /// <summary>
        /// What to actually open. A link written without a scheme still has to be
        /// handed to the browser with one.
        /// </summary>
        private static string Absolute(string found)
        {
            // A mention is shorthand for a t.me address, so it is given one. That is
            // what lets a caller route mentions and pasted links through one path
            // instead of carrying a second kind of link all the way up.
            if (found.Length > 1 && found[0] == '@')
                return "https://t.me/" + found.Substring(1);

            if (Starts(found, 0, "http://") || Starts(found, 0, "https://") ||
                Starts(found, 0, "tg://")) return found;
            return "http://" + found;
        }
    }
}
