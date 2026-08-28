using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>A file that has been uploaded and can now be attached to a message.</summary>
    public sealed class UploadedFile
    {
        public long FileId;
        public int Parts;
        public string Name;
        public bool Big;
        public long Size;
    }

    /// <summary>
    /// Uploading attachments.
    ///
    /// Files go up in numbered parts and are then referenced by id - the bytes and
    /// the message that carries them are two separate operations, which is what
    /// allows a large upload to be resumed or reused.
    ///
    /// Part size must divide 1 MB and stay constant for the whole file. 64 KB is
    /// used here for the same reason as downloads: a 512 MB phone has very little
    /// headroom, and each part is a live allocation.
    ///
    /// Above 10 MB Telegram requires the "big" variants, which additionally carry
    /// the total part count so the server can allocate ahead. Getting that boundary
    /// wrong is rejected rather than silently mishandled.
    /// </summary>
    public static class Upload
    {
        public const int PartSize = 64 * 1024;
        public const long BigFileThreshold = 10 * 1024 * 1024;

        /// <summary>
        /// Sends the bytes. <paramref name="readPart"/> is called with a buffer to
        /// fill and returns how many bytes it wrote, so the caller can stream from
        /// storage instead of holding the whole file.
        /// </summary>
        public static async Task<UploadedFile> SendFileAsync(MtprotoClient client, ICrypto crypto,
                                                             string name, long totalSize,
                                                             Func<byte[], int> readPart,
                                                             Action<long, long> progress = null,
                                                             ClientInfo info = null)
        {
            long fileId = BitConverter.ToInt64(crypto.Random(8), 0);
            bool big = totalSize >= BigFileThreshold;

            int totalParts = (int)((totalSize + PartSize - 1) / PartSize);
            if (totalParts <= 0) totalParts = 1;
            if (totalParts > 4000)
                throw new MtprotoException("file is too large: " + totalParts + " parts");

            var buffer = new byte[PartSize];
            long sent = 0;

            for (int part = 0; part < totalParts; part++)
            {
                int read = readPart(buffer);
                if (read <= 0) break;

                byte[] chunk = read == buffer.Length ? buffer : buffer.Slice(0, read);

                var q = new TlWriter(read + 64);
                if (big)
                {
                    q.WriteConstructor(TlConstructors.UploadSaveBigFilePart)
                     .WriteLong(fileId)
                     .WriteInt(part)
                     .WriteInt(totalParts)
                     .WriteBytes(chunk);
                }
                else
                {
                    q.WriteConstructor(TlConstructors.UploadSaveFilePart)
                     .WriteLong(fileId)
                     .WriteInt(part)
                     .WriteBytes(chunk);
                }

                TlReader r = await client.InvokeAsync(q.ToArray(), info);
                if (!r.ReadBool())
                    throw new MtprotoException("server rejected part " + part);

                sent += read;
                if (progress != null) progress(sent, totalSize);
            }

            return new UploadedFile
            {
                FileId = fileId,
                Parts = totalParts,
                Name = name,
                Big = big,
                Size = sent,
            };
        }

        private static byte[] BuildInputFile(UploadedFile file)
        {
            var w = new TlWriter(64);
            if (file.Big)
            {
                w.WriteConstructor(TlConstructors.InputFileBig)
                 .WriteLong(file.FileId)
                 .WriteInt(file.Parts)
                 .WriteString(file.Name ?? "file");
            }
            else
            {
                w.WriteConstructor(TlConstructors.InputFile)
                 .WriteLong(file.FileId)
                 .WriteInt(file.Parts)
                 .WriteString(file.Name ?? "file")
                 .WriteString("");            // md5_checksum: optional, and the server
                                              // does not require it
            }
            return w.ToArray();
        }

        /// <summary>Sends an uploaded file as a photo.</summary>
        public static async Task<int> SendPhotoAsync(MtprotoClient client, ICrypto crypto,
                                                        byte[] inputPeer, UploadedFile file,
                                                        string caption, ClientInfo info = null)
        {
            var media = new TlWriter(96);
            media.WriteConstructor(TlConstructors.InputMediaUploadedPhoto)
                 .WriteInt(0)                       // flags: no stickers, no ttl
                 .WriteRaw(BuildInputFile(file));

            return await SendMediaAsync(client, crypto, inputPeer, media.ToArray(), caption, info);
        }

        /// <summary>
        /// Sends an uploaded file as a video.
        ///
        /// documentAttributeVideo is what makes a client show a player rather than a
        /// file to download, and documentAttributeFilename is what gives it a name.
        /// Duration is a double in the current layer.
        /// </summary>
        public static async Task<int> SendVideoAsync(MtprotoClient client, ICrypto crypto,
                                                        byte[] inputPeer, UploadedFile file,
                                                        string caption, string mimeType,
                                                        int durationSeconds, int width, int height,
                                                        ClientInfo info = null)
        {
            var attributes = new TlWriter(96);
            attributes.WriteConstructor(TlConstructors.Vector)
                      .WriteInt(2);

            attributes.WriteConstructor(TlConstructors.DocumentAttributeVideo)
                      .WriteInt(1 << 1)             // supports_streaming
                      .WriteDouble(durationSeconds)
                      .WriteInt(width)
                      .WriteInt(height);

            attributes.WriteConstructor(TlConstructors.DocumentAttributeFilename)
                      .WriteString(file.Name ?? "video.mp4");

            var media = new TlWriter(160);
            media.WriteConstructor(TlConstructors.InputMediaUploadedDocument)
                 .WriteInt(0)                       // flags: no thumb, no stickers
                 .WriteRaw(BuildInputFile(file))
                 .WriteString(mimeType ?? "video/mp4")
                 .WriteRaw(attributes.ToArray());

            return await SendMediaAsync(client, crypto, inputPeer, media.ToArray(), caption, info);
        }

        /// <summary>
        /// Sends an uploaded file as a voice message.
        ///
        /// The voice flag on documentAttributeAudio is what separates a voice message
        /// from an attached audio file: with it, other clients draw the waveform and
        /// a play button inline; without it, the same bytes arrive as a file to
        /// download. The waveform is optional and worth sending - a voice message
        /// with a blank bar chart looks broken next to everyone else's.
        ///
        /// No documentAttributeFilename, deliberately. A voice message has no name,
        /// and giving it one is another way to be shown as a file.
        /// </summary>
        public static async Task<int> SendVoiceAsync(MtprotoClient client, ICrypto crypto,
                                                        byte[] inputPeer, UploadedFile file,
                                                        int durationSeconds, byte[] waveform,
                                                        ClientInfo info = null)
        {
            const int voiceFlag = 1 << 10;
            const int waveformFlag = 1 << 2;

            bool hasWaveform = waveform != null && waveform.Length > 0;

            var attributes = new TlWriter(96);
            attributes.WriteConstructor(TlConstructors.Vector)
                      .WriteInt(1)
                      .WriteConstructor(TlConstructors.DocumentAttributeAudio)
                      .WriteInt(voiceFlag | (hasWaveform ? waveformFlag : 0))
                      .WriteInt(durationSeconds);

            if (hasWaveform) attributes.WriteBytes(waveform);

            var media = new TlWriter(160);
            media.WriteConstructor(TlConstructors.InputMediaUploadedDocument)
                 .WriteInt(0)
                 .WriteRaw(BuildInputFile(file))
                 .WriteString("audio/ogg")
                 .WriteRaw(attributes.ToArray());

            return await SendMediaAsync(client, crypto, inputPeer, media.ToArray(), null, info);
        }

        /// <summary>
        /// Sends a location. Nothing is uploaded - the coordinates travel in the
        /// message itself.
        /// </summary>
        public static async Task<int> SendLocationAsync(MtprotoClient client, ICrypto crypto,
                                                           byte[] inputPeer,
                                                           double latitude, double longitude,
                                                           int accuracyMetres,
                                                           ClientInfo info = null)
        {
            var point = new TlWriter(40);
            point.WriteConstructor(TlConstructors.InputGeoPoint)
                 .WriteInt(accuracyMetres > 0 ? 1 : 0)
                 // Latitude first here. geoPoint, coming back, puts longitude first.
                 .WriteDouble(latitude)
                 .WriteDouble(longitude);

            if (accuracyMetres > 0) point.WriteInt(accuracyMetres);

            var media = new TlWriter(64);
            media.WriteConstructor(TlConstructors.InputMediaGeoPoint)
                 .WriteRaw(point.ToArray());

            return await SendMediaAsync(client, crypto, inputPeer, media.ToArray(), null, info);
        }

        /// <summary>Sends an uploaded file as a plain document, keeping its name.</summary>
        public static async Task<int> SendDocumentAsync(MtprotoClient client, ICrypto crypto,
                                                           byte[] inputPeer, UploadedFile file,
                                                           string caption, string mimeType,
                                                           ClientInfo info = null)
        {
            var attributes = new TlWriter(64);
            attributes.WriteConstructor(TlConstructors.Vector)
                      .WriteInt(1)
                      .WriteConstructor(TlConstructors.DocumentAttributeFilename)
                      .WriteString(file.Name ?? "file");

            var media = new TlWriter(160);
            media.WriteConstructor(TlConstructors.InputMediaUploadedDocument)
                 .WriteInt(1 << 4)                  // force_file: keep it a document
                 .WriteRaw(BuildInputFile(file))
                 .WriteString(mimeType ?? "application/octet-stream")
                 .WriteRaw(attributes.ToArray());

            return await SendMediaAsync(client, crypto, inputPeer, media.ToArray(), caption, info);
        }

        private static async Task<int> SendMediaAsync(MtprotoClient client, ICrypto crypto,
                                                         byte[] inputPeer, byte[] media,
                                                         string caption, ClientInfo info)
        {
            long randomId = BitConverter.ToInt64(crypto.Random(8), 0);

            var q = new TlWriter(media.Length + 96);
            q.WriteConstructor(TlConstructors.MessagesSendMedia)
             .WriteInt(0)                           // flags
             .WriteRaw(inputPeer)
             .WriteRaw(media)
             .WriteString(caption ?? "")
             .WriteLong(randomId);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            return Messages.SentMessageId(TlSchema.ReadObject(r));
        }
    }
}
