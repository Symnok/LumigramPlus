using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// Which chats are muted, as last seen from the server.
    ///
    /// A cache, not a preference. The setting itself lives on the account - muting
    /// on another device has to be respected here, and muting here has to hold
    /// everywhere - so this only remembers what the dialog list said, and is
    /// rewritten whenever that list is read.
    ///
    /// On disk rather than in memory because the background agent decides whether to
    /// toast, and it runs in a process that has never seen a dialog list. Without
    /// this the agent would happily announce messages from a chat the user muted,
    /// which is the same bug in a place that is much harder to notice.
    /// </summary>
    public static class MuteStore
    {
        private const string FileName = "muted.dat";
        private const int Version = 1;

        private static readonly object Gate = new object();
        private static HashSet<long> _cache;

        /// <summary>Replaces the set with what the dialog list reported.</summary>
        public static void Replace(IEnumerable<long> mutedPeerIds)
        {
            var set = new HashSet<long>();
            if (mutedPeerIds != null)
                foreach (long id in mutedPeerIds) if (id != 0) set.Add(id);

            lock (Gate)
            {
                _cache = set;
                Save(set);
            }
        }

        /// <summary>Records one change without waiting for the list to be re-read.</summary>
        public static void Set(long peerId, bool muted)
        {
            if (peerId == 0) return;

            lock (Gate)
            {
                HashSet<long> set = Cached();
                if (muted) set.Add(peerId); else set.Remove(peerId);
                Save(set);
            }
        }

        public static bool IsMuted(long peerId)
        {
            if (peerId == 0) return false;
            lock (Gate) return Cached().Contains(peerId);
        }

        /// <summary>Forgets everything - used when the account is signed out of.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                _cache = new HashSet<long>();
                Save(_cache);
            }
        }

        private static HashSet<long> Cached()
        {
            if (_cache == null) _cache = Load();
            return _cache;
        }

        private static HashSet<long> Load()
        {
            var set = new HashSet<long>();

            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.FileExists(FileName)) return set;

                    using (var fs = store.OpenFile(FileName, FileMode.Open, FileAccess.Read))
                    using (var r = new BinaryReader(fs))
                    {
                        if (r.ReadInt32() != Version) return set;

                        int count = r.ReadInt32();
                        for (int i = 0; i < count; i++) set.Add(r.ReadInt64());
                    }
                }
            }
            catch (Exception)
            {
                return new HashSet<long>();
            }

            return set;
        }

        private static void Save(HashSet<long> set)
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                using (var fs = store.OpenFile(FileName, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(Version);
                    w.Write(set.Count);
                    foreach (long id in set) w.Write(id);
                }
            }
            catch (Exception) { }
        }
    }
}
