using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml.Media.Imaging;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// Profile and chat pictures.
    ///
    /// Kept on disk under the photo id the server gave them, so each is fetched once
    /// and survives restarts - and a changed picture is a different id, which means
    /// there is nothing to invalidate.
    ///
    /// Files are handed to XAML as ms-appdata URIs rather than decoded here. The
    /// framework then loads and caches them itself, off the UI thread, and a picture
    /// that fails to decode simply does not appear instead of throwing somewhere
    /// unrelated.
    ///
    /// Everything fails quietly. An avatar is decoration: a chat list that loads
    /// without pictures is far better than one that does not load.
    /// </summary>
    internal static class AvatarCache
    {
        private const string Folder = "avatars";

        /// <summary>Photo ids already tried and found wanting, so they are not retried.</summary>
        private static readonly HashSet<long> Missing = new HashSet<long>();

        private static readonly Dictionary<long, BitmapImage> Loaded =
            new Dictionary<long, BitmapImage>();

        /// <summary>
        /// Why the last picture failed, if one did.
        ///
        /// Avatars fail quietly by design - a chat list without pictures is far
        /// better than one that will not load - but quiet is also why a whole class
        /// of them going missing is invisible. This keeps the reason so it can be
        /// asked for once, rather than guessed at.
        /// </summary>
        public static string LastError;

        /// <summary>
        /// The picture for a chat, downloading it if this is the first time.
        /// Null when there is nothing to show.
        /// </summary>
        public static async Task<BitmapImage> GetAsync(MtprotoClient client, DialogItem chat)
        {
            if (chat == null || chat.PhotoId == 0) return null;

            BitmapImage cached;
            lock (Loaded) if (Loaded.TryGetValue(chat.PhotoId, out cached)) return cached;

            lock (Missing) if (Missing.Contains(chat.PhotoId)) return null;

            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync(Folder, CreationCollisionOption.OpenIfExists);

                string name = chat.PhotoId.ToString("x16") + ".jpg";
                bool have = false;

                try
                {
                    await folder.GetFileAsync(name);
                    have = true;
                }
                catch (Exception)
                {
                    // Not cached yet.
                }

                if (!have)
                {
                    var peer = new PeerInfo
                    {
                        Id = chat.PeerId,
                        AccessHash = chat.AccessHash,
                        PhotoId = chat.PhotoId,
                        Name = chat.Title,
                    };

                    // The peer has to be addressed as what it is - a group picture
                    // asked for as though it belonged to a user is simply not found.
                    byte[] location = Peers.PhotoLocation(peer, chat.Kind);
                    if (location == null) return null;

                    byte[] bytes = await Media.DownloadLocationAsync(
                        client, location, TelegramService.Info);

                    if (bytes == null || bytes.Length == 0)
                    {
                        lock (Missing) Missing.Add(chat.PhotoId);
                        return null;
                    }

                    StorageFile file = await folder.CreateFileAsync(
                        name, CreationCollisionOption.ReplaceExisting);

                    await FileIO.WriteBytesAsync(file, bytes);
                }

                var image = new BitmapImage(
                    new Uri("ms-appdata:///local/" + Folder + "/" + name));

                lock (Loaded) Loaded[chat.PhotoId] = image;
                return image;
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                LastError = (chat.Kind ?? "?") + ": " +
                            (rpc != null ? rpc.ErrorType : ex.Message);

                lock (Missing) Missing.Add(chat.PhotoId);
                return null;
            }
        }
    }
}
