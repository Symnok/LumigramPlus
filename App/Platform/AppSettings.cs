using System;
using Windows.Storage;

namespace LumigramPlus.App
{
    /// <summary>What the app is allowed to do while it is not on screen.</summary>
    internal enum BackgroundMode
    {
        /// <summary>Nothing runs. The default.</summary>
        Off = 0,

        /// <summary>A timer wakes a task to look for new messages.</summary>
        Periodic = 1,
    }

    /// <summary>
    /// What the user has chosen.
    ///
    /// LocalSettings rather than a file: these are a handful of switches, they are
    /// read on every message drawn, and the platform already keeps a key-value store
    /// that is loaded before the app is. A file would mean an async read in front of
    /// decisions that have to be made synchronously while binding.
    ///
    /// Every setting has a default that works without being asked about, and reading
    /// one that has never been written returns it.
    /// </summary>
    internal static class AppSettings
    {
        private const string AutoLoadPhotosKey = "autoLoadPhotos";
        private const string NotificationsKey = "notifications";
        private const string NotificationSoundKey = "notificationSound";
        private const string BackgroundKey = "backgroundMode";

        /// <summary>
        /// Whether pictures are fetched as soon as they appear.
        ///
        /// On by default: a messenger that shows a row of "tap to load" where the
        /// pictures should be is technically thriftier and worse to use. The switch
        /// exists because the opposite is a legitimate preference on a metered
        /// connection, not because the default is in doubt.
        /// </summary>
        public static bool AutoLoadPhotos
        {
            get { return Read(AutoLoadPhotosKey, true); }
            set { Write(AutoLoadPhotosKey, value); }
        }

        /// <summary>
        /// Whether arriving messages are announced.
        ///
        /// On by default. Only the foreground case exists for now - the app has to
        /// be running to notice anything - so this is a switch over what happens
        /// while it is open, and a background setting can join it when there is
        /// something to switch.
        /// </summary>
        public static bool Notifications
        {
            get { return Read(NotificationsKey, true); }
            set { Write(NotificationsKey, value); }
        }

        /// <summary>
        /// Whether a notification makes a sound.
        ///
        /// Off by default, unlike the notifications themselves. A messenger that
        /// stays quiet until asked is a reasonable thing to install; one that starts
        /// making noise the moment it is signed in is not.
        /// </summary>
        public static bool NotificationSound
        {
            get { return Read(NotificationSoundKey, false); }
            set { Write(NotificationSoundKey, value); }
        }

        /// <summary>
        /// What the app may do while it is not on screen.
        ///
        /// Off by default, and deliberately so: background work costs battery on a
        /// phone that has not had a new one in a decade, and the two modes that are
        /// not off both need permission the user has to grant. An app that starts
        /// taking that on its own is an app that gets uninstalled.
        /// </summary>
        public static BackgroundMode BackgroundMode
        {
            get
            {
                try
                {
                    object stored = ApplicationData.Current.LocalSettings.Values[BackgroundKey];
                    if (!(stored is int)) return BackgroundMode.Off;

                    int value = (int)stored;

                    // 2 was a real-time mode that no longer exists. It always
                    // registered the timer as well, so the intent behind it survives
                    // as the timer rather than as off.
                    return value == 0 ? BackgroundMode.Off : BackgroundMode.Periodic;
                }
                catch (Exception)
                {
                    return BackgroundMode.Off;
                }
            }
            set
            {
                try { ApplicationData.Current.LocalSettings.Values[BackgroundKey] = (int)value; }
                catch (Exception) { }
            }
        }

        private static bool Read(string key, bool fallback)
        {
            try
            {
                object stored = ApplicationData.Current.LocalSettings.Values[key];
                return stored is bool ? (bool)stored : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static void Write(string key, bool value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
            catch (Exception)
            {
                // A setting that cannot be stored is not worth failing over; it
                // simply reverts to the default next launch.
            }
        }
    }
}
