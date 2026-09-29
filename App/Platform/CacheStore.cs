using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;

namespace LumigramPlus.App
{
    /// <summary>How much of something there is on disk.</summary>
    internal sealed class CacheSize
    {
        public long Bytes;
        public int Files;

        /// <summary>
        /// Files that would not go.
        ///
        /// Only meaningful for a clear. A file being played or decoded at that
        /// moment is locked, and a cache that refuses to be emptied without saying
        /// why is worse than one that says it kept two files.
        /// </summary>
        public int Kept;
    }

    /// <summary>
    /// The downloaded copies, and getting rid of them.
    ///
    /// Everything named here can be fetched again from Telegram, which is what makes
    /// deleting it safe and is the whole definition of what belongs in this list.
    /// The authorisation key lives in the same LocalFolder and emphatically does not
    /// belong: deleting it would sign the phone out. So this walks named folders
    /// rather than the local folder itself - a cache cleaner that works by emptying
    /// everything would take the session with it.
    ///
    /// The half-recorded video message at the local folder root is left alone for
    /// the same reason: it has never been anywhere else, so it is not a copy of
    /// anything and deleting it would lose it.
    /// </summary>
    internal static class CacheStore
    {
        /// <summary>
        /// Named by the caches themselves rather than spelled again here, so a
        /// folder that gets renamed cannot quietly stop being cleaned.
        /// </summary>
        private static readonly string[] Folders = { MediaCache.Folder, AvatarCache.Folder };

        /// <summary>What is on disk now. Reads sizes; changes nothing.</summary>
        public static async Task<CacheSize> MeasureAsync()
        {
            var total = new CacheSize();

            foreach (string name in Folders)
            {
                foreach (StorageFile file in await FilesAsync(name))
                {
                    try
                    {
                        Windows.Storage.FileProperties.BasicProperties basic =
                            await file.GetBasicPropertiesAsync();

                        total.Bytes += (long)basic.Size;
                        total.Files++;
                    }
                    catch (Exception)
                    {
                        // A file that cannot be measured still counts as one file;
                        // its size is the only thing not known.
                        total.Files++;
                    }
                }
            }

            return total;
        }

        /// <summary>
        /// Deletes every cached file, and reports what that freed.
        ///
        /// The size is taken before each delete rather than by measuring first and
        /// deleting after: a download finishing in between would otherwise be
        /// counted as freed while still being there.
        ///
        /// Per file rather than per folder. Deleting the folder is one call and
        /// fails whole if any single file in it is open - which is exactly the case
        /// this has to survive, since the way to reach the settings from a video is
        /// to leave it playing.
        /// </summary>
        public static async Task<CacheSize> ClearAsync()
        {
            var freed = new CacheSize();

            foreach (string name in Folders)
            {
                foreach (StorageFile file in await FilesAsync(name))
                {
                    long size = 0;

                    try
                    {
                        Windows.Storage.FileProperties.BasicProperties basic =
                            await file.GetBasicPropertiesAsync();

                        size = (long)basic.Size;
                    }
                    catch (Exception)
                    {
                        // Deleted anyway; only the arithmetic suffers.
                    }

                    try
                    {
                        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);

                        freed.Bytes += size;
                        freed.Files++;
                    }
                    catch (Exception)
                    {
                        freed.Kept++;
                    }
                }
            }

            // The pictures already decoded are held in memory too, and a chat list
            // showing avatars whose files have gone is the lesser half of the
            // problem: the record of which ones failed is what would stop them ever
            // being fetched again.
            AvatarCache.Forget();

            return freed;
        }

        /// <summary>The files in one cache folder, or none if it is not there yet.</summary>
        private static async Task<IReadOnlyList<StorageFile>> FilesAsync(string name)
        {
            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .GetFolderAsync(name);

                return await folder.GetFilesAsync();
            }
            catch (Exception)
            {
                return new StorageFile[0];
            }
        }

        /// <summary>
        /// A size in the units a person would use for it.
        ///
        /// One decimal place from a megabyte up, none below: "1.4 MB" says something
        /// "1 MB" does not, where "437.0 KB" only takes up room.
        /// </summary>
        public static string Describe(long bytes)
        {
            if (bytes <= 0) return "nothing";
            if (bytes < 1024) return bytes + " bytes";
            if (bytes < 1024 * 1024) return (bytes / 1024) + " KB";

            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }
    }
}
