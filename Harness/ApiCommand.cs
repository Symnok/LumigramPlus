using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// Exercises the encrypted message layer with a call that needs no
    /// authorisation: help.getNearestDc.
    ///
    /// This is the step between "we have a key" and "we can log in". If the
    /// encrypted layer has a fault - msg_key derivation, the key schedule, sequence
    /// numbering - it shows up here, where nothing is at stake, rather than in the
    /// middle of a login attempt.
    /// </summary>
    internal static class ApiCommand
    {
        /// <summary>
        /// help.getCountriesList against a real datacenter, no account needed:
        /// countries [host] [number-to-try...]
        ///
        /// Proves the calling codes the contacts page relies on are read correctly
        /// from what the server actually sends, and shows what each given number
        /// would be tried as, taking the first code of the list as "own".
        /// </summary>
        public static int RunCountries(string[] args)
        {
            string host = args.Length > 1 ? args[1] : TelegramServers.ProductionDc2Host;

            try
            {
                return RunCountriesAsync(host, args).GetAwaiter().GetResult();
            }
            catch (AggregateException ex)
            {
                Report(ex.InnerException);
                return 1;
            }
            catch (Exception ex)
            {
                Report(ex);
                return 1;
            }
        }

        private static async Task<int> RunCountriesAsync(string host, string[] args)
        {
            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(new DesktopCrypto(), transport, s => { });
                Console.WriteLine("connecting to {0}...", host);
                await client.ConnectAsync(host, TelegramServers.DefaultPort);

                var info = ClientInfo.Default;
                info.ApiId = Secrets.ApiId;
                info.ApiHash = Secrets.ApiHash;

                var codes = await PhoneNumbers.GetCallingCodesAsync(client, info);
                Console.WriteLine("{0} calling codes: {1}", codes.Count, string.Join(" ", codes));

                foreach (string probe in new[] { "16175551234", "447911123456", "79161234567",
                                                 "972501234567", "4930123456", "12684123456" })
                    Console.WriteLine("  {0} -> code {1}", probe,
                                      PhoneNumbers.CodeOf(probe, codes) ?? "(none)");

                for (int i = 2; i < args.Length; i++)
                    Console.WriteLine("  '{0}' as +44 user tries: {1}", args[i],
                                      string.Join(", ", PhoneNumbers.Candidates(args[i], "44")));

                return codes.Count > 100 ? 0 : 1;
            }
        }

        /// <summary>
        /// A real DH handshake and an encrypted call through an MTProto proxy:
        /// mtproxy host:port secret [datacenter-ip] [clock-error-seconds]
        ///
        /// The handshake sends unencrypted messages and the call encrypted ones, so
        /// both ways of finding the end of a padded packet are exercised, in both
        /// directions. The last argument is a clock offset, applied as the phone
        /// applies the session's measured one.
        /// </summary>
        public static int RunThroughMtProxy(string[] args)
        {
            if (args.Length < 3 || args[1].IndexOf(':') < 0)
            {
                Console.WriteLine("usage: mtproxy <host:port> <secret> [datacenter-ip] [clock-error-seconds]");
                return 2;
            }

            string[] hostPort = args[1].Split(':');
            string error;
            var proxy = new MtProxySettings
            {
                Host = hostPort[0],
                Port = int.Parse(hostPort[1]),
                Secret = MtProxySecret.Parse(args[2], out error),
            };

            if (proxy.Secret == null)
            {
                Console.WriteLine("secret: " + error);
                return 2;
            }

            string dc = args.Length > 3 ? args[3] : TelegramServers.ProductionDc2Host;
            int offset = args.Length > 4 ? int.Parse(args[4]) : 0;

            Console.WriteLine("through MTProto proxy {0}:{1}  ({2})", proxy.Host, proxy.Port,
                              proxy.Secret.Describe());

            try
            {
                return RunThroughMtProxyAsync(proxy, dc, offset).GetAwaiter().GetResult();
            }
            catch (AggregateException ex)
            {
                Report(ex.InnerException);
                return 1;
            }
            catch (Exception ex)
            {
                Report(ex);
                return 1;
            }
        }

        private static async Task<int> RunThroughMtProxyAsync(MtProxySettings proxy, string dc, int offset)
        {
            var crypto = new DesktopCrypto();
            var sw = Stopwatch.StartNew();

            // The offset is added to the clock exactly as the phone adds the
            // session's, so a large one here is a phone whose correction is wrong -
            // and a FakeTLS proxy should refuse it.
            using (var transport = new MtProxyTransport(new TcpTransport(), proxy, crypto, offset))
            {
                var client = new MtprotoClient(crypto, transport, Console.WriteLine);

                Console.WriteLine("connecting and negotiating an auth key with {0} (dc {1})...",
                                  dc, TelegramServers.DcFor(dc));
                await client.ConnectAsync(dc, TelegramServers.DefaultPort);
                Console.WriteLine("auth key ready ({0} ms)", sw.ElapsedMilliseconds);

                var info = ClientInfo.Default;
                info.ApiId = Secrets.ApiId;
                info.ApiHash = Secrets.ApiHash;

                var query = new TlWriter();
                query.WriteConstructor(TlConstructors.HelpGetNearestDc);
                TlReader result = await client.InvokeAsync(query.ToArray(), info);

                result.Expect(TlConstructors.NearestDc, "nearestDc");
                string country = result.ReadString();
                int thisDc = result.ReadInt();

                Console.WriteLine();
                Console.WriteLine("SUCCESS through the proxy - country {0}, this_dc {1}, {2} ms",
                                  country, thisDc, sw.ElapsedMilliseconds);
                return 0;
            }
        }

        public static int RunNearestDc(string[] args)
        {
            string host = args.Length > 1 ? args[1] : TelegramServers.TestDc2Host;
            int port = args.Length > 2 ? int.Parse(args[2]) : TelegramServers.DefaultPort;

            Console.WriteLine("encrypted session test: help.getNearestDc");
            Console.WriteLine("  target: {0}:{1}{2}", host, port,
                              host == TelegramServers.TestDc2Host ? "  (test datacenter)" : "");
            Console.WriteLine("  layer:  {0}", TlConstructors.Layer);
            Console.WriteLine();

            try
            {
                return RunAsync(host, port).GetAwaiter().GetResult();
            }
            catch (AggregateException ex)
            {
                Report(ex.InnerException);
                return 1;
            }
            catch (Exception ex)
            {
                Report(ex);
                return 1;
            }
        }

        private static void Report(Exception ex)
        {
            Console.WriteLine();
            var rpc = ex as RpcException;
            if (rpc != null)
            {
                Console.WriteLine("SERVER REJECTED THE CALL");
                Console.WriteLine("  code: {0}", rpc.Code);
                Console.WriteLine("  type: {0}", rpc.ErrorType);
            }
            else
            {
                Console.WriteLine("FAILED: {0}: {1}", ex.GetType().Name, ex.Message);
            }
        }

        private static async Task<int> RunAsync(string host, int port)
        {
            var crypto = new DesktopCrypto();
            var sw = Stopwatch.StartNew();

            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(crypto, transport, Console.WriteLine);

                Console.WriteLine("connecting and negotiating an auth key...");
                await client.ConnectAsync(host, port);
                Console.WriteLine("auth key ready ({0} ms)", sw.ElapsedMilliseconds);
                Console.WriteLine();

                var info = ClientInfo.Default;
                info.ApiId = Secrets.ApiId;
                info.ApiHash = Secrets.ApiHash;

                var query = new TlWriter();
                query.WriteConstructor(TlConstructors.HelpGetNearestDc);

                Console.WriteLine("-> invokeWithLayer({0}) + initConnection + help.getNearestDc",
                                  TlConstructors.Layer);

                TlReader result = await client.InvokeAsync(query.ToArray(), info);

                result.Expect(TlConstructors.NearestDc, "nearestDc");
                string country = result.ReadString();
                int thisDc = result.ReadInt();
                int nearestDc = result.ReadInt();

                sw.Stop();
                Console.WriteLine();
                Console.WriteLine("SUCCESS - the encrypted layer works end to end");
                Console.WriteLine("  country      : {0}", country);
                Console.WriteLine("  this_dc      : {0}", thisDc);
                Console.WriteLine("  nearest_dc   : {0}", nearestDc);
                Console.WriteLine("  elapsed      : {0} ms", sw.ElapsedMilliseconds);
                Console.WriteLine();
                Console.WriteLine("  Layer {0} was accepted for an unauthenticated call.", TlConstructors.Layer);
                return 0;
            }
        }
    }
}
