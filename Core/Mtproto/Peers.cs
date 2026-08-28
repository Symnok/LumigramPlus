using System;
using System.Collections.Generic;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>Who someone is, as far as one response was able to say.</summary>
    public sealed class PeerInfo
    {
        public long Id;
        public long AccessHash;
        public string Name;

        /// <summary>Their profile photo, or 0 when they have none set.</summary>
        public long PhotoId;

        /// <summary>Which datacenter holds it.</summary>
        public int PhotoDcId;

        /// <summary>
        /// Initials for the two-letter circle drawn when there is no photo, or none
        /// yet. Better than an empty disc, and it is what every other client falls
        /// back to.
        /// </summary>
        public string Initials
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return "?";

                string[] words = Name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) return "?";
                if (words.Length == 1) return words[0].Substring(0, 1).ToUpper();

                return (words[0].Substring(0, 1) + words[words.Length - 1].Substring(0, 1)).ToUpper();
            }
        }
    }

    /// <summary>
    /// The people mentioned by a response.
    ///
    /// Almost every reply carries a users vector alongside whatever was asked for,
    /// because messages identify their sender by id alone. Throwing it away is what
    /// made group chats unreadable: the messages were all there, with no way to tell
    /// who had written any of them.
    /// </summary>
    public static class Peers
    {
        /// <summary>Reads the users out of any response that carries some.</summary>
        public static Dictionary<long, PeerInfo> Read(TlObject response)
        {
            var found = new Dictionary<long, PeerInfo>();
            if (response == null || !response.Has("users")) return found;

            foreach (object o in response.Vec("users"))
            {
                var u = o as TlObject;
                if (u == null || !u.Has("id")) continue;

                long id = u.Long("id");

                var peer = new PeerInfo
                {
                    Id = id,
                    AccessHash = u.Has("access_hash") ? u.Long("access_hash") : 0,
                    Name = DisplayName(u, id),
                };

                if (u.Has("photo"))
                {
                    TlObject photo = u.Obj("photo");
                    if (photo.Ctor == TlConstructors.UserProfilePhoto)
                    {
                        peer.PhotoId = photo.Has("photo_id") ? photo.Long("photo_id") : 0;
                        peer.PhotoDcId = photo.IntOr("dc_id", 0);
                    }
                }

                found[id] = peer;
            }

            return found;
        }

        private static string DisplayName(TlObject user, long id)
        {
            string first = Str(user, "first_name");
            string last = Str(user, "last_name");

            string name = (first + " " + last).Trim();
            if (name.Length > 0) return name;

            name = Str(user, "username");
            if (name.Length > 0) return name;

            return "user " + id;
        }

        private static string Str(TlObject o, string field)
        {
            if (!o.Has(field)) return "";
            string v = o.Str(field);
            return v ?? "";
        }

        /// <summary>
        /// Where to fetch someone's profile picture from.
        ///
        /// The small size, not the big one: these are drawn at the height of a line
        /// of text, and the large version is several times the download for no
        /// visible difference.
        /// </summary>
        public static byte[] PhotoLocation(PeerInfo peer, string kind)
        {
            if (peer == null || peer.PhotoId == 0) return null;

            var w = new TlWriter(48);
            w.WriteConstructor(TlConstructors.InputPeerPhotoFileLocation)
             .WriteInt(0)                       // flags: small
             // The peer has to be addressed as what it is: a group picture asked for
             // as though it belonged to a user is simply not found.
             .WriteRaw(Messages.InputPeerFor(kind ?? "user", peer.Id, peer.AccessHash))
             .WriteLong(peer.PhotoId);
            return w.ToArray();
        }
    }
}
