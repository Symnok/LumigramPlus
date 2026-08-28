using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// Attachments, kept on disk once fetched.
    ///
    /// Keyed by the id Telegram gave the file, so the same picture is downloaded
    /// once however many times it is looked at, and survives restarts. Nothing is
    /// ever evicted yet - worth adding before this is used in anger, but a cache
    /// that grows is a better problem than a photo that downloads on every scroll.
    ///
    /// Files are handed on as ms-appdata paths rather than decoded here, so XAML
    /// loads them itself, off the UI thread.
    /// </summary>
    internal static class MediaCache
    {
        private const string Folder = "media";

        /// <summary>
        /// Fetches an attachment if it is not already here, and returns the URI to
        /// show it from. Null when it could not be fetched.
        /// </summary>
        public static async Task<Uri> GetAsync(MtprotoClient client, MediaInfo info,
                                               Action<long, long> progress = null)
        {
            if (info == null || info.Id == 0) return null;

            string name = Name(info);

            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync(Folder, CreationCollisionOption.OpenIfExists);

                try
                {
                    await folder.GetFileAsync(name);
                    return Uri(name);
                }
                catch (Exception)
                {
                    // Not cached; fetch it below.
                }

                StorageFile file = await folder.CreateFileAsync(
                    name, CreationCollisionOption.ReplaceExisting);

                // Written as it arrives rather than assembled first.
                //
                // A photo fits in memory and a video does not: holding a fifty
                // megabyte file whole, on a phone with half a gigabyte to share
                // between everything, is how an app gets killed mid-download. Each
                // chunk goes straight to disk, so the memory cost is one chunk
                // whatever the size of the file.
                long written = 0;

                using (System.IO.Stream stream = await file.OpenStreamForWriteAsync())
                {
                    await Media.DownloadAsync(client, info,
                        delegate (byte[] chunk)
                        {
                            stream.Write(chunk, 0, chunk.Length);
                            written += chunk.Length;
                        },
                        progress, TelegramService.Info);
                }

                if (written == 0)
                {
                    // Nothing came back; leaving an empty file behind would look
                    // cached and never be fetched again.
                    try { await file.DeleteAsync(); }
                    catch (Exception) { }

                    return null;
                }

                return Uri(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Fetches the small picture that stands in for a video or document.
        ///
        /// The same file as far as Telegram is concerned - same id, same reference -
        /// asked for by a different size name. Cached under its own name so it does
        /// not collide with the file itself.
        /// </summary>
        public static async Task<Uri> GetThumbAsync(MtprotoClient client, MediaInfo info)
        {
            if (info == null || info.Id == 0 || string.IsNullOrEmpty(info.ThumbSizeType))
                return null;

            var thumb = new MediaInfo
            {
                Kind = MediaKind.Document,      // addressed as a document either way
                Id = info.Id,
                AccessHash = info.AccessHash,
                FileReference = info.FileReference,
                DcId = info.DcId,
                SizeType = info.ThumbSizeType,
            };

            string name = info.Id.ToString("x16") + "-thumb.jpg";

            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync(Folder, CreationCollisionOption.OpenIfExists);

                try
                {
                    await folder.GetFileAsync(name);
                    return Uri(name);
                }
                catch (Exception)
                {
                    // Not cached yet.
                }

                byte[] bytes = await Media.DownloadLocationAsync(
                    client, Media.BuildLocation(thumb), TelegramService.Info);

                if (bytes == null || bytes.Length == 0) return null;

                StorageFile file = await folder.CreateFileAsync(
                    name, CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteBytesAsync(file, bytes);
                return Uri(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The cached file if there is one, without fetching anything.</summary>
        public static async Task<StorageFile> FindAsync(MediaInfo info)
        {
            if (info == null || info.Id == 0) return null;

            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .GetFolderAsync(Folder);

                return await folder.GetFileAsync(Name(info));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The extension matters: the picture control and the video player both
        /// decide what to do partly by the file name, and a video saved as .jpg is
        /// refused by both.
        /// </summary>
        private static string Name(MediaInfo info)
        {
            string extension = info.Kind == MediaKind.Photo ? ".jpg"
                             : info.Kind == MediaKind.Video ? ".mp4"
                             : ".bin";

            return info.Id.ToString("x16") + extension;
        }

        private static Uri Uri(string name)
        {
            return new Uri("ms-appdata:///local/" + Folder + "/" + name);
        }
    }
}
