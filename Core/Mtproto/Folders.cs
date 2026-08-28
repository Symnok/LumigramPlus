using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// One of the user's chat folders.
    ///
    /// A folder is a rule, not a container: chats are not moved into it, they are
    /// selected by it. That is why the membership is worked out here from the chat
    /// list rather than fetched - the server stores the rule and expects the client
    /// to apply it.
    ///
    /// The archive is not one of these. It really is a separate list, addressed by
    /// folder id, and is fetched rather than filtered.
    /// </summary>
    public sealed class ChatFolder
    {
        public int Id;
        public string Title;

        /// <summary>
        /// A shared folder, which names its chats explicitly and has no category
        /// rules at all.
        /// </summary>
        public bool ListedOnly;

        // Whole categories the folder takes in.
        public bool Contacts;
        public bool NonContacts;
        public bool Groups;
        public bool Broadcasts;
        public bool Bots;

        // ...and what it then leaves out again.
        public bool ExcludeMuted;
        public bool ExcludeRead;
        public bool ExcludeArchived;

        /// <summary>Named individually, whatever the categories say.</summary>
        public readonly List<long> Include = new List<long>();

        /// <summary>Kept out, whatever the categories say.</summary>
        public readonly List<long> Exclude = new List<long>();

        /// <summary>Named and shown first.</summary>
        public readonly List<long> Pinned = new List<long>();

        // Editing a folder means sending the whole thing back, so everything read
        // has to be kept in a form that can be written out again - not only the
        // parts this client happens to use. The peers are held as the bytes they
        // will be re-sent as, which is the one representation guaranteed to survive
        // the round trip whether or not the chat is one we have loaded.

        internal int RawFlags;
        internal string Emoticon;
        internal int Color;

        internal readonly List<byte[]> PinnedRaw = new List<byte[]>();
        internal readonly List<byte[]> IncludeRaw = new List<byte[]>();
        internal readonly List<byte[]> ExcludeRaw = new List<byte[]>();

        /// <summary>
        /// Whether this client is willing to edit it.
        ///
        /// Shared folders are somebody else's list; changing one here would be
        /// changing it for everyone it was shared with.
        /// </summary>
        public bool Editable { get { return !ListedOnly; } }
    }

    /// <summary>Reading the user's folders, and deciding what belongs in one.</summary>
    public static class Folders
    {
        /// <summary>The archive is addressed as a folder of its own.</summary>
        public const int ArchiveFolderId = 1;

        // dialogFilter flag bits.
        private const int Contacts = 1 << 0;
        private const int NonContacts = 1 << 1;
        private const int Groups = 1 << 2;
        private const int Broadcasts = 1 << 3;
        private const int Bots = 1 << 4;
        private const int ExcludeMuted = 1 << 11;
        private const int ExcludeRead = 1 << 12;
        private const int ExcludeArchived = 1 << 13;

        public static async Task<List<ChatFolder>> GetAsync(MtprotoClient client,
                                                            ClientInfo info = null)
        {
            var q = new TlWriter(8);
            q.WriteConstructor(TlConstructors.MessagesGetDialogFilters);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlObject response = TlSchema.ReadObject(r);

            var folders = new List<ChatFolder>();
            if (!response.Has("filters")) return folders;

            foreach (object o in response.Vec("filters"))
            {
                var f = o as TlObject;
                if (f == null) continue;

                // dialogFilterDefault is the main list, which is already a tab of its
                // own and is not a folder to show alongside the others.
                if (f.Ctor != TlConstructors.DialogFilter &&
                    f.Ctor != TlConstructors.DialogFilterChatlist) continue;

                int flags = f.IntOr("flags", 0);

                var folder = new ChatFolder
                {
                    Id = f.IntOr("id", 0),
                    Title = Title(f),
                    ListedOnly = f.Ctor == TlConstructors.DialogFilterChatlist,
                    Contacts = (flags & Contacts) != 0,
                    NonContacts = (flags & NonContacts) != 0,
                    Groups = (flags & Groups) != 0,
                    Broadcasts = (flags & Broadcasts) != 0,
                    Bots = (flags & Bots) != 0,
                    ExcludeMuted = (flags & ExcludeMuted) != 0,
                    ExcludeRead = (flags & ExcludeRead) != 0,
                    ExcludeArchived = (flags & ExcludeArchived) != 0,
                };

                folder.RawFlags = flags;
                folder.Emoticon = f.Has("emoticon") ? f.Str("emoticon") : null;
                folder.Color = f.IntOr("color", 0);

                Collect(f, "pinned_peers", folder.Pinned, folder.PinnedRaw);
                Collect(f, "include_peers", folder.Include, folder.IncludeRaw);
                Collect(f, "exclude_peers", folder.Exclude, folder.ExcludeRaw);

                folders.Add(folder);
            }

            return folders;
        }

        /// <summary>
        /// Whether a chat belongs in a folder.
        ///
        /// Named chats win over categories in both directions: something listed by
        /// hand is in even if no category covers it, and something excluded by hand
        /// is out even if one does.
        /// </summary>
        public static bool Contains(ChatFolder folder, DialogEntry dialog, int nowUtc)
        {
            if (folder == null || dialog == null) return false;

            if (folder.Exclude.Contains(dialog.PeerId)) return false;
            if (folder.Pinned.Contains(dialog.PeerId)) return true;
            if (folder.Include.Contains(dialog.PeerId)) return true;

            // A shared folder is only ever the chats it names.
            if (folder.ListedOnly) return false;

            if (folder.ExcludeArchived && dialog.Archived) return false;
            if (folder.ExcludeMuted && dialog.IsMuted(nowUtc)) return false;
            if (folder.ExcludeRead && dialog.UnreadCount == 0) return false;

            return InCategory(folder, dialog);
        }

        private static bool InCategory(ChatFolder folder, DialogEntry dialog)
        {
            if (dialog.Kind == "chat") return folder.Groups;

            if (dialog.Kind == "channel")
            {
                // Supergroups and broadcast channels are both "channel" on the wire.
                // Without the megagroup flag they cannot be told apart here, so a
                // folder asking for either takes both rather than silently dropping
                // chats the user can see in other clients.
                return folder.Broadcasts || folder.Groups;
            }

            if (dialog.IsBot) return folder.Bots;
            if (dialog.IsContact) return folder.Contacts;
            return folder.NonContacts;
        }

        private static string Title(TlObject filter)
        {
            if (!filter.Has("title")) return "folder";

            // title is a TextWithEntities; only the plain text is of interest, since
            // there is nowhere to render the formatting on a tab.
            TlObject title = filter.Obj("title");
            if (title != null && title.Has("text"))
            {
                string text = title.Str("text");
                if (!string.IsNullOrEmpty(text)) return text;
            }

            return "folder";
        }

        private static void Collect(TlObject filter, string field, List<long> ids,
                                    List<byte[]> raw)
        {
            if (!filter.Has(field)) return;

            foreach (object o in filter.Vec(field))
            {
                var peer = o as TlObject;
                if (peer == null) continue;

                long id = PeerId(peer);
                if (id == 0) continue;

                ids.Add(id);
                raw.Add(WriteInputPeer(peer));
            }
        }

        /// <summary>
        /// Turns a parsed InputPeer back into the bytes it came from.
        ///
        /// Only a handful of shapes exist and all are small, which is what makes
        /// this safe to do by hand - and doing it is what lets a folder be edited
        /// without knowing anything about the chats it already names.
        /// </summary>
        private static byte[] WriteInputPeer(TlObject peer)
        {
            var w = new TlWriter(24);

            if (peer.Has("user_id"))
            {
                w.WriteConstructor(TlConstructors.InputPeerUser)
                 .WriteLong(peer.Long("user_id"))
                 .WriteLong(peer.Has("access_hash") ? peer.Long("access_hash") : 0);
            }
            else if (peer.Has("channel_id"))
            {
                w.WriteConstructor(TlConstructors.InputPeerChannel)
                 .WriteLong(peer.Long("channel_id"))
                 .WriteLong(peer.Has("access_hash") ? peer.Long("access_hash") : 0);
            }
            else if (peer.Has("chat_id"))
            {
                w.WriteConstructor(TlConstructors.InputPeerChat)
                 .WriteLong(peer.Long("chat_id"));
            }
            else
            {
                w.WriteConstructor(TlConstructors.InputPeerEmpty);
            }

            return w.ToArray();
        }

        /// <summary>
        /// Moves a chat into the archive, or back out of it.
        ///
        /// The archive is a folder in the server's sense - a list a chat belongs to -
        /// so this is a move rather than a flag being set.
        /// </summary>
        public static async Task SetArchivedAsync(MtprotoClient client, byte[] inputPeer,
                                                  bool archived, ClientInfo info = null)
        {
            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.FoldersEditPeerFolders)
             .WriteConstructor(TlConstructors.Vector)
             .WriteInt(1)
             .WriteConstructor(TlConstructors.InputFolderPeer)
             .WriteRaw(inputPeer)
             .WriteInt(archived ? ArchiveFolderId : 0);

            await client.InvokeAsync(q.ToArray(), info);
        }

        /// <summary>Adds a chat to a folder, or takes it out again.</summary>
        public static async Task SetMembershipAsync(MtprotoClient client, ChatFolder folder,
                                                    byte[] inputPeer, long peerId, bool member,
                                                    ClientInfo info = null)
        {
            if (folder == null || !folder.Editable) return;

            Drop(folder.Include, folder.IncludeRaw, peerId);
            Drop(folder.Pinned, folder.PinnedRaw, peerId);
            Drop(folder.Exclude, folder.ExcludeRaw, peerId);

            if (member)
            {
                folder.Include.Add(peerId);
                folder.IncludeRaw.Add(inputPeer);
            }

            await SaveAsync(client, folder, info);
        }

        private static void Drop(List<long> ids, List<byte[]> raw, long peerId)
        {
            for (int i = ids.Count - 1; i >= 0; i--)
            {
                if (ids[i] != peerId) continue;

                ids.RemoveAt(i);
                if (i < raw.Count) raw.RemoveAt(i);
            }
        }

        /// <summary>
        /// Writes a folder back.
        ///
        /// The whole filter goes, not the change - there is no method for editing one
        /// piece of it. Every field read earlier is written out again, including the
        /// ones this client never looks at, because anything omitted is not left
        /// alone but cleared.
        /// </summary>
        public static async Task SaveAsync(MtprotoClient client, ChatFolder folder,
                                           ClientInfo info = null)
        {
            var filter = new TlWriter(256);
            filter.WriteConstructor(TlConstructors.DialogFilter)
                  .WriteInt(folder.RawFlags)
                  .WriteInt(folder.Id)
                  .WriteConstructor(TlConstructors.TextWithEntities)
                  .WriteString(folder.Title ?? "")
                  .WriteConstructor(TlConstructors.Vector)
                  .WriteInt(0);                       // no entities

            if ((folder.RawFlags & (1 << 25)) != 0) filter.WriteString(folder.Emoticon ?? "");
            if ((folder.RawFlags & (1 << 27)) != 0) filter.WriteInt(folder.Color);

            WritePeers(filter, folder.PinnedRaw);
            WritePeers(filter, folder.IncludeRaw);
            WritePeers(filter, folder.ExcludeRaw);

            var q = new TlWriter(320);
            q.WriteConstructor(TlConstructors.MessagesUpdateDialogFilter)
             .WriteInt(1)                             // flags: the filter is present
             .WriteInt(folder.Id)
             .WriteRaw(filter.ToArray());

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            r.ReadBool();
        }

        private static void WritePeers(TlWriter w, List<byte[]> peers)
        {
            w.WriteConstructor(TlConstructors.Vector).WriteInt(peers.Count);
            foreach (byte[] peer in peers) w.WriteRaw(peer);
        }

        /// <summary>
        /// The id inside an InputPeer, whichever kind it is. The field is named
        /// after the kind rather than being a single "id".
        /// </summary>
        private static long PeerId(TlObject peer)
        {
            if (peer.Has("user_id")) return peer.Long("user_id");
            if (peer.Has("chat_id")) return peer.Long("chat_id");
            if (peer.Has("channel_id")) return peer.Long("channel_id");
            return 0;
        }
    }
}
