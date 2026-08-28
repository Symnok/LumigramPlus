using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Mtproto;

namespace Lumigram.Harness
{
    /// <summary>
    /// Runs a real auth-key handshake against a Telegram datacenter.
    ///
    /// Defaults to the test cluster, which accepts the same protocol but is not
    /// attached to real accounts - so the whole crypto path can be exercised end to
    /// end without a phone number or any risk to a live account.
    /// </summary>
    internal static class HandshakeCommand
    {
        public static int Run(string[] args)
        {
            string host = args.Length > 1 ? args[1] : TelegramServers.TestDc2Host;
            int port = args.Length > 2 ? int.Parse(args[2]) : TelegramServers.DefaultPort;

            Console.WriteLine("MTProto 2.0 auth key handshake");
            Console.WriteLine("  target: {0}:{1}{2}", host, port,
                              host == TelegramServers.TestDc2Host ? "  (test datacenter)" : "");
            Console.WriteLine();

            try
            {
                return RunAsync(host, port).GetAwaiter().GetResult();
            }
            catch (AggregateException ex)
            {
                Console.WriteLine();
                Console.WriteLine("FAILED: " + ex.InnerException.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("FAILED: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        private static async Task<int> RunAsync(string host, int port)
        {
            var crypto = new DesktopCrypto();
            var sw = Stopwatch.StartNew();

            using (var transport = new TcpTransport())
            {
                Console.WriteLine("connecting...");
                await transport.ConnectAsync(host, port);
                Console.WriteLine("connected in {0} ms", sw.ElapsedMilliseconds);
                Console.WriteLine();

                var handshake = new AuthKeyHandshake(crypto, transport, Console.WriteLine);
                AuthKey key = await handshake.RunAsync();

                sw.Stop();
                Console.WriteLine();
                Console.WriteLine("SUCCESS - authorisation key established");
                Console.WriteLine("  auth_key_id : {0:x16}", key.KeyId);
                Console.WriteLine("  server_salt : {0:x16}", key.ServerSalt);
                Console.WriteLine("  key length  : {0} bytes", key.Key.Length);
                Console.WriteLine("  time offset : {0} s", key.TimeOffset);
                Console.WriteLine("  elapsed     : {0} ms", sw.ElapsedMilliseconds);
                Console.WriteLine();
                Console.WriteLine("  key fingerprint (first 16 bytes): {0}", key.Key.Slice(0, 16).ToHex());
                return 0;
            }
        }
    }
}
