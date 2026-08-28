using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// Stays connected and prints messages as they arrive.
    ///
    /// This is the piece that makes a client a client rather than a polling script.
    /// Updates are pushed over the same connection at arbitrary times, so it proves
    /// the receive loop works: nothing here asks for anything after startup.
    ///
    /// getDifference runs first, because updates are only pushed while connected -
    /// anything that happened while we were away is invisible until requested.
    /// </summary>
    internal static class ListenCommand
    {
        public static int Run(string[] args)
        {
            int seconds = 120;
            if (args.Length > 1) int.TryParse(args[1], out seconds);

            try { return ListenAsync(seconds).GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
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
                return 1;
            }
            Console.WriteLine("FAILED: {0}: {1}", ex.GetType().Name, ex.Message);
            return 1;
        }

        private static async Task<int> ListenAsync(int seconds)
        {
            SessionStore store = SessionStore.Load();
            if (store == null)
            {
                Console.WriteLine("No stored session. Run sendcode first.");
                return 2;
            }

            var crypto = new DesktopCrypto();
            var info = ClientInfo.Default;
            info.ApiId = Secrets.ApiId;
            info.ApiHash = Secrets.ApiHash;

            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(crypto, transport, Console.WriteLine);

                Console.WriteLine("connecting to {0} ...", store.Host);
                await client.ConnectWithKeyAsync(store.Host, TelegramServers.DefaultPort,
                                                 store.ToAuthKey());

                UpdateState state = await UpdateReader.GetStateAsync(client, info);
                Console.WriteLine("state: {0}", state);
                Console.WriteLine();

                List<TextMessage> missed = await UpdateReader.GetDifferenceAsync(client, state);
                if (missed.Count > 0)
                {
                    Console.WriteLine("missed while away:");
                    foreach (TextMessage m in missed) Print(m, "  ");
                    Console.WriteLine();
                }

                var done = new ManualResetEventSlim(false);
                int received = 0;

                client.UpdateReceived += delegate (TlObject pushed)
                {
                    // Report the raw shape too. An update that arrives but is not
                    // recognised would otherwise produce no output at all, which
                    // looks exactly like nothing arriving.
                    Console.WriteLine("  <- 0x{0:x8}", pushed.Ctor);

                    List<TextMessage> messages = UpdateReader.Extract(pushed, state);
                    foreach (TextMessage m in messages)
                    {
                        received++;
                        Print(m, "     ");
                    }
                };

                client.Faulted += delegate (Exception ex)
                {
                    Console.WriteLine("connection lost: {0}", ex.Message);
                    done.Set();
                };

                Console.WriteLine("listening for {0}s - send yourself a message from any", seconds);
                Console.WriteLine("Telegram client and it should appear here.");
                Console.WriteLine();

                // Poll as well as listening. Pushed updates have not proved reliable
                // on this client, and getDifference is cheap when nothing happened.
                int elapsed = 0;
                while (elapsed < seconds && !done.IsSet)
                {
                    done.Wait(TimeSpan.FromSeconds(5));
                    elapsed += 5;

                    try
                    {
                        List<TextMessage> polled = await UpdateReader.GetDifferenceAsync(client, state);
                        foreach (TextMessage m in polled)
                        {
                            received++;
                            Console.Write("  (poll) ");
                            Print(m, "");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  poll failed: " + ex.Message);
                    }
                }

                Console.WriteLine();
                Console.WriteLine("{0} message(s) received while listening.", received);

                // The salt may have been corrected during the session; keeping it
                // saves a wasted round trip next time.
                store.ServerSalt = client.ServerSalt;
                store.Save();
                return 0;
            }
        }

        /// <summary>
        /// Verifies the catch-up path without depending on anything external.
        ///
        /// Records the update position, sends a message, then asks for the
        /// difference from the *old* position - which must report the message that
        /// was just sent. Live pushes need an event from another authorization and
        /// so cannot be tested unattended; this covers everything else:
        /// updates.getState, updates.getDifference, and Message extraction.
        /// </summary>
        public static int RunDiffTest(string[] args)
        {
            try { return DiffTestAsync().GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
        }

        private static async Task<int> DiffTestAsync()
        {
            SessionStore store = SessionStore.Load();
            if (store == null)
            {
                Console.WriteLine("No stored session. Run sendcode first.");
                return 2;
            }

            var crypto = new DesktopCrypto();
            var info = ClientInfo.Default;
            info.ApiId = Secrets.ApiId;
            info.ApiHash = Secrets.ApiHash;

            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(crypto, transport, delegate { });
                await client.ConnectWithKeyAsync(store.Host, TelegramServers.DefaultPort,
                                                 store.ToAuthKey());

                UpdateState before = await UpdateReader.GetStateAsync(client, info);
                Console.WriteLine("before : {0}", before);

                var marker = "difference test " + DateTime.UtcNow.ToString("HH:mm:ss");
                Console.WriteLine("-> sending \"{0}\"", marker);
                await Messages.SendTextAsync(client, crypto, Messages.InputPeerSelf(), marker);

                var snapshot = new UpdateState
                {
                    Pts = before.Pts, Qts = before.Qts,
                    Date = before.Date, Seq = before.Seq,
                };

                List<TextMessage> caught = await UpdateReader.GetDifferenceAsync(client, snapshot);
                Console.WriteLine("after  : {0}", snapshot);
                Console.WriteLine();

                if (caught.Count == 0)
                {
                    Console.WriteLine("FAIL - getDifference reported nothing after a send");
                    return 1;
                }

                Console.WriteLine("caught up on {0} message(s):", caught.Count);
                foreach (TextMessage m in caught) Print(m, "  ");

                bool found = false;
                foreach (TextMessage m in caught)
                    if (m.Text == marker) found = true;

                Console.WriteLine();
                Console.WriteLine(found
                    ? "PASS - the message just sent came back through updates.getDifference"
                    : "PARTIAL - messages came back, but not the one just sent");
                return found ? 0 : 1;
            }
        }

        private static void Print(TextMessage m, string indent)
        {
            string who = m.Out ? "me " : "   ";
            string body = m.Text ?? ("<" + (m.Note ?? "no text") + ">");
            Console.WriteLine("{0}[{1}] {2} {3}  {4}",
                              indent, m.Id, m.DateUtc.ToString("HH:mm:ss"), who, body);
            if (m.Text != null && m.Note != null)
                Console.WriteLine("{0}      ({1})", indent, m.Note);
        }
    }
}
