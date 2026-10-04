using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// Setting up the SOCKS5 proxy.
    ///
    /// A page of its own because it is opened from two places: the settings, and
    /// the sign-in screen. The second matters most - on a network that blocks
    /// Telegram there is no signing in without the proxy, and so no reaching the
    /// settings to set it up.
    ///
    /// Saving reconnects at once and says how that went. A wrong port or password
    /// found here, with the fields still on screen, is a typo; found later as
    /// "cannot connect" on the chat list, it is a mystery.
    /// </summary>
    public sealed partial class ProxyPage : Page
    {
        public ProxyPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            EnabledSwitch.IsOn = AppSettings.ProxyEnabled;
            HostBox.Text = AppSettings.ProxyHost;
            PortBox.Text = AppSettings.ProxyPort.ToString();
            UserBox.Text = AppSettings.ProxyUser;
            PasswordBox.Password = AppSettings.ProxyPassword;
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            string host = (HostBox.Text ?? "").Trim();
            string user = (UserBox.Text ?? "").Trim();
            bool enabled = EnabledSwitch.IsOn;

            int port;
            if (!int.TryParse((PortBox.Text ?? "").Trim(), out port)) port = 0;

            // Checked only when it is going to be used. Switching the proxy off
            // with a half-typed address in the boxes is a reasonable thing to do,
            // and refusing it would trap the user on a setting that does not work.
            if (enabled && host.Length == 0)
            {
                StatusText.Text = "Enter the proxy's address, or switch the proxy off.";
                return;
            }

            if (enabled && (port <= 0 || port > 65535))
            {
                StatusText.Text = "The port is a number from 1 to 65535 - 1080 is the usual one.";
                return;
            }

            AppSettings.SetProxy(host, port > 0 ? port : 1080, user, PasswordBox.Password);
            AppSettings.ProxyEnabled = enabled;

            await ReconnectAsync(enabled);
        }

        /// <summary>
        /// Drops the current connection and makes a new one the new way.
        ///
        /// Every connection is built through PhoneTransport.Open, which reads the
        /// setting just saved - so dropping the old ones is all it takes for the
        /// next to go through the proxy, or not.
        /// </summary>
        private async System.Threading.Tasks.Task ReconnectAsync(bool viaProxy)
        {
            SaveButton.IsEnabled = false;
            Busy.Visibility = Visibility.Visible;
            StatusText.Text = viaProxy ? "Connecting through the proxy..." : "Connecting directly...";

            try
            {
                TelegramService.Disconnect();
                await TelegramService.ConnectAsync();

                StatusText.Text = viaProxy
                    ? "Connected to Telegram through the proxy."
                    : "Connected to Telegram directly.";
            }
            catch (Socks5Exception ex)
            {
                // Already a sentence about the proxy - which part of it is wrong.
                StatusText.Text = "Not connected: " + ex.Message + ".";
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                StatusText.Text = "Not connected: " + (rpc != null ? rpc.ErrorType : ex.Message);
            }
            finally
            {
                SaveButton.IsEnabled = true;
                Busy.Visibility = Visibility.Collapsed;
            }
        }
    }
}
