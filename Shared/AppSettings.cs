using System;
using System.IO;
using System.IO.IsolatedStorage;

namespace Lumigram.Phone.Services
{
    /// <summary>How hard the app tries to stay running when it is not on screen.</summary>
    public enum BackgroundMode
    {
        /// <summary>Nothing runs once the app is closed or backgrounded.</summary>
        Disabled = 0,

        /// <summary>
        /// A scheduled agent wakes roughly every 30 minutes.
        ///
        /// The interval is the operating system's, not ours: Windows Phone runs
        /// periodic agents about every 30 minutes for around 25 seconds, and
        /// reschedules or drops them as it sees fit. Messages arrive in batches at
        /// wake-up rather than as they are sent.
        /// </summary>
        Periodic = 1,

        /// <summary>
        /// Continuous background execution, kept alive by location tracking.
        ///
        /// This is the only way an ordinary Windows Phone 8.1 app stays connected
        /// while backgrounded, and it is why the option mentions location: the OS
        /// grants it to apps that track position. It costs battery, and the phone
        /// shows the location indicator while it is on.
        /// </summary>
        AlwaysOn = 2,
    }

    /// <summary>
    /// User-facing settings, persisted in isolated storage.
    ///
    /// Deliberately a small flat file rather than IsolatedStorageSettings: that API
    /// serialises a dictionary and throws on load if any stored type changes,
    /// which turns a settings edit into a launch crash.
    /// </summary>
    public sealed class AppSettings
    {
        private const string FileName = "settings.dat";
        private const int Version = 1;

        private static AppSettings _current;
        private static readonly object _lock = new object();

        /// <summary>Notifications for one-to-one chats. On by default.</summary>
        public bool NotificationsEnabled = true;

        /// <summary>
        /// Whether a notification makes a sound. Off by default - a phone that
        /// chirps without being asked to is worse than one that stays quiet.
        /// </summary>
        public bool NotificationSound = false;

        public BackgroundMode Background = BackgroundMode.Disabled;

        public static AppSettings Current
        {
            get
            {
                lock (_lock)
                {
                    if (_current == null) _current = Load();
                    return _current;
                }
            }
        }

        private static AppSettings Load()
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (!store.FileExists(FileName)) return new AppSettings();

                    using (var fs = store.OpenFile(FileName, FileMode.Open, FileAccess.Read))
                    using (var r = new BinaryReader(fs))
                    {
                        if (r.ReadInt32() != Version) return new AppSettings();

                        return new AppSettings
                        {
                            NotificationsEnabled = r.ReadBoolean(),
                            NotificationSound = r.ReadBoolean(),
                            Background = (BackgroundMode)r.ReadInt32(),
                        };
                    }
                }
            }
            catch (Exception)
            {
                // Corrupt settings must not stop the app starting; defaults are fine.
                return new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                using (var fs = store.OpenFile(FileName, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(Version);
                    w.Write(NotificationsEnabled);
                    w.Write(NotificationSound);
                    w.Write((int)Background);
                }
            }
            catch (Exception) { }
        }
    }
}
