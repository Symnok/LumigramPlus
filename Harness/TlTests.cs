using System;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// TL serialisation tests, plus the RSA key fingerprint check.
    ///
    /// The fingerprint check is the valuable one. Telegram's key fingerprints are
    /// the low 64 bits of SHA1 over the TL serialisation of (modulus, exponent), so
    /// deriving them from the PEMs and matching the published values exercises the
    /// PEM decoder, the DER parser, TL byte-string encoding *including its padding*,
    /// and SHA1 - all against an external source of truth, before any of it is
    /// pointed at a live server.
    /// </summary>
    internal static class TlTests
    {
        private static int _checks;
        private static int _failures;

        public static bool RunAll()
        {
            var rng = new Random(20260825);
            var crypto = new DesktopCrypto();

            Section("primitives round-trip");
            for (int i = 0; i < 500; i++)
            {
                int a = rng.Next(int.MinValue, int.MaxValue);
                long b = ((long)rng.Next() << 32) ^ rng.Next();
                double d = rng.NextDouble() * 1e9;

                var w = new TlWriter();
                w.WriteInt(a).WriteLong(b).WriteDouble(d).WriteBool(true).WriteBool(false);

                var r = new TlReader(w.ToArray());
                Eq("int", a, r.ReadInt());
                Eq("long", b, r.ReadLong());
                Eq("double", d, r.ReadDouble());
                Eq("true", true, r.ReadBool());
                Eq("false", false, r.ReadBool());
                Eq("consumed", 0, r.Remaining);
            }

            Section("sendMessage and forwardMessages");
            {
                byte[] peer = Messages.InputPeerFor("user", 1234567, 0x1122334455667788L);
                byte[] other = Messages.InputPeerFor("channel", 998877, 0x0102030405060708L);

                // A plain send: flags clear, and nothing between the peer and the
                // text. A stray reply_to here would be read as the message body.
                {
                    var r = new TlReader(Messages.SendTextBody(peer, "hello", 42L, 0));

                    Eq("send ctor", TlConstructors.MessagesSendMessage, r.ReadConstructor());
                    Eq("send flags", 0, r.ReadInt());
                    Eq("send peer", TlConstructors.InputPeerUser, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("send text", "hello", r.ReadString());
                    Eq("send random", 42L, r.ReadLong());
                    Eq("send consumed", 0, r.Remaining);
                }

                // A reply: flags.0 set, and a boxed InputReplyTo before the text.
                // The message id is the second field of that box, after its own
                // flags - written first, the server sees a reply to message 0.
                {
                    var r = new TlReader(Messages.SendTextBody(peer, "hi", 7L, 91011));

                    Eq("reply ctor", TlConstructors.MessagesSendMessage, r.ReadConstructor());
                    Eq("reply flags", 1, r.ReadInt());
                    Eq("reply peer", TlConstructors.InputPeerUser, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("reply box", TlConstructors.InputReplyToMessage, r.ReadConstructor());
                    Eq("reply box flags", 0, r.ReadInt());
                    Eq("reply to id", 91011, r.ReadInt());
                    Eq("reply text", "hi", r.ReadString());
                    Eq("reply random", 7L, r.ReadLong());
                    Eq("reply consumed", 0, r.Remaining);
                }

                // Forwarding: two vectors of the same length, ids then random ids,
                // and the destination last. Swapping the peers sends the copy back
                // where it came from.
                {
                    var ids = new int[] { 5, 6, 7 };
                    var randoms = new long[] { 100L, 200L, 300L };

                    var r = new TlReader(Messages.ForwardBody(peer, ids, randoms, other));

                    Eq("fwd ctor", TlConstructors.MessagesForwardMessages, r.ReadConstructor());
                    Eq("fwd flags", 0, r.ReadInt());
                    Eq("fwd from", TlConstructors.InputPeerUser, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();

                    Eq("fwd ids vector", TlConstructors.Vector, r.ReadConstructor());
                    Eq("fwd id count", 3, r.ReadInt());
                    for (int i = 0; i < ids.Length; i++)
                        Eq("fwd id " + i, ids[i], r.ReadInt());

                    Eq("fwd random vector", TlConstructors.Vector, r.ReadConstructor());
                    Eq("fwd random count", 3, r.ReadInt());
                    for (int i = 0; i < randoms.Length; i++)
                        Eq("fwd random " + i, randoms[i], r.ReadLong());

                    Eq("fwd to", TlConstructors.InputPeerChannel, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("fwd consumed", 0, r.Remaining);
                }
            }

            Section("forum topics");
            {
                byte[] forum = Messages.InputPeerFor("channel", 555444, 0x1020304050607080L);
                const int topic = 9876;

                // Reading a topic. msg_id is the thread and comes first, before the
                // offsets - written after them, the server answers with the whole
                // group starting at an offset nobody asked for.
                {
                    var r = new TlReader(Topics.HistoryBody(forum, topic, 30));

                    Eq("replies ctor", TlConstructors.MessagesGetReplies, r.ReadConstructor());
                    Eq("replies peer", TlConstructors.InputPeerChannel, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("replies thread", topic, r.ReadInt());
                    Eq("replies offset_id", 0, r.ReadInt());
                    Eq("replies offset_date", 0, r.ReadInt());
                    Eq("replies add_offset", 0, r.ReadInt());
                    Eq("replies limit", 30, r.ReadInt());
                    Eq("replies max_id", int.MaxValue, r.ReadInt());
                    Eq("replies min_id", 0, r.ReadInt());
                    Eq("replies hash", 0L, r.ReadLong());
                    Eq("replies consumed", 0, r.Remaining);
                }

                // Sending into one. The topic is named twice: as the thread, and as
                // the message being replied to - which is what files the message
                // under it rather than at the top of the group.
                {
                    var r = new TlReader(Messages.SendTextBody(forum, "hi", 11L, 0, topic));

                    Eq("topic send ctor", TlConstructors.MessagesSendMessage, r.ReadConstructor());
                    Eq("topic send flags", 1, r.ReadInt());
                    Eq("topic send peer", TlConstructors.InputPeerChannel, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("topic box", TlConstructors.InputReplyToMessage, r.ReadConstructor());
                    Eq("topic box flags", 1, r.ReadInt());
                    Eq("topic reply target", topic, r.ReadInt());
                    Eq("topic top_msg_id", topic, r.ReadInt());
                    Eq("topic send text", "hi", r.ReadString());
                    Eq("topic send random", 11L, r.ReadLong());
                    Eq("topic send consumed", 0, r.Remaining);
                }

                // Replying to a particular message inside a topic: the thread is
                // still named, but the reply target is the message, not the topic.
                // Both, or the reply lands in the forum with no thread.
                {
                    var r = new TlReader(Messages.SendTextBody(forum, "re", 12L, 12345, topic));

                    r.ReadConstructor();
                    Eq("in-topic reply flags", 1, r.ReadInt());
                    r.ReadConstructor(); r.ReadLong(); r.ReadLong();
                    Eq("in-topic box", TlConstructors.InputReplyToMessage, r.ReadConstructor());
                    Eq("in-topic box flags", 1, r.ReadInt());
                    Eq("in-topic reply target", 12345, r.ReadInt());
                    Eq("in-topic top_msg_id", topic, r.ReadInt());
                    Eq("in-topic text", "re", r.ReadString());
                }

                // The General topic. Its id is not a message that can be replied
                // to, so it is named only as the thread and the reply target stays
                // zero - sending it as a reply to message 1 is refused.
                {
                    var r = new TlReader(
                        Messages.SendTextBody(forum, "g", 13L, 0, Topics.GeneralTopicId));

                    r.ReadConstructor();
                    Eq("general flags", 1, r.ReadInt());
                    r.ReadConstructor(); r.ReadLong(); r.ReadLong();
                    Eq("general box", TlConstructors.InputReplyToMessage, r.ReadConstructor());
                    Eq("general box flags", 1, r.ReadInt());
                    Eq("general reply target", 0, r.ReadInt());
                    Eq("general top_msg_id", Topics.GeneralTopicId, r.ReadInt());
                    Eq("general text", "g", r.ReadString());
                }

                // And with no topic at all, nothing above may leak into an ordinary
                // send: no reply box, and no top_msg_id bit.
                {
                    var r = new TlReader(Messages.SendTextBody(forum, "plain", 14L, 0, 0));

                    r.ReadConstructor();
                    Eq("plain flags", 0, r.ReadInt());
                    r.ReadConstructor(); r.ReadLong(); r.ReadLong();
                    Eq("plain text", "plain", r.ReadString());
                    Eq("plain random", 14L, r.ReadLong());
                    Eq("plain consumed", 0, r.Remaining);
                }
            }

            Section("editing and topic mute");
            {
                byte[] forum = Messages.InputPeerFor("channel", 555444, 0x1020304050607080L);
                const int topic = 9876;

                // Editing. message is flags.11, a long way from the low bits
                // anything else here would set - and a wrong bit means the server
                // reads the text as some other optional field, or reads no text at
                // all and edits the message to empty.
                {
                    var r = new TlReader(Messages.EditTextBody(forum, 4321, "fixed"));

                    Eq("edit ctor", TlConstructors.MessagesEditMessage, r.ReadConstructor());
                    Eq("edit flags", 1 << 11, r.ReadInt());
                    Eq("edit peer", TlConstructors.InputPeerChannel, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("edit id", 4321, r.ReadInt());
                    Eq("edit text", "fixed", r.ReadString());
                    Eq("edit consumed", 0, r.Remaining);
                }

                // Muting one topic. The thread id sits after the peer inside the
                // boxed InputNotifyPeer; written the other way round this mutes
                // whatever channel happens to have the thread's id.
                {
                    var r = new TlReader(Topics.MuteBody(forum, topic, true));

                    Eq("mute ctor", TlConstructors.AccountUpdateNotifySettings,
                       r.ReadConstructor());
                    Eq("mute scope", TlConstructors.InputNotifyForumTopic, r.ReadConstructor());
                    Eq("mute peer", TlConstructors.InputPeerChannel, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("mute thread", topic, r.ReadInt());
                    Eq("mute settings", TlConstructors.InputPeerNotifySettings,
                       r.ReadConstructor());
                    Eq("mute flags", 1 << 2, r.ReadInt());
                    Eq("mute until", int.MaxValue, r.ReadInt());
                    Eq("mute consumed", 0, r.Remaining);
                }

                // ...and unmuting is the same message with the time at zero, not a
                // different request and not an absent field.
                {
                    var r = new TlReader(Topics.MuteBody(forum, topic, false));

                    r.ReadConstructor(); r.ReadConstructor(); r.ReadConstructor();
                    r.ReadLong(); r.ReadLong();
                    Eq("unmute thread", topic, r.ReadInt());
                    r.ReadConstructor();
                    Eq("unmute flags", 1 << 2, r.ReadInt());
                    Eq("unmute until", 0, r.ReadInt());
                    Eq("unmute consumed", 0, r.Remaining);
                }
            }

            Section("reactions");
            {
                byte[] forum = Messages.InputPeerFor("channel", 555444, 0x1020304050607080L);

                // Putting one on: flags.0 set, then a one-element vector of
                // reactionEmoji. The emoji is several bytes of UTF-8, which is the
                // padding case the string tests above exist for.
                {
                    var r = new TlReader(Reactions.SendBody(forum, 77, Reactions.ThumbsUp));

                    Eq("react ctor", TlConstructors.MessagesSendReaction, r.ReadConstructor());
                    Eq("react flags", 1, r.ReadInt());
                    Eq("react peer", TlConstructors.InputPeerChannel, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("react id", 77, r.ReadInt());
                    Eq("react vector", TlConstructors.Vector, r.ReadConstructor());
                    Eq("react count", 1, r.ReadInt());
                    Eq("react kind", TlConstructors.ReactionEmoji, r.ReadConstructor());
                    Eq("react emoji", Reactions.ThumbsUp, r.ReadString());
                    Eq("react consumed", 0, r.Remaining);
                }

                // The heart goes out exactly as Telegram spells it: one code point,
                // no variation selector. The red version most keyboards produce is
                // refused by the server as an unknown reaction.
                {
                    var r = new TlReader(Reactions.SendBody(forum, 78, Reactions.Heart));

                    r.ReadConstructor(); r.ReadInt();
                    r.ReadConstructor(); r.ReadLong(); r.ReadLong();
                    r.ReadInt(); r.ReadConstructor(); r.ReadInt(); r.ReadConstructor();
                    string heart = r.ReadString();

                    Eq("heart is one code point", 1, heart.Length);
                    Eq("heart code point", 0x2764, (int)heart[0]);
                }

                // Taking ours off: flags.0 clear and nothing after the id - not an
                // empty vector, and not a reactionEmpty.
                {
                    var r = new TlReader(Reactions.SendBody(forum, 79, null));

                    Eq("unreact ctor", TlConstructors.MessagesSendReaction, r.ReadConstructor());
                    Eq("unreact flags", 0, r.ReadInt());
                    r.ReadConstructor(); r.ReadLong(); r.ReadLong();
                    Eq("unreact id", 79, r.ReadInt());
                    Eq("unreact consumed", 0, r.Remaining);
                }

                // Reading them back through the generated schema, which is the path
                // every message takes. Two counts - one ours - and one custom-emoji
                // reaction, which must be skipped rather than read as a blank emoji.
                {
                    var w = new TlWriter();
                    w.WriteConstructor(TlConstructors.MessageReactions)
                     .WriteInt(0)                               // flags
                     .WriteConstructor(TlConstructors.Vector)
                     .WriteInt(3);

                    w.WriteConstructor(TlConstructors.ReactionCount)
                     .WriteInt(1).WriteInt(0)                   // flags.0: chosen_order
                     .WriteConstructor(TlConstructors.ReactionEmoji)
                     .WriteString(Reactions.ThumbsUp)
                     .WriteInt(3);

                    w.WriteConstructor(TlConstructors.ReactionCount)
                     .WriteInt(0)
                     .WriteConstructor(TlConstructors.ReactionEmoji)
                     .WriteString(Reactions.Heart)
                     .WriteInt(1);

                    w.WriteConstructor(TlConstructors.ReactionCount)
                     .WriteInt(0)
                     .WriteConstructor(TlConstructors.ReactionCustomEmoji)
                     .WriteLong(123456789L)
                     .WriteInt(5);

                    var read = Reactions.Read(TlSchema.ReadObject(new TlReader(w.ToArray())));

                    Eq("read count", 2, read.Count);
                    Eq("read first", Reactions.ThumbsUp, read[0].Emoticon);
                    Eq("read first count", 3, read[0].Count);
                    Eq("read first mine", true, read[0].Mine);
                    Eq("read second", Reactions.Heart, read[1].Emoticon);
                    Eq("read second count", 1, read[1].Count);
                    Eq("read second mine", false, read[1].Mine);
                }
            }

            Section("replies");
            {
                // An ordinary reply: just the message it answers.
                {
                    var t = Reply(1 << 4, 391, 0, false, null);
                    Eq("plain reply", 391, t.ReplyToId);
                    Eq("plain not elsewhere", false, t.ReplyElsewhere);
                    Eq("plain no quote", null, t.ReplyQuote);
                }

                // A plain message in forum topic 12 looks like a reply to 12. It is
                // not one, and must not quote the topic's creation notice.
                {
                    var t = Reply((1 << 4) | (1 << 3), 12, 0, false, null);
                    Eq("topic membership is not a reply", 0, t.ReplyToId);
                }

                // A real reply inside that topic names both the message and the topic.
                {
                    var t = Reply((1 << 4) | (1 << 3) | (1 << 1), 391, 12, false, null);
                    Eq("reply inside a topic", 391, t.ReplyToId);
                }

                // The sender quoted part of the original: that part is what is shown.
                {
                    var t = Reply((1 << 4) | (1 << 6), 391, 0, false, "just this bit");
                    Eq("quote id", 391, t.ReplyToId);
                    Eq("quote text", "just this bit", t.ReplyQuote);
                }

                // An original in another chat: its id means nothing here.
                {
                    var t = Reply((1 << 4) | (1 << 0) | (1 << 6), 77, 0, true, "from over there");
                    Eq("elsewhere", true, t.ReplyElsewhere);
                    Eq("elsewhere has no local id", 0, t.ReplyToId);
                    Eq("elsewhere keeps its quote", "from over there", t.ReplyQuote);
                }

                // Fetching originals: a channel's ids are its own, so it has to be
                // asked through channels.getMessages with the channel named.
                {
                    var r = new TlReader(Messages.ByIdBody("channel", 555444,
                        0x1020304050607080L, new[] { 10, 20 }));

                    Eq("by id channel ctor", TlConstructors.ChannelsGetMessages, r.ReadConstructor());
                    Eq("by id channel", TlConstructors.InputChannel, r.ReadConstructor());
                    Eq("by id channel id", 555444L, r.ReadLong());
                    r.ReadLong();
                    Eq("by id vector", TlConstructors.Vector, r.ReadConstructor());
                    Eq("by id count", 2, r.ReadInt());
                    Eq("by id first", TlConstructors.InputMessageID, r.ReadConstructor());
                    Eq("by id first id", 10, r.ReadInt());
                    Eq("by id second", TlConstructors.InputMessageID, r.ReadConstructor());
                    Eq("by id second id", 20, r.ReadInt());
                    Eq("by id consumed", 0, r.Remaining);
                }

                // ...and anything else through messages.getMessages, with no peer.
                {
                    var r = new TlReader(Messages.ByIdBody("user", 999, 1L, new[] { 5 }));

                    Eq("by id user ctor", TlConstructors.MessagesGetMessages, r.ReadConstructor());
                    Eq("by id user vector", TlConstructors.Vector, r.ReadConstructor());
                    Eq("by id user count", 1, r.ReadInt());
                    r.ReadConstructor();
                    Eq("by id user id", 5, r.ReadInt());
                    Eq("by id user consumed", 0, r.Remaining);
                }
            }

            Section("older messages");
            {
                byte[] chat = Messages.InputPeerFor("channel", 555444, 0x1020304050607080L);

                // The page before a message: offset_id is the oldest on screen and
                // add_offset is zero. A negative add_offset here - which is what the
                // window around a linked message uses - would send back messages
                // that are already on screen.
                {
                    var r = new TlReader(Messages.BeforeBody(chat, 5000, 30));

                    Eq("older ctor", TlConstructors.MessagesGetHistory, r.ReadConstructor());
                    r.ReadConstructor(); r.ReadLong(); r.ReadLong();
                    Eq("older offset_id", 5000, r.ReadInt());
                    Eq("older offset_date", 0, r.ReadInt());
                    Eq("older add_offset", 0, r.ReadInt());
                    Eq("older limit", 30, r.ReadInt());
                    Eq("older max_id", 0, r.ReadInt());
                    Eq("older min_id", 0, r.ReadInt());
                    Eq("older hash", 0L, r.ReadLong());
                    Eq("older consumed", 0, r.Remaining);
                }

                // Inside a topic: the thread first, then the same offsets.
                {
                    var r = new TlReader(Topics.BeforeBody(chat, 12, 5000, 30));

                    Eq("topic older ctor", TlConstructors.MessagesGetReplies, r.ReadConstructor());
                    r.ReadConstructor(); r.ReadLong(); r.ReadLong();
                    Eq("topic older thread", 12, r.ReadInt());
                    Eq("topic older offset_id", 5000, r.ReadInt());
                    Eq("topic older offset_date", 0, r.ReadInt());
                    Eq("topic older add_offset", 0, r.ReadInt());
                    Eq("topic older limit", 30, r.ReadInt());
                    Eq("topic older max_id", int.MaxValue, r.ReadInt());
                    Eq("topic older min_id", 0, r.ReadInt());
                    Eq("topic older hash", 0L, r.ReadLong());
                    Eq("topic older consumed", 0, r.Remaining);
                }

                // Whether there is more: a full slice says yes; a short page, or the
                // server's "this is everything", says no.
                {
                    var full = new Messages.History();
                    for (int i = 0; i < 30; i++) full.Messages.Add(new TextMessage());

                    Eq("full slice has older", true, full.HasOlder(30));

                    full.Complete = true;
                    Eq("complete has none", false, full.HasOlder(30));

                    var shortPage = new Messages.History();
                    for (int i = 0; i < 12; i++) shortPage.Messages.Add(new TextMessage());
                    Eq("short page has none", false, shortPage.HasOlder(30));
                }
            }

            Section("audio files");
            {
                // An MP3 the usual way: an audio attribute without the voice flag,
                // with a title and artist, and an audio mime type.
                {
                    MediaInfo m = Document("audio/mpeg", "song.mp3", true, false,
                                           "Yesterday", "The Beatles", 125);

                    Eq("mp3 kind", MediaKind.Audio, m.Kind);
                    Eq("mp3 extension", ".mp3", Media.PlayableExtension(m));
                    Eq("mp3 title", "Yesterday", m.Title);
                    Eq("mp3 performer", "The Beatles", m.Performer);
                    Eq("mp3 duration", 125, m.DurationSeconds);
                    Eq("mp3 name", "The Beatles - Yesterday", m.TrackName());
                }

                // Nothing but the name to go on - octet-stream, no audio attribute.
                // This is what turned up as "xxx.mp3.bin".
                {
                    MediaInfo m = Document("application/octet-stream", "Track 07.MP3",
                                           false, false, null, null, 0);

                    Eq("named mp3 kind", MediaKind.Audio, m.Kind);
                    Eq("named mp3 extension", ".mp3", Media.PlayableExtension(m));
                    Eq("named mp3 track name", "Track 07.MP3", m.TrackName());
                }

                // A voice message is audio too, and must stay a voice message: it
                // is OGG/Opus, which this player cannot play, and has a decoder of
                // its own.
                {
                    MediaInfo m = Document("audio/ogg", null, true, true, null, null, 4);
                    Eq("voice kind", MediaKind.Voice, m.Kind);
                }

                // Music the phone cannot decode stays a file to save, rather than
                // becoming a play button that fails.
                {
                    MediaInfo m = Document("audio/ogg", "song.ogg", true, false,
                                           "x", "y", 60);
                    Eq("ogg kind", MediaKind.Document, m.Kind);

                    MediaInfo f = Document("audio/flac", "song.flac", true, false,
                                           null, null, 60);
                    Eq("flac kind", MediaKind.Document, f.Kind);
                }

                // M4A, every way it turns up: the three mime types apps use for it,
                // and a bare name. Each has to play, and be stored as ".m4a" - the
                // player decides what a file is partly from that extension.
                {
                    string[] mimes = { "audio/mp4", "audio/m4a", "audio/x-m4a" };
                    foreach (string mime in mimes)
                    {
                        MediaInfo m = Document(mime, "song.m4a", true, false,
                                               "Title", "Artist", 200);
                        Eq("m4a kind (" + mime + ")", MediaKind.Audio, m.Kind);
                        Eq("m4a extension (" + mime + ")", ".m4a", Media.PlayableExtension(m));
                    }

                    MediaInfo named = Document("application/octet-stream", "Voice Memo.M4A",
                                               false, false, null, null, 0);
                    Eq("named m4a kind", MediaKind.Audio, named.Kind);
                    Eq("named m4a extension", ".m4a", Media.PlayableExtension(named));
                }

                // A name and a mime type that disagree about the container. Apps
                // often label an M4A "audio/aac", which is the codec inside it rather
                // than the container; stored as ".aac" the player expects a bare AAC
                // stream and refuses the file. The name is what the file was called
                // on the phone it came from, so for the container it wins.
                {
                    MediaInfo m = Document("audio/aac", "song.m4a", true, false,
                                           null, null, 200);
                    Eq("aac-labelled m4a kind", MediaKind.Audio, m.Kind);
                    Eq("aac-labelled m4a extension", ".m4a", Media.PlayableExtension(m));
                }

                // And an ordinary document is left alone.
                {
                    MediaInfo m = Document("application/pdf", "report.pdf", false, false,
                                           null, null, 0);
                    Eq("pdf kind", MediaKind.Document, m.Kind);
                }
            }

            Section("byte strings and padding");
            {
                // Lengths around every boundary that changes the encoding.
                int[] lengths = { 0, 1, 2, 3, 4, 5, 15, 16, 17, 252, 253, 254, 255, 256, 257, 1000, 65535, 65536 };
                foreach (int len in lengths)
                {
                    var data = new byte[len];
                    rng.NextBytes(data);

                    var w = new TlWriter();
                    w.WriteBytes(data);
                    byte[] wire = w.ToArray();

                    _checks++;
                    if (wire.Length % 4 != 0)
                        Fail("padding", "len " + len + " produced " + wire.Length + " bytes, not a multiple of 4");

                    var back = new TlReader(wire).ReadBytes();
                    _checks++;
                    if (!Same(data, back)) Fail("bytes", "round-trip failed at length " + len);
                }
            }

            Section("strings and vectors");
            {
                var w = new TlWriter();
                w.WriteString("hello é世界");        // non-ASCII must survive
                w.WriteVectorOfLong(new long[] { 1, -2, long.MaxValue, long.MinValue });

                var r = new TlReader(w.ToArray());
                Eq("string", "hello é世界", r.ReadString());
                var v = r.ReadVectorOfLong();
                Eq("vector len", 4, v.Length);
                Eq("vector[3]", long.MinValue, v[3]);
            }

            Section("reader rejects malformed input");
            {
                ExpectThrow("short buffer", delegate { new TlReader(new byte[2]).ReadInt(); });
                ExpectThrow("bad length prefix", delegate { new TlReader(new byte[] { 0xFF, 0, 0, 0 }).ReadBytes(); });
                ExpectThrow("length past end", delegate { new TlReader(new byte[] { 0x40, 1, 2, 3 }).ReadBytes(); });
                ExpectThrow("wrong constructor", delegate { new TlReader(new byte[] { 1, 2, 3, 4 }).Expect(TlConstructors.ResPQ, "resPQ"); });
            }

            Section("RSA keys: derived fingerprints vs published");
            {
                var keys = TelegramServers.LoadPublicKeys(crypto);
                Eq("key count", TelegramServers.ExpectedFingerprints.Length, keys.Count);

                for (int i = 0; i < keys.Count; i++)
                {
                    long expected = TelegramServers.ExpectedFingerprints[i];
                    _checks++;
                    if (keys[i].Fingerprint != expected)
                    {
                        Fail("fingerprint[" + i + "]",
                             "derived " + keys[i].Fingerprint.ToString("x16") +
                             ", published " + expected.ToString("x16"));
                    }
                    else
                    {
                        Console.WriteLine("    key {0}: {1:x16}  ({2}-bit modulus)",
                                          i, keys[i].Fingerprint, keys[i].Modulus.BitLength);
                    }
                }
            }

            Section("RSA exponentiation shape");
            {
                var keys = TelegramServers.LoadPublicKeys(crypto);
                var key = keys[0];

                var block = new byte[255];
                rng.NextBytes(block);
                byte[] enc = key.Encrypt(block);

                Eq("output size", 256, enc.Length);

                // The public exponent is 65537 for all of Telegram's keys.
                Eq("exponent", "010001", key.Exponent.ToBytesBE().ToHex());

                // A block that is not smaller than the modulus must be refused
                // rather than silently reduced.
                var tooBig = new byte[256];
                for (int i = 0; i < 256; i++) tooBig[i] = 0xFF;
                ExpectThrow("oversized block", delegate { key.Encrypt(tooBig); });
            }

            Section("gzip inflate vs framework");
            {
                // Round-trip data the framework compressed. Hand-written inflate is
                // exactly the kind of code that works on simple input and fails on
                // real payloads, so the cases below cover the three block types:
                // highly compressible (dynamic Huffman), random (stored/fixed), and
                // repetitive runs that exercise overlapping back-references.
                foreach (int size in new[] { 1, 2, 100, 1000, 50000 })
                {
                    Check("random", RandomData(rng, size));
                    Check("compressible", CompressibleData(size));
                    Check("repetitive", RepetitiveData(size));
                }
            }

            Console.WriteLine();
            Console.WriteLine("{0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        private static byte[] RandomData(Random rng, int n)
        {
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        private static byte[] CompressibleData(int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)('a' + (i % 4));
            return b;
        }

        private static byte[] RepetitiveData(int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = 0x5A;
            return b;
        }

        private static void Check(string what, byte[] original)
        {
            byte[] gz = GzipCompress(original);
            byte[] back;
            try
            {
                back = Lumigram.Tl.Inflate.Gunzip(gz);
            }
            catch (Exception ex)
            {
                _checks++;
                Fail("inflate " + what + "/" + original.Length, ex.GetType().Name + ": " + ex.Message);
                return;
            }

            _checks++;
            if (!Same(original, back))
                Fail("inflate " + what + "/" + original.Length,
                     "got " + back.Length + " bytes, expected " + original.Length);
        }

        private static byte[] GzipCompress(byte[] data)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms,
                           System.IO.Compression.CompressionMode.Compress, true))
                {
                    gz.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Builds a messageReplyHeader the way the server sends one - fields in
        /// declaration order, each present only when its bit is set - and reads it
        /// back through the generated schema.
        /// </summary>
        private static TextMessage Reply(int flags, int replyTo, int topId,
                                         bool elsewhere, string quote)
        {
            var w = new TlWriter();
            w.WriteConstructor(TlConstructors.MessageReplyHeader).WriteInt(flags);

            if ((flags & (1 << 4)) != 0) w.WriteInt(replyTo);              // reply_to_msg_id
            if ((flags & (1 << 0)) != 0)                                   // reply_to_peer_id
                w.WriteConstructor(TlConstructors.PeerChannel).WriteLong(424242);
            if ((flags & (1 << 1)) != 0) w.WriteInt(topId);                // reply_to_top_id
            if ((flags & (1 << 6)) != 0) w.WriteString(quote);             // quote_text

            var t = new TextMessage();
            Messages.ReadReply(TlSchema.ReadObject(new TlReader(w.ToArray())), t);
            return t;
        }

        /// <summary>
        /// Builds a document the way the server sends one, and reads it back through
        /// the generated schema - the same path every received file takes.
        /// </summary>
        private static MediaInfo Document(string mime, string fileName, bool audio,
                                          bool voice, string title, string performer,
                                          int duration)
        {
            int attributes = (audio ? 1 : 0) + (fileName != null ? 1 : 0);

            var w = new TlWriter();
            w.WriteConstructor(TlConstructors.Document)
             .WriteInt(0)                               // flags: no thumbs
             .WriteLong(1001).WriteLong(2002)           // id, access_hash
             .WriteBytes(new byte[] { 1, 2, 3 })        // file_reference
             .WriteInt(1700000000)                      // date
             .WriteString(mime)
             .WriteLong(4096)                           // size
             .WriteInt(2)                               // dc_id
             .WriteConstructor(TlConstructors.Vector)
             .WriteInt(attributes);

            if (audio)
            {
                int flags = (voice ? TlConstructors.DocumentAttributeAudioVoiceFlag : 0) |
                            (title != null ? 1 : 0) | (performer != null ? 2 : 0);

                w.WriteConstructor(TlConstructors.DocumentAttributeAudio)
                 .WriteInt(flags)
                 .WriteInt(duration);

                if (title != null) w.WriteString(title);
                if (performer != null) w.WriteString(performer);
            }

            if (fileName != null)
            {
                w.WriteConstructor(TlConstructors.DocumentAttributeFilename)
                 .WriteString(fileName);
            }

            return Media.FromDocument(TlSchema.ReadObject(new TlReader(w.ToArray())));
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("  [{0}]", name);
        }

        private static void Eq(string what, object expected, object actual)
        {
            _checks++;
            if (!Equals(expected, actual)) Fail(what, expected + " != " + actual);
        }

        private static void ExpectThrow(string what, Action action)
        {
            _checks++;
            try
            {
                action();
                Fail(what, "expected an exception, none thrown");
            }
            catch (TlParseException) { }
            catch (ArgumentException) { }
            catch (FormatException) { }
            catch (Exception ex)
            {
                Fail(what, "unexpected " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Fail(string what, string detail)
        {
            _failures++;
            if (_failures <= 6) Console.WriteLine("    FAIL {0}: {1}", what, detail);
            else if (_failures == 7) Console.WriteLine("    ... further failures suppressed");
        }
    }
}
