using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>One topic in a forum supergroup.</summary>
    public sealed class ForumTopic
    {
        /// <summary>
        /// The topic, and also a message id in the parent group.
        ///
        /// A topic is a thread hanging off the service message that created it, and
        /// that message's id is the topic's name everywhere: reading one is
        /// getReplies on this id, sending into one is a reply to it, and marking one
        /// read is readDiscussion on it. There is no separate topic identifier to
        /// confuse this with.
        /// </summary>
        public int Id;

        public string Title;
        public int Date;
        public int TopMessageId;

        /// <summary>The newest message already read, as for a chat.</summary>
        public int ReadInboxMaxId;
        public int UnreadCount;

        /// <summary>Colour of the default topic badge, when there is no emoji.</summary>
        public int IconColor;

        /// <summary>A custom emoji used as the badge, or 0 for the plain one.</summary>
        public long IconEmojiId;

        public bool Closed;
        public bool Pinned;
        public bool Hidden;

        /// <summary>
        /// When this topic's own mute expires, or 0 when it has no setting of its
        /// own and follows the forum.
        ///
        /// Expressed as a time rather than a flag, like a chat's - and read back
        /// from the server rather than stored here, because the setting belongs to
        /// the account and a topic muted on another device should be quiet here.
        /// </summary>
        public int MutedUntil;

        public bool IsMuted(int nowUtc) { return MutedUntil > nowUtc; }

        /// <summary>Text of the topic's newest message, when the server sent it.</summary>
        public string LastText;

        /// <summary>
        /// Whether this is the topic a forum has before anyone makes one.
        ///
        /// It behaves differently in one place that matters: a message sent to it
        /// carries no reply_to_msg_id, only the top_msg_id. Sending one addressed to
        /// message 1 is rejected, because in most forums message 1 is not a topic.
        /// </summary>
        public bool IsGeneral { get { return Id == Topics.GeneralTopicId; } }
    }

    /// <summary>Reading a forum's topics, and talking inside one.</summary>
    public static class Topics
    {
        /// <summary>
        /// The topic every forum starts with.
        ///
        /// Fixed by the server rather than chosen: enabling topics on a supergroup
        /// files everything already in it under id 1.
        /// </summary>
        public const int GeneralTopicId = 1;

        /// <summary>A page of topics, and whether the server held any back.</summary>
        public sealed class TopicPage
        {
            public List<ForumTopic> Topics = new List<ForumTopic>();

            /// <summary>
            /// How many topics the forum has in total, as the server counts them.
            /// </summary>
            public int Count;

            /// <summary>
            /// Where to continue from, as the three values getForumTopics wants
            /// back: the date, top message id and id of the last topic returned.
            /// All three, because topics are ordered by the time of their last
            /// message and several can share one.
            /// </summary>
            public int NextOffsetDate;
            public int NextOffsetId;
            public int NextOffsetTopic;

            public bool HasMore;
        }

        /// <summary>
        /// Reads a forum's topics, newest activity first.
        ///
        /// Pinned topics come back at the head of the list whatever their dates, as
        /// the server orders them; nothing is re-sorted here.
        /// </summary>
        public static async Task<TopicPage> GetAsync(MtprotoClient client, byte[] inputPeer,
                                                     int limit, int offsetDate = 0,
                                                     int offsetId = 0, int offsetTopic = 0,
                                                     ClientInfo info = null)
        {
            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.MessagesGetForumTopics)
             .WriteInt(0)                      // flags: no search string
             .WriteRaw(inputPeer)
             .WriteInt(offsetDate)
             .WriteInt(offsetId)
             .WriteInt(offsetTopic)
             .WriteInt(limit);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlObject response = TlSchema.ReadObject(r);

            var page = new TopicPage { Count = response.IntOr("count", 0) };
            if (!response.Has("topics")) return page;

            // Counted before the deleted ones are dropped. Whether the server held
            // anything back is a question about what it sent, not about what
            // survived the filter below.
            int returned = response.Vec("topics").Count;

            // The messages vector holds each topic's newest message, so the preview
            // line comes from here rather than from a second request per topic.
            var lastText = new Dictionary<int, string>();
            if (response.Has("messages"))
            {
                foreach (object o in response.Vec("messages"))
                {
                    var m = o as TlObject;
                    if (m == null || m.Ctor != TlConstructors.Message) continue;

                    TextMessage tm = Messages.ToTextMessage(m);
                    lastText[tm.Id] = tm.Text ?? tm.Note;
                }
            }

            foreach (object o in response.Vec("topics"))
            {
                var t = o as TlObject;
                if (t == null) continue;

                // forumTopicDeleted carries an id and nothing else. Read as a live
                // topic it would become a nameless row that opens an empty thread.
                if (t.Ctor != TlConstructors.ForumTopic) continue;

                var topic = new ForumTopic
                {
                    Id = t.IntOr("id", 0),
                    Title = t.Has("title") ? t.Str("title") : null,
                    Date = t.IntOr("date", 0),
                    TopMessageId = t.IntOr("top_message", 0),
                    ReadInboxMaxId = t.IntOr("read_inbox_max_id", 0),
                    UnreadCount = t.IntOr("unread_count", 0),
                    IconColor = t.IntOr("icon_color", 0),
                    IconEmojiId = t.Has("icon_emoji_id") ? t.Long("icon_emoji_id") : 0,

                    // All three are true-flags, which carry no field of their own -
                    // the bit in the flags word is the whole value. Flag takes the
                    // bit's number, not a mask.
                    Closed = t.Flag("flags", 2),
                    Pinned = t.Flag("flags", 3),
                    Hidden = t.Flag("flags", 6),
                };

                if (t.Has("notify_settings"))
                    topic.MutedUntil = t.Obj("notify_settings").IntOr("mute_until", 0);

                if (string.IsNullOrEmpty(topic.Title))
                    topic.Title = topic.IsGeneral ? "General" : "topic " + topic.Id;

                string text;
                if (lastText.TryGetValue(topic.TopMessageId, out text)) topic.LastText = text;

                page.Topics.Add(topic);
            }

            if (page.Topics.Count > 0)
            {
                ForumTopic last = page.Topics[page.Topics.Count - 1];
                page.NextOffsetDate = last.Date;
                page.NextOffsetId = last.TopMessageId;
                page.NextOffsetTopic = last.Id;
            }

            // getForumTopics answers with one shape whether or not it held any back,
            // so unlike the dialog list there is no slice to recognise. A short page
            // is the signal: the server fills the limit while it has topics left.
            //
            // Not "fewer than the total count" - deleted topics are counted by the
            // server and dropped here, so on a forum that has ever lost one that
            // test says there is more to fetch forever.
            page.HasMore = returned >= limit && page.Topics.Count > 0;

            return page;
        }

        /// <summary>
        /// Reads one topic's messages, newest first.
        ///
        /// getHistory would answer with the whole group: the server has no way to
        /// know which thread was wanted, because the topic is not a peer. getReplies
        /// names the thread by the message it hangs off, which is the topic id.
        /// </summary>
        public static async Task<Messages.History> GetHistoryAsync(MtprotoClient client,
                                                                   byte[] inputPeer, int topicId,
                                                                   int count,
                                                                   ClientInfo info = null)
        {
            TlReader r = await client.InvokeAsync(HistoryBody(inputPeer, topicId, count), info);
            TlObject response = TlSchema.ReadObject(r);

            return Messages.ReadHistory(response);
        }

        /// <summary>
        /// The messages.getReplies payload, separated so it can be checked without a
        /// connection. Eight ints in a row after the peer, and the two that are not
        /// zero are the two easiest to put in the wrong place.
        /// </summary>
        public static byte[] HistoryBody(byte[] inputPeer, int topicId, int count)
        {
            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.MessagesGetReplies)
             .WriteRaw(inputPeer)
             .WriteInt(topicId)                // msg_id: the thread, not an offset
             .WriteInt(0)                      // offset_id: from the newest
             .WriteInt(0)                      // offset_date
             .WriteInt(0)                      // add_offset
             .WriteInt(count)                  // limit
             .WriteInt(int.MaxValue)           // max_id: no ceiling
             .WriteInt(0)                      // min_id
             .WriteLong(0);                    // hash

            return q.ToArray();
        }

        /// <summary>
        /// Reads a topic's messages around one of them, rather than the newest.
        ///
        /// The same window as Messages.GetHistoryAroundAsync, asked of the thread
        /// instead of the whole group - see there for why add_offset is negative.
        /// </summary>
        public static async Task<Messages.History> GetHistoryAroundAsync(MtprotoClient client,
                                                                         byte[] inputPeer,
                                                                         int topicId,
                                                                         int messageId, int count,
                                                                         ClientInfo info = null)
        {
            TlReader r = await client.InvokeAsync(
                AroundBody(inputPeer, topicId, messageId, count), info);

            TlObject response = TlSchema.ReadObject(r);

            return Messages.ReadHistory(response);
        }

        /// <summary>The messages.getReplies payload for that window.</summary>
        public static byte[] AroundBody(byte[] inputPeer, int topicId, int messageId, int count)
        {
            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.MessagesGetReplies)
             .WriteRaw(inputPeer)
             .WriteInt(topicId)                // msg_id: the thread
             .WriteInt(messageId)              // offset_id: the message to centre on
             .WriteInt(0)                      // offset_date
             .WriteInt(-(count / 2))           // add_offset: step back for newer ones
             .WriteInt(count)                  // limit
             .WriteInt(int.MaxValue)           // max_id: no ceiling
             .WriteInt(0)                      // min_id
             .WriteLong(0);                    // hash

            return q.ToArray();
        }

        /// <summary>
        /// Reads the page of a topic's messages older than one - the same as
        /// Messages.GetHistoryBeforeAsync, asked of the thread.
        /// </summary>
        public static async Task<Messages.History> GetHistoryBeforeAsync(MtprotoClient client,
                                                                         byte[] inputPeer,
                                                                         int topicId,
                                                                         int beforeId, int count,
                                                                         ClientInfo info = null)
        {
            TlReader r = await client.InvokeAsync(
                BeforeBody(inputPeer, topicId, beforeId, count), info);

            return Messages.ReadHistory(TlSchema.ReadObject(r));
        }

        /// <summary>The messages.getReplies payload for the page before a message.</summary>
        public static byte[] BeforeBody(byte[] inputPeer, int topicId, int beforeId, int count)
        {
            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.MessagesGetReplies)
             .WriteRaw(inputPeer)
             .WriteInt(topicId)                // msg_id: the thread
             .WriteInt(beforeId)               // offset_id: older than this
             .WriteInt(0)                      // offset_date
             .WriteInt(0)                      // add_offset: nothing newer
             .WriteInt(count)                  // limit
             .WriteInt(int.MaxValue)           // max_id: no ceiling
             .WriteInt(0)                      // min_id
             .WriteLong(0);                    // hash

            return q.ToArray();
        }

        /// <summary>
        /// Marks a topic read up to <paramref name="maxId"/>.
        ///
        /// Not readHistory: that one takes the channel and clears every topic in it
        /// at once, so reading one thread would silence the unread counts on all the
        /// others.
        /// </summary>
        public static async Task ReadAsync(MtprotoClient client, byte[] inputPeer,
                                           int topicId, int maxId, ClientInfo info = null)
        {
            if (maxId <= 0) return;

            var q = new TlWriter(48);
            q.WriteConstructor(TlConstructors.MessagesReadDiscussion)
             .WriteRaw(inputPeer)
             .WriteInt(topicId)
             .WriteInt(maxId);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlSchema.ReadObject(r);
        }

        /// <summary>
        /// Mutes or unmutes one topic.
        ///
        /// inputNotifyForumTopic rather than inputNotifyPeer: the peer form is the
        /// whole forum, so using it here would silence every thread in it. Settings
        /// cascade from topic to chat to account, and writing one here is what gives
        /// this topic an answer of its own.
        /// </summary>
        public static async Task SetMutedAsync(MtprotoClient client, byte[] inputPeer,
                                               int topicId, bool muted,
                                               ClientInfo info = null)
        {
            TlReader r = await client.InvokeAsync(
                MuteBody(inputPeer, topicId, muted), info);

            r.ReadBool();
        }

        /// <summary>
        /// The account.updateNotifySettings payload for one topic.
        ///
        /// Separated to be checked without a connection: the peer and the thread id
        /// sit next to each other inside the boxed InputNotifyPeer, and a client
        /// that writes them the other way round mutes whatever channel happens to
        /// have the thread's id.
        /// </summary>
        public static byte[] MuteBody(byte[] inputPeer, int topicId, bool muted)
        {
            var settings = new TlWriter(32);
            settings.WriteConstructor(TlConstructors.InputPeerNotifySettings)
                    .WriteInt(1 << 2)                   // only mute_until is set
                    .WriteInt(muted ? int.MaxValue : 0);

            var q = new TlWriter(96);
            q.WriteConstructor(TlConstructors.AccountUpdateNotifySettings)
             .WriteConstructor(TlConstructors.InputNotifyForumTopic)
             .WriteRaw(inputPeer)
             .WriteInt(topicId)
             .WriteRaw(settings.ToArray());

            return q.ToArray();
        }

        /// <summary>
        /// Mutes or unmutes every topic in a forum, and the forum itself.
        ///
        /// Both halves are needed and neither is enough. Setting the forum alone
        /// leaves any topic the user has already given its own setting - which is
        /// the whole point of having had that setting - so a muted forum would go on
        /// notifying from those. Setting the topics alone leaves topics created
        /// later unmuted, because a new one has no setting and follows the forum.
        ///
        /// One request per topic, which is why it reports progress: a busy forum has
        /// dozens, and silence on a phone connection is indistinguishable from a
        /// hang.
        /// </summary>
        public static async Task<int> SetAllMutedAsync(MtprotoClient client,
                                                       string kind, long peerId,
                                                       long accessHash, bool muted,
                                                       Action<int, int> progress = null,
                                                       ClientInfo info = null)
        {
            byte[] inputPeer = Messages.InputPeerFor(kind, peerId, accessHash);

            // The forum first, so that if the per-topic pass is interrupted the
            // setting that governs everything without an override is already right.
            await Messages.SetMutedAsync(client, kind, peerId, accessHash, muted, info);

            var ids = new List<int>();

            int offsetDate = 0, offsetId = 0, offsetTopic = 0;
            for (int page = 0; page < MutePages; page++)
            {
                TopicPage list = await GetAsync(client, inputPeer, MutePageSize,
                                               offsetDate, offsetId, offsetTopic, info);

                foreach (ForumTopic t in list.Topics) ids.Add(t.Id);

                if (!list.HasMore || list.Topics.Count == 0) break;

                offsetDate = list.NextOffsetDate;
                offsetId = list.NextOffsetId;
                offsetTopic = list.NextOffsetTopic;
            }

            int done = 0;
            foreach (int id in ids)
            {
                try
                {
                    await SetMutedAsync(client, inputPeer, id, muted, info);
                }
                catch (RpcException)
                {
                    // One topic refusing - deleted between the list and here, most
                    // likely - is not a reason to leave the rest as they were.
                }

                done++;
                if (progress != null) progress(done, ids.Count);
            }

            return done;
        }

        /// <summary>How far to page when collecting topics to mute.</summary>
        private const int MutePages = 5;
        private const int MutePageSize = 100;

        /// <summary>
        /// Sends text into a topic, returning the id the server assigned it.
        ///
        /// Ordinary sending with the thread named; see
        /// <see cref="Messages.SendTextBody"/> for how the two ids are written.
        /// </summary>
        public static async Task<int> SendTextAsync(MtprotoClient client, ICrypto crypto,
                                                    byte[] inputPeer, int topicId, string text,
                                                    int replyToMsgId = 0)
        {
            return await Messages.SendTextAsync(client, crypto, inputPeer, text,
                                                replyToMsgId, topicId);
        }
    }
}
