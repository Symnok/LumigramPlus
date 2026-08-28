using System;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// Starting a conversation with someone who is not in the chat list yet.
    ///
    /// The lookup itself lives in Core and is deliberately the one that does not
    /// change the account: resolving a phone number reads it rather than importing
    /// it, so searching for someone does not quietly add them to the user's
    /// contacts as a side effect.
    /// </summary>
    public sealed partial class FindPage : Page
    {
        private DialogItem _found;

        public FindPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            QueryBox.Focus(FocusState.Programmatic);
        }

        private void Query_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;

            e.Handled = true;
            Find_Click(null, null);
        }

        private async void Find_Click(object sender, RoutedEventArgs e)
        {
            string query = (QueryBox.Text ?? "").Trim();
            if (query.Length == 0) return;

            FindButton.IsEnabled = false;
            FoundPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = "Looking...";

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                ResolvedPeer peer = await Contacts.ResolveAsync(
                    client, query, TelegramService.Info);

                if (peer == null)
                {
                    StatusText.Text = "No one found for that.";
                    return;
                }

                _found = new DialogItem
                {
                    PeerId = peer.PeerId,
                    AccessHash = peer.AccessHash,
                    Kind = peer.Kind,
                    Title = peer.Title,
                };

                StatusText.Text = "";
                FoundText.Text = peer.Title ?? peer.Username ?? peer.Phone ?? "found";

                // Only a person can be called. A channel resolves perfectly well and
                // there is nobody at the other end of it to answer.
                CallButton.Visibility = peer.Kind == "user"
                    ? Visibility.Visible : Visibility.Collapsed;

                FoundPanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                string error = rpc != null ? rpc.ErrorType : ex.Message;

                // The common ones, said in words rather than in Telegram's.
                StatusText.Text =
                    error == "USERNAME_NOT_OCCUPIED" ? "There is no such username." :
                    error == "USERNAME_INVALID" ? "That is not a valid username." :
                    error == "PHONE_NOT_OCCUPIED" ? "Nobody on Telegram has that number." :
                    "Could not look that up: " + error;
            }
            finally
            {
                FindButton.IsEnabled = true;
            }
        }

        private void Message_Click(object sender, RoutedEventArgs e)
        {
            if (_found == null) return;

            Frame.Navigate(typeof(ConversationPage), _found);
        }

        private void Call_Click(object sender, RoutedEventArgs e)
        {
            if (_found == null) return;

            Frame.Navigate(typeof(CallPage), new CallRequest { Peer = _found });
        }
    }
}
