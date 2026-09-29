using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// Forum topics, against a real forum on the signed-in account.
    ///
    /// Unlike the messaging commands there is no Saved Messages equivalent here: a
    /// topic only exists inside a forum supergroup, so this has to be pointed at
    /// one. It reads and never writes, which is what makes that safe - listing a
    /// stranger's forum and reading one thread of it delivers nothing to anybody.
    ///
    /// Worth having on the desktop rather than only on the phone for the usual
    /// reason: getReplies either returns the thread or returns the whole group, and
    /// telling those apart in a console takes seconds where telling them apart in a
    /// deployed app takes a deploy.
    /// </summary>
    internal static class TopicsCommand
    {
        public static int Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: topics <forum title or part of it> [topic id]");
                Console.WriteLine();
                Console.WriteLine("  Lists the forum's topics. With a topic id, reads that");
                Console.WriteLine("  topic's newest messages instead.");
                return 2;
            }

            string name = args[1];

            int topicId = 0;
            if (args.Length > 2) int.TryParse(args[2], out topicId);

            try { return RunAsync(name, topicId).GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
        }

        private static async Task<int> RunAsync(string name, int topicId)
        {
            var crypto = new DesktopCrypto();

            using (var transport = new TcpTransport())
            {
                MtprotoClient client = await ConnectAsync(transport, crypto);

                // The first call on a connection carries initConnection.
                var warmup = new TlWriter();
                warmup.WriteConstructor(TlConstructors.HelpGetNearestDc);
                await client.InvokeAsync(warmup.ToArray(), BuildInfo());

                DialogEntry forum = await FindForumAsync(client, name);
                if (forum == null) return 1;

                Console.WriteLine("forum: {0}  (id {1})", forum.Title, forum.PeerId);
                Console.WriteLine();

                byte[] peer = Messages.InputPeerFor(forum);

                if (topicId == 0) return await ListAsync(client, peer);

                return await ReadAsync(client, peer, topicId);
            }
        }

        /// <summary>
        /// Finds a forum in the chat list by name.
        ///
        /// Matching on the title rather than taking an id, because a forum's id is
        /// not something anyone has to hand - and printing the forums found when
        /// there is no match is more use than saying there was none.
        /// </summary>
        private static async Task<DialogEntry> FindForumAsync(MtprotoClient client, string name)
        {
            List<DialogEntry> dialogs = await Messages.GetDialogsAsync(client, 100);

            var forums = new List<DialogEntry>();
            foreach (DialogEntry d in dialogs) if (d.IsForum) forums.Add(d);

            foreach (DialogEntry d in forums)
            {
                if (d.Title != null &&
                    d.Title.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return d;
            }

            Console.WriteLine("No forum matching \"{0}\" in the newest 100 chats.", name);
            Console.WriteLine();

            if (forums.Count == 0)
            {
                Console.WriteLine("  (no forums at all - the account is in none, or the");
                Console.WriteLine("   forum flag is not being read from the chats vector)");
                return null;
            }

            Console.WriteLine("Forums that are there:");
            foreach (DialogEntry d in forums) Console.WriteLine("  {0}", d.Title);

            return null;
        }

        private static async Task<int> ListAsync(MtprotoClient client, byte[] peer)
        {
            Console.WriteLine("-> messages.getForumTopics");
            Console.WriteLine();

            Topics.TopicPage page = await Topics.GetAsync(client, peer, 40, 0, 0, 0, BuildInfo());

            if (page.Topics.Count == 0)
            {
                Console.WriteLine("  (no topics)");
                return 0;
            }

            foreach (ForumTopic t in page.Topics)
            {
                string flags = (t.Pinned ? " pinned" : "") + (t.Closed ? " closed" : "") +
                               (t.Hidden ? " hidden" : "") + (t.IsGeneral ? " general" : "");
                string unread = t.UnreadCount > 0 ? "  (" + t.UnreadCount + " unread)" : "";

                Console.WriteLine("  [{0,-8}] {1}{2}{3}", t.Id, t.Title, unread, flags);

                if (!string.IsNullOrEmpty(t.LastText))
                {
                    // (char)10/(char)13 rather than escapes, as elsewhere here.
                    string preview = t.LastText.Replace((char)10, ' ').Replace((char)13, ' ');
                    if (preview.Length > 60) preview = preview.Substring(0, 60) + "...";
                    Console.WriteLine("             {0}", preview);
                }
            }

            Console.WriteLine();
            Console.WriteLine("{0} of {1} topic(s){2}.",
                              page.Topics.Count, page.Count, page.HasMore ? ", more available" : "");
            Console.WriteLine("Read one with: topics <forum> <topic id>");
            return 0;
        }

        private static async Task<int> ReadAsync(MtprotoClient client, byte[] peer, int topicId)
        {
            Console.WriteLine("-> messages.getReplies  (thread {0}, newest 20)", topicId);
            Console.WriteLine();

            Messages.History history = await Topics.GetHistoryAsync(
                client, peer, topicId, 20, BuildInfo());

            if (history.Messages.Count == 0)
            {
                Console.WriteLine("  (no messages)");
                return 0;
            }

            foreach (TextMessage m in history.Messages)
            {
                PeerInfo who;
                string sender = history.Senders.TryGetValue(m.FromId, out who)
                    ? who.Name : (m.Out ? "me" : m.FromId.ToString());

                string body = m.Text ?? ("<" + (m.Note ?? "no text") + ">");

                Console.WriteLine("  [{0}] {1}  {2}: {3}",
                                  m.Id, m.DateUtc.ToString("yyyy-MM-dd HH:mm"), sender, body);
            }

            Console.WriteLine();
            Console.WriteLine("{0} message(s) in thread {1}.", history.Messages.Count, topicId);
            Console.WriteLine();
            Console.WriteLine("These should all belong to that one topic. If the whole");
            Console.WriteLine("group came back, the thread id went to the wrong field.");
            return 0;
        }

        private static int Report(Exception ex)
        {
            Console.WriteLine();
            var rpc = ex as RpcException;
            if (rpc != null)
            {
                Console.WriteLine("SERVER REJECTED THE CALL");
                Console.WriteLine("  code: {0}", rpc.Code);
                Console.WriteLine("  type: {0}", rpc.ErrorType);
                if ((rpc.ErrorType ?? "").Contains("AUTH_KEY_UNREGISTERED"))
                    Console.WriteLine("  -> the stored session is not signed in; run sendcode/signin again.");
                return 1;
            }
            Console.WriteLine("FAILED: {0}: {1}", ex.GetType().Name, ex.Message);
            return 1;
        }

        private static async Task<MtprotoClient> ConnectAsync(TcpTransport transport, ICrypto crypto)
        {
            SessionStore store = SessionStore.Load();
            if (store == null) throw new MtprotoException("no stored session - run sendcode first");

            var client = new MtprotoClient(crypto, transport, delegate { });
            await client.ConnectWithKeyAsync(store.Host, TelegramServers.DefaultPort, store.ToAuthKey());
            return client;
        }

        private static ClientInfo BuildInfo()
        {
            var info = ClientInfo.Default;
            info.ApiId = Secrets.ApiId;
            info.ApiHash = Secrets.ApiHash;
            return info;
        }
    }
}
