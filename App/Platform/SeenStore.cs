using System;
using System.Collections.Generic;
using System.Text;
using Windows.Storage;

namespace LumigramPlus.App
{
    /// <summary>
    /// The newest message already announced in each chat, kept on disk.
    ///
    /// This used to be a static dictionary, which was correct while the app was the
    /// only thing raising notifications. A background task is a different process:
    /// its statics start empty, so on every wake it would treat every chat as new
    /// and announce the same messages the app announced an hour ago. The two have to
    /// agree, and the only thing they share is storage.
    ///
    /// LocalSettings rather than a file, for the same reason as the other settings -
    /// it is loaded before the process is, and a background task has seconds to do
    /// its whole job. The whole map is one string: a few hundred chats is a few
    /// kilobytes, and reading one value beats opening a file.
    /// </summary>
    internal static class SeenStore
    {
        private const string SeenKey = "seenTopIds";
        private const string BaselineKey = "seenBaseline";

        /// <summary>
        /// How many chats to remember.
        ///
        /// Kept well inside the 8K a single setting can hold. Chats fall off the end
        /// in the order the server listed them, which is newest first, so what is
        /// dropped is what has been silent longest.
        /// </summary>
        private const int Max = 150;

        /// <summary>
        /// Whether a first look has been recorded.
        ///
        /// Without it the first read after installing would announce every chat with
        /// anything unread in it - a wall of toasts for messages the user has had for
        /// hours. Stored rather than held in memory so a background task waking
        /// before the app has ever run does not do that either.
        /// </summary>
        public static bool HasBaseline
        {
            get
            {
                try
                {
                    object stored = ApplicationData.Current.LocalSettings.Values[BaselineKey];
                    return stored is bool && (bool)stored;
                }
                catch (Exception)
                {
                    return false;
                }
            }
            set
            {
                try { ApplicationData.Current.LocalSettings.Values[BaselineKey] = value; }
                catch (Exception) { }
            }
        }

        public static Dictionary<long, int> Load()
        {
            var map = new Dictionary<long, int>();

            try
            {
                var packed = ApplicationData.Current.LocalSettings.Values[SeenKey] as string;
                if (string.IsNullOrEmpty(packed)) return map;

                foreach (string pair in packed.Split(';'))
                {
                    int colon = pair.IndexOf(':');
                    if (colon <= 0) continue;

                    long peer;
                    int top;

                    if (!long.TryParse(pair.Substring(0, colon), out peer)) continue;
                    if (!int.TryParse(pair.Substring(colon + 1), out top)) continue;

                    map[peer] = top;
                }
            }
            catch (Exception)
            {
                // An unreadable map is the same as an empty one: the baseline flag
                // stops that turning into a wall of notifications.
            }

            return map;
        }

        /// <summary>
        /// Writes the map back, keeping the most recent entries.
        ///
        /// <paramref name="order"/> is the chat list as the server returned it, so
        /// the ones kept are the ones with the newest messages rather than whichever
        /// ones a dictionary happened to enumerate first.
        /// </summary>
        public static void Save(Dictionary<long, int> map, IList<long> order)
        {
            try
            {
                var text = new StringBuilder();
                var written = new HashSet<long>();

                if (order != null)
                {
                    foreach (long peer in order)
                    {
                        if (written.Count >= Max) break;
                        Append(text, map, peer, written);
                    }
                }

                foreach (KeyValuePair<long, int> entry in map)
                {
                    if (written.Count >= Max) break;
                    Append(text, map, entry.Key, written);
                }

                ApplicationData.Current.LocalSettings.Values[SeenKey] = text.ToString();
            }
            catch (Exception)
            {
                // Losing the map costs one duplicated notification, which is not
                // worth failing a refresh over.
            }
        }

        private static void Append(StringBuilder text, Dictionary<long, int> map,
                                   long peer, HashSet<long> written)
        {
            int top;
            if (!map.TryGetValue(peer, out top)) return;
            if (!written.Add(peer)) return;

            if (text.Length > 0) text.Append(';');
            text.Append(peer).Append(':').Append(top);
        }

        public static void Clear()
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values.Remove(SeenKey);
                ApplicationData.Current.LocalSettings.Values.Remove(BaselineKey);
            }
            catch (Exception) { }
        }
    }
}
