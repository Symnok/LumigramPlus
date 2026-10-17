using System;
using System.Threading.Tasks;
using Windows.UI.Popups;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// The proxy settings on a page of their own, for the places that cannot show
    /// the settings' "proxy" tab: the sign-in screen, before there is anything to
    /// set, and a proxy link tapped in a chat. The settings themselves are
    /// ProxyPanel, shared with that tab.
    /// </summary>
    public sealed partial class ProxyPage : Page
    {
        /// <summary>Navigation parameter: test the saved settings as soon as the page opens.</summary>
        public const string TestNow = "test";

        public ProxyPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            Panel.Load();
            if (e.Parameter as string == TestNow && AppSettings.ProxyEnabled)
                await Panel.TestAsync();
        }

        /// <summary>
        /// A proxy link tapped in a chat: asks, then sets it up and tests it.
        ///
        /// Asked first because accepting sends everything this app says through a
        /// server run by whoever posted the link, and a stray tap should not do
        /// that. Returns a sentence when the link is unusable, or null.
        /// </summary>
        public static async Task<string> OfferAsync(Frame frame, string url)
        {
            string error;
            ProxyLink link = ProxyLink.Parse(url, out error);
            if (link == null) return error;

            string type = link.IsMtproto ? "MTProto proxy" : "SOCKS5 proxy";
            var dialog = new MessageDialog(
                "Connect through the " + type + " " + link.Server + ":" + link.Port + "?\n\n" +
                "Everything Lumigram+ sends will pass through that server, which is run by " +
                "whoever published the link. It can see when and how much you use Telegram, " +
                "though not what you say.",
                "use this proxy?");

            bool accepted = false;
            dialog.Commands.Add(new UICommand("use it", delegate { accepted = true; }));
            dialog.Commands.Add(new UICommand("cancel"));
            dialog.DefaultCommandIndex = 0;
            dialog.CancelCommandIndex = 1;

            await dialog.ShowAsync();
            if (!accepted) return null;

            AppSettings.SetProxy(link.Server, link.Port, link.User ?? "", link.Password ?? "");
            if (link.IsMtproto) AppSettings.ProxySecret = link.SecretText;
            AppSettings.ProxyKind = link.IsMtproto ? ProxyKind.MtProto : ProxyKind.Socks5;
            AppSettings.ProxyEnabled = true;

            frame.Navigate(typeof(ProxyPage), TestNow);
            return null;
        }
    }
}
