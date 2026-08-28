using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// The authorisation key and where it belongs, kept on disk.
    ///
    /// The Silverlight client stores this in isolated storage, which WinRT does not
    /// have. The bytes on disk are deliberately identical all the same - same
    /// version marker, same field order - because the two apps are the same protocol
    /// wearing different app models, and a format that drifts for no reason is a
    /// format that has to be debugged twice.
    ///
    /// What could not be kept is the shape of the API. Every file operation here is
    /// asynchronous, where isolated storage is not, so callers have to await what
    /// they previously just called. That is the tax WinRT charges for its storage
    /// and it is paid here rather than spread through the app.
    ///
    /// An auth key is a credential: whoever holds it is signed in as the user until
    /// the session is revoked. It stays in the app's own data folder and is never
    /// copied anywhere else.
    /// </summary>
    internal sealed class SessionStore
    {
        private const string FileName = "session.dat";
        private const int Version = 1;

        public byte[] AuthKey;
        public long AuthKeyId;
        public long ServerSalt;
        public int TimeOffset;

        public string Host;
        public string Phone;
        public string PhoneCodeHash;
        public bool SignedIn;

        public async Task SaveAsync()
        {
            StorageFile file = await ApplicationData.Current.LocalFolder
                .CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);

            using (var buffer = new MemoryStream())
            {
                using (var w = new BinaryWriter(buffer))
                {
                    w.Write(Version);
                    w.Write(AuthKey.Length);
                    w.Write(AuthKey);
                    w.Write(AuthKeyId);
                    w.Write(ServerSalt);
                    w.Write(TimeOffset);
                    w.Write(Host ?? "");
                    w.Write(Phone ?? "");
                    w.Write(PhoneCodeHash ?? "");
                    w.Write(SignedIn);
                }

                await FileIO.WriteBytesAsync(file, buffer.ToArray());
            }
        }

        /// <summary>
        /// Reads the stored session, or null when there is not one.
        ///
        /// A half-written or corrupt file is deleted rather than reported. Signing in
        /// again is a minor annoyance; an app that crashes on every launch and cannot
        /// be recovered without reinstalling is not.
        /// </summary>
        public static async Task<SessionStore> LoadAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder
                    .GetFileAsync(FileName);

                // CryptographicBuffer rather than an extension method: WinRT hands
                // back an IBuffer, and the usual ToArray() lives in a namespace this
                // project does not otherwise need.
                Windows.Storage.Streams.IBuffer read = await FileIO.ReadBufferAsync(file);

                byte[] bytes;
                Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(read, out bytes);

                using (var stream = new MemoryStream(bytes))
                using (var r = new BinaryReader(stream))
                {
                    if (r.ReadInt32() != Version) return null;

                    var s = new SessionStore();
                    s.AuthKey = r.ReadBytes(r.ReadInt32());
                    s.AuthKeyId = r.ReadInt64();
                    s.ServerSalt = r.ReadInt64();
                    s.TimeOffset = r.ReadInt32();
                    s.Host = r.ReadString();
                    s.Phone = r.ReadString();
                    s.PhoneCodeHash = r.ReadString();
                    s.SignedIn = r.ReadBoolean();
                    return s;
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (Exception)
            {
                await DeleteAsync();
                return null;
            }
        }

        public static async Task DeleteAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder
                    .GetFileAsync(FileName);

                await file.DeleteAsync();
            }
            catch (Exception)
            {
                // Not there, or not removable. Either way there is nothing useful to
                // do about it here.
            }
        }

        public AuthKey ToAuthKey()
        {
            return new AuthKey
            {
                Key = AuthKey,
                KeyId = AuthKeyId,
                ServerSalt = ServerSalt,
                TimeOffset = TimeOffset,
            };
        }

        public static SessionStore FromAuthKey(AuthKey key, string host)
        {
            return new SessionStore
            {
                AuthKey = key.Key,
                AuthKeyId = key.KeyId,
                ServerSalt = key.ServerSalt,
                TimeOffset = key.TimeOffset,
                Host = host,
            };
        }
    }
}
