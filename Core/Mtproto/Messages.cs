using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>One message, reduced to what a text-only client displays.</summary>
    public sealed class TextMessage
    {
        public int Id;
        public int Date;
        public bool Out;
        public long FromId;
        public long PeerId;
        public string Text;
        public string Note;          // set when the message is not plain text
        public MediaInfo Media;      // null when the message is text only

        /// <summary>
        /// True for group and channel messages.
        ///
        /// Carried on the message because notification policy depends on it and the
        /// UI layer has no other way to tell: a Peer id alone does not say what kind
        /// of peer it is.
        /// </summary>
        public bool IsGroup;

        public DateTime DateUtc
        {
            get { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Date); }
        }
    }

    /// <summary>A chat in the dialog list.</summary>
    public sealed class DialogEntry
    {
        public long PeerId;
        public long AccessHash;      // required to address users and channels
        public string Kind;          // user / chat / channel
        public string Title;
        public int TopMessageId;

        /// <summary>
        /// The newest message already read in this chat.
        ///
        /// Anything above it is unread. Kept so a conversation can open where the
        /// reading stopped rather than at the newest message, which is the
        /// difference between resuming and having to scroll back.
        /// </summary>
        public int ReadInboxMaxId;
        public int UnreadCount;
        public string LastText;

        /// <summary>
        /// When the server says notifications for this chat resume, as a unix time.
        ///
        /// Zero means not muted. Telegram expresses "muted" as a time in the future
        /// rather than as a flag, so muting for an hour and muting forever are the
        /// same field with different values.
        /// </summary>
        public int MutedUntil;

        /// <summary>Profile or chat picture, and where it lives. Zero when unset.</summary>
        public long PhotoId;
        public int PhotoDcId;

        /// <summary>
        /// When the newest message in this chat was sent.
        ///
        /// Kept because asking for the next page of chats means telling the server
        /// where the last one left off, and it identifies that point by date, message
        /// id and peer together.
        /// </summary>
        public int TopMessageDate;

        /// <summary>Which list it came from, since folders can exclude the archive.</summary>
        public bool Archived;

        /// <summary>
        /// What the user is, for folders that take in whole categories rather than
        /// naming chats. Both false for anything that is not a one-to-one chat.
        /// </summary>
        public bool IsBot;
        public bool IsContact;

        /// <summary>True if the mute is still in force.</summary>
        public bool IsMuted(int nowUtc) { return MutedUntil > nowUtc; }
    }

    /// <summary>
    /// Sending and reading text.
    ///
    /// Every response here is parsed by the generated schema walker
    /// (<see cref="TlSchema"/>) rather than by hand. That matters most for
    /// message#7600b9d3, whose thirty-odd optional fields reach a dozen further
    /// types: reading a whole vector of messages requires consuming each element
    /// exactly, because TL elements carry no length of their own.
    /// </summary>
    public static class Messages
    {
        /// <summary>Saved Messages - the account's own chat with itself.</summary>
        public static byte[] InputPeerSelf()
        {
            var w = new TlWriter(4);
            w.WriteConstructor(TlConstructors.InputPeerSelf);
            return w.ToArray();
        }

        public static byte[] InputPeerUser(long userId, long accessHash)
        {
            var w = new TlWriter(20);
            w.WriteConstructor(TlConstructors.InputPeerUser).WriteLong(userId).WriteLong(accessHash);
            return w.ToArray();
        }

        public static byte[] InputPeerChat(long chatId)
        {
            var w = new TlWriter(12);
            w.WriteConstructor(TlConstructors.InputPeerChat).WriteLong(chatId);
            return w.ToArray();
        }

        /// <summary>
        /// An InputChannel, which is not the same thing as an InputPeer for the
        /// same channel - channel-specific methods take this narrower type.
        /// </summary>
        public static byte[] InputChannel(long channelId, long accessHash)
        {
            var w = new TlWriter(24);
            w.WriteConstructor(TlConstructors.InputChannel).WriteLong(channelId).WriteLong(accessHash);
            return w.ToArray();
        }

        public static byte[] InputPeerChannel(long channelId, long accessHash)
        {
            var w = new TlWriter(20);
            w.WriteConstructor(TlConstructors.InputPeerChannel).WriteLong(channelId).WriteLong(accessHash);
            return w.ToArray();
        }

        /// <summary>
        /// Builds an InputPeer from what the dialog list gave us.
        ///
        /// Users and channels need an access_hash alongside the id - the id alone is
        /// not enough to address them, which is why DialogEntry carries it.
        /// </summary>
        public static byte[] InputPeerFor(DialogEntry dialog)
        {
            if (dialog.Kind == "chat") return InputPeerChat(dialog.PeerId);
            if (dialog.Kind == "channel") return InputPeerChannel(dialog.PeerId, dialog.AccessHash);
            return InputPeerUser(dialog.PeerId, dialog.AccessHash);
        }

        public static byte[] InputPeerFor(string kind, long peerId, long accessHash)
        {
            if (kind == "chat") return InputPeerChat(peerId);
            if (kind == "channel") return InputPeerChannel(peerId, accessHash);
            return InputPeerUser(peerId, accessHash);
        }

        public static byte[] InputPeerEmpty()
        {
            var w = new TlWriter(4);
            w.WriteConstructor(TlConstructors.InputPeerEmpty);
            return w.ToArray();
        }

        /// <summary>
        /// The id the server gave a message we just sent, or 0 if it did not say.
        ///
        /// Worth digging out rather than discarding, because the sender sees its own
        /// message twice without it: once as the copy shown immediately, and again
        /// when the same message comes back through updates. Only the id ties those
        /// two together - matching on the text would merge a message deliberately
        /// sent twice.
        ///
        /// A plain text send answers with updateShortSentMessage; anything with
        /// media answers with a container carrying updateMessageID.
        /// </summary>
        public static int SentMessageId(TlObject updates)
        {
            if (updates == null) return 0;

            if (updates.Ctor == TlConstructors.UpdateShortSentMessage)
                return updates.IntOr("id", 0);

            if (updates.Ctor == TlConstructors.UpdateMessageID)
                return updates.IntOr("id", 0);

            if (updates.Has("update"))
            {
                int id = SentMessageId(updates.Obj("update"));
                if (id != 0) return id;
            }

            if (updates.Has("updates"))
            {
                foreach (object o in updates.Vec("updates"))
                {
                    var inner = o as TlObject;
                    if (inner == null) continue;

                    int id = SentMessageId(inner);
                    if (id != 0) return id;
                }
            }

            return 0;
        }

        /// <summary>
        /// Sends text, returning the id the server assigned it.
        ///
        /// A reply is the same send with one extra field. reply_to is a boxed
        /// InputReplyTo rather than a bare message id - the type carries quoting and
        /// cross-chat replies that this client does not use, but the wire format has
        /// to be written whole regardless.
        /// </summary>
        public static async Task<int> SendTextAsync(MtprotoClient client, ICrypto crypto,
                                                    byte[] inputPeer, string text,
                                                    int replyToMsgId = 0)
        {
            long randomId = BitConverter.ToInt64(crypto.Random(8), 0);

            TlReader r = await client.InvokeAsync(
                SendTextBody(inputPeer, text, randomId, replyToMsgId));

            return SentMessageId(TlSchema.ReadObject(r));
        }

        /// <summary>
        /// The messages.sendMessage payload, separated so it can be checked without
        /// a connection. The reply field is the part worth checking: a boxed
        /// InputReplyTo written at the wrong offset is accepted by the writer and
        /// rejected only by the server.
        /// </summary>
        public static byte[] SendTextBody(byte[] inputPeer, string text, long randomId,
                                          int replyToMsgId)
        {
            var q = new TlWriter(text.Length + 64);
            q.WriteConstructor(TlConstructors.MessagesSendMessage)
             .WriteInt(replyToMsgId != 0 ? 1 : 0)   // flags.0: reply_to
             .WriteRaw(inputPeer);

            if (replyToMsgId != 0)
            {
                q.WriteConstructor(TlConstructors.InputReplyToMessage)
                 .WriteInt(0)                       // no top_msg_id, peer, or quote
                 .WriteInt(replyToMsgId);
            }

            q.WriteString(text)
             .WriteLong(randomId);

            return q.ToArray();
        }

        /// <summary>
        /// Copies messages into another chat.
        ///
        /// One random id per message: the server uses them to collapse a retried
        /// send, so sharing one across a batch would forward a single message.
        /// </summary>
        public static async Task ForwardMessagesAsync(MtprotoClient client, ICrypto crypto,
                                                      byte[] fromPeer, IList<int> ids,
                                                      byte[] toPeer, ClientInfo info = null)
        {
            if (ids == null || ids.Count == 0) return;

            var randomIds = new long[ids.Count];
            for (int i = 0; i < ids.Count; i++)
                randomIds[i] = BitConverter.ToInt64(crypto.Random(8), 0);

            TlReader r = await client.InvokeAsync(
                ForwardBody(fromPeer, ids, randomIds, toPeer), info);

            TlSchema.ReadObject(r);            // Updates
        }

        /// <summary>
        /// The messages.forwardMessages payload. Separated for the same reason as
        /// the send: two vectors that have to be the same length and in the same
        /// order is exactly the kind of thing that is wrong once and never noticed.
        /// </summary>
        public static byte[] ForwardBody(byte[] fromPeer, IList<int> ids,
                                         IList<long> randomIds, byte[] toPeer)
        {
            var q = new TlWriter(64 + ids.Count * 12);
            q.WriteConstructor(TlConstructors.MessagesForwardMessages)
             .WriteInt(0)                       // no silent, no drop_author
             .WriteRaw(fromPeer);

            q.WriteConstructor(TlConstructors.Vector).WriteInt(ids.Count);
            foreach (int id in ids) q.WriteInt(id);

            q.WriteConstructor(TlConstructors.Vector).WriteInt(randomIds.Count);
            foreach (long id in randomIds) q.WriteLong(id);

            q.WriteRaw(toPeer);

            return q.ToArray();
        }

        /// <summary>
        /// Tells the server this client is at the keyboard.
        ///
        /// Costs one small request and changes what the server is willing to send.
        /// An incoming call is only pushed to sessions the server believes someone
        /// is looking at; a client that never reports its status is not one of them,
        /// however busy its connection is with polling.
        /// </summary>
        public static async Task SetOnlineAsync(MtprotoClient client, bool online,
                                                ClientInfo info = null)
        {
            var q = new TlWriter(12);
            q.WriteConstructor(TlConstructors.AccountUpdateStatus)
             .WriteBool(!online);          // the field is offline, not online

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlSchema.ReadObject(r);
        }

        /// <summary>
        /// Ends this authorisation on the server.
        ///
        /// This is not the same as forgetting the key locally: the session must be
        /// revoked server-side, or it keeps showing up under Settings -> Devices and
        /// remains usable by anyone holding the stored key. The local key must be
        /// deleted too, and that is the caller's job.
        /// </summary>
        public static async Task<bool> LogOutAsync(MtprotoClient client, ClientInfo info = null)
        {
            var q = new TlWriter(8);
            q.WriteConstructor(TlConstructors.AuthLogOut);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlObject result = TlSchema.ReadObject(r);
            return result.Ctor == TlConstructors.AuthLoggedOut;
        }

        /// <summary>The signed-in user, for display in the UI.</summary>
        public static async Task<string> GetSelfNameAsync(MtprotoClient client, ClientInfo info = null)
        {
            var q = new TlWriter(24);
            q.WriteConstructor(TlConstructors.UsersGetUsers)
             .WriteConstructor(TlConstructors.Vector)
             .WriteInt(1)
             .WriteConstructor(TlConstructors.InputUserSelf);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);

            uint vec = r.ReadConstructor();
            if (vec != TlConstructors.Vector) return null;
            if (r.ReadInt() < 1) return null;

            TlObject user = TlSchema.ReadObject(r);
            string name = Join(SafeStr(user, "first_name"), SafeStr(user, "last_name"));
            if (string.IsNullOrEmpty(name)) name = SafeStr(user, "username");
            if (string.IsNullOrEmpty(name)) name = SafeStr(user, "phone");
            return name;
        }

        /// <summary>Reads recent messages newest-first, in a single request.</summary>
        /// <summary>Recent messages, together with the people who wrote them.</summary>
        public sealed class History
        {
            public List<TextMessage> Messages = new List<TextMessage>();

            /// <summary>Sender id to who they are. Empty for a one-to-one chat.</summary>
            public Dictionary<long, PeerInfo> Senders = new Dictionary<long, PeerInfo>();
        }

        /// <summary>
        /// Reads history and keeps the users that came with it.
        ///
        /// A message names its sender by id and nothing else, so in a group the
        /// accompanying users vector is the only thing that says who wrote it.
        /// Discarding it - which is what this did - left every group message
        /// anonymous.
        /// </summary>
        public static async Task<History> GetHistoryAsync(MtprotoClient client,
                                                          byte[] inputPeer, int count,
                                                          ClientInfo info = null)
        {
            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.MessagesGetHistory)
             .WriteRaw(inputPeer)
             .WriteInt(0).WriteInt(0).WriteInt(0)
             .WriteInt(count)
             .WriteInt(0).WriteInt(0)
             .WriteLong(0);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlObject response = TlSchema.ReadObject(r);

            var history = new History { Senders = Peers.Read(response) };
            foreach (object o in response.Vec("messages"))
                history.Messages.Add(ToTextMessage((TlObject)o));

            return history;
        }

        public static async Task<List<TextMessage>> GetRecentAsync(MtprotoClient client,
                                                                   byte[] inputPeer, int count)
        {
            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.MessagesGetHistory)
             .WriteRaw(inputPeer)
             .WriteInt(0)                      // offset_id: from the newest
             .WriteInt(0)                      // offset_date
             .WriteInt(0)                      // add_offset
             .WriteInt(count)                  // limit
             .WriteInt(0)                      // max_id
             .WriteInt(0)                      // min_id
             .WriteLong(0);                    // hash

            TlReader r = await client.InvokeAsync(q.ToArray());
            TlObject response = TlSchema.ReadObject(r);

            var result = new List<TextMessage>();
            foreach (object o in response.Vec("messages"))
                result.Add(ToTextMessage((TlObject)o));

            return result;
        }

        /// <summary>
        /// Marks a chat read up to <paramref name="maxId"/>.
        ///
        /// The unread count belongs to the server, not to us. Zeroing it locally
        /// makes it reappear on the next getDialogs; this is what actually clears it,
        /// and it is also what stops other devices showing the chat as unread.
        /// </summary>
        /// <summary>
        /// Mutes or unmutes a chat on the server.
        ///
        /// The setting belongs to the account, not to this phone: muting here should
        /// mute everywhere, and a chat muted on another device should be quiet here
        /// without the user saying so twice. That is also why it is read back from
        /// the dialog list rather than stored locally as a preference.
        ///
        /// Muting is expressed as a time the mute expires. Telegram's own clients use
        /// the largest value the field holds for "forever", which is what an
        /// indefinite mute means here.
        /// </summary>
        public static async Task SetMutedAsync(MtprotoClient client, string kind,
                                               long peerId, long accessHash, bool muted,
                                               ClientInfo info = null)
        {
            var settings = new TlWriter(32);
            settings.WriteConstructor(TlConstructors.InputPeerNotifySettings)
                    .WriteInt(1 << 2)                       // only mute_until is set
                    .WriteInt(muted ? int.MaxValue : 0);

            var q = new TlWriter(96);
            q.WriteConstructor(TlConstructors.AccountUpdateNotifySettings)
             .WriteConstructor(TlConstructors.InputNotifyPeer)
             .WriteRaw(InputPeerFor(kind ?? "user", peerId, accessHash))
             .WriteRaw(settings.ToArray());

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            r.ReadBool();
        }

        /// <summary>
        /// Deletes messages, for this account alone or for everyone.
        ///
        /// Channels have their own method here, the same way they do for reading -
        /// and it takes no revoke flag, because removing a message from a channel
        /// always removes it for everybody and needs the rights to do so. Asking for
        /// <paramref name="revoke"/> on a channel is therefore not refused, it is
        /// simply not a choice that exists.
        ///
        /// A failure matters to the caller: "delete for everyone" can be refused
        /// when the message is too old or was sent by someone else, and a message
        /// that silently stays put is worse than being told why.
        /// </summary>
        public static async Task DeleteMessagesAsync(MtprotoClient client, string kind,
                                                     long peerId, long accessHash,
                                                     IList<int> ids, bool revoke,
                                                     ClientInfo info = null)
        {
            if (ids == null || ids.Count == 0) return;

            var q = new TlWriter(32 + ids.Count * 4);

            if (kind == "channel")
            {
                q.WriteConstructor(TlConstructors.ChannelsDeleteMessages)
                 .WriteRaw(InputChannel(peerId, accessHash));
            }
            else
            {
                q.WriteConstructor(TlConstructors.MessagesDeleteMessages)
                 .WriteInt(revoke ? 1 : 0);
            }

            q.WriteConstructor(TlConstructors.Vector).WriteInt(ids.Count);
            foreach (int id in ids) q.WriteInt(id);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlSchema.ReadObject(r);            // messages.affectedMessages
        }

        /// <summary>
        /// Marks a chat read, using whichever method the peer actually accepts.
        ///
        /// Channels do not take messages.readHistory - they have their own method,
        /// over an InputChannel rather than an InputPeer. Sending the wrong one
        /// fails, and since a failed read is not worth interrupting anyone about,
        /// the only symptom was a channel badge that never cleared.
        /// </summary>
        public static async Task MarkReadAsync(MtprotoClient client, string kind,
                                               long peerId, long accessHash,
                                               int maxId, ClientInfo info = null)
        {
            if (kind == "channel")
            {
                var q = new TlWriter(32);
                q.WriteConstructor(TlConstructors.ChannelsReadHistory)
                 .WriteRaw(InputChannel(peerId, accessHash))
                 .WriteInt(maxId);

                TlReader r = await client.InvokeAsync(q.ToArray(), info);

                // Bool, not a schema object: boolTrue and boolFalse are built into
                // the language rather than declared in it, so they are absent from
                // the generated table and ReadObject would reject them.
                r.ReadBool();
                return;
            }

            await ReadHistoryAsync(client, InputPeerFor(kind ?? "user", peerId, accessHash),
                                   maxId, info);
        }

        public static async Task ReadHistoryAsync(MtprotoClient client, byte[] inputPeer,
                                                  int maxId, ClientInfo info = null)
        {
            var q = new TlWriter(32);
            q.WriteConstructor(TlConstructors.MessagesReadHistory)
             .WriteRaw(inputPeer)
             .WriteInt(maxId);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlSchema.ReadObject(r);            // messages.affectedMessages; nothing needed from it
        }

        /// <summary>
        /// Clears a chat, or removes it entirely.
        ///
        /// <paramref name="justClear"/> empties the history but leaves the chat in
        /// the list. Without it the dialog itself disappears. <paramref name="revoke"/>
        /// deletes the messages for the other participant too - which cannot be
        /// undone, and is why the caller is expected to have asked first.
        ///
        /// The server answers with an offset and may need calling repeatedly until
        /// it reports nothing left to remove.
        /// </summary>
        public static async Task DeleteHistoryAsync(MtprotoClient client, byte[] inputPeer,
                                                    bool justClear, bool revoke,
                                                    ClientInfo info = null)
        {
            int flags = (justClear ? 1 : 0) | (revoke ? 2 : 0);

            for (int pass = 0; pass < 20; pass++)
            {
                var q = new TlWriter(32);
                q.WriteConstructor(TlConstructors.MessagesDeleteHistory)
                 .WriteInt(flags)
                 .WriteRaw(inputPeer)
                 .WriteInt(0);                  // max_id: everything

                TlReader r = await client.InvokeAsync(q.ToArray(), info);
                TlObject affected = TlSchema.ReadObject(r);

                // offset > 0 means there is more to remove than one call covered.
                if (affected.IntOr("offset", 0) <= 0) return;
            }
        }

        /// <summary>The chat list, most recent first.</summary>
        /// <summary>One page of the chat list.</summary>
        public sealed class DialogPage
        {
            public List<DialogEntry> Entries = new List<DialogEntry>();

            /// <summary>
            /// Whether the server says there are more beyond this page.
            ///
            /// It answers with a slice when it has held some back and with a plain
            /// list when that is everything, so this is the server's own answer
            /// rather than a guess from the number returned.
            /// </summary>
            public bool HasMore;
        }

        public static async Task<List<DialogEntry>> GetDialogsAsync(MtprotoClient client, int limit)
        {
            DialogPage page = await GetDialogPageAsync(client, limit, 0, 0, null);
            return page.Entries;
        }

        /// <summary>
        /// Reads the chat list, starting after a given point.
        ///
        /// Passing zeros and a null peer asks for the newest chats. To continue,
        /// hand back the date, top message id and peer of the last entry received:
        /// all three are needed, because chats are ordered by the time of their last
        /// message and several can share one.
        /// </summary>
        public static async Task<DialogPage> GetDialogPageAsync(MtprotoClient client, int limit,
                                                                int offsetDate, int offsetId,
                                                                byte[] offsetPeer,
                                                                ClientInfo info = null,
                                                                int folderId = 0)
        {
            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.MessagesGetDialogs)
             // folder_id is always sent, including the zero that means the main
             // list. Leaving it out does not mean "the main list" - it means no
             // folder was asked for, and the server answers with every folder at
             // once, which puts the archived chats back among the ordinary ones.
             .WriteInt(1 << 1)
             .WriteInt(folderId)
             .WriteInt(offsetDate)
             .WriteInt(offsetId)
             .WriteRaw(offsetPeer ?? InputPeerEmpty())
             .WriteInt(limit)
             .WriteLong(0);                    // hash

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlObject response = TlSchema.ReadObject(r);

            // Titles live in the users and chats vectors, keyed by id; the dialog
            // entries themselves carry only a Peer.
            var titles = new Dictionary<string, string>();
            var lastText = new Dictionary<string, string>();
            var lastDate = new Dictionary<string, int>();
            var contacts = new List<long>();
            var bots = new List<long>();
            var hashes = new Dictionary<string, long>();
            var photos = new Dictionary<string, long>();
            var photoDcs = new Dictionary<string, int>();

            foreach (object o in response.Vec("users"))
            {
                var u = (TlObject)o;
                if (!u.Has("id")) continue;
                string name = Join(SafeStr(u, "first_name"), SafeStr(u, "last_name"));
                if (string.IsNullOrEmpty(name)) name = SafeStr(u, "username");
                if (string.IsNullOrEmpty(name)) name = "user " + u.Long("id");
                titles["user:" + u.Long("id")] = name;
                if (u.Has("access_hash")) hashes["user:" + u.Long("id")] = u.Long("access_hash");

                // Flags rather than fields: contact is bit 11 and bot is bit 14, and
                // neither appears in the generated table because both are true-flags.
                int userFlags = u.IntOr("flags", 0);
                if ((userFlags & (1 << 11)) != 0) contacts.Add(u.Long("id"));
                if ((userFlags & (1 << 14)) != 0) bots.Add(u.Long("id"));

                if (u.Has("photo"))
                {
                    TlObject p = u.Obj("photo");
                    if (p.Ctor == TlConstructors.UserProfilePhoto && p.Has("photo_id"))
                    {
                        photos["user:" + u.Long("id")] = p.Long("photo_id");
                        photoDcs["user:" + u.Long("id")] = p.IntOr("dc_id", 0);
                    }
                }
            }

            foreach (object o in response.Vec("chats"))
            {
                var c = (TlObject)o;
                if (!c.Has("id")) continue;
                string title = SafeStr(c, "title");
                titles["chat:" + c.Long("id")] = string.IsNullOrEmpty(title)
                    ? "chat " + c.Long("id") : title;
                titles["channel:" + c.Long("id")] = titles["chat:" + c.Long("id")];

                if (c.Has("photo"))
                {
                    TlObject p = c.Obj("photo");
                    if (p.Ctor == TlConstructors.ChatPhoto && p.Has("photo_id"))
                    {
                        photos["chat:" + c.Long("id")] = p.Long("photo_id");
                        photos["channel:" + c.Long("id")] = p.Long("photo_id");
                        photoDcs["chat:" + c.Long("id")] = p.IntOr("dc_id", 0);
                        photoDcs["channel:" + c.Long("id")] = p.IntOr("dc_id", 0);
                    }
                }
                if (c.Has("access_hash"))
                {
                    long h = c.Long("access_hash");
                    hashes["chat:" + c.Long("id")] = h;
                    hashes["channel:" + c.Long("id")] = h;
                }
            }

            foreach (object o in response.Vec("messages"))
            {
                var m = (TlObject)o;
                if (m.Ctor != TlConstructors.Message) continue;
                TextMessage tm = ToTextMessage(m);
                string key = PeerKey(m.Obj("peer_id"));
                if (key == null) continue;
                if (tm.Text != null) lastText[key] = tm.Text;
                lastDate[key] = m.IntOr("date", 0);
            }

            var result = new List<DialogEntry>();
            foreach (object o in response.Vec("dialogs"))
            {
                var d = (TlObject)o;
                if (!d.Has("peer")) continue;

                TlObject peer = d.Obj("peer");
                string key = PeerKey(peer);
                if (key == null) continue;

                string title;
                titles.TryGetValue(key, out title);

                string text;
                lastText.TryGetValue(key, out text);

                long accessHash;
                hashes.TryGetValue(key, out accessHash);

                int mutedUntil = 0;
                if (d.Has("notify_settings"))
                    mutedUntil = d.Obj("notify_settings").IntOr("mute_until", 0);

                long photoId = 0;
                int photoDc = 0;
                if (photos.ContainsKey(key))
                {
                    photoId = photos[key];
                    photoDc = photoDcs.ContainsKey(key) ? photoDcs[key] : 0;
                }

                result.Add(new DialogEntry
                {
                    PeerId = PeerId(peer),
                    AccessHash = accessHash,
                    Kind = key.Substring(0, key.IndexOf(':')),
                    Title = title ?? key,
                    TopMessageId = d.IntOr("top_message", 0),
                    UnreadCount = d.IntOr("unread_count", 0),
                    LastText = text,
                    MutedUntil = mutedUntil,
                    PhotoId = photoId,
                    PhotoDcId = photoDc,
                    TopMessageDate = lastDate.ContainsKey(key) ? lastDate[key] : 0,
                    ReadInboxMaxId = d.IntOr("read_inbox_max_id", 0),
                    Archived = folderId != 0,
                    IsBot = bots.Contains(PeerId(peer)),
                    IsContact = contacts.Contains(PeerId(peer)),
                });
            }

            return new DialogPage
            {
                Entries = result,
                // The server answers with a slice when it has held some back and
                // with a plain list when that is everything, so this is its own
                // answer rather than a guess from the number returned.
                HasMore = response.Ctor == TlConstructors.MessagesDialogsSlice &&
                          result.Count > 0,
            };
        }

        public static TextMessage ToTextMessage(TlObject m)
        {
            var t = new TextMessage { Id = m.IntOr("id", 0) };

            if (m.Ctor == TlConstructors.MessageEmpty)
            {
                t.Note = "empty";
                return t;
            }

            if (m.Ctor == TlConstructors.MessageService)
            {
                t.Note = "service message";
                t.Date = m.IntOr("date", 0);
                return t;
            }

            t.Date = m.IntOr("date", 0);
            t.Out = m.Flag("flags", 1);
            t.Text = m.Has("message") ? m.Str("message") : null;

            if (m.Has("from_id")) t.FromId = PeerId(m.Obj("from_id"));
            if (m.Has("peer_id"))
            {
                TlObject peer = m.Obj("peer_id");
                t.PeerId = PeerId(peer);
                t.IsGroup = peer.Ctor == TlConstructors.PeerChat ||
                            peer.Ctor == TlConstructors.PeerChannel;
            }

            if (m.Has("media"))
            {
                t.Media = Lumigram.Mtproto.Media.FromMessage(m);
                t.Note = t.Media != null ? t.Media.Describe() : "media";
            }
            if (m.Has("fwd_from")) t.Note = (t.Note == null ? "" : t.Note + ", ") + "forwarded";
            if (m.Has("reply_to")) t.Note = (t.Note == null ? "" : t.Note + ", ") + "reply";

            return t;
        }

        private static string PeerKey(TlObject peer)
        {
            if (peer == null) return null;
            if (peer.Ctor == TlConstructors.PeerUser) return "user:" + peer.Long("user_id");
            if (peer.Ctor == TlConstructors.PeerChat) return "chat:" + peer.Long("chat_id");
            if (peer.Ctor == TlConstructors.PeerChannel) return "channel:" + peer.Long("channel_id");
            return null;
        }

        private static long PeerId(TlObject peer)
        {
            if (peer == null) return 0;
            if (peer.Ctor == TlConstructors.PeerUser) return peer.Long("user_id");
            if (peer.Ctor == TlConstructors.PeerChat) return peer.Long("chat_id");
            if (peer.Ctor == TlConstructors.PeerChannel) return peer.Long("channel_id");
            return 0;
        }

        private static string SafeStr(TlObject o, string name)
        {
            try { return o.Has(name) ? o.Str(name) : null; }
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
