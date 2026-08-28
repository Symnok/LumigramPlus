using System;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Phone;

namespace LumigramPlus.App
{
    /// <summary>
    /// The app's connection to Telegram.
    ///
    /// One connection, held for as long as the app runs, because an authorisation
    /// key costs several seconds to negotiate and a login is several calls that must
    /// share one session. Reconnecting between them invalidates whatever the
    /// previous call set up.
    ///
    /// Deliberately smaller than the Silverlight client's equivalent: there is no
    /// polling loop, no reconnect timer and no update plumbing here yet. Those
    /// belong with the chat list, and adding them before there is anything to show
    /// would be building machinery with nothing to drive.
    /// </summary>
    internal static class TelegramService
    {
        public static readonly ICrypto Crypto = new PhoneCrypto();

        private static readonly object Gate = new object();
        private static MtprotoClient _client;
        private static PhoneTransport _transport;

        public static SessionStore Session;

        /// <summary>
        /// Raised whenever a new connection is built.
        ///
        /// Pushed updates arrive on one connection, and this app replaces its
        /// connection routinely - on resume, after a fault, after a datacenter
        /// migration. Anything listening for updates has to follow, or it goes deaf
        /// the first time the socket is rebuilt and stays that way.
        /// </summary>
        public static event Action<MtprotoClient> ClientCreated;

        /// <summary>
        /// What this client tells Telegram about itself.
        ///
        /// A fresh instance every time. ClientInfo.Default used to be a shared
        /// mutable object, and call sites assigning over it meant one caller's
        /// credentials silently became everyone's - and anything running before them
        /// sent api_id 0, which Telegram rejects as CONNECTION_API_ID_INVALID.
        /// </summary>
        public static ClientInfo Info
        {
            get
            {
                ClientInfo info = ClientInfo.Default;
                info.ApiId = Secrets.ApiId;
                info.ApiHash = Secrets.ApiHash;
                // Distinct from the Silverlight client on purpose. Both share an
                // api_id, so Telegram lists both under the same registered app name
                // - this is the only field that says which of the two a session
                // actually came from.
                info.DeviceModel = "Windows Phone (Lumigram+)";
                info.SystemVersion = "8.1";
                info.AppVersion = "Lumigram+";
                return info;
            }
        }

        public static MtprotoClient Current
        {
            get { lock (Gate) return _client; }
        }

        /// <summary>
        /// The live connection, opening one if there is not already one.
        ///
        /// Reuses the stored authorisation key when there is one, which is the normal
        /// path after first launch: keys are permanent, and paying for the DH
        /// exchange once is the entire point of storing it.
        /// </summary>
        public static async Task<MtprotoClient> ConnectAsync(Action<string> progress = null)
        {
            lock (Gate)
            {
                // A cached connection is only worth having while it works. The
                // system closes sockets during suspension, so the one held from
                // before the app was put away is dead - and returning it means the
                // first thing the user does after reopening fails, rather than
                // reconnecting.
                if (_client != null && (_transport == null || _transport.IsConnected))
                    return _client;
            }

            Disconnect();

            progress = progress ?? delegate { };

            if (Session == null) Session = await SessionStore.LoadAsync();

            var transport = new PhoneTransport();
            var client = new MtprotoClient(Crypto, transport, delegate { });
            client.Info = Info;

            if (Session != null)
            {
                progress("Connecting...");
                await client.ConnectWithKeyAsync(Session.Host, TelegramServers.DefaultPort,
                                                 Session.ToAuthKey());
            }
            else
            {
                progress("Negotiating an authorisation key...");
                string host = TelegramServers.ProductionDc2Host;

                await client.ConnectAsync(host, TelegramServers.DefaultPort);

                Session = SessionStore.FromAuthKey(client.AuthKey, host);
                await Session.SaveAsync();
            }

            lock (Gate)
            {
                _transport = transport;
                _client = client;
            }

            Announce(client);
            return client;
        }

