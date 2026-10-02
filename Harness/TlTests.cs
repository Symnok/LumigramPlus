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
