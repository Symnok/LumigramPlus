using System;
using System.Threading;
using System.Threading.Tasks;
using Lumigram.Mtproto;
using Lumigram.Qr;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// Signs in by QR code, printed to the terminal so it can be scanned from the
    /// screen with another Telegram client.
    ///
    /// Useful in its own right when login codes are not being delivered, and it is
    /// also how the phone's QR screen gets exercised before it exists.
    /// </summary>
    internal static class QrLoginCommand
    {
        private const string ProductionDc2 = "149.154.167.51";

        public static int Run(string[] args)
        {
            string host = args.Length > 1 ? args[1] : ProductionDc2;

            try { return RunAsync(host).GetAwaiter().GetResult(); }
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
                if ((rpc.ErrorType ?? "").Contains("SESSION_PASSWORD_NEEDED"))
                    Console.WriteLine("  -> the token was accepted; finish with:  password <your-password>");
                return 1;
            }
            Console.WriteLine("FAILED: {0}: {1}", ex.GetType().Name, ex.Message);
            return 1;
        }

        private static string DatacenterHost(int dcId)
        {
            switch (dcId)
            {
                case 1: return "149.154.175.50";
                case 2: return "149.154.167.51";
                case 3: return "149.154.175.100";
                case 4: return "149.154.167.91";
                case 5: return "149.154.171.5";
                default: return ProductionDc2;
            }
        }

        private static async Task<int> RunAsync(string host)
        {
            var crypto = new DesktopCrypto();
            var info = ClientInfo.Default;
            info.ApiId = Secrets.ApiId;
            info.ApiHash = Secrets.ApiHash;

            var accepted = new ManualResetEventSlim(false);

            // A token accepted on the wrong datacenter must be carried to the right
            // one and imported. Exporting a fresh token instead abandons the
            // authorisation that was just created, leaving a dead session behind and
            // requiring a second scan.
            byte[] pendingImport = null;

            while (true)
            {
                using (var transport = new TcpTransport())
                {
                    var client = new MtprotoClient(crypto, transport, delegate { });

                    Console.WriteLine("connecting to {0} ...", host);
                    await client.ConnectAsync(host, TelegramServers.DefaultPort);

                    client.UpdateReceived += delegate (TlObject pushed)
                    {
                        if (QrLogin.IsTokenAccepted(pushed)) accepted.Set();
                    };

                    if (pendingImport != null)
                    {
                        byte[] token = pendingImport;
                        pendingImport = null;

                        // The token has been ageing through the handshake above. If
                        // it has expired, say so plainly and show a fresh code on
                        // this datacenter - where no migrate is needed at all.

                        QrLoginStep imported;
                        try
                        {
                            imported = await QrLogin.ImportTokenAsync(client, token, info);
                        }
                        catch (RpcException ex)
                        {
                            string t = ex.ErrorType ?? "";
                            if (t.Contains("SESSION_PASSWORD_NEEDED"))
                            {
                                Console.WriteLine();
                                Console.WriteLine("Token imported - two-step verification required.");
                                SaveSession(client, host);
                                Console.WriteLine("Finish with:  password <your-password>");
                                return 0;
                            }
                            if (t.Contains("AUTH_TOKEN_EXPIRED"))
                            {
                                Console.WriteLine("Token expired during the datacenter switch.");
                                Console.WriteLine("Showing a fresh code on DC - scan it once more.");
                                imported = null;
                            }
                            else throw;
                        }

                        if (imported == null)
                        {
                            accepted.Reset();
                            // fall through to export a fresh token on this datacenter
                        }
                        else

                        if (imported.Status == QrLoginStatus.Success)
                        {
                            Console.WriteLine();
                            Console.WriteLine("SUCCESS - signed in by QR (one scan).");
                            SaveSession(client, host);
                            return 0;
                        }
                    }

                    QrLoginStep step = await QrLogin.ExportTokenAsync(
                        client, info.ApiId, info.ApiHash, info);

                    if (step.Status == QrLoginStatus.Migrate)
                    {
                        pendingImport = step.Token;
                        host = DatacenterHost(step.DcId);
                        Console.WriteLine("account is on DC{0}, importing token there...", step.DcId);
                        continue;
                    }

                    if (step.Status == QrLoginStatus.Success)
                    {
                        Console.WriteLine();
                        Console.WriteLine("SUCCESS - signed in by QR.");
                        SaveSession(client, host);
                        return 0;
                    }

                    // Show it, then wait for the push (with a poll as a backstop).
                    Show(step, true);

                    // Poll. The push (updateLoginToken) is not reliable, so
                    // re-exporting is what actually detects the scan. Each export can
                    // return a different token, so redraw whenever it changes -
                    // a stale code on screen is what yields AUTH_TOKEN_EXPIRED.
                    int limit = Math.Max(10, step.SecondsRemaining);
                    int waited = 0;
                    bool migrated = false;

                    while (waited < limit)
                    {
                        accepted.Wait(TimeSpan.FromSeconds(2));
                        waited += 2;

                        QrLoginStep poll;
                        try
                        {
                            poll = await QrLogin.ExportTokenAsync(client, info.ApiId, info.ApiHash);
                        }
                        catch (RpcException ex)
                        {
                            if ((ex.ErrorType ?? "").Contains("SESSION_PASSWORD_NEEDED"))
                            {
                                Console.WriteLine();
                                Console.WriteLine("Token accepted - two-step verification required.");
                                SaveSession(client, host);
                                Console.WriteLine("Finish with:  password <your-password>");
                                return 0;
                            }
                            throw;
                        }

                        if (poll.Status == QrLoginStatus.Success)
                        {
                            Console.WriteLine();
                            Console.WriteLine("SUCCESS - signed in by QR.");
                            SaveSession(client, host);
                            return 0;
                        }
                        if (poll.Status == QrLoginStatus.Migrate)
                        {
                            pendingImport = poll.Token;
                            host = DatacenterHost(poll.DcId);
                            Console.WriteLine("account is on DC{0}, importing token there...", poll.DcId);
                            migrated = true;
                            break;
                        }
                        if (poll.Token != null &&
                            QrLoginStep.Base64Url(poll.Token) != QrLoginStep.Base64Url(step.Token))
                        {
                            step = poll;
                            Show(step, false);
                            waited = 0;
                            limit = Math.Max(10, step.SecondsRemaining);
                        }
                    }

                    if (migrated) { accepted.Reset(); continue; }

                    Console.WriteLine("Token expired unscanned - requesting a fresh one...");
                    accepted.Reset();
                }
            }
        }

        /// <summary>
        /// Renders the token: an image file that can actually be scanned, plus the
        /// terminal drawing when the console can show it.
        /// </summary>
        private static void Show(QrLoginStep step, bool firstTime)
        {
            bool[,] modules = QrCode.Encode(step.Url);

            string path = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location), "login-qr.bmp");

            try { QrTests.SaveBmp(modules, path, 12, 4); }
            catch (Exception) { path = null; }

            ClearIfPossible();
            Console.WriteLine();
            QrTests.Render(modules);
            Console.WriteLine();
            if (path != null) Console.WriteLine("  QR image: {0}", path);
            if (firstTime)
            {
                Console.WriteLine("  Scan with Telegram on another device:");
                Console.WriteLine("  Settings > Devices > Link Desktop Device");
            }
            Console.WriteLine("  expires in {0}s", step.SecondsRemaining);
            Console.WriteLine();
        }

        /// <summary>
        /// Console.Clear throws when output is redirected, which it is whenever the
        /// harness runs under a tool rather than a terminal.
        /// </summary>
        private static void ClearIfPossible()
        {
            try { Console.Clear(); }
            catch (Exception) { Console.WriteLine(); }
        }

        private static void SaveSession(MtprotoClient client, string host)
        {
            SessionStore store = SessionStore.FromAuthKey(client.AuthKey, host);
            store.ServerSalt = client.ServerSalt;
            store.Save();
            Console.WriteLine("session saved to {0}", SessionStore.Path);
        }
    }
}
