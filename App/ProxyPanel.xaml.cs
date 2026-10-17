using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace LumigramPlus.App
{
    /// <summary>
    /// Setting up a proxy: Telegram's own MTProto type, or SOCKS5.
    ///
    /// A control rather than a page because it appears in two places: the "proxy"
    /// tab of the settings, and the proxy page that the sign-in screen and a
    /// tapped proxy link open. The sign-in screen matters most - on a network that
    /// blocks Telegram there is no signing in without the proxy, and so no reaching
    /// the settings to set it up.
    ///
    /// Saving reconnects at once and makes one real request through the new route,
    /// then says how that went. With an MTProto proxy that request is the only
    /// test there is: the proxy accepts the connection whatever the secret, and a
    /// wrong one shows only as silence afterwards. A wrong port or secret found
    /// here, with the fields still on screen, is a typo; found later as "cannot
    /// connect" on the chat list, it is a mystery.
    /// </summary>
    public sealed partial class ProxyPanel : UserControl
    {
        /// <summary>How long to wait for Telegram's answer through the proxy.</summary>
        private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(25);

        // Positions in TypeBox.
        private const int MtProtoIndex = 0;
        private const int Socks5Index = 1;

        private bool _loading;

        public ProxyPanel()
        {
            InitializeComponent();
        }

        /// <summary>Puts the saved settings in the boxes.</summary>
        public void Load()
        {
            _loading = true;
            EnabledSwitch.IsOn = AppSettings.ProxyEnabled;
            TypeBox.SelectedIndex = AppSettings.ProxyKind == ProxyKind.MtProto ? MtProtoIndex : Socks5Index;
            HostBox.Text = AppSettings.ProxyHost;
            PortBox.Text = AppSettings.ProxyHost.Length > 0 ? AppSettings.ProxyPort.ToString() : "";
            UserBox.Text = AppSettings.ProxyUser;
            PasswordBox.Password = AppSettings.ProxyPassword;
            SecretBox.Text = AppSettings.ProxySecret;
            _loading = false;

            ShowType();
            DescribeSecret();
        }

        /// <summary>Reconnects with the saved settings and says how it went.</summary>
        public Task TestAsync()
        {
            return ReconnectAsync(AppSettings.ProxyEnabled);
        }

        private bool IsMtProto { get { return TypeBox.SelectedIndex != Socks5Index; } }

        private void Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowType();

            // The usual ports differ; offer the right one when the box still holds
            // the other type's default rather than anything the user typed.
            if (_loading) return;
            string port = (PortBox.Text ?? "").Trim();
            if (IsMtProto && (port == "1080" || port.Length == 0)) PortBox.Text = "443";
            else if (!IsMtProto && (port == "443" || port.Length == 0)) PortBox.Text = "1080";
        }

        private void ShowType()
        {
            SocksPanel.Visibility = IsMtProto ? Visibility.Collapsed : Visibility.Visible;
            MtProtoPanel.Visibility = IsMtProto ? Visibility.Visible : Visibility.Collapsed;
            PortBox.PlaceholderText = IsMtProto ? "443" : "1080";
        }

        private void Secret_TextChanged(object sender, TextChangedEventArgs e)
        {
            DescribeSecret();
        }

        /// <summary>Says which type of MTProto proxy the secret is for, or what is wrong with it.</summary>
        private void DescribeSecret()
        {
            string text = (SecretBox.Text ?? "").Trim();
            if (text.Length == 0)
            {
                SecretTypeText.Text = "";
                return;
            }

            string error;
            MtProxySecret secret = MtProxySecret.Parse(text, out error);
            SecretTypeText.Text = secret != null ? secret.Describe() : "Not usable: " + error + ".";
        }

        private void UseLink_Click(object sender, RoutedEventArgs e)
        {
            string error;
            ProxyLink link = ProxyLink.Parse((LinkBox.Text ?? "").Trim(), out error);

            if (link == null)
            {
                StatusText.Text = error;
                return;
            }

            TypeBox.SelectedIndex = link.IsMtproto ? MtProtoIndex : Socks5Index;
            HostBox.Text = link.Server;
            PortBox.Text = link.Port.ToString();

            if (link.IsMtproto)
            {
                SecretBox.Text = link.SecretText;
            }
            else
            {
                UserBox.Text = link.User ?? "";
                PasswordBox.Password = link.Password ?? "";
            }

            EnabledSwitch.IsOn = true;
            LinkBox.Text = "";
            StatusText.Text = "Filled in from the link. Check it, then save and connect.";
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            string host = (HostBox.Text ?? "").Trim();
            string user = (UserBox.Text ?? "").Trim();
            string secret = (SecretBox.Text ?? "").Trim();
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
                StatusText.Text = IsMtProto
                    ? "The port is a number from 1 to 65535 - 443 is the usual one."
                    : "The port is a number from 1 to 65535 - 1080 is the usual one.";
                return;
            }

            if (enabled && IsMtProto)
            {
                string error;
                if (MtProxySecret.Parse(secret, out error) == null)
                {
                    StatusText.Text = "The secret is not usable: " + error + ".";
                    return;
                }
            }

            AppSettings.SetProxy(host, port > 0 ? port : (IsMtProto ? 443 : 1080), user,
                                 PasswordBox.Password);
            AppSettings.ProxySecret = secret;
            AppSettings.ProxyKind = IsMtProto ? ProxyKind.MtProto : ProxyKind.Socks5;
            AppSettings.ProxyEnabled = enabled;

            await ReconnectAsync(enabled);
        }

        /// <summary>
        /// Drops the current connection, makes a new one the new way, and asks
        /// Telegram one question through it.
        ///
        /// Every connection is built through PhoneTransport.Open, which reads the
        /// settings just saved - so dropping the old ones is all it takes for the
        /// next to go through the proxy, or not.
        /// </summary>
        private async Task ReconnectAsync(bool viaProxy)
        {
            SaveButton.IsEnabled = false;
            Busy.Visibility = Visibility.Visible;
            StatusText.Text = viaProxy ? "Connecting through the proxy..." : "Connecting directly...";

            try
            {
                TelegramService.Disconnect();
                MtprotoClient client = await TelegramService.ConnectAsync();

                var q = new TlWriter(8);
                q.WriteConstructor(TlConstructors.HelpGetNearestDc);

                Task<TlReader> ask = client.InvokeAsync(q.ToArray(), TelegramService.Info);
                if (await Task.WhenAny(ask, Task.Delay(AnswerTimeout)) != ask)
                {
                    TelegramService.Disconnect();
                    StatusText.Text = viaProxy
                        ? "No answer from Telegram through the proxy. " + Hint()
                        : "No answer from Telegram.";
                    return;
                }

                await ask;
                StatusText.Text = viaProxy
                    ? "Connected - Telegram answered through the proxy."
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
                StatusText.Text = "Not connected: " + (rpc != null ? rpc.ErrorType : ex.Message) +
                                  (viaProxy && rpc == null ? ". " + Hint() : "");
            }
            finally
            {
                SaveButton.IsEnabled = true;
                Busy.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>What to check, by type - an MTProto proxy cannot say what is wrong.</summary>
        private string Hint()
        {
            if (!IsMtProto) return "Check the address, port and login.";

            string error;
            MtProxySecret secret = MtProxySecret.Parse(SecretBox.Text, out error);

            return secret != null && secret.Mode == MtProxyMode.FakeTls
                ? "Check the secret, and that the phone's clock is right - this type of proxy refuses a clock more than a few minutes out."
                : "Check the secret - a proxy given the wrong one stays silent rather than saying so.";
        }
    }
}
