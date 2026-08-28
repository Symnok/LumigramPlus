using System;
using System.Threading.Tasks;
using Lumigram.Mtproto;
using Lumigram.Phone;
using LumigramPlus.App;

namespace LumigramPlus.Tasks
{
    /// <summary>
    /// Looking for what arrived while the app was away.
    ///
    /// Shared by every trigger. The timer says "fifteen minutes have passed" and the
    /// control channel says "bytes arrived on the connection", but the answer to
    /// both is the same question - what is waiting? - so there is one answer to it
    /// here rather than one per entry point.
    ///
    /// This may be a different process from the app, with its own statics that start
    /// empty. Everything it has to agree with the app about - which messages have
    /// already been announced - is on disk; everything else comes back from the
    /// server on each wake.
    /// </summary>
    internal static class BackgroundCheck
    {
        /// <summary>
        /// How many chats to read.
        ///
        /// A wake has seconds, not minutes, and the list is ordered by the time of
        /// the last message: anything that arrived since the last look is at the top
        /// of it. Asking for more would cost time to learn nothing.
        /// </summary>
        private const int Chats = 20;

        /// <summary>
        /// What this client tells Telegram about itself.
        ///
        /// The same answer the app gives, so a wake does not appear in Telegram's
        /// device list as a second client.
        /// </summary>
        public static ClientInfo Info
        {
            get
            {
                ClientInfo info = ClientInfo.Default;
                info.ApiId = Secrets.ApiId;
                info.ApiHash = Secrets.ApiHash;
                info.DeviceModel = "Windows Phone (Lumigram+)";
                info.SystemVersion = "8.1";
                info.AppVersion = "Lumigram+";
                return info;
            }
        }

        /// <returns>What happened, in a few words, for the app to show.</returns>
        public static async Task<string> RunAsync()
        {
            SessionStore session = await SessionStore.LoadAsync();

            // Signed out, or never signed in. Either way there is nothing to read
            // and nothing to say about it.
            if (session == null || !session.SignedIn || session.AuthKey == null)
                return "not signed in";

            var transport = new PhoneTransport();
            var client = new MtprotoClient(new PhoneCrypto(), transport, delegate { });

            client.Info = Info;

            try
            {
                await client.ConnectWithKeyAsync(session.Host, TelegramServers.DefaultPort,
                                                 session.ToAuthKey());

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, Chats, 0, 0, null, Info);

                // Raises the toasts and writes the tile. The seen-message map is on
                // disk, so this announces only what the app has not already
                // announced - and records what it does announce, so the app does not
                // repeat it when it is next opened.
                int announced = Notifications.Observe(page.Entries);

                return page.Entries.Count + " chats read, " +
                       (announced == 0 ? "nothing new" : announced + " announced");
            }
            finally
            {
                // Nothing else will close this. A connection left open is one the
                // phone keeps a radio awake for until the server gives up on it.
                client.Dispose();
            }
        }
    }
}
