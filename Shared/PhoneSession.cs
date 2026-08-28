using System;
using System.IO;
using System.IO.IsolatedStorage;
using Lumigram.Mtproto;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// The stored authorisation, in isolated storage.
    ///
    /// The auth key is a full account credential: anyone holding it can read and
    /// send as this account until the session is revoked server-side. Isolated
    /// storage is per-application and not readable by other apps, which is the
    /// protection the platform offers; it is not encrypted at rest, so a device
    /// with no lock screen offers none.
    ///
    /// Deleting this file is *not* signing out. The server-side session has to be
    /// revoked with auth.logOut as well, or it keeps working and keeps appearing
    /// under Settings -> Devices. <see cref="TelegramService.SignOutAsync"/> does
    /// both, in that order.
    /// </summary>
    public sealed class PhoneSession
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

        public static bool Exists()
        {
            using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                return store.FileExists(FileName);
        }

        public void Save()
        {
            using (var store = IsolatedStorageFile.GetUserStoreForApplication())
            using (var fs = store.OpenFile(FileName, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
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
        }

        public static PhoneSession Load()
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.FileExists(FileName)) return null;

                    using (var fs = store.OpenFile(FileName, FileMode.Open, FileAccess.Read))
                    using (var r = new BinaryReader(fs))
                    {
                        if (r.ReadInt32() != Version) return null;

                        var s = new PhoneSession();
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
            }
            catch (Exception)
            {
                // A corrupt or half-written session must not brick the app: the user
                // can always sign in again, which is far better than a launch crash.
                Delete();
                return null;
            }
        }

        public static void Delete()
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                    if (store.FileExists(FileName)) store.DeleteFile(FileName);
            }
            catch (Exception) { }
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

        public static PhoneSession FromAuthKey(AuthKey key, string host)
        {
            return new PhoneSession
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
