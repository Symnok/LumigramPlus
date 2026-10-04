using System;
using Windows.Storage;
using Lumigram.Mtproto;

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
    /// How large the text people read is drawn.
    ///
    /// Beside the setting rather than beside TextSizes, which applies it: the
    /// background task compiles this file and cannot see a XAML type.
    /// </summary>
    internal enum TextSize
    {
        /// <summary>What the app was before anyone asked.</summary>
        Small = 0,

        /// <summary>The default.</summary>
        Medium = 1,

        Large = 2,

        // Added after the first three, and numbered after them rather than around
        // them. The value is what is written to LocalSettings, so renumbering to
        // put these in visual order would quietly turn somebody's stored Large into
        // Medium on the next launch. TextSizes.Order carries the order they are
        // shown in, which is the only place it matters.
        ExtraSmall = 3,
        ExtraLarge = 4,
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
        private const string TextSizeKey = "textSize";
        private const string ProxyEnabledKey = "proxyEnabled";
        private const string ProxyHostKey = "proxyHost";
        private const string ProxyPortKey = "proxyPort";
        private const string ProxyUserKey = "proxyUser";
        private const string ProxyPasswordKey = "proxyPassword";

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

        /// <summary>
        /// How large the text people read is drawn.
        ///
        /// Medium by default, and Medium is larger than the app shipped with -
        /// which was one size for everything and too small to read comfortably on
        /// the screens this runs on. Small is that original size, kept so the change
        /// takes nothing away from anyone who was happy with it.
        /// </summary>
        public static TextSize TextSize
        {
            get
            {
                try
                {
                    object stored = ApplicationData.Current.LocalSettings.Values[TextSizeKey];
                    if (!(stored is int)) return TextSize.Medium;

                    int value = (int)stored;

                    // Asked of the enum rather than range-checked. The range test
                    // this replaces was written when Large was the largest number
                    // as well as the largest size, and adding two members after it
                    // would have made both of them read back as Medium forever.
                    if (!Enum.IsDefined(typeof(TextSize), value)) return TextSize.Medium;

                    return (TextSize)value;
                }
                catch (Exception)
                {
                    return TextSize.Medium;
                }
            }
            set
            {
                try { ApplicationData.Current.LocalSettings.Values[TextSizeKey] = (int)value; }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// The SOCKS5 proxy to connect through, or null to connect directly.
        ///
        /// Null as well when it is switched on but incomplete - no host, or no
        /// usable port. Connecting directly then is the only thing that can work,
        /// and the proxy page says why it is not being used.
        ///
        /// Read by the background task as well as the app, which is why it lives
        /// here: a message check that went around the proxy would fail on exactly
        /// the networks the proxy is for, and quietly show the user's real address
        /// to Telegram while doing it.
        ///
        /// The password is kept in the app's private settings, with the same
        /// protection as the sign-in key the app already keeps in its private
        /// storage. Encrypting the lesser secret while the greater one is not would
        /// be theatre.
        /// </summary>
        public static ProxySettings Proxy
        {
            get
            {
                if (!ProxyEnabled) return null;

                var proxy = new ProxySettings
                {
                    Host = ReadString(ProxyHostKey),
                    Port = ProxyPort,
                    User = ReadString(ProxyUserKey),
                    Password = ReadString(ProxyPasswordKey),
                };

                return proxy.IsUsable ? proxy : null;
            }
        }

        public static bool ProxyEnabled
        {
            get { return Read(ProxyEnabledKey, false); }
            set { Write(ProxyEnabledKey, value); }
        }

        public static string ProxyHost { get { return ReadString(ProxyHostKey); } }
        public static string ProxyUser { get { return ReadString(ProxyUserKey); } }
        public static string ProxyPassword { get { return ReadString(ProxyPasswordKey); } }

        public static int ProxyPort
        {
            get
            {
                try
                {
                    object stored = ApplicationData.Current.LocalSettings.Values[ProxyPortKey];
                    return stored is int ? (int)stored : 1080;
                }
                catch (Exception)
                {
                    return 1080;
                }
            }
        }

        /// <summary>Stores the proxy's address and login, without switching it on or off.</summary>
        public static void SetProxy(string host, int port, string user, string password)
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                values[ProxyHostKey] = host ?? "";
                values[ProxyPortKey] = port;
                values[ProxyUserKey] = user ?? "";
                values[ProxyPasswordKey] = password ?? "";
            }
            catch (Exception)
            {
            }
        }

        private static string ReadString(string key)
        {
            try
            {
                return ApplicationData.Current.LocalSettings.Values[key] as string ?? "";
            }
            catch (Exception)
            {
                return "";
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
