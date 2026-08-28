using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using Microsoft.Phone.Shell;

namespace Lumigram.Phone.Services
{
    /// <summary>One chat holding unread messages.</summary>
    public sealed class TileChat
    {
        public long PeerId;
        public string Title;
    }

    /// <summary>
    /// The start screen tile: how many chats have something new in them, and which.
    ///
    /// Chats rather than messages, deliberately. A message count has to be arrived
    /// at by adding up as messages arrive, and anything that delivers a message
    /// twice inflates it - which is exactly what happens here, since a message
    /// arrives once as a pushed update and again from getDifference. A set of chats
    /// cannot be inflated that way: adding a chat that is already in it changes
    /// nothing. It is also the more useful number - "three conversations are
    /// waiting" is what you act on, not "seventeen messages".
    ///
    /// The set is kept on disk, for the same reason the update position is: the
    /// background agent runs in its own process and would otherwise have no idea
    /// what the tile currently says.
    ///
    /// Two ways in. <see cref="Set"/> is authoritative and used by the chat list,
    /// which has the server's own unread counts; <see cref="Add"/> is for messages
    /// arriving with no list loaded, and is corrected by the next Set.
    /// </summary>
    public static class LiveTile
    {
        private const string FileName = "tile.dat";

        /// <summary>Version 1 counted messages; a version bump discards that file.</summary>
        private const int Version = 2;

        /// <summary>Chats named on the back of the tile. More would not fit.</summary>
        private const int MaxLines = 3;

        /// <summary>Windows Phone renders no badge above this.</summary>
        private const int MaxCount = 99;

        private static readonly object Gate = new object();

        /// <summary>Replaces the tile with an exact set of chats.</summary>
        public static void Set(IList<TileChat> chats)
        {
            var kept = new List<TileChat>();
            if (chats != null)
            {
                foreach (TileChat c in chats)
                    if (c != null && !Contains(kept, c.PeerId)) kept.Add(c);
            }

            lock (Gate)
            {
                Save(kept);
                Apply(kept);
            }
        }

        /// <summary>
        /// Notes that a chat has something new in it.
        ///
        /// Idempotent by peer: a chat already on the tile stays counted once however
        /// many messages arrive in it, or however many times the same message is
        /// delivered. The title is refreshed when one is offered, since the agent has
        /// no names and the app does.
        /// </summary>
        public static void Add(long peerId, string title)
        {
            if (peerId == 0) return;

            lock (Gate)
            {
                List<TileChat> chats = Load();

                TileChat existing = Find(chats, peerId);
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(title)) existing.Title = title;
                }
                else
                {
                    chats.Insert(0, new TileChat { PeerId = peerId, Title = title });
                }

                Save(chats);
                Apply(chats);
            }
        }

        /// <summary>Back to a plain tile - nothing unread, nothing on the back.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                _appliedKey = null;
                Save(new List<TileChat>());
                Apply(new List<TileChat>());
            }
        }

        private static bool Contains(List<TileChat> chats, long peerId)
        {
            return Find(chats, peerId) != null;
        }

        private static TileChat Find(List<TileChat> chats, long peerId)
        {
            foreach (TileChat c in chats) if (c.PeerId == peerId) return c;
            return null;
        }

        /// <summary>What the tile was last set to, so it is not rewritten for nothing.</summary>
        private static string _appliedKey;

        private static void Apply(List<TileChat> chats)
        {
            int count = chats.Count;

            var named = new List<string>();
            foreach (TileChat c in chats)
            {
                if (named.Count >= MaxLines) break;
                if (!string.IsNullOrEmpty(c.Title)) named.Add(c.Title);
            }

            string back = string.Join(Environment.NewLine, named.ToArray());

            // The chat list refreshes on every incoming message, so without this the
            // shell would be handed an identical tile several times a message.
            string key = count + "|" + back;
            if (key == _appliedKey) return;
            _appliedKey = key;

            // ShellTile is shell interop, like ShellToast, and wants the UI thread -
            // and Add is called from the receive loop. Calling it from there appeared
            // to work because every failure here is deliberately swallowed, which is
            // precisely the trap the toast fell into.
            try
            {
                System.Windows.Threading.Dispatcher dispatcher =
                    System.Windows.Deployment.Current.Dispatcher;

                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(delegate { ApplyCore(count, back); });
                    return;
                }
            }
            catch (Exception)
            {
                // No dispatcher at all - the background agent's process.
            }

            ApplyCore(count, back);
        }

        private static void ApplyCore(int count, string back)
        {
            ShellTile tile = Primary();
            if (tile == null) return;

            var data = new FlipTileData
            {
                // Zero removes the badge rather than drawing a nought.
                Count = count > 0 ? Math.Min(count, MaxCount) : 0,
                BackTitle = count <= 0 ? "" : count == 1 ? "1 chat" : count + " chats",
                BackContent = back,
                WideBackContent = back,
            };

            try { tile.Update(data); }
            catch (Exception)
            {
                // The tile may have been unpinned, and a tile that cannot be drawn
                // is never worth taking a message down for.
            }
        }

        /// <summary>
        /// The application tile. First in ActiveTiles by definition; the rest are
        /// secondary tiles the user pinned, which are not ours to write to.
        /// </summary>
        private static ShellTile Primary()
        {
            try
            {
                foreach (ShellTile tile in ShellTile.ActiveTiles) return tile;
            }
            catch (Exception) { }
            return null;
        }

        private static List<TileChat> Load()
        {
            var chats = new List<TileChat>();

            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.FileExists(FileName)) return chats;

                    using (var fs = store.OpenFile(FileName, FileMode.Open, FileAccess.Read))
                    using (var r = new BinaryReader(fs))
                    {
                        if (r.ReadInt32() != Version) return chats;

                        int count = r.ReadInt32();
                        for (int i = 0; i < count; i++)
                        {
                            long peerId = r.ReadInt64();
                            string title = r.ReadString();
                            chats.Add(new TileChat
                            {
                                PeerId = peerId,
                                Title = string.IsNullOrEmpty(title) ? null : title,
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
                return new List<TileChat>();
            }

            return chats;
        }

        private static void Save(List<TileChat> chats)
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                using (var fs = store.OpenFile(FileName, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(Version);
                    w.Write(chats.Count);
                    foreach (TileChat c in chats)
                    {
                        w.Write(c.PeerId);
                        w.Write(c.Title ?? "");
                    }
                }
            }
            catch (Exception) { }
        }
    }
}
