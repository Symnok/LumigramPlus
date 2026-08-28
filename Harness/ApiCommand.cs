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
