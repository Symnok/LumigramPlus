using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// Turning a number as an address book stores it into the forms Telegram can
    /// look up.
    ///
    /// Telegram knows people by international number. Address books mostly hold
    /// local ones - "(617) 555-1234", "07911 123456", "8 916 123-45-67" - and those,
    /// sent as they are, find nobody. The country they are local to is the user's
    /// own, which Telegram already knows from the account's number; the calling
    /// codes to recognise it by come from Telegram too (help.getCountriesList, the
    /// list its own sign-in screens use), so there is no table here to go stale.
    /// </summary>
    public static class PhoneNumbers
    {
        /// <summary>Every calling code Telegram knows, without the plus.</summary>
        public static async Task<List<string>> GetCallingCodesAsync(MtprotoClient client,
                                                                    ClientInfo info = null)
        {
            var q = new TlWriter(16);
            q.WriteConstructor(TlConstructors.HelpGetCountriesList)
             .WriteString("en")
             .WriteInt(0);                      // hash: nothing cached

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            return ReadCallingCodes(TlSchema.ReadObject(r));
        }

        /// <summary>Reads the calling codes out of a help.countriesList.</summary>
        public static List<string> ReadCallingCodes(TlObject list)
        {
            var codes = new List<string>();
            if (list == null || !list.Has("countries")) return codes;

            foreach (object o in list.Vec("countries"))
            {
                var country = o as TlObject;
                if (country == null || !country.Has("country_codes")) continue;

                foreach (object c in country.Vec("country_codes"))
                {
                    var code = c as TlObject;
                    if (code == null || !code.Has("country_code")) continue;

                    string digits = Digits(code.Str("country_code"));
                    if (digits.Length > 0 && !codes.Contains(digits)) codes.Add(digits);
                }
            }

            return codes;
        }

        /// <summary>This account's own number, digits only, or null if it is hidden.</summary>
        public static async Task<string> GetSelfPhoneAsync(MtprotoClient client,
                                                           ClientInfo info = null)
        {
            var q = new TlWriter(24);
            q.WriteConstructor(TlConstructors.UsersGetUsers)
             .WriteConstructor(TlConstructors.Vector)
             .WriteInt(1)
             .WriteConstructor(TlConstructors.InputUserSelf);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);

            if (r.ReadConstructor() != TlConstructors.Vector) return null;
            if (r.ReadInt() < 1) return null;

            TlObject user = TlSchema.ReadObject(r);
            if (!user.Has("phone")) return null;

            string phone = Digits(user.Str("phone"));
            return phone.Length > 0 ? phone : null;
        }

        /// <summary>
        /// The calling code a full international number starts with, or null.
        ///
        /// Calling codes are meant to be prefix-free, but the longest match is taken
        /// anyway, so a list that carried both "1" and "1684" would still pick the
        /// more specific one.
        /// </summary>
        public static string CodeOf(string international, ICollection<string> codes)
        {
            if (string.IsNullOrEmpty(international) || codes == null) return null;

            for (int length = Math.Min(4, international.Length); length >= 1; length--)
            {
                string head = international.Substring(0, length);
                if (codes.Contains(head)) return head;
            }

            return null;
        }

        /// <summary>
        /// The calling code to put in front of the user's own local numbers.
        ///
        /// Usually just <see cref="CodeOf"/>, but Telegram lists the Caribbean and
        /// Pacific parts of the North American plan as codes of their own - 1876,
        /// 1684 - while locally everyone there dials ten digits, area code first,
        /// exactly as in the US. So every "1..." comes back as plain 1.
        /// </summary>
        public static string OwnCode(string selfPhone, ICollection<string> codes)
        {
            string code = CodeOf(Digits(selfPhone), codes);
            if (code != null && code.StartsWith("1")) return "1";
            return code;
        }

        /// <summary>
        /// The numbers to try for one address-book entry, most likely first.
        ///
        /// Written internationally - a plus, or 00 - it can only mean one thing.
        /// Otherwise it may be international without the plus ("16175551234") or
        /// local to the user's country, and both are offered, local first when it
        /// starts with the trunk 0 that only a local number has. Russia and
        /// Kazakhstan dial a trunk 8 instead of 0, and that is handled as well,
        /// since an 8 there is never the start of an international number.
        ///
        /// <paramref name="ownCode"/> is the user's calling code; without it only
        /// the number as written can be tried.
        /// </summary>
        public static List<string> Candidates(string raw, string ownCode)
        {
            var tries = new List<string>();
            if (string.IsNullOrEmpty(raw)) return tries;

            string trimmed = raw.Trim();
            string digits = Digits(trimmed);
            if (digits.Length == 0) return tries;

            if (trimmed.StartsWith("+"))
            {
                tries.Add(digits);
                return tries;
            }

            if (digits.StartsWith("00") && digits.Length > 4)
            {
                tries.Add(digits.Substring(2));
                return tries;
            }

            bool hasCode = !string.IsNullOrEmpty(ownCode);

            if (digits.StartsWith("0"))
            {
                // A trunk 0: only ever a local number.
                if (hasCode) tries.Add(ownCode + digits.Substring(1));
                return tries;
            }

            if (hasCode && ownCode == "7" && digits.StartsWith("8") && digits.Length == 11)
            {
                tries.Add("7" + digits.Substring(1));
                return tries;
            }

            // Already international without the plus, or local with no trunk
            // prefix (North America): try it as written, then as local.
            tries.Add(digits);
            if (hasCode && !digits.StartsWith(ownCode)) tries.Add(ownCode + digits);

            return tries;
        }

        public static string Digits(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                if (c >= '0' && c <= '9') sb.Append(c);

            return sb.ToString();
        }
    }
}
