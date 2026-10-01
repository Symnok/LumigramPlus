using System;
using System.Collections.ObjectModel;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// Which conversation to open, and which part of it.
    ///
    /// ConversationPage used to be navigated to with a DialogItem and nothing else,
    /// which is still the whole answer for an ordinary chat. A forum needs a second
    /// value, and a navigation parameter is one object - so the peer and the topic
    /// travel together rather than the topic being left somewhere for the page to
    /// find.
    /// </summary>
    public sealed class ConversationRequest
    {
        public DialogItem Peer;

        /// <summary>The forum topic to show, or 0 for the whole chat.</summary>
        public int TopicId;

        /// <summary>
        /// A message to open at, or 0 for wherever the reading stopped.
        ///
        /// Set when a link named one. It changes which history is fetched, not only
        /// where the list is scrolled: the message may be thousands back, and the
        /// newest thirty would not contain it.
        /// </summary>
        public int FocusMessageId;

        /// <summary>Shown in place of the chat title, so the thread is named.</summary>
        public string TopicTitle;

        /// <summary>
        /// The newest message already read in this topic.
        ///
        /// Carried because the chat list's copy is the forum's, counted across every
        /// topic at once - opening one thread at the forum's marker would land in
        /// the wrong place, usually at the bottom.
        /// </summary>
        public int ReadInboxMaxId;
    }

    /// <summary>One topic in the list.</summary>
    public sealed class TopicItem
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string Preview { get; set; }
        public int UnreadCount { get; set; }
        public bool Closed { get; set; }

        /// <summary>The topic's own colour, as the forum chose it.</summary>
        public int IconColor { get; set; }

        /// <summary>What the last message read in this topic was.</summary>
        public int ReadInboxMaxId { get; set; }

        public Visibility UnreadVisibility
        {
            get { return UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility ClosedVisibility
        {
            get { return Closed ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>One letter standing in for the topic's emoji badge.</summary>
        public string Initial
        {
            get
            {
                return string.IsNullOrEmpty(Title) ? "#" : Title.Substring(0, 1).ToUpper();
            }
        }

        /// <summary>
        /// The badge colour.
        ///
        /// icon_color is a plain RGB value with no alpha, so the bytes have to be
        /// pulled out and an opaque alpha supplied: read as ARGB the whole value
        /// comes out transparent.
        ///
        /// Zero means the topic never chose one, which is General's case, and which
        /// is not black - it is the accent, as anywhere else in the app. Looked up
        /// and checked rather than indexed, because a theme resource that is not
        /// there throws rather than answering null, and a list that cannot draw its
        /// own rows is a worse failure than a grey disc.
        /// </summary>
        public Brush IconBrush
        {
            get
            {
                if (IconColor == 0)
                {
                    var accent = Application.Current.Resources.ContainsKey("PhoneAccentBrush")
                        ? Application.Current.Resources["PhoneAccentBrush"] as Brush
                        : null;

                    return accent ?? new SolidColorBrush(Color.FromArgb(255, 90, 90, 90));
                }

                return new SolidColorBrush(Color.FromArgb(
                    255,
                    (byte)((IconColor >> 16) & 0xFF),
                    (byte)((IconColor >> 8) & 0xFF),
                    (byte)(IconColor & 0xFF)));
            }
        }
    }

    /// <summary>
    /// The topics of one forum supergroup.
    ///
    /// A page of its own rather than a picker inside the conversation, because a
    /// forum's topics are what its chat list entry actually contains: opening the
    /// group and being shown every topic's messages interleaved is not a view anyone
    /// wants, and it is not one the server offers cheaply either - each topic is a
    /// separate thread to read.
    ///
    /// No avatars and no polling. A topic has no picture of its own, and the list
    /// changes at the speed people create topics rather than the speed they send
    /// messages, so refresh is a button rather than a timer.
    /// </summary>
    public sealed partial class TopicsPage : Page
    {
        /// <summary>How many topics to ask for at a time.</summary>
        private const int PageSize = 40;

        private readonly ObservableCollection<TopicItem> _topics =
            new ObservableCollection<TopicItem>();

        private DialogItem _forum;
        private byte[] _inputPeer;

        // Where to continue from. All three, because topics are ordered by the time
        // of their last message and several can share one.
        private int _offsetDate;
        private int _offsetId;
        private int _offsetTopic;

        private bool _hasMore;

        public TopicsPage()
        {
            InitializeComponent();
            TopicList.ItemsSource = _topics;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _forum = e.Parameter as DialogItem;
            if (_forum == null)
            {
                SetBusy(false, "No forum to open.");
                return;
            }

            ForumTitle.Text = _forum.Title ?? "forum";
            _inputPeer = Messages.InputPeerFor(_forum.Kind, _forum.PeerId, _forum.AccessHash);

            // Reloaded on every arrival, including coming back from a topic, which
            // is exactly when the unread counts have changed.
            Load();
        }

        private async void Load()
        {
            SetBusy(true, "Loading topics...");

            _topics.Clear();
            _offsetDate = 0;
            _offsetId = 0;
            _offsetTopic = 0;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync(
                    delegate (string s) { SetBusy(true, s); });

                Topics.TopicPage page = await Topics.GetAsync(
                    client, _inputPeer, PageSize, 0, 0, 0, TelegramService.Info);

                Adopt(page);

                SetBusy(false, _topics.Count == 0
                    ? "No topics."
                    : _topics.Count + " topics" + (_hasMore ? " (more available)" : ""));
            }
            catch (RpcException ex) when (TelegramService.IsAuthGone(ex))
            {
                await TelegramService.AuthGoneAsync();
                Frame.Navigate(typeof(QrLoginPage));
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not load topics: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        private async void LoadMore_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasMore) return;

            LoadMoreButton.Visibility = Visibility.Collapsed;
            SetBusy(true, "Loading more topics...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Topics.TopicPage page = await Topics.GetAsync(
                    client, _inputPeer, PageSize,
                    _offsetDate, _offsetId, _offsetTopic, TelegramService.Info);

                int before = _topics.Count;
                Adopt(page);

                SetBusy(false, _topics.Count == before
                    ? "No more topics."
                    : _topics.Count + " topics");
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not load more: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));

                LoadMoreButton.Visibility = _hasMore ? Visibility.Visible
                                                     : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Adds a page of topics to the list, skipping any already in it.
        ///
        /// The server pages by the last topic's position, and a topic whose newest
        /// message arrives between two requests moves - so the same one can come
        /// back on the following page. Added twice it would be openable twice, with
        /// the two copies disagreeing about what had been read.
        /// </summary>
        private void Adopt(Topics.TopicPage page)
        {
            foreach (ForumTopic t in page.Topics)
            {
                if (Contains(t.Id)) continue;

                _topics.Add(new TopicItem
                {
                    Id = t.Id,
                    Title = t.Title,
                    Preview = Shorten(t.LastText),
                    UnreadCount = t.UnreadCount,
                    Closed = t.Closed,
                    IconColor = t.IconColor,
                    ReadInboxMaxId = t.ReadInboxMaxId,
                });
            }

            _offsetDate = page.NextOffsetDate;
            _offsetId = page.NextOffsetId;
            _offsetTopic = page.NextOffsetTopic;

            _hasMore = page.HasMore;
            LoadMoreButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool Contains(int id)
        {
            foreach (TopicItem item in _topics)
                if (item.Id == id) return true;

            return false;
        }

        private void Topic_Click(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as TopicItem;
            if (item == null || _forum == null) return;

            Frame.Navigate(typeof(ConversationPage), new ConversationRequest
            {
                Peer = _forum,
                TopicId = item.Id,
                TopicTitle = item.Title,
                ReadInboxMaxId = item.ReadInboxMaxId,
            });
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Load();
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string one = text.Replace(Environment.NewLine, " ").Replace('\n', ' ');
            return one.Length <= 80 ? one : one.Substring(0, 77) + "...";
        }

        private void SetBusy(bool busy, string status)
        {
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = status ?? "";
        }
    }
}
