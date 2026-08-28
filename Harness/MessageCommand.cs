using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// Sending and reading text, against Saved Messages.
    ///
    /// Saved Messages is the right target for a first messaging test: it is a real
    /// chat on a real account exercising the real API, but nothing is delivered to
    /// anyone else, so a bug cannot spam another person.
    /// </summary>
    internal static class MessageCommand
    {
        public static int RunSend(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: send <text>          (to Saved Messages)");
                return 2;
            }

            string text = string.Join(" ", args, 1, args.Length - 1);

            try { return SendAsync(text).GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
        }

        public static int RunHistory(string[] args)
        {
            int count = 5;
            if (args.Length > 1) int.TryParse(args[1], out count);

            try { return HistoryAsync(count).GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
        }

        public static int RunDialogs(string[] args)
        {
            int count = 20;
            if (args.Length > 1) int.TryParse(args[1], out count);

            try { return DialogsAsync(count).GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
        }

        private static async Task<int> DialogsAsync(int count)
        {
            var crypto = new DesktopCrypto();

            using (var transport = new TcpTransport())
            {
                MtprotoClient client = await ConnectAsync(transport, crypto);

                var warmup = new TlWriter();
                warmup.WriteConstructor(TlConstructors.HelpGetNearestDc);
                await client.InvokeAsync(warmup.ToArray(), BuildInfo());

                Console.WriteLine("-> messages.getDialogs  (newest {0})", count);
                Console.WriteLine();

                List<DialogEntry> dialogs = await Messages.GetDialogsAsync(client, count);

                if (dialogs.Count == 0)
                {
                    Console.WriteLine("  (no dialogs)");
                    return 0;
                }

                foreach (DialogEntry d in dialogs)
                {
                    string unread = d.UnreadCount > 0 ? "  (" + d.UnreadCount + " unread)" : "";
                    Console.WriteLine("  {0,-8} {1}{2}", d.Kind, d.Title, unread);
                    if (!string.IsNullOrEmpty(d.LastText))
                    {
                        // (char)10/(char)13 rather than escapes - see Tools note
                        string preview = d.LastText.Replace((char)10, ' ').Replace((char)13, ' ');
                        if (preview.Length > 60) preview = preview.Substring(0, 60) + "...";
                        Console.WriteLine("           {0}", preview);
                    }
                }

                Console.WriteLine();
                Console.WriteLine("{0} dialog(s) in a single request.", dialogs.Count);
                return 0;
            }
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

        private static async Task<int> SendAsync(string text)
        {
            var crypto = new DesktopCrypto();

            using (var transport = new TcpTransport())
            {
                MtprotoClient client = await ConnectAsync(transport, crypto);

                // The first call on a connection carries initConnection.
                var warmup = new TlWriter();
                warmup.WriteConstructor(TlConstructors.HelpGetNearestDc);
                await client.InvokeAsync(warmup.ToArray(), BuildInfo());

                Console.WriteLine("-> messages.sendMessage  \"{0}\"", text);
                int result = await Messages.SendTextAsync(client, crypto,
                                                             Messages.InputPeerSelf(), text);

                Console.WriteLine("<- {0}", result);
                Console.WriteLine();
                Console.WriteLine("Check Saved Messages in any Telegram client.");
                return 0;
            }
        }

        private static async Task<int> HistoryAsync(int count)
        {
            var crypto = new DesktopCrypto();

            using (var transport = new TcpTransport())
            {
                MtprotoClient client = await ConnectAsync(transport, crypto);

                var warmup = new TlWriter();
                warmup.WriteConstructor(TlConstructors.HelpGetNearestDc);
                await client.InvokeAsync(warmup.ToArray(), BuildInfo());

                Console.WriteLine("-> messages.getHistory  (Saved Messages, newest {0})", count);
                Console.WriteLine();

                List<TextMessage> messages = await Messages.GetRecentAsync(
                    client, Messages.InputPeerSelf(), count);

                if (messages.Count == 0)
                {
                    Console.WriteLine("  (no messages)");
                    return 0;
                }

                foreach (TextMessage m in messages)
                {
                    string who = m.Out ? "me " : "   ";
                    string body = m.Text ?? ("<" + (m.Note ?? "no text") + ">");
                    Console.WriteLine("  [{0}] {1} {2}  {3}",
                                      m.Id, m.DateUtc.ToString("yyyy-MM-dd HH:mm"), who, body);
                    if (m.Text != null && m.Note != null)
                        Console.WriteLine("        ({0})", m.Note);
                }

                Console.WriteLine();
                Console.WriteLine("{0} message(s) in a single request.", messages.Count);
                return 0;
            }
        }
    }
}
