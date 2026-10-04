using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Lumigram.Mtproto;
using Windows.UI.Xaml.Navigation;

namespace LumigramPlus.App
{
    /// <summary>Where the handful of choices live.</summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>
        /// Set while the switch is being put into its stored position.
        ///
        /// Assigning IsOn raises Toggled, so without this the act of showing the
        /// current setting would write it straight back - harmless here, and the
        /// kind of loop that is not harmless once a setting does something on
        /// change.
        /// </summary>
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
            ShowAbout();
        }

        /// <summary>
        /// Fills in the about tab.
        ///
        /// The version comes from the installed package's own identity - the number
        /// in Package.appxmanifest that is bumped for every build - so what is shown
        /// is what is installed, not what someone remembered to type.
        ///
        /// The API layer is there for the same reason a version is: it is the first
        /// thing worth knowing when Telegram changes something and a feature stops
        /// working.
        /// </summary>
        private void ShowAbout()
        {
            try
            {
                Windows.ApplicationModel.PackageVersion v =
                    Windows.ApplicationModel.Package.Current.Id.Version;

                VersionText.Text = "version " + v.Major + "." + v.Minor + "." +
                                   v.Build + "." + v.Revision;
            }
            catch (Exception)
            {
                VersionText.Text = "";
            }

            LayerText.Text = "Telegram API layer " + Lumigram.Tl.TlConstructors.Layer;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _loading = true;
            // Through TextSizes, not a cast: the list is in size order and the
            // stored values are in the order the sizes were added, which stopped
            // being the same thing when extra small and extra large arrived.
            TextSizeBox.SelectedIndex = TextSizes.IndexOf(AppSettings.TextSize);
            AutoLoadSwitch.IsOn = AppSettings.AutoLoadPhotos;
            NotificationsSwitch.IsOn = AppSettings.Notifications;
            SoundSwitch.IsOn = AppSettings.NotificationSound;
            BackgroundBox.SelectedIndex = (int)AppSettings.BackgroundMode;
            DescribeBackground(null);
            ShowLastRun();

            // For testing an incoming call, where there is no page to watch: this
            // says whether any call update reached the phone at all.
            CallText.Text = "pushed " + CallService.PushedCount +
                ", call updates " + CallService.UpdateCount +
                (CallService.LastUpdate == null ? "" : ", last " + CallService.LastUpdate) +
                (CallService.LastError == null ? "" : "\n" + CallService.LastError) +
                "\n" + CallService.Pushed;

            // Shown only when something is wrong. Toasts fail silently by design,
            // so a working app and a refused one look identical without this.
            string trouble = Notifications.LastError ?? Notifications.NotifierState();
            ToastText.Text = trouble == null ? "" : "Toasts unavailable: " + trouble;
            _loading = false;

            AccountText.Text = TelegramService.Session != null
                ? "Signed in. Authorisation stored for " + TelegramService.Session.Host + "."
                : "Not signed in.";

            ShowCacheSize();
            ShowReactionTest();
            ShowProxy();
        }

        /// <summary>
        /// Says whether connections go through a proxy, and which. Shown on the way
        /// back from the proxy page too, since that is where it changes.
        /// </summary>
        private void ShowProxy()
        {
            ProxySettings proxy = AppSettings.Proxy;

            if (proxy != null)
                ProxyText.Text = "On - connecting through " + proxy.Host + ":" + proxy.Port +
                                 (proxy.HasLogin ? " as " + proxy.User : "") + ".";
            else if (AppSettings.ProxyEnabled)
                ProxyText.Text = "Switched on but incomplete, so connecting directly.";
            else
                ProxyText.Text = "Off - connecting directly.";
        }

        private void Proxy_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ProxyPage));
        }

        /// <summary>
        /// Draws every reaction the picker offers, each with its number, exactly as
        /// a message would draw it - including the heart's red.
        /// </summary>
        private void ShowReactionTest()
        {
            ReactionTestText.Inlines.Clear();

            var numberBrush = new Windows.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(160, 160, 160, 160));

            for (int i = 0; i < ReactionSet.Old.Length; i++)
            {
                string emoji = ReactionSet.Old[i];

                ReactionTestText.Inlines.Add(new Windows.UI.Xaml.Documents.Run
                {
                    Text = (i + 1) + "\u00A0",
                    FontSize = 12,
                    Foreground = numberBrush,
                });

                ReactionTestText.Inlines.Add(new Windows.UI.Xaml.Documents.Run
                {
                    Text = ReactionSet.Display(emoji) + "  ",
                    Foreground = ReactionSet.BrushFor(emoji),
                });
            }
        }

        /// <summary>
        /// Puts the size of the downloaded files on screen.
        ///
        /// Every file is asked for its size one at a time, which on a well-used
        /// cache is hundreds of calls - so the page is drawn first and this fills in
        /// after, rather than the settings taking a second to open.
        /// </summary>
        private async void ShowCacheSize()
        {
            CacheText.Text = "Measuring...";

            try
            {
                CacheSize size = await CacheStore.MeasureAsync();

                CacheText.Text = size.Files == 0
                    ? "Nothing downloaded yet."
                    : size.Files + " file" + (size.Files == 1 ? "" : "s") + ", " +
                      CacheStore.Describe(size.Bytes) + ".";

                ClearCacheButton.IsEnabled = size.Files > 0;
            }
            catch (Exception)
            {
                // Not worth a message of its own: the button still works, and
                // pressing it reports what it actually did.
                CacheText.Text = "";
                ClearCacheButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Empties the caches, once the user has said so.
        ///
        /// Confirmed first because it cannot be undone in any useful sense - the
        /// files come back only by downloading them again, which on a phone
        /// connection is the cost this is being asked about.
        /// </summary>
        private async void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Windows.UI.Popups.MessageDialog(
                "Delete the downloaded photos, videos, files and chat pictures? " +
                "They will be downloaded again when they are next looked at.",
                "Delete downloaded files");

            dialog.Commands.Add(new Windows.UI.Popups.UICommand("yes"));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;

            Windows.UI.Popups.IUICommand chosen = await dialog.ShowAsync();
            if (chosen == null || chosen.Label != "yes") return;

            ClearCacheButton.IsEnabled = false;
            CacheText.Text = "Deleting...";

            try
            {
                CacheSize freed = await CacheStore.ClearAsync();

                // The files kept are the ones something else had open, and saying so
                // is the difference between a cache that did not empty and one that
                // could not - the second is fixed by trying again in a moment.
                CacheText.Text = "Freed " + CacheStore.Describe(freed.Bytes) + "." +
                    (freed.Kept > 0
                        ? " " + freed.Kept + " file" + (freed.Kept == 1 ? " was" : "s were") +
                          " in use and kept."
                        : "");

                ClearCacheButton.IsEnabled = freed.Kept > 0;
            }
            catch (Exception ex)
            {
                CacheText.Text = "Could not delete: " + ex.Message;
                ClearCacheButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Chooses how large the text people read is drawn.
        ///
        /// Applied at once so the sizes are in the resources, but the screens already
        /// built keep what they were parsed with - a page reads its sizes once, when
        /// it is created. In practice that is invisible: going back from here builds
        /// the chat list again, and opening a conversation builds that. This page
        /// itself does not change, which is why the note under the box says so
        /// rather than leaving the user to wonder whether it worked.
        /// </summary>
        private void TextSize_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            int index = TextSizeBox.SelectedIndex;
            if (index < 0) return;

            AppSettings.TextSize = TextSizes.At(index);
            TextSizes.Apply();
        }

        private async void SignOut_Click(object sender, RoutedEventArgs e)
        {
            AccountText.Text = "Signing out...";

            bool revoked = await TelegramService.SignOutAsync();

            if (!revoked)
            {
                // The key is gone from this phone either way, but the session may
                // still be live on Telegram's side - and only the user can clear it
                // from another device.
                var dialog = new Windows.UI.Popups.MessageDialog(
                    "The key on this phone has been deleted, but Telegram could not " +
                    "be reached to end the session. Remove it from another device " +
                    "under Settings, Devices.",
                    "Signed out on this phone");

                await dialog.ShowAsync();
            }

            Frame.Navigate(typeof(QrLoginPage));
            Frame.BackStack.Clear();
        }

        /// <summary>
        /// Closes the app rather than leaving it suspended.
        ///
        /// Worth having on a phone with an authorisation key in memory: suspending
        /// keeps the process and everything in it, where this ends both.
        /// </summary>
        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            TelegramService.Disconnect();
            Application.Current.Exit();
        }

        private async void Background_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            int index = BackgroundBox.SelectedIndex;

            BackgroundMode mode = index == (int)BackgroundMode.Periodic
                ? BackgroundMode.Periodic : BackgroundMode.Off;

            AppSettings.BackgroundMode = mode;

            BackgroundText.Text = "Asking the system...";

            // Registration needs permission the user may refuse, and real time needs
            // a slot the system may not have. Whatever comes back is shown rather
            // than swallowed: the difference between "on" and "asked for and
            // refused" is invisible otherwise.
            string trouble = await BackgroundNotifications.ApplyAsync(mode);

            DescribeBackground(trouble);
        }

        /// <summary>
        /// What the background task last did, if it has ever run.
        ///
        /// The task is silent by construction - it has no screen and must fail
        /// quietly - so without this, "the trigger never fired" and "it fired and
        /// found nothing" are the same observation: no notification.
        /// </summary>
        private void ShowLastRun()
        {
            string last = BackgroundLog.Last;

            if (!string.IsNullOrEmpty(last))
            {
                LastRunText.Text = "Last background check: " + last;
                return;
            }

            LastRunText.Text = BackgroundNotifications.PeriodicRegistered
                ? "Registered, but has not run yet."
                : "Not registered.";
        }

        /// <summary>
        /// Runs the same check the background task runs, here and now.
        ///
        /// This separates two failures that look identical from the outside: the
        /// trigger never firing, and the work failing when it does. If this button
        /// notifies and the background never does, the connection and the toast are
        /// fine and the problem is the wake.
        /// </summary>
        private async void CheckNow_Click(object sender, RoutedEventArgs e)
        {
            CheckNowButton.IsEnabled = false;
            LastRunText.Text = "Checking...";

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, 20, 0, 0, null, TelegramService.Info);

                int announced = Notifications.Observe(page.Entries);

                LastRunText.Text = page.Entries.Count + " chats read, " +
                    (announced == 0
                        ? "nothing new since the last check."
                        : announced + " announced.");
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                LastRunText.Text = "Failed: " + (rpc != null ? rpc.ErrorType : ex.Message);
            }
            finally
            {
                CheckNowButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Measures the managed Opus codec against a call's frame budget.
        ///
        /// Temporary, and here rather than in the harness because the harness runs
        /// on a desktop - where the answer is yes for any phone and therefore
        /// worthless. The only machine whose answer matters is this one.
        /// </summary>
        private async void Benchmark_Click(object sender, RoutedEventArgs e)
        {
            BenchmarkButton.IsEnabled = false;
            BenchmarkText.Text = "Measuring, about ten seconds...";

            try
            {
                VoiceBenchmark.Result[] results = await VoiceBenchmark.RunAsync();

                var text = new System.Text.StringBuilder();
                foreach (VoiceBenchmark.Result result in results)
                    text.AppendLine(result.ToString());

                BenchmarkText.Text = text.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                BenchmarkText.Text = "Failed: " + ex.Message;
            }
            finally
            {
                BenchmarkButton.IsEnabled = true;
            }
        }

        private void DescribeBackground(string trouble)
        {
            if (!string.IsNullOrEmpty(trouble))
            {
                BackgroundText.Text = trouble;
                return;
            }

            switch (AppSettings.BackgroundMode)
            {
                case BackgroundMode.Periodic:
                    BackgroundText.Text = "Fifteen minutes is the shortest the "
                                        + "platform allows, and it is a floor rather "
                                        + "than a schedule - the phone decides when "
                                        + "within it to wake.";
                    break;

                default:
                    BackgroundText.Text = "Messages are only noticed while the app is open.";
                    break;
            }
        }

        private void Sound_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.NotificationSound = SoundSwitch.IsOn;
        }

        private void Notifications_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.Notifications = NotificationsSwitch.IsOn;
        }

        private void AutoLoad_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.AutoLoadPhotos = AutoLoadSwitch.IsOn;
        }
    }
}
