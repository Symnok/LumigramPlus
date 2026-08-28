using System;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// Login, split into two commands that share one authorisation key.
    ///
    ///   sendcode &lt;phone&gt; [host]   negotiates a key, requests a code, stores both
    ///   signin   &lt;code&gt;           reuses the stored key and completes the login
    ///
    /// They are separate because a login code is bound to the authorisation that
    /// requested it. Combining them would be fine in one process, but the user needs
    /// time in between to read the code - and re-running a combined command issues a
    /// *new* code, silently invalidating the one they are typing. That is a real
    /// mistake this layout prevents rather than a hypothetical one.
    /// </summary>
    internal static class LoginCommand
    {
        // Production datacenters, from the same source as the rest of the DC list.
        private const string ProductionDc1 = "149.154.175.50";

        public static int RunSendCode(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: sendcode <phone-without-plus> [host]");
                return 2;
            }

            string phone = args[1].TrimStart('+');
            string host = args.Length > 2 ? args[2] : ProductionDc1;

            Console.WriteLine("auth.sendCode");
            Console.WriteLine("  phone : +{0}", phone);
            Console.WriteLine("  dc    : {0}", host);
            Console.WriteLine("  layer : {0}", TlConstructors.Layer);
            Console.WriteLine();

            try { return SendCodeAsync(phone, host).GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
        }

        public static int RunSignIn(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: signin <code>");
                return 2;
            }

            try { return SignInAsync(args[1].Trim()).GetAwaiter().GetResult(); }
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
                Console.WriteLine();

                string t = rpc.ErrorType ?? "";
                if (t.StartsWith("PHONE_MIGRATE_"))
                    Console.WriteLine("  -> the account lives on DC{0}; re-run against that datacenter.",
                                      t.Substring(t.Length - 1));
                else if (t.Contains("PHONE_CODE_INVALID"))
                    Console.WriteLine("  -> wrong code, or a newer code superseded it.");
                else if (t.Contains("PHONE_CODE_EXPIRED"))
                    Console.WriteLine("  -> the code timed out; run sendcode again.");
                else if (t.Contains("SESSION_PASSWORD_NEEDED"))
                    Console.WriteLine("  -> two-step verification is on; use:  password <your-password>");
                else if (t.Contains("FLOOD_WAIT"))
                    Console.WriteLine("  -> rate limited; wait before retrying.");
                return 1;
            }
            Console.WriteLine("FAILED: {0}: {1}", ex.GetType().Name, ex.Message);
            return 1;
        }

        public static int RunPassword(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: password <your-two-step-password>");
                return 2;
            }

            // Everything after the command is the password, so spaces survive.
            string password = string.Join(" ", args, 1, args.Length - 1);

            try { return PasswordAsync(password).GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
        }

        /// <summary>
        /// Completes a login that stopped at SESSION_PASSWORD_NEEDED.
        ///
        /// Reuses the stored authorisation key, so the phone code already accepted
        /// stays valid and no new code is sent.
        /// </summary>
        private static async Task<int> PasswordAsync(string password)
        {
            SessionStore store = SessionStore.Load();
            if (store == null)
            {
                Console.WriteLine("No stored session. Run sendcode first.");
                return 2;
            }

            Console.WriteLine("two-step verification");
            Console.WriteLine("  dc  : {0}", store.Host);
            Console.WriteLine("  key : {0:x16}  (reused)", store.AuthKeyId);
            Console.WriteLine();

            var crypto = new DesktopCrypto();

            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(crypto, transport, Console.WriteLine);
                await client.ConnectWithKeyAsync(store.Host, TelegramServers.DefaultPort, store.ToAuthKey());

                // --- account.getPassword: the server's SRP parameters ---------
                var q = new TlWriter();
                q.WriteConstructor(TlConstructors.AccountGetPassword);

                Console.WriteLine("-> account.getPassword");
                TlReader r = await client.InvokeAsync(q.ToArray(), BuildInfo());

                r.Expect(TlConstructors.AccountPassword, "account.password");
                int flags = r.ReadInt();
                bool hasPassword = (flags & 4) != 0;

                if (!hasPassword)
                {
                    Console.WriteLine("This account has no two-step password set.");
                    return 1;
                }

                uint algo = r.ReadConstructor();
                if (algo != TlConstructors.PasswordKdfAlgoSha256Pbkdf2)
                    throw new MtprotoException("unsupported password KDF 0x" + algo.ToString("x8"));

                byte[] salt1 = r.ReadBytes();
                byte[] salt2 = r.ReadBytes();
                int g = r.ReadInt();
                byte[] pBytes = r.ReadBytes();
                byte[] srpB = r.ReadBytes();
                long srpId = r.ReadLong();

                Console.WriteLine("<- account.password  g={0} p={1} bits srp_id={2:x16}",
                                  g, pBytes.Length * 8, srpId);
                Console.WriteLine();
                Console.WriteLine("computing SRP proof (100,000 PBKDF2-HMAC-SHA512 iterations)...");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                SrpProof proof = Srp.ComputeProof(crypto, password, salt1, salt2, g, pBytes, srpB,
                                                  Console.WriteLine);
                sw.Stop();
                Console.WriteLine("proof ready in {0} ms", sw.ElapsedMilliseconds);
                Console.WriteLine();

                // --- auth.checkPassword ---------------------------------------
                q = new TlWriter();
                q.WriteConstructor(TlConstructors.AuthCheckPassword)
                 .WriteConstructor(TlConstructors.InputCheckPasswordSrp)
                 .WriteLong(srpId)
                 .WriteBytes(proof.A)
                 .WriteBytes(proof.M1);

                Console.WriteLine("-> auth.checkPassword");
                r = await client.InvokeAsync(q.ToArray());

                r.Expect(TlConstructors.AuthAuthorization, "auth.authorization");
                r.ReadInt();

                Console.WriteLine("<- auth.authorization");
                Console.WriteLine();
                Console.WriteLine("SUCCESS - signed in with two-step verification at layer {0}.",
                                  TlConstructors.Layer);
                Console.WriteLine("Full path: sendCode -> signIn -> checkPassword -> authorization.");
                Console.WriteLine();
                Console.WriteLine("This session is revocable from Telegram: Settings -> Devices.");
                return 0;
            }
        }

        private static ClientInfo BuildInfo()
        {
            var info = ClientInfo.Default;
            info.ApiId = Secrets.ApiId;
            info.ApiHash = Secrets.ApiHash;
            return info;
        }

        private static async Task<int> SendCodeAsync(string phone, string host)
        {
            var crypto = new DesktopCrypto();

            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(crypto, transport, Console.WriteLine);

                Console.WriteLine("negotiating an auth key...");
                await client.ConnectAsync(host, TelegramServers.DefaultPort);
                Console.WriteLine("auth key ready: {0:x16}", client.AuthKey.KeyId);
                Console.WriteLine();

                var info = BuildInfo();

                var q = new TlWriter();
                q.WriteConstructor(TlConstructors.AuthSendCode)
                 .WriteString(phone)
                 .WriteInt(info.ApiId)
                 .WriteString(info.ApiHash)
                 .WriteConstructor(TlConstructors.CodeSettings)
                 .WriteInt(0);

                Console.WriteLine("-> auth.sendCode");
                TlReader r = await client.InvokeAsync(q.ToArray(), info);

                r.Expect(TlConstructors.AuthSentCode, "auth.sentCode");
                r.ReadInt();                                   // flags

                uint codeType = r.ReadConstructor();
                string codeTypeName = codeType == TlConstructors.AuthSentCodeTypeApp ? "Telegram app"
                                    : codeType == TlConstructors.AuthSentCodeTypeSms ? "SMS"
                                    : "0x" + codeType.ToString("x8");
                int codeLength = r.ReadInt();
                string phoneCodeHash = r.ReadString();

                var store = SessionStore.FromAuthKey(client.AuthKey, host);
                store.Phone = phone;
                store.PhoneCodeHash = phoneCodeHash;
                store.Save();

                Console.WriteLine("<- auth.sentCode");
                Console.WriteLine("   delivered via : {0}", codeTypeName);
                Console.WriteLine("   code length   : {0}", codeLength);
                Console.WriteLine();
                Console.WriteLine("Session saved to {0}", SessionStore.Path);
                Console.WriteLine("Now run:  signin <code>   (this will NOT send a new code)");
                return 0;
            }
        }

        private static async Task<int> SignInAsync(string code)
        {
            SessionStore store = SessionStore.Load();
            if (store == null)
            {
                Console.WriteLine("No stored session. Run sendcode first.");
                return 2;
            }

            Console.WriteLine("auth.signIn");
            Console.WriteLine("  phone : +{0}", store.Phone);
            Console.WriteLine("  dc    : {0}", store.Host);
            Console.WriteLine("  key   : {0:x16}  (reused - no new code is sent)", store.AuthKeyId);
            Console.WriteLine();

            var crypto = new DesktopCrypto();

            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(crypto, transport, Console.WriteLine);
                await client.ConnectWithKeyAsync(store.Host, TelegramServers.DefaultPort, store.ToAuthKey());

                var q = new TlWriter();
                q.WriteConstructor(TlConstructors.AuthSignIn)
                 .WriteInt(1)                                  // flags: phone_code present
                 .WriteString(store.Phone)
                 .WriteString(store.PhoneCodeHash)
                 .WriteString(code);

                Console.WriteLine("-> auth.signIn");
                TlReader r = await client.InvokeAsync(q.ToArray(), BuildInfo());

                r.Expect(TlConstructors.AuthAuthorization, "auth.authorization");
                r.ReadInt();                                   // flags

                Console.WriteLine("<- auth.authorization");
                Console.WriteLine();
                Console.WriteLine("SUCCESS - signed in at layer {0}.", TlConstructors.Layer);
                Console.WriteLine("The full login path works: sendCode -> signIn -> authorization.");
                Console.WriteLine();
                Console.WriteLine("This session is revocable from Telegram: Settings -> Devices.");
                return 0;
            }
        }
    }
}
