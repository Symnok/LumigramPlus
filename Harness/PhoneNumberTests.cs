using System;
using System.Collections.Generic;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// Address-book numbers into the international forms Telegram looks people up by.
    ///
    /// The cases are the ways real address books hold numbers: with a plus, with
    /// 00, with a national trunk 0 or 8, with no prefix at all, and full of the
    /// spaces, dashes and brackets people type.
    /// </summary>
    internal static class PhoneNumberTests
    {
        private static int _checks;
        private static int _failures;

        public static bool RunAll()
        {
            _checks = 0;
            _failures = 0;

            // Written internationally: exactly one reading.
            Same("plus", new[] { "447911123456" }, PhoneNumbers.Candidates("+44 7911 123456", "1"));
            Same("plus, own code irrelevant", new[] { "16175551234" },
                 PhoneNumbers.Candidates("+1 (617) 555-1234", "44"));
            Same("00", new[] { "4930123456" }, PhoneNumbers.Candidates("0049 30 123456", "7"));
            Same("plus, no own code", new[] { "972501234567" },
                 PhoneNumbers.Candidates("+972-50-123-4567", null));

            // A trunk 0 is always local.
            Same("UK local", new[] { "447911123456" }, PhoneNumbers.Candidates("07911 123456", "44"));
            Same("Israel local", new[] { "972501234567" },
                 PhoneNumbers.Candidates("050-123-4567", "972"));
            Same("trunk 0, no own code: nothing to try", new string[0],
                 PhoneNumbers.Candidates("07911 123456", null));

            // Russia and Kazakhstan: a trunk 8.
            Same("Russia local 8", new[] { "79161234567" },
                 PhoneNumbers.Candidates("8 (916) 123-45-67", "7"));
            Same("Russia, 7 without plus", new[] { "79161234567" },
                 PhoneNumbers.Candidates("7 916 123 45 67", "7"));
            Same("8 elsewhere is not a trunk", new[] { "89161234567", "4489161234567" },
                 PhoneNumbers.Candidates("8 916 123 45 67", "44"));

            // No prefix: as written, then as local.
            Same("North America local", new[] { "6175551234", "16175551234" },
                 PhoneNumbers.Candidates("(617) 555-1234", "1"));
            Same("North America with 1", new[] { "16175551234" },
                 PhoneNumbers.Candidates("1-617-555-1234", "1"));
            Same("international without plus", new[] { "447911123456", "1447911123456" },
                 PhoneNumbers.Candidates("447911123456", "1"));
            Same("no prefix, no own code", new[] { "6175551234" },
                 PhoneNumbers.Candidates("617 555 1234", ""));

            // Nothing usable.
            Same("empty", new string[0], PhoneNumbers.Candidates("", "1"));
            Same("null", new string[0], PhoneNumbers.Candidates(null, "1"));
            Same("letters only", new string[0], PhoneNumbers.Candidates("call me", "1"));
            Same("lone plus", new string[0], PhoneNumbers.Candidates("+", "1"));

            Eq("digits", "79161234567", PhoneNumbers.Digits(" +7 (916) 123-45-67 ext"));

            // Which calling code a full number starts with.
            var codes = new List<string> { "1", "7", "44", "49", "972", "1684" };
            Eq("code: 1", "1", PhoneNumbers.CodeOf("16175551234", codes));
            Eq("code: 44", "44", PhoneNumbers.CodeOf("447911123456", codes));
            Eq("code: 972", "972", PhoneNumbers.CodeOf("972501234567", codes));
            Eq("code: 7", "7", PhoneNumbers.CodeOf("79161234567", codes));
            Eq("code: longest wins", "1684", PhoneNumbers.CodeOf("16841234567", codes));
            Eq("own code: NANP area folds to 1", "1", PhoneNumbers.OwnCode("+1 684 1234567", codes));
            Eq("own code: US", "1", PhoneNumbers.OwnCode("16175551234", codes));
            Eq("own code: Israel", "972", PhoneNumbers.OwnCode("972501234567", codes));
            Eq("own code: unknown", null, PhoneNumbers.OwnCode("999", codes));
            Eq("own code: hidden", null, PhoneNumbers.OwnCode(null, codes));
            Eq("code: unknown", null, PhoneNumbers.CodeOf("999123", codes));
            Eq("code: empty", null, PhoneNumbers.CodeOf("", codes));
            Eq("code: no list", null, PhoneNumbers.CodeOf("447911123456", null));

            // help.countriesList as the server sends it.
            {
                var w = new TlWriter(256);
                w.WriteConstructor(TlConstructors.HelpCountriesList)
                 .WriteConstructor(TlConstructors.Vector).WriteInt(3);

                Country(w, "GB", "United Kingdom", "44");
                Country(w, "US", "USA", "1");
                Country(w, "AS", "American Samoa", "1684", "1");   // shares a code with US

                w.WriteInt(12345);                                  // hash

                TlObject list = TlSchema.ReadObject(new TlReader(w.ToArray()));
                List<string> read = PhoneNumbers.ReadCallingCodes(list);
                Same("countries list codes", new[] { "44", "1", "1684" }, read);
            }

            {
                var w = new TlWriter(8);
                w.WriteConstructor(TlConstructors.HelpCountriesListNotModified);
                TlObject list = TlSchema.ReadObject(new TlReader(w.ToArray()));
                Same("not-modified has no codes", new string[0], PhoneNumbers.ReadCallingCodes(list));
            }

            Same("null list", new string[0], PhoneNumbers.ReadCallingCodes(null));

            // The request itself.
            {
                var q = new TlWriter(16);
                q.WriteConstructor(TlConstructors.HelpGetCountriesList).WriteString("en").WriteInt(0);
                var r = new TlReader(q.ToArray());
                Eq("getCountriesList ctor", TlConstructors.HelpGetCountriesList, r.ReadConstructor());
                Eq("getCountriesList lang", "en", r.ReadString());
                Eq("getCountriesList hash", 0, r.ReadInt());
            }

            Console.WriteLine("  phone numbers: {0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        /// <summary>
        /// Writes one help.country. A country may list several calling codes;
        /// the name is left out (flags.1) to cover the optional field.
        /// </summary>
        private static void Country(TlWriter w, string iso2, string name, params string[] codes)
        {
            w.WriteConstructor(TlConstructors.HelpCountry)
             .WriteInt(0)                                   // flags: not hidden, no name
             .WriteString(iso2)
             .WriteString(name)
             .WriteConstructor(TlConstructors.Vector).WriteInt(codes.Length);

            foreach (string code in codes)
            {
                // A code with prefixes (flags.0) and one without, both shapes.
                bool withPrefixes = code.Length > 2;
                w.WriteConstructor(TlConstructors.HelpCountryCode)
                 .WriteInt(withPrefixes ? 1 : 0)
                 .WriteString(code);

                if (withPrefixes)
                {
                    w.WriteConstructor(TlConstructors.Vector).WriteInt(2)
                     .WriteString("684").WriteString("685");
                }
            }
        }

        private static void Same(string what, IList<string> expected, IList<string> actual)
        {
            _checks++;
            string e = string.Join(",", expected);
            string a = actual == null ? "(null)" : string.Join(",", actual);
            if (e != a) Fail(what + ": got [" + a + "], expected [" + e + "]");
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
