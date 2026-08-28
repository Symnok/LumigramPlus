using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>The server's update sequence position.</summary>
    public sealed class UpdateState
    {
        public int Pts;
        public int Qts;
        public int Date;
        public int Seq;
        public int UnreadCount;

        public override string ToString()
        {
            return "pts=" + Pts + " qts=" + Qts + " seq=" + Seq + " unread=" + UnreadCount;
        }
    }

    /// <summary>
    /// Turns pushed Updates into messages.
    ///
    /// The server sends several shapes for the same event. A message in a private
    /// chat usually arrives as updateShortMessage, which inlines its fields and
    /// carries no Message object at all; the same message in a group arrives as
    /// updateShortChatMessage; and after a gap it comes back inside updates.difference
    /// as a full Message. All three have to produce the same thing for a caller.
    ///
    /// Nothing here parses fields by hand - it all goes through the generated schema
    /// walker, so an unexpected shape is a clean miss rather than a wrong value.
    /// </summary>
    public static class UpdateReader
    {
        public static async Task<UpdateState> GetStateAsync(MtprotoClient client, ClientInfo info = null)
        {
            var q = new TlWriter(8);
            q.WriteConstructor(TlConstructors.UpdatesGetState);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlObject s = TlSchema.ReadObject(r);

            return new UpdateState
            {
                Pts = s.IntOr("pts", 0),
                Qts = s.IntOr("qts", 0),
                Date = s.IntOr("date", 0),
                Seq = s.IntOr("seq", 0),
                UnreadCount = s.IntOr("unread_count", 0),
            };
        }

        /// <summary>
        /// Fetches everything missed since <paramref name="state"/>, and advances it.
        ///
        /// Needed because updates are only pushed while connected: anything that
        /// happened while the app was closed is invisible until asked for.
        /// </summary>
        public static async Task<List<TextMessage>> GetDifferenceAsync(MtprotoClient client,
                                                                       UpdateState state)
        {
            var q = new TlWriter(32);
            q.WriteConstructor(TlConstructors.UpdatesGetDifference)
             .WriteInt(0)                        // flags
             .WriteInt(state.Pts)
             .WriteInt(state.Date)
             .WriteInt(state.Qts);

            TlReader r = await client.InvokeAsync(q.ToArray());
            TlObject diff = TlSchema.ReadObject(r);

            var result = new List<TextMessage>();

            if (diff.Ctor == TlConstructors.UpdatesDifferenceEmpty)
            {
                state.Date = diff.IntOr("date", state.Date);
                state.Seq = diff.IntOr("seq", state.Seq);
                return result;
            }

            if (diff.Ctor == TlConstructors.UpdatesDifferenceTooLong)
            {
                // Too far behind to catch up incrementally; the caller has to
                // resynchronise from scratch.
                state.Pts = diff.IntOr("pts", state.Pts);
                return result;
            }

            foreach (object o in diff.Vec("new_messages"))
                result.Add(Messages.ToTextMessage((TlObject)o));

            foreach (object o in diff.Vec("other_updates"))
                CollectFromUpdate((TlObject)o, result);

            // difference carries "state", differenceSlice carries "intermediate_state".
            TlObject newState = null;
            if (diff.Has("state")) newState = diff.Obj("state");
            else if (diff.Has("intermediate_state")) newState = diff.Obj("intermediate_state");

            if (newState != null)
            {
                state.Pts = newState.IntOr("pts", state.Pts);
                state.Qts = newState.IntOr("qts", state.Qts);
                state.Date = newState.IntOr("date", state.Date);
                state.Seq = newState.IntOr("seq", state.Seq);
            }

            return result;
        }

        /// <summary>
        /// Extracts new messages from anything the server pushed.
        ///
        /// <paramref name="state"/> is accepted but deliberately left untouched -
        /// see the note at the end of the method for why moving it here loses
        /// messages.
        /// </summary>
        public static List<TextMessage> Extract(TlObject pushed, UpdateState state)
        {
            var result = new List<TextMessage>();
            if (pushed == null) return result;

            switch (pushed.Ctor)
            {
                case TlConstructors.UpdateShortMessage:
                case TlConstructors.UpdateShortChatMessage:
                    result.Add(FromShortMessage(pushed));
                    break;

                case TlConstructors.UpdateShort:
                    if (pushed.Has("update")) CollectFromUpdate(pushed.Obj("update"), result);
                    break;

                case TlConstructors.Updates:
                case TlConstructors.UpdatesCombined:
                    foreach (object o in pushed.Vec("updates"))
                        CollectFromUpdate((TlObject)o, result);
                    break;

                default:
                    // A bare Update can also arrive on its own.
                    CollectFromUpdate(pushed, result);
                    break;
            }

            // Deliberately does NOT advance state.Pts.
            //
            // pts is the client's claim about how much of the update stream it has
            // applied. Advancing it for a pushed object we could not turn into a
            // message means the next getDifference is asked about a point *after*
            // that message - so the server correctly reports nothing new, and the
            // message is lost until something forces a full reload.
            //
            // Only getDifference moves pts, because only getDifference hands over
            // everything in the range it covers. Pushes are treated as a hint that
            // arrives early; anything they miss is picked up by the next difference.
            return result;
        }

        private static void CollectFromUpdate(TlObject update, List<TextMessage> into)
        {
            if (update == null) return;

            if (update.Ctor == TlConstructors.UpdateNewMessage ||
                update.Ctor == TlConstructors.UpdateNewChannelMessage)
            {
                if (update.Has("message"))
                    into.Add(Messages.ToTextMessage(update.Obj("message")));
                return;
            }

            if (update.Ctor == TlConstructors.UpdateShortMessage ||
                update.Ctor == TlConstructors.UpdateShortChatMessage)
            {
                into.Add(FromShortMessage(update));
            }
        }

        /// <summary>
        /// updateShortMessage inlines the message rather than nesting a Message
        /// object, so it needs its own mapping.
        /// </summary>
        private static TextMessage FromShortMessage(TlObject u)
        {
            var m = new TextMessage
            {
                Id = u.IntOr("id", 0),
                Date = u.IntOr("date", 0),
                Out = u.Flag("flags", 1),
                Text = u.Has("message") ? u.Str("message") : null,
            };

            if (u.Has("user_id")) m.FromId = u.Long("user_id");
            else if (u.Has("from_id")) m.FromId = u.Long("from_id");

            if (u.Has("chat_id"))
            {
                // updateShortChatMessage only ever describes a group.
                m.PeerId = u.Long("chat_id");
                m.IsGroup = true;
            }
            else
            {
                m.PeerId = m.FromId;
            }

            if (u.Has("fwd_from")) m.Note = "forwarded";
            if (u.Has("reply_to")) m.Note = (m.Note == null ? "" : m.Note + ", ") + "reply";

            return m;
        }
    }
}
