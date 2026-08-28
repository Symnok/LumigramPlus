using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// Lists media in a chat and downloads one, so the file path can be exercised on
    /// the desktop before any of it runs on the phone.
    /// </summary>
    internal static class MediaCommand
    {
        public static int Run(string[] args)
        {
            try { return RunAsync(args).GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
        }

        /// <summary>Uploads a local file to Saved Messages, to prove the send path.</summary>
        public static int RunSend(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: sendphoto <path-to-image>");
                return 2;
            }

            try { return SendAsync(args[1]).GetAwaiter().GetResult(); }
            catch (AggregateException ex) { return Report(ex.InnerException); }
            catch (Exception ex) { return Report(ex); }
        }

        private static async Task<int> SendAsync(string path)
        {
            if (!File.Exists(path)) { Console.WriteLine("no such file: " + path); return 2; }

            SessionStore store = SessionStore.Load();
            if (store == null) { Console.WriteLine("No stored session."); return 2; }

            var crypto = new DesktopCrypto();
            var info = ClientInfo.Default;
            info.ApiId = Secrets.ApiId;
            info.ApiHash = Secrets.ApiHash;

            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(crypto, transport, delegate { });
                await client.ConnectWithKeyAsync(store.Host, TelegramServers.DefaultPort,
                                                 store.ToAuthKey());

                var warm = new TlWriter();
                warm.WriteConstructor(TlConstructors.HelpGetNearestDc);
                await client.InvokeAsync(warm.ToArray(), info);

                var fi = new FileInfo(path);
                Console.WriteLine("uploading {0} ({1} bytes)...", fi.Name, fi.Length);

                using (var fs = File.OpenRead(path))
                {
                    UploadedFile uploaded = await Upload.SendFileAsync(
                        client, crypto, fi.Name, fi.Length,
                        delegate (byte[] buffer) { return fs.Read(buffer, 0, buffer.Length); },
                        delegate (long sent, long total)
                        {
                            Console.WriteLine("  {0} / {1} bytes", sent, total);
                        }, info);

                    Console.WriteLine("uploaded as file id {0} in {1} part(s)",
                                      uploaded.FileId, uploaded.Parts);

                    int result = await Upload.SendPhotoAsync(
                        client, crypto, Messages.InputPeerSelf(), uploaded,
                        "from Lumigram", info);

                    Console.WriteLine("{0}", result);
                }

                Console.WriteLine();
                Console.WriteLine("Check Saved Messages.");
                return 0;
            }
        }

        private static int Report(Exception ex)
        {
            Console.WriteLine();
            var rpc = ex as RpcException;
            if (rpc != null)
            {
                Console.WriteLine("SERVER REJECTED THE CALL");
                Console.WriteLine("  code: {0}", rpc.Code);
                Console.WriteLine("  type: {0}", rpc.ErrorType);
                if ((rpc.ErrorType ?? "").Contains("FILE_REFERENCE"))
                    Console.WriteLine("  -> the file reference has expired; re-fetch the message.");
                return 1;
            }
            Console.WriteLine("FAILED: {0}: {1}", ex.GetType().Name, ex.Message);
            return 1;
        }

        /// <summary>Names the file from what the message said it is.</summary>
        private static string Extension(MediaInfo info)
        {
            if (info.FileName != null)
            {
                int dot = info.FileName.LastIndexOf('.');
                if (dot > 0) return info.FileName.Substring(dot);
            }
            if (info.Kind == MediaKind.Photo) return ".jpg";
            if (info.Kind == MediaKind.Video) return ".mp4";
            return ".bin";
        }

        /// <summary>
        /// Identifies the content from its leading bytes, which is the cheapest way
        /// to tell a real download from a plausible-looking pile of nothing.
        /// </summary>
        private static string Sniff(byte[] d)
        {
            if (d.Length < 4) return "(too short)";
            if (d[0] == 0xFF && d[1] == 0xD8) return "JPEG";
            if (d[0] == 0x89 && d[1] == 0x50) return "PNG";
            if (d.Length > 11 && d[4] == 0x66 && d[5] == 0x74 && d[6] == 0x79 && d[7] == 0x70)
                return "MP4/ISO container";
            if (d[0] == 0x7B || d[0] == 0x5B) return "JSON/text";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Math.Min(8, d.Length); i++) sb.Append(d[i].ToString("x2") + " ");
            return sb.ToString();
        }

        private static async Task<int> RunAsync(string[] args)
        {
            SessionStore store = SessionStore.Load();
            if (store == null) { Console.WriteLine("No stored session."); return 2; }

            var crypto = new DesktopCrypto();
            var info = ClientInfo.Default;
            info.ApiId = Secrets.ApiId;
            info.ApiHash = Secrets.ApiHash;

            using (var transport = new TcpTransport())
            {
                var client = new MtprotoClient(crypto, transport, delegate { });
                await client.ConnectWithKeyAsync(store.Host, TelegramServers.DefaultPort,
                                                 store.ToAuthKey());

                var warm = new TlWriter();
                warm.WriteConstructor(TlConstructors.HelpGetNearestDc);
                await client.InvokeAsync(warm.ToArray(), info);

                Console.WriteLine("scanning recent chats for media...");
                Console.WriteLine();

                List<DialogEntry> dialogs = await Messages.GetDialogsAsync(client, 15);
                int found = 0;

                foreach (DialogEntry d in dialogs)
                {
                    List<TextMessage> history;
                    try
                    {
                        history = await Messages.GetRecentAsync(
                            client, Messages.InputPeerFor(d), 20);
                    }
                    catch (Exception) { continue; }

                    foreach (TextMessage m in history)
                    {
                        if (m.Media == null) continue;
                        found++;

                        Console.WriteLine("  [{0}] {1,-24} {2}  dc{3}  {4} bytes",
                                          m.Id, d.Title, m.Media.Describe(),
                                          m.Media.DcId, m.Media.FileSize);

                        if (found == 1)
                        {
                            Console.WriteLine();
                            Console.WriteLine("  downloading this one...");

                            byte[] data = await Media.DownloadToMemoryAsync(
                                client, m.Media,
                                delegate (long got, long total)
                                {
                                    Console.WriteLine("    {0} / {1} bytes", got, total);
                                }, info);

                            string path = Path.Combine(
                                Path.GetDirectoryName(
                                    System.Reflection.Assembly.GetExecutingAssembly().Location),
                                "media-" + m.Id + Extension(m.Media));
                            File.WriteAllBytes(path, data);

                            Console.WriteLine("    saved {0} bytes to {1}", data.Length, path);
                            Console.WriteLine("    first bytes: {0}", Sniff(data));
                            Console.WriteLine();
                        }

                        if (found >= 12) break;
                    }
                    if (found >= 12) break;
                }

                Console.WriteLine();
                Console.WriteLine("{0} message(s) with media found.", found);
                return 0;
            }
        }
    }
}
