using System;
using System.Collections.Generic;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// Checks the link finder.
    ///
    /// Worth testing rather than eyeballing on a phone, because both failure modes
    /// are quiet: a missed link is simply not tappable, and a false positive makes
    /// ordinary words look tappable and go nowhere. The awkward cases here are the
    /// ones real messages actually contain - a URL at the end of a sentence, one in
    /// brackets, and text that merely looks like one.
    /// </summary>
    internal static class LinkTests
    {
        private static int _checks;
        private static int _failures;

        public static bool RunAll()
        {
            _checks = 0;
            _failures = 0;

            // Plain addresses.
            One("https://example.com", "https://example.com");
            One("http://example.com/a/b?c=1", "http://example.com/a/b?c=1");
            One("www.example.com", "http://www.example.com");
            One("t.me/lumigram", "http://t.me/lumigram");

            // Surrounded by ordinary text.
            One("see https://example.com for details", "https://example.com");
            One("start https://example.com", "https://example.com");

            // Sentence punctuation is not part of the address.
            One("go to https://example.com.", "https://example.com");
            One("try https://example.com, then stop", "https://example.com");
            One("really? https://example.com!", "https://example.com");

            // Brackets: kept when the link opened one, dropped when the text did.
            One("(see https://example.com)", "https://example.com");
            One("https://en.wikipedia.org/wiki/A_(b)", "https://en.wikipedia.org/wiki/A_(b)");

            // Not links.
            None("no link here");
            None("");
            None("email me at name@example.com");
            None("https://");
            None("nothttp://example.com");        // must start at a boundary
            None("read the http protocol");

            // Several in one message, in order.
            Many("a https://one.com b www.two.com c",
                 new[] { "https://one.com", "http://www.two.com" });

            // Mentions, which are links to a peer and are given the address they
            // are shorthand for.
            One("@lumigram", "https://t.me/lumigram");
            One("ask @lumigram about it", "https://t.me/lumigram");
            One("(@lumigram)", "https://t.me/lumigram");
            Many("@one and @two", new[] { "https://t.me/one", "https://t.me/two" });

            // ...and the things an at-sign turns up in that are not mentions.
            None("name@example.com");             // mid-word, so not at a boundary
            None("see you @ 5pm");                // nothing follows the at-sign
            None("@2026 was the year");           // a username cannot start with a digit
            None("@");
            None("@a");                           // too short to be a username

            Rebuilds("ask @lumigram about it");
            Rebuilds("@one and @two");

            // The split has to put the message back together unchanged, or the text
            // on screen would quietly differ from the text that was sent.
            Rebuilds("see https://example.com. thanks");
            Rebuilds("plain text only");
            Rebuilds("https://one.com and https://two.com");

            InternalLinks();

            Console.WriteLine("  {0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        /// <summary>
        /// Which links open in the app, and what they point at.
        ///
        /// The two failure modes are opposites and both bad: treating a t.me feature
        /// as a username looks up somebody who does not exist and loses the user the
        /// link, and failing to recognise a real one sends them to a browser to be
        /// told to install Telegram.
        /// </summary>
        private static void InternalLinks()
        {
            // A peer, however it was written.
            Peer("@lumigram", "lumigram");
            Peer("https://t.me/lumigram", "lumigram");
            Peer("http://t.me/lumigram", "lumigram");
            Peer("t.me/lumigram", "lumigram");
            Peer("https://telegram.me/lumigram", "lumigram");
            Peer("https://www.t.me/lumigram", "lumigram");
            Peer("https://t.me/lumigram/", "lumigram");

            // One message in it, and the decoration a shared link carries.
            Message("https://t.me/durov/391", "durov", 0, 391);
            Message("https://t.me/durov/391?single", "durov", 0, 391);
            Message("https://t.me/durov/391#start", "durov", 0, 391);

            // A chat with no username: the id is its own, with no -100 in front.
            Message("https://t.me/c/4398718315/391", null, 4398718315L, 391);
            Message("t.me/c/4398718315/391", null, 4398718315L, 391);

            // A forum: topic first, then the message inside it.
            Topic("https://t.me/somegroup/12/391", "somegroup", 12, 391);
            Topic("https://t.me/c/4398718315/12/391", null, 12, 391);

            // A t.me link with no message opens the chat at the newest.
            {
                TelegramLink link = TelegramLinks.Parse("https://t.me/c/4398718315");
                Check("private chat, no message",
                      link != null && link.ChannelId == 4398718315L && link.MessageId == 0);
            }

            // Everything t.me serves that is not a peer stays a web address.
            External("https://t.me/joinchat/AAAAAEkk2WdoDrB4");
            External("https://t.me/+AAAAAEkk2WdoDrB4");
            External("https://t.me/addstickers/Animals");
            External("https://t.me/proxy?server=1.2.3.4");
            External("https://t.me/share/url?url=x");
            External("https://example.com/lumigram");
            External("https://nott.me/lumigram");
            External("https://t.me.evil.com/lumigram");
            External("https://t.me/");
            External("@2026");

            // Building one back, which is what the copy-link menu item hands over.
            Built("public message", TelegramLinks.ForMessage("durov", 0, 391),
                  "https://t.me/durov/391");
            Built("private message", TelegramLinks.ForMessage(null, 4398718315L, 391),
                  "https://t.me/c/4398718315/391");
            Built("public topic message", TelegramLinks.ForMessage("somegroup", 0, 391, 12),
                  "https://t.me/somegroup/12/391");
            Built("private topic message", TelegramLinks.ForMessage(null, 4398718315L, 391, 12),
                  "https://t.me/c/4398718315/12/391");

            // A one-to-one chat has no addressable messages, so there is no link to
            // offer and the menu item has to know that.
            Built("no peer to name", TelegramLinks.ForMessage(null, 0, 391), null);
            Built("no message", TelegramLinks.ForMessage("durov", 0, 0), null);

            // Every link the splitter finds has to survive being classified, which
            // is the join between the two halves of this file.
            string mixed = "hi @durov see https://t.me/durov/391 and https://example.com";
            foreach (TextPart part in Links.Split(mixed))
            {
                if (!part.IsLink) continue;

                bool internalLink = TelegramLinks.Parse(part.Url) != null;
                bool expected = part.Url.IndexOf("t.me/", StringComparison.Ordinal) >= 0;

                Check("classify " + part.Url, internalLink == expected);
            }
        }

        private static void Peer(string text, string username)
        {
            TelegramLink link = TelegramLinks.Parse(text);
            Check("peer " + Show(text),
                  link != null && link.Username == username && link.MessageId == 0);
        }

        private static void Message(string text, string username, long channelId, int messageId)
        {
            TelegramLink link = TelegramLinks.Parse(text);
            Check("message " + Show(text),
                  link != null && link.Username == username &&
                  link.ChannelId == channelId && link.MessageId == messageId &&
                  link.TopicId == 0);
        }

        private static void Topic(string text, string username, int topicId, int messageId)
        {
            TelegramLink link = TelegramLinks.Parse(text);
            Check("topic " + Show(text),
                  link != null && link.Username == username &&
                  link.TopicId == topicId && link.MessageId == messageId);
        }

        private static void External(string text)
        {
            Check("external " + Show(text), TelegramLinks.Parse(text) == null);
        }

        private static void Built(string label, string got, string expected)
        {
            Check("build " + label + " -> " + (got ?? "(none)"), got == expected);
        }

        private static void Check(string label, bool ok)
        {
            _checks++;
            if (ok) return;

            _failures++;
            Console.WriteLine("  FAIL {0}", label);
        }

        private static void One(string text, string expected)
        {
            _checks++;
            List<string> found = Links.Find(text);

            if (found.Count == 1 && found[0] == expected) return;

            _failures++;
            Console.WriteLine("  FAIL {0}", Show(text));
            Console.WriteLine("       expected [{0}], got [{1}]", expected, string.Join(", ", found.ToArray()));
        }

        private static void None(string text)
        {
            _checks++;
            List<string> found = Links.Find(text);
            if (found.Count == 0) return;

            _failures++;
            Console.WriteLine("  FAIL {0}", Show(text));
            Console.WriteLine("       expected no links, got [{0}]", string.Join(", ", found.ToArray()));
        }

        private static void Many(string text, string[] expected)
        {
            _checks++;
            List<string> found = Links.Find(text);

            bool same = found.Count == expected.Length;
            if (same)
                for (int i = 0; i < expected.Length; i++)
                    if (found[i] != expected[i]) same = false;

            if (same) return;

            _failures++;
            Console.WriteLine("  FAIL {0}", Show(text));
            Console.WriteLine("       expected [{0}], got [{1}]",
                              string.Join(", ", expected), string.Join(", ", found.ToArray()));
        }

        private static void Rebuilds(string text)
        {
            _checks++;

            string rebuilt = "";
            foreach (TextPart part in Links.Split(text)) rebuilt += part.Text;

            if (rebuilt == text) return;

            _failures++;
            Console.WriteLine("  FAIL rebuild {0}", Show(text));
            Console.WriteLine("       got {0}", Show(rebuilt));
        }

        private static string Show(string text)
        {
            return "\"" + text + "\"";
        }
    }
}