        /// <summary>
        /// Opens a second connection without disturbing the current one.
        ///
        /// Needed for the QR migrate step: a login token expires in seconds and the
        /// handshake on the new datacenter takes several. Closing the old connection
        /// first means the token is usually dead before it can be imported, so both
        /// are held open until the import has been made.
        /// </summary>
        public static async Task<MtprotoClient> ConnectSeparateAsync(int dcId,
                                                                     Action<string> progress = null)
        {
            progress = progress ?? delegate { };
            progress("Preparing datacenter " + dcId + "...");

            string host = TelegramServers.HostFor(dcId);

            var transport = new PhoneTransport();
            var client = new MtprotoClient(Crypto, transport, delegate { });
            client.Info = Info;

            await client.ConnectAsync(host, TelegramServers.DefaultPort);
            return client;
        }

        private static void Announce(MtprotoClient client)
        {
            Action<MtprotoClient> handler = ClientCreated;
            if (handler == null) return;

            try { handler(client); }
            catch (Exception) { }
        }

        /// <summary>Makes a separately established connection the app's connection.</summary>
        public static async Task AdoptAsync(MtprotoClient client, int dcId)
        {
            MtprotoClient old;
            lock (Gate)
            {
                old = _client;
                _client = client;
                _transport = null;
            }

            if (old != null)
            {
                // Give up the authorisation on the datacenter being left behind.
                //
                // Exporting a login token requires initConnection, which registers a
                // connection attempt - so migrating leaves an authorisation on the
                // old datacenter that will never be completed, and Telegram lists it
                // as an unsuccessful login. Nothing else clears it: the key is not
                // signed in, so there is no session to end from anywhere.
                try { await Messages.LogOutAsync(old, Info); }
                catch (Exception) { }

                try { old.Dispose(); }
                catch (Exception) { }
            }

            Session = SessionStore.FromAuthKey(client.AuthKey, TelegramServers.HostFor(dcId));
            await Session.SaveAsync();
        }

        /// <summary>
        /// Whether this error means the authorisation no longer exists.
        ///
        /// The session can be ended from anywhere - another device, Telegram's own
        /// device list, or the account being deactivated - and the only way this app
        /// finds out is by being refused. Retrying cannot help, and retrying quietly
        /// is how an app ends up looking broken instead of signed out.
        /// </summary>
        public static bool IsAuthGone(RpcException ex)
        {
            string type = ex == null ? "" : (ex.ErrorType ?? "");

            return type.Contains("AUTH_KEY_UNREGISTERED")
                || type.Contains("SESSION_REVOKED")
                || type.Contains("SESSION_EXPIRED")
                || type.Contains("USER_DEACTIVATED")
                || type.Contains("AUTH_KEY_DUPLICATED");
        }

        /// <summary>
        /// Raised when the server has refused the stored authorisation.
        ///
        /// Pages listen so whatever is on screen can stop and send the user back to
        /// signing in, rather than sitting there failing every few seconds.
        /// </summary>
        public static event Action SignedOutRemotely;

        /// <summary>Forgets an authorisation the server has already discarded.</summary>
        public static async Task AuthGoneAsync()
        {
            Disconnect();
            Session = null;
            Notifications.Reset();

            await SessionStore.DeleteAsync();

            Action handler = SignedOutRemotely;
            if (handler != null) handler();
        }

        /// <summary>
        /// Ends the session on the server as well as here.
        ///
        /// Deleting the key locally is not signing out: the session stays live in
        /// Telegram's device list and anyone holding the key could still use it.
        /// The local copy goes either way - the user asked to sign out, and a
        /// network failure must not leave a credential on the phone - so the caller
        /// is told whether the server end was reached.
        /// </summary>
        public static async Task<bool> SignOutAsync()
        {
            bool revoked = false;
            Notifications.Reset();

            try
            {
                MtprotoClient client = await ConnectAsync();
                revoked = await Messages.LogOutAsync(client, Info);
            }
            catch (Exception)
            {
                revoked = false;
            }

            Disconnect();
            Session = null;

            await SessionStore.DeleteAsync();
            return revoked;
        }

        public static async Task SignedInAsync()
        {
            if (Session == null) return;

            Session.SignedIn = true;
            await Session.SaveAsync();
        }

        public static void Disconnect()
        {
            MtprotoClient client;
            lock (Gate)
            {
                client = _client;
                _client = null;
                _transport = null;
            }

            if (client == null) return;

            try { client.Dispose(); }
            catch (Exception) { }
        }
    }
}
