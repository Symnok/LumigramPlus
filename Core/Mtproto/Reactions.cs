using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>One reaction on a message: which emoji, how many, and whether one is ours.</summary>
    public sealed class MessageReaction
    {
        /// <summary>The emoji exactly as Telegram spells it - see Reactions.Heart.</summary>
        public string Emoticon;

        public int Count;

        /// <summary>True when this account is one of the people who chose it.</summary>
        public bool Mine;
    }

    /// <summary>A reaction the server is prepared to accept.</summary>
    public sealed class AvailableReaction
    {
        public string Emoticon;
        public string Title;

        /// <summary>Only for Premium accounts; offering it to anyone else is a refusal.</summary>
        public bool Premium;

        /// <summary>Retired: still shown on old messages, no longer accepted on new ones.</summary>
        public bool Inactive;
    }

    /// <summary>
    /// Reactions: reading them off messages, and putting one on.
    ///
    /// A reaction belongs to the message it is on. It is not a reply and not a
    /// message of its own - messages.sendReaction changes the message, and every
    /// client that has it shows the new count in place.
    ///
    /// Only plain emoji are handled. A custom-emoji reaction is a Premium sticker,
    /// and this client has no sticker support to draw one with, so those are left
    /// out of what is read rather than shown as blanks.
    /// </summary>
    public static class Reactions
    {
        /// <summary>
        /// The heart, as Telegram spells it: U+2764 with nothing after it.
        ///
        /// Not "❤️" with the emoji variation selector, which is what most keyboards
        /// produce and what draws red - the server refuses that spelling as an
        /// unknown reaction. The red is a matter for whoever draws it.
        /// </summary>
        public const string Heart = "\u2764";

        public const string ThumbsUp = "\U0001F44D";

        /// <summary>
        /// Reads a message's reactions, or an empty list when it has none.
        ///
        /// Takes the messageReactions object itself. ToTextMessage calls this for
        /// every message, and an updateMessageReactions carries the same object.
        /// </summary>
        public static List<MessageReaction> Read(TlObject messageReactions)
        {
            var list = new List<MessageReaction>();
            if (messageReactions == null || !messageReactions.Has("results")) return list;

            foreach (object o in messageReactions.Vec("results"))
            {
                var count = o as TlObject;
                if (count == null || count.Ctor != TlConstructors.ReactionCount) continue;
                if (!count.Has("reaction")) continue;

                TlObject reaction = count.Obj("reaction");
                if (reaction == null || reaction.Ctor != TlConstructors.ReactionEmoji) continue;

                list.Add(new MessageReaction
                {
                    Emoticon = reaction.Str("emoticon"),
                    Count = count.IntOr("count", 0),

                    // chosen_order is present exactly when this account chose it -
                    // the number is the order among its own choices, which only
                    // matters to a Premium account that can choose several.
                    Mine = count.Has("chosen_order"),
                });
            }

            return list;
        }

        /// <summary>
        /// Puts a reaction on a message, or takes ours off with null.
        ///
        /// Returns what the message's reactions are now, as the server reports them,
        /// or null when the reply did not say. The caller has usually already shown
        /// its own guess; this is what corrects it.
        /// </summary>
        public static async Task<List<MessageReaction>> SendAsync(MtprotoClient client,
                                                                  byte[] inputPeer,
                                                                  int messageId,
                                                                  string emoticon,
                                                                  ClientInfo info = null)
        {
            TlReader r = await client.InvokeAsync(SendBody(inputPeer, messageId, emoticon), info);
            return FindUpdated(TlSchema.ReadObject(r), messageId);
        }

        /// <summary>
        /// The messages.sendReaction payload, separated to be checked without a
        /// connection.
        ///
        /// Removing a reaction is the same request with flags.0 clear and no vector
        /// at all - not an empty vector, and not a reactionEmpty. The two shapes
        /// differ by one bit and four bytes, which is exactly the kind of thing that
        /// is wrong once and silently turns "remove" into "refused".
        /// </summary>
        public static byte[] SendBody(byte[] inputPeer, int messageId, string emoticon)
        {
            bool adding = !string.IsNullOrEmpty(emoticon);

            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.MessagesSendReaction)
             .WriteInt(adding ? 1 : 0)              // flags.0: reaction
             .WriteRaw(inputPeer)
             .WriteInt(messageId);

            if (adding)
            {
                q.WriteConstructor(TlConstructors.Vector)
                 .WriteInt(1)
                 .WriteConstructor(TlConstructors.ReactionEmoji)
                 .WriteString(emoticon);
            }

            return q.ToArray();
        }

        /// <summary>
        /// The reactions the server will accept, in the order it lists them.
        ///
        /// Asked of the server rather than written into the app, because Telegram
        /// adds and retires reactions without client releases - a hard-coded list
        /// would be offering refusals within a year.
        /// </summary>
        public static async Task<List<AvailableReaction>> GetAvailableAsync(MtprotoClient client,
                                                                            ClientInfo info = null)
        {
            var q = new TlWriter(8);
            q.WriteConstructor(TlConstructors.MessagesGetAvailableReactions)
             .WriteInt(0);                          // hash: nothing cached

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlObject response = TlSchema.ReadObject(r);

            var list = new List<AvailableReaction>();
            if (!response.Has("reactions")) return list;

            foreach (object o in response.Vec("reactions"))
            {
                var a = o as TlObject;
                if (a == null || a.Ctor != TlConstructors.AvailableReaction) continue;

                list.Add(new AvailableReaction
                {
                    Emoticon = a.Str("reaction"),
                    Title = a.Has("title") ? a.Str("title") : null,
                    Inactive = a.Flag("flags", 0),
                    Premium = a.Flag("flags", 2),
                });
            }

            return list;
        }

        /// <summary>
        /// Digs the message's new reactions out of an Updates reply.
        ///
        /// sendReaction answers with the whole Updates envelope; somewhere in it is
        /// an updateMessageReactions for this message. Searched for rather than
        /// assumed at a fixed place, because the envelope's shape depends on what
        /// else the server decided to send along with it.
        /// </summary>
        private static List<MessageReaction> FindUpdated(TlObject updates, int messageId)
        {
            if (updates == null) return null;

            if (updates.Ctor == TlConstructors.UpdateMessageReactions &&
                updates.IntOr("msg_id", 0) == messageId && updates.Has("reactions"))
                return Read(updates.Obj("reactions"));

            if (updates.Has("update"))
            {
                List<MessageReaction> found = FindUpdated(updates.Obj("update"), messageId);
                if (found != null) return found;
            }

            if (updates.Has("updates"))
            {
                foreach (object o in updates.Vec("updates"))
                {
                    List<MessageReaction> found = FindUpdated(o as TlObject, messageId);
                    if (found != null) return found;
                }
            }

            return null;
        }
    }
}
