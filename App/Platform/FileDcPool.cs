using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// Connections to the datacenters the account's files live on.
    ///
    /// An account is signed in on one datacenter, but its files are spread over all
    /// of them: a photo is stored where the phone that sent it was, so a chat list
    /// of people in three countries refers to files on three datacenters. Asking the
    /// signed-in one for a file it does not hold answers FILE_MIGRATE_n, which is
    /// not a failure - it is the address.
    ///
    /// Reaching n means a whole second connection: its own handshake, its own auth
    /// key, and the account imported onto it. That costs several seconds on a phone,
    /// which is why the connections are kept for as long as the app runs rather than
    /// built per file - a chat list is dozens of pictures from a handful of
    /// datacenters, so the cost is paid once each and then not again.
    ///
    /// Not kept across launches. The keys could be stored the way the main one is,
    /// but each is a live credential for the account, and writing three more of them
    /// to disk to save a few seconds once per launch is not a trade worth making
    /// without asking.
    /// </summary>
    internal static class FileDcPool
    {
        private static readonly object Gate = new object();

        /// <summary>
        /// Held as the task that builds it, not as the finished client.
        ///
        /// Avatars are fetched one after another, but a conversation loading
        /// pictures while the chat list fetches avatars is two callers at once - and
        /// storing only finished clients means both see nothing there and both start
        /// a handshake. Parking the task means the second one waits for the first.
        /// </summary>
        private static readonly Dictionary<int, Task<MtprotoClient>> Clients =
            new Dictionary<int, Task<MtprotoClient>>();

        /// <summary>
        /// Runs a download, on the datacenter that turns out to hold the file.
        ///
        /// Optimistic: the signed-in connection is tried first, because most files
        /// are on it and asking is cheaper than knowing. The server's answer is what
        /// settles it, and once a datacenter has answered for one file the
        /// connection to it is reused for the rest.
        ///
        /// <paramref name="dcId"/> is where the file said it lives, which is a hint
        /// and not a decision: it is used only to go straight to a connection
        /// already open. It is never a reason to build one, because the file may be
        /// on the home datacenter and building a second connection to reach it would
        /// cost seconds to arrive back where it started.
        ///
        /// The work is passed as something that can be run again rather than as a
        /// started download, because a retry has to begin from the first byte.
        /// </summary>
        public static async Task<T> RunAsync<T>(int dcId, Func<MtprotoClient, Task<T>> download)
        {
            MtprotoClient known = Opened(dcId);

            if (known != null)
            {
                try
                {
                    return await download(known);
                }
                catch (RpcException ex)
                {
                    // The hint was stale - a file reference can be renewed against a
                    // different datacenter - so the server's answer wins over what
                    // was open.
                    if (ex.MigrateDatacenter == 0) { Forget(dcId, ex); throw; }

                    return await download(await GetAsync(ex.MigrateDatacenter));
                }
                catch (Exception)
                {
                    // Not the server refusing: the socket. WinRT closes these while
                    // the app is suspended and hands back something that still looks
                    // connected, so this is the ordinary shape of the first request
                    // after a resume. Drop it and let the signed-in connection
                    // answer, which will name the datacenter again if the file
                    // really is elsewhere.
                    Forget(dcId, null);
                }
            }

            MtprotoClient home = await TelegramService.ConnectAsync();

            try
            {
                return await download(home);
            }
            catch (RpcException ex)
            {
                int elsewhere = ex.MigrateDatacenter;
                if (elsewhere == 0) throw;

                MtprotoClient moved = await GetAsync(elsewhere);

                // Once only. A second migrate from the datacenter that just named
                // itself is the server disagreeing with itself, and chasing that
                // loop is how a missing picture becomes a flat battery.
                return await download(moved);
            }
        }

        /// <summary>A connection already open to this datacenter, or null.</summary>
        private static MtprotoClient Opened(int dcId)
        {
            if (dcId == 0) return null;

            Task<MtprotoClient> task;
            lock (Gate) if (!Clients.TryGetValue(dcId, out task)) return null;

            // Only if it is already finished: waiting here would make the caller pay
            // for a handshake started for somebody else, when the home connection is
            // sitting there and probably has the file anyway.
            return task.Status == TaskStatus.RanToCompletion ? task.Result : null;
        }

        /// <summary>
        /// The connection to one datacenter, building and signing it in if this is
        /// the first file to need it.
        /// </summary>
        private static async Task<MtprotoClient> GetAsync(int dcId)
        {
            Task<MtprotoClient> task;

            lock (Gate)
            {
                if (!Clients.TryGetValue(dcId, out task))
                {
                    task = BuildAsync(dcId);
                    Clients[dcId] = task;
                }
            }

            try
            {
                return await task;
            }
            catch (Exception)
            {
                // A failed build must not be remembered as the answer, or one bad
                // moment means that datacenter is unreachable until the app is
                // restarted.
                lock (Gate) if (Clients.ContainsKey(dcId) && Clients[dcId] == task)
                    Clients.Remove(dcId);

                throw;
            }
        }

        private static async Task<MtprotoClient> BuildAsync(int dcId)
        {
            // Exported first, on the connection that is signed in. The credential is
            // short-lived, so it is fetched for this handshake rather than kept.
            MtprotoClient home = await TelegramService.ConnectAsync();
            ExportedAuth auth = await CrossDc.ExportAsync(home, dcId, TelegramService.Info);

            MtprotoClient moved = await TelegramService.ConnectSeparateAsync(dcId);

            try
            {
                await CrossDc.ImportAsync(moved, auth, TelegramService.Info);
            }
            catch (Exception)
            {
                try { moved.Dispose(); }
                catch (Exception) { }

                throw;
            }

            return moved;
        }

        /// <summary>
        /// Drops a connection that has stopped working, unless the trouble is one
        /// that trying again would survive.
        /// </summary>
        private static void Forget(int dcId, Exception ex)
        {
            var rpc = ex as RpcException;

            // A rate limit is the datacenter working and saying "not yet"; throwing
            // the connection away would mean paying for a handshake to be told the
            // same thing.
            if (rpc != null && rpc.FloodWaitSeconds > 0) return;

            lock (Gate) Clients.Remove(dcId);
        }

        /// <summary>
        /// Closes every extra connection.
        ///
        /// Called wherever the main connection is dropped. These are sockets like
        /// any other: WinRT closes them while the app is suspended and hands back
        /// something that still looks connected, so keeping them past that point
        /// means the next picture fails on a connection that is already dead.
        /// </summary>
        public static void Reset()
        {
            List<Task<MtprotoClient>> tasks;

            lock (Gate)
            {
                tasks = new List<Task<MtprotoClient>>(Clients.Values);
                Clients.Clear();
            }

            foreach (Task<MtprotoClient> task in tasks)
            {
                if (task.Status != TaskStatus.RanToCompletion) continue;

                try { task.Result.Dispose(); }
                catch (Exception) { }
            }
        }
    }
}
