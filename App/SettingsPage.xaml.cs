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
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _loading = true;
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
