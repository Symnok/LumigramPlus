using System;
using System.IO;
using Lumigram.Mtproto;

namespace Lumigram.Harness
{
    /// <summary>
    /// Persists an authorisation key and an in-progress login between harness runs.
    ///
    /// The phone app will need the same thing - an auth key is expensive to create
    /// and permanent once made, so it is stored and reused rather than renegotiated
    /// on every launch. Here it also makes a two-step login possible: auth.sendCode
    /// and auth.signIn must run under one authorisation, otherwise the second
    /// connection's sendCode issues a fresh code and invalidates the one the user is
    /// holding.
    ///
    /// Plain file, no encryption. This is a development harness on a trusted
    /// machine; the file is gitignored. The phone build must not copy this approach
    /// unchanged - an auth key is a full account credential.
    /// </summary>
    internal sealed class SessionStore
    {
        public byte[] AuthKey;
        public long AuthKeyId;
        public long ServerSalt;
        public int TimeOffset;

        public string Host;
        public string Phone;
        public string PhoneCodeHash;

        private const string FileName = "session.dat";
        private const int Version = 1;

        /// <summary>
        /// Kept next to the executable rather than in the current directory, so the
        /// command works from any shell regardless of where it is launched.
        /// </summary>
        public static string Path
        {
            get
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe), FileName);
            }
        }

        public void Save()
        {
            using (var fs = new FileStream(Path, FileMode.Create, FileAccess.Write))
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
            }
        }

        public static SessionStore Load()
        {
            if (!File.Exists(Path)) return null;

            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read))
            using (var r = new BinaryReader(fs))
            {
                int version = r.ReadInt32();
                if (version != Version)
                    throw new InvalidDataException("session.dat is version " + version +
                                                   ", expected " + Version);

                var s = new SessionStore();
                int len = r.ReadInt32();
                s.AuthKey = r.ReadBytes(len);
                s.AuthKeyId = r.ReadInt64();
                s.ServerSalt = r.ReadInt64();
                s.TimeOffset = r.ReadInt32();
                s.Host = r.ReadString();
                s.Phone = r.ReadString();
                s.PhoneCodeHash = r.ReadString();
                return s;
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
