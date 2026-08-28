using System;
using System.Threading.Tasks;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>Looks up a username or phone number, the way the New chat screen will.</summary>
    internal static class ResolveCommand
    {
        public static int Run(string[] args)
        {
            // With no argument, just show how inputs are classified - no network.
            if (args.Length < 2)
            {
                string[] samples =
                {
                    "testuser", "@testuser", "https://t.me/testuser",
                    "+19990001234", "19990001234", "+1 999 000-1234", "(999) 000.1234",
                    "a1234567", "1234",
                };
                Console.WriteLine("input classification:");
                foreach (string s in samples)
                {
                    bool phone = Contacts.LooksLikePhone(s);
                    Console.WriteLine("  {0,-24} -> {1,-8} {2}", s,
                        phone ? "phone" : "username",
                        phone ? Contacts.NormalisePhone(s) : Contacts.NormaliseUsername(s));
                }
                Console.WriteLine();
                Console.WriteLine("usage: resolve <username-or-phone>   (to query the server)");
                return 0;
            }

            try { return RunAsync(args[1]).GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
        }

        private static int Report(Exception ex)
        {
            Console.WriteLine();
            var rpc = ex as RpcException;
            if (rpc != null)
            {
                Console.WriteLine("NOT FOUND / REJECTED");
                Console.WriteLine("  code: {0}", rpc.Code);
                Console.WriteLine("  type: {0}", rpc.ErrorType);

                string t = rpc.ErrorType ?? "";
                if (t.Contains("USERNAME_NOT_OCCUPIED")) Console.WriteLine("  -> no such username.");
                else if (t.Contains("USERNAME_INVALID")) Console.WriteLine("  -> not a valid username.");
                else if (t.Contains("PHONE_NOT_OCCUPIED")) Console.WriteLine("  -> no Telegram account on that number.");
                return 1;
            }
            Console.WriteLine("FAILED: {0}: {1}", ex.GetType().Name, ex.Message);
            return 1;
        }

        private static async Task<int> RunAsync(string query)
        {
            SessionStore store = SessionStore.Load();
            if (store == null) { Console.WriteLine("No stored session."); return 2; }

            var crypto = new DesktopCrypto();
            var info = ClientInfo.Default;
            info.ApiId = Secrets.ApiId;
            info.ApiHash = Secrets.ApiHash;

            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(crypto, transport, delegate { });
                await client.ConnectWithKeyAsync(store.Host, TelegramServers.DefaultPort,
                                                 store.ToAuthKey());

                var warm = new TlWriter();
                warm.WriteConstructor(TlConstructors.HelpGetNearestDc);
                await client.InvokeAsync(warm.ToArray(), info);

                Console.WriteLine("looking up {0} as {1}...", query,
                                  Contacts.LooksLikePhone(query) ? "a phone number" : "a username");

                ResolvedPeer peer = await Contacts.ResolveAsync(client, query, info);

                Console.WriteLine();
                Console.WriteLine("FOUND");
                Console.WriteLine("  kind        : {0}", peer.Kind);
                Console.WriteLine("  id          : {0}", peer.PeerId);
                Console.WriteLine("  access_hash : {0:x16}", peer.AccessHash);
                Console.WriteLine("  title       : {0}", peer.Title);
                if (!string.IsNullOrEmpty(peer.Username)) Console.WriteLine("  username    : {0}", peer.Username);
                if (!string.IsNullOrEmpty(peer.Phone)) Console.WriteLine("  phone       : {0}", peer.Phone);
                return 0;
            }
        }
    }
}
