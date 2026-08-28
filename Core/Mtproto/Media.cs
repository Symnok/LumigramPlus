using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    public enum MediaKind
    {
        None,
        Photo,
        Video,
        Document,
        Voice,
        Location,
    }

    /// <summary>
    /// Everything needed to fetch one attachment, flattened out of the message.
    ///
    /// The three identifiers travel together and all three are required:
    /// <see cref="Id"/>, <see cref="AccessHash"/> and <see cref="FileReference"/>.
    /// The file reference is the one that surprises people - it is short-lived, and
    /// a stale one fails with FILE_REFERENCE_EXPIRED even though the id and hash are
    /// still correct. Refreshing means re-fetching the message it came from.
    /// </summary>
    public sealed class MediaInfo
    {
        public MediaKind Kind;

        public long Id;
        public long AccessHash;
        public byte[] FileReference;
        public int DcId;

        /// <summary>Which PhotoSize to fetch - the "type" string, e.g. "x" or "m".</summary>
        public string SizeType;

        public int Width;
        public int Height;
        public long FileSize;

        public string MimeType;
        public string FileName;
        public int DurationSeconds;

        /// <summary>Set when <see cref="Kind"/> is Location.</summary>
        public double Latitude;
        public double Longitude;

        /// <summary>A tiny inline preview, when the message carried one.</summary>
        public byte[] InlineThumbnail;

        /// <summary>
        /// The size name of a thumbnail this document carries, if any.
        ///
        /// A video is addressed by the same id and reference as its thumbnail; only
        /// the size name differs. That is what makes showing a preview cheap - the
        /// picture is a few kilobytes where the video is megabytes.
        /// </summary>
        public string ThumbSizeType;

        public bool IsPhoto { get { return Kind == MediaKind.Photo; } }

        /// <summary>Duration as m:ss, the way every other client shows it.</summary>
        public string Clock()
        {
            int seconds = DurationSeconds;
            return (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }

        public string Describe()
        {
            switch (Kind)
            {
                case MediaKind.Photo:
                    return "photo " + Width + "x" + Height;
                case MediaKind.Video:
                    return "video" + (DurationSeconds > 0 ? " " + DurationSeconds + "s" : "");
                case MediaKind.Location:
                    return "location " + Latitude.ToString("F5") + ", " +
                                         Longitude.ToString("F5");
                case MediaKind.Voice:
                    return "voice message" + (DurationSeconds > 0 ? " " + Clock() : "");
                case MediaKind.Document:
                    return FileName ?? (MimeType ?? "file");
                default:
                    return "";
            }
        }
    }

    /// <summary>
    /// Reading attachments out of messages, and downloading them.
    ///
    /// Downloads run in chunks through upload.getFile. Chunks are handed to a
    /// callback rather than accumulated, because a video on a 512 MB phone cannot be
    /// held in memory - the caller decides whether to buffer or stream to storage.
    /// </summary>
    public static class Media
    {
        /// <summary>
        /// Chunk size. Must divide 1 MB and be a multiple of 4 KB. 64 KB is a
        /// deliberate compromise: larger chunks mean fewer round trips, but each one
        /// is a live allocation on a phone that has very little headroom.
        /// </summary>
        public const int ChunkSize = 64 * 1024;

        /// <summary>Pulls attachment details out of a parsed message, or null.</summary>
        public static MediaInfo FromMessage(TlObject message)
        {
            if (message == null || !message.Has("media")) return null;

            TlObject media = message.Obj("media");

            if (media.Ctor == TlConstructors.MessageMediaPhoto)
                return media.Has("photo") ? FromPhoto(media.Obj("photo")) : null;

            if (media.Ctor == TlConstructors.MessageMediaGeo && media.Has("geo"))
            {
                var point = media.Obj("geo");
                if (point.Ctor != TlConstructors.GeoPoint) return null;

                return new MediaInfo
                {
                    Kind = MediaKind.Location,
                    // geoPoint carries long before lat - the opposite way round from
                    // inputGeoPoint, which is how coordinates end up transposed.
                    Longitude = point.DoubleOr("long", 0),
                    Latitude = point.DoubleOr("lat", 0),
                };
            }

            if (media.Ctor == TlConstructors.MessageMediaDocument)
                return media.Has("document") ? FromDocument(media.Obj("document")) : null;

            return null;
        }

        public static MediaInfo FromPhoto(TlObject photo)
        {
            if (photo == null || photo.Ctor != TlConstructors.Photo) return null;

            var info = new MediaInfo
            {
                Kind = MediaKind.Photo,
                Id = photo.Long("id"),
                AccessHash = photo.Long("access_hash"),
                FileReference = photo.Bytes("file_reference"),
                DcId = photo.IntOr("dc_id", 0),
            };

            ChooseSize(photo.Vec("sizes"), info);
            return info;
        }

        public static MediaInfo FromDocument(TlObject document)
        {
            if (document == null || document.Ctor != TlConstructors.Document) return null;

            var info = new MediaInfo
            {
                Kind = MediaKind.Document,
                Id = document.Long("id"),
                AccessHash = document.Long("access_hash"),
                FileReference = document.Bytes("file_reference"),
                DcId = document.IntOr("dc_id", 0),
                MimeType = document.Has("mime_type") ? document.Str("mime_type") : null,
                FileSize = document.Has("size") ? document.Long("size") : 0,
            };

            // Videos and many documents carry a small picture alongside the file.
            // It is addressed the same way, so keeping its size name is all that is
            // needed to fetch it later.
            if (document.Has("thumbs")) info.ThumbSizeType = BestThumb(document.Vec("thumbs"));

            foreach (object o in document.Vec("attributes"))
            {
                var a = (TlObject)o;

                if (a.Ctor == TlConstructors.DocumentAttributeFilename && a.Has("file_name"))
                    info.FileName = a.Str("file_name");

                if (a.Ctor == TlConstructors.DocumentAttributeVideo)
                {
                    info.Kind = MediaKind.Video;
                    info.Width = a.IntOr("w", 0);
                    info.Height = a.IntOr("h", 0);
                    // duration is a double in the current layer.
                    if (a.Has("duration"))
                    {
                        object d = a["duration"];
                        info.DurationSeconds = d is double ? (int)(double)d : 0;
                    }
                }

                if (a.Ctor == TlConstructors.DocumentAttributeAudio)
                {
                    // voice is a true-flag, so it is not a field of its own - the
                    // generated table carries the flags word and nothing else.
                    int flags = a.IntOr("flags", 0);
                    if ((flags & TlConstructors.DocumentAttributeAudioVoiceFlag) != 0)
                        info.Kind = MediaKind.Voice;

                    if (a.Has("duration"))
                    {
                        object d = a["duration"];
                        if (d is int) info.DurationSeconds = (int)d;
                        else if (d is double) info.DurationSeconds = (int)(double)d;
                    }
                }

                if (a.Ctor == TlConstructors.DocumentAttributeImageSize)
                {
                    info.Width = a.IntOr("w", 0);
                    info.Height = a.IntOr("h", 0);
                }
            }

            // A document that is really a picture should behave like one. A voice
            // message is deliberately not caught by this: its mime type is audio, and
            // it has already identified itself.
            if (info.Kind == MediaKind.Document && info.MimeType != null &&
                info.MimeType.StartsWith("image/"))
                info.Kind = MediaKind.Photo;

            return info;
        }

        /// <summary>
        /// Picks which photo size to download.
        ///
        /// Sizes come largest-last. On a 480x800 screen the biggest one is wasted
        /// bandwidth and memory, so this takes the largest that is still modest, and
        /// keeps any stripped thumbnail as an instant placeholder.
        /// </summary>
        /// <summary>
        /// Picks which thumbnail to ask for.
        ///
        /// The smallest real size, not the largest: this is drawn at the size of a
        /// message bubble, and a bigger one costs download time for pixels nobody
        /// sees. Stripped sizes are skipped - they carry bytes rather than a name,
        /// and cannot be requested.
        /// </summary>
        private static string BestThumb(List<object> thumbs)
        {
            string best = null;
            int bestArea = int.MaxValue;

            foreach (object o in thumbs)
            {
                var size = (TlObject)o;

                if (size.Ctor != TlConstructors.PhotoSize &&
                    size.Ctor != TlConstructors.PhotoSizeProgressive) continue;

                if (!size.Has("type")) continue;

                int area = size.IntOr("w", 0) * size.IntOr("h", 0);
                if (area <= 0 || area >= bestArea) continue;

                bestArea = area;
                best = size.Str("type");
            }

            return best;
        }

        private static void ChooseSize(List<object> sizes, MediaInfo info)
        {
            const int preferredMaxDimension = 800;

            TlObject best = null;
            int bestScore = -1;

            foreach (object o in sizes)
            {
                var size = (TlObject)o;

                if (size.Ctor == TlConstructors.PhotoStrippedSize)
                {
                    if (size.Has("bytes")) info.InlineThumbnail = size.Bytes("bytes");
                    continue;
                }

                if (size.Ctor != TlConstructors.PhotoSize &&
                    size.Ctor != TlConstructors.PhotoSizeProgressive &&
                    size.Ctor != TlConstructors.PhotoCachedSize)
                    continue;

                int w = size.IntOr("w", 0), h = size.IntOr("h", 0);
                int largest = Math.Max(w, h);

                // Prefer the biggest that fits the screen; if everything is larger,
                // take the smallest of those.
                int score = largest <= preferredMaxDimension ? largest : 10000 - largest;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = size;
                }
            }

            if (best == null) return;

            info.SizeType = best.Has("type") ? best.Str("type") : "x";
            info.Width = best.IntOr("w", 0);
            info.Height = best.IntOr("h", 0);

            if (best.Ctor == TlConstructors.PhotoSize)
                info.FileSize = best.IntOr("size", 0);
            else if (best.Ctor == TlConstructors.PhotoSizeProgressive)
            {
                // Progressive sizes list several lengths; the last is the full one.
                List<object> lengths = best.Vec("sizes");
                if (lengths.Count > 0) info.FileSize = (int)lengths[lengths.Count - 1];
            }
        }

        public static byte[] BuildLocation(MediaInfo info)
        {
            var w = new TlWriter(64);

            if (info.Kind == MediaKind.Photo && info.SizeType != null)
            {
                w.WriteConstructor(TlConstructors.InputPhotoFileLocation)
                 .WriteLong(info.Id)
                 .WriteLong(info.AccessHash)
                 .WriteBytes(info.FileReference)
                 .WriteString(info.SizeType);
            }
            else
            {
                w.WriteConstructor(TlConstructors.InputDocumentFileLocation)
                 .WriteLong(info.Id)
                 .WriteLong(info.AccessHash)
                 .WriteBytes(info.FileReference)
                 .WriteString(info.SizeType ?? "");
            }
            return w.ToArray();
        }

        /// <summary>
        /// Downloads a file, handing each chunk to <paramref name="onChunk"/>.
        ///
        /// Stops when the server returns a short chunk, which is how upload.getFile
        /// signals the end - the declared size is not always exact.
        /// </summary>
        /// <summary>
        /// Downloads a small file addressed by a location built elsewhere.
        ///
        /// Not everything worth fetching is an attachment: a profile picture is
        /// addressed by a location of its own and has no MediaInfo behind it. Small
        /// enough to hold in memory by definition - the caller is expected to be
        /// asking for a thumbnail-sized thing.
        /// </summary>
        public static async Task<byte[]> DownloadLocationAsync(MtprotoClient client,
                                                               byte[] location,
                                                               ClientInfo clientInfo = null)
        {
            var parts = new List<byte>();
            long offset = 0;

            while (true)
            {
                var q = new TlWriter(location.Length + 32);
                q.WriteConstructor(TlConstructors.UploadGetFile)
                 .WriteInt(0)
                 .WriteRaw(location)
                 .WriteLong(offset)
                 .WriteInt(ChunkSize);

                TlReader r = await client.InvokeAsync(q.ToArray(), clientInfo);
                TlObject file = TlSchema.ReadObject(r);

                if (file.Ctor != TlConstructors.UploadFile) return parts.ToArray();

                byte[] bytes = file.Bytes("bytes");
                if (bytes.Length > 0)
                {
                    parts.AddRange(bytes);
                    offset += bytes.Length;
                }

                if (bytes.Length < ChunkSize) break;
            }

            return parts.ToArray();
        }

        public static async Task<long> DownloadAsync(MtprotoClient client, MediaInfo info,
                                                     Action<byte[]> onChunk,
                                                     Action<long, long> progress = null,
                                                     ClientInfo clientInfo = null)
        {
            byte[] location = BuildLocation(info);
            long offset = 0;

            while (true)
            {
                var q = new TlWriter(location.Length + 32);
                q.WriteConstructor(TlConstructors.UploadGetFile)
                 .WriteInt(0)                       // flags: not precise, no CDN
                 .WriteRaw(location)
                 .WriteLong(offset)
                 .WriteInt(ChunkSize);

                TlReader r = await client.InvokeAsync(q.ToArray(), clientInfo);
                TlObject file = TlSchema.ReadObject(r);

                if (file.Ctor == TlConstructors.UploadFileCdnRedirect)
                    throw new MtprotoException(
                        "file is served from a CDN, which this client does not support yet");

                if (file.Ctor != TlConstructors.UploadFile)
                    throw new MtprotoException("unexpected upload.File 0x" + file.Ctor.ToString("x8"));

                byte[] bytes = file.Bytes("bytes");
                if (bytes.Length > 0)
                {
                    onChunk(bytes);
                    offset += bytes.Length;
                    if (progress != null) progress(offset, info.FileSize);
                }

                // A short read means the end, whatever the declared size said.
                if (bytes.Length < ChunkSize) break;
                if (info.FileSize > 0 && offset >= info.FileSize) break;
            }

            return offset;
        }

        /// <summary>Convenience for things small enough to hold in memory - photos.</summary>
        public static async Task<byte[]> DownloadToMemoryAsync(MtprotoClient client, MediaInfo info,
                                                               Action<long, long> progress = null,
                                                               ClientInfo clientInfo = null)
        {
            var parts = new List<byte[]>();
            long total = await DownloadAsync(client, info, delegate (byte[] b) { parts.Add(b); },
                                             progress, clientInfo);

            var result = new byte[total];
            int at = 0;
            foreach (byte[] p in parts)
            {
                Buffer.BlockCopy(p, 0, result, at, p.Length);
                at += p.Length;
            }
            return result;
        }
    }
}
