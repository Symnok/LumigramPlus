using System;
using System.IO;
using System.IO.IsolatedStorage;
using Lumigram.Mtproto;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// The update position, kept on disk.
    ///
    /// pts is how the client says "I have applied everything up to here". Holding
    /// it only in memory means every launch - and every background wake - starts
    /// from scratch and either re-reports old messages or, worse, calls getState
    /// and skips whatever arrived while the app was away.
    ///
    /// Shared between the app and the background agent, which is the whole reason
    /// it lives in a file: the agent runs in its own process and has no other way
    /// to know where the app got to.
    /// </summary>
    public static class UpdateStateStore
    {
        private const string FileName = "updatestate.dat";
        private const int Version = 1;

        public static UpdateState Load()
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.FileExists(FileName)) return new UpdateState();

                    using (var fs = store.OpenFile(FileName, FileMode.Open, FileAccess.Read))
                    using (var r = new BinaryReader(fs))
                    {
                        if (r.ReadInt32() != Version) return new UpdateState();

                        return new UpdateState
                        {
                            Pts = r.ReadInt32(),
                            Qts = r.ReadInt32(),
                            Date = r.ReadInt32(),
                            Seq = r.ReadInt32(),
                        };
                    }
                }
            }
            catch (Exception)
            {
                return new UpdateState();
            }
        }

        public static void Save(UpdateState state)
        {
            if (state == null) return;

            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                using (var fs = store.OpenFile(FileName, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(Version);
                    w.Write(state.Pts);
                    w.Write(state.Qts);
                    w.Write(state.Date);
                    w.Write(state.Seq);
                }
            }
            catch (Exception) { }
        }
    }
}
