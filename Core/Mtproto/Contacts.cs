using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>A person or chat found by lookup, ready to open a conversation with.</summary>
    public sealed class ResolvedPeer
    {
        public long PeerId;
        public long AccessHash;
        public string Kind;          // user / chat / channel
        public string Title;
        public string Username;
        public string Phone;
    }

    /// <summary>
    /// Finding someone to start a chat with.
    ///
    /// Two lookups, chosen by what the input looks like. Neither changes the
    /// account: contacts.resolvePhone is used rather than contacts.importContacts
    /// precisely because importing would quietly add the person to the user's
    /// contact list as a side effect of searching for them.
    /// </summary>
    public static class Contacts
    {
        /// <summary>
        /// Looks up a username or a phone number.
        ///
        /// Accepts what a person would actually type: "@name" or "name",
        /// "+1 999 000 1234" or "19990001234", and a t.me link.
        /// </summary>
        public static async Task<ResolvedPeer> ResolveAsync(MtprotoClient client, string query,
                                                            ClientInfo info = null)
        {
            if (string.IsNullOrEmpty(query)) throw new MtprotoException("nothing to look up");

            string trimmed = query.Trim();
            bool isPhone = LooksLikePhone(trimmed);

            var q = new TlWriter(64);
            if (isPhone)
            {
                q.WriteConstructor(TlConstructors.ContactsResolvePhone)
                 .WriteString(NormalisePhone(trimmed));
            }
            else
            {
                q.WriteConstructor(TlConstructors.ContactsResolveUsername)
                 .WriteInt(0)                       // flags: no referer
                 .WriteString(NormaliseUsername(trimmed));
            }

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlObject resolved = TlSchema.ReadObject(r);

            if (resolved.Ctor != TlConstructors.ContactsResolvedPeer)
                throw new MtprotoException("unexpected reply 0x" + resolved.Ctor.ToString("x8"));

            TlObject peer = resolved.Obj("peer");
            var result = new ResolvedPeer();

            if (peer.Ctor == TlConstructors.PeerUser)
            {
                result.Kind = "user";
                result.PeerId = peer.Long("user_id");
            }
            else if (peer.Ctor == TlConstructors.PeerChat)
            {
                result.Kind = "chat";
                result.PeerId = peer.Long("chat_id");
            }
            else if (peer.Ctor == TlConstructors.PeerChannel)
            {
                result.Kind = "channel";
                result.PeerId = peer.Long("channel_id");
            }
            else
            {
                throw new MtprotoException("unknown peer type");
            }

            // The access hash lives in the users/chats vectors, not in the Peer -
            // and without it the peer cannot be addressed at all.
            foreach (object o in resolved.Vec("users"))
            {
                var u = (TlObject)o;
                if (!u.Has("id") || u.Long("id") != result.PeerId) continue;

                if (u.Has("access_hash")) result.AccessHash = u.Long("access_hash");
                result.Title = Join(Safe(u, "first_name"), Safe(u, "last_name"));
                result.Username = Safe(u, "username");
                result.Phone = Safe(u, "phone");
            }

            foreach (object o in resolved.Vec("chats"))
            {
                var c = (TlObject)o;
                if (!c.Has("id") || c.Long("id") != result.PeerId) continue;

                if (c.Has("access_hash")) result.AccessHash = c.Long("access_hash");
                result.Title = Safe(c, "title");
            }

            if (string.IsNullOrEmpty(result.Title))
                result.Title = result.Username;
            if (string.IsNullOrEmpty(result.Title))
                result.Title = "id " + result.PeerId;

            return result;
        }

        /// <summary>
        /// True when the input is meant as a phone number.
        ///
        /// Usernames cannot begin with a digit and cannot contain a plus sign, so
        /// anything leading with either is a number. Separators people actually type
        /// - spaces, dashes, brackets - are ignored for the test.
        /// </summary>
        public static bool LooksLikePhone(string value)
        {
            string stripped = StripSeparators(value);
            if (stripped.Length == 0) return false;

            int start = stripped[0] == (char)43 ? 1 : 0;      // '+'
            if (start >= stripped.Length) return false;

            for (int i = start; i < stripped.Length; i++)
                if (stripped[i] < (char)48 || stripped[i] > (char)57) return false;

            return stripped.Length - start >= 5;
        }

        /// <summary>Digits only - the server wants no plus sign and no separators.</summary>
        public static string NormalisePhone(string value)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in value)
                if (c >= (char)48 && c <= (char)57) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Strips a leading at-sign, and accepts a pasted t.me link.</summary>
        public static string NormaliseUsername(string value)
        {
            string name = value.Trim();
            if (name.Length > 0 && name[0] == (char)64) name = name.Substring(1);   // '@'

            int slash = name.LastIndexOf((char)47);                                  // '/'
            if (slash >= 0 && slash + 1 < name.Length) name = name.Substring(slash + 1);

            return name;
        }

        private static string StripSeparators(string value)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in value)
            {
                if (c == (char)32 || c == (char)45 || c == (char)40 ||
                    c == (char)41 || c == (char)46) continue;     // space - ( ) .
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Safe(TlObject o, string field)
        {
            try { return o.Has(field) ? o.Str(field) : null; }
            catch (TlParseException) { return null; }
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b ?? "";
            if (string.IsNullOrEmpty(b)) return a;
            return a + " " + b;
        }
    }
}
