using System;
using System.Threading.Tasks;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// Proves the client recovers from a wrong device clock.
    ///
    /// The phone this app is built for had its clock several minutes out, and every
    /// request failed with bad_msg_notification 16 - message id too low. The client
    /// now learns the time from the server and re-sends, and that is exactly the
    /// kind of fix that is easy to write, easy to believe, and worth actually
    /// running against the real server before claiming.
    ///
    /// The session's clock is skewed by hand after the handshake, so the first
    /// request is guaranteed to be rejected.
    /// </summary>
    internal static class ClockSkewCommand
    {
        /// <summary>Far outside the window the server accepts, in both directions.</summary>
        private const int SkewSeconds = 900;

        public static int Run(string[] args)
        {
            try
            {
                return RunAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: {0}: {1}", ex.GetType().Name, ex.Message);
                return 1;
            }
        }

        private static async Task<int> RunAsync()
        {
            int failures = 0;
            failures += await OneAsync(-SkewSeconds, "behind");
            failures += await OneAsync(SkewSeconds, "ahead");

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "ALL PASS - the client recovers from a wrong device clock"
                : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static async Task<int> OneAsync(int skew, string direction)
        {
            Console.WriteLine();
            Console.WriteLine("--- clock {0} by {1} s ---", direction, Math.Abs(skew));

            var crypto = new DesktopCrypto();
            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(crypto, transport, Console.WriteLine);
                await client.ConnectAsync(TelegramServers.ProductionDc2Host, TelegramServers.DefaultPort);

                int adjusted = 0;
                client.ClockAdjusted += delegate(int offset) { adjusted++; };

                // Pretend the device clock is wrong. The offset the handshake worked
                // out is discarded, so message ids now fall outside the window the
                // server accepts and it must refuse the first attempt.
                client.Session.TimeOffset = client.Session.TimeOffset + skew;

                var info = ClientInfo.Default;
                info.ApiId = Secrets.ApiId;
                info.ApiHash = Secrets.ApiHash;

                var query = new TlWriter();
                query.WriteConstructor(TlConstructors.HelpGetNearestDc);

                TlReader result = await client.InvokeAsync(query.ToArray(), info);
                result.Expect(TlConstructors.NearestDc, "nearestDc");
                result.ReadString();

                int remaining = client.Session.DriftFrom(
                    (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds
                    + client.Session.TimeOffset);

                bool corrected = adjusted > 0 || Math.Abs(client.Session.TimeOffset) < 60;

                Console.WriteLine("  request succeeded after the skew");
                Console.WriteLine("  clock adjustments : {0}", adjusted);
                Console.WriteLine("  offset now        : {0} s", client.Session.TimeOffset);

                if (!corrected)
                {
                    Console.WriteLine("  FAIL - the request worked but the clock was never corrected");
                    return 1;
                }

                Console.WriteLine("  PASS");
                return 0;
            }
        }
    }
}
