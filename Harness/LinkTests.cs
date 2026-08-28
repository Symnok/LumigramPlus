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

            // The split has to put the message back together unchanged, or the text
            // on screen would quietly differ from the text that was sent.
            Rebuilds("see https://example.com. thanks");
            Rebuilds("plain text only");
            Rebuilds("https://one.com and https://two.com");

            Console.WriteLine("  {0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
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
