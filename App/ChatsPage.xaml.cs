using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>One row of the chat list.</summary>
    public sealed class DialogItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        public long PhotoId { get; set; }

        /// <summary>Newest message already read, so a chat can open where it left off.</summary>
        public int ReadInboxMaxId { get; set; }

        /// <summary>True when this came from the archive rather than the main list.</summary>
        public bool Archived { get; set; }

        /// <summary>
        /// What the server said about this chat.
        ///
        /// Kept whole so a folder rule can be applied without copying every field a
        /// rule might read onto this class.
        /// </summary>
        public DialogEntry Entry { get; set; }

        /// <summary>
        /// The picture, once it has been fetched.
        ///
        /// Set after the list is already on screen, which is why this class has to
        /// raise a change - the rows are bound before any picture exists.
        /// </summary>
        private Windows.UI.Xaml.Media.ImageSource _avatar;
        public Windows.UI.Xaml.Media.ImageSource Avatar
        {
            get { return _avatar; }
            set { _avatar = value; Raise("Avatar"); }
        }

        public long PeerId { get; set; }
        public long AccessHash { get; set; }
        public string Kind { get; set; }

        // These change while the list is on screen, so each one tells its row about
        // it. Rebuilding the collection instead would flicker every few seconds and
        // throw away the avatars that had been fetched.

        private string _title;
        public string Title
        {
            get { return _title; }
            set { _title = value; Raise("Title"); Raise("Initials"); }
        }

        private string _preview;
        public string Preview
        {
            get { return _preview; }
            set { _preview = value; Raise("Preview"); }
        }

        private int _unread;
        public int UnreadCount
        {
            get { return _unread; }
            set { _unread = value; Raise("UnreadCount"); Raise("UnreadVisibility"); }
        }

        private bool _muted;
        public bool Muted
        {
            get { return _muted; }
            set { _muted = value; Raise("Muted"); Raise("MutedVisibility"); }
        }

        public Visibility UnreadVisibility
        {
            get { return UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility MutedVisibility
        {
            get { return Muted ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>
        /// Two letters standing in for a picture.
        ///
        /// Not a placeholder waiting to be replaced: plenty of chats have no photo
        /// set at all, and two letters identify one where an empty square says
        /// nothing.
        /// </summary>
        public string Initials
        {
            get
            {
                string name = (Title ?? "").Trim();
                if (name.Length == 0) return "?";

                string[] words = name.Split(new[] { ' ' },
                                            StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) return "?";
                if (words.Length == 1) return words[0].Substring(0, 1).ToUpper();

                return (words[0].Substring(0, 1) +
                        words[words.Length - 1].Substring(0, 1)).ToUpper();
            }
        }
    }

    /// <summary>
    /// The chat list.
    ///
    /// Read from the server on every visit rather than cached. The server owns the
    /// unread counts, and the Silverlight client learned the hard way that keeping a
    /// local tally of them is how badges end up wrong: it took three attempts before
    /// the answer turned out to be "ask the server again".
    ///
    /// Deliberately without folders, archive, paging, avatars or live updates. Each
    /// of those is worth having and none of them is worth blocking a list that can
    /// be looked at.
    /// </summary>
    public sealed partial class ChatsPage : Page
    {
        /// <summary>
        /// How many chats to ask for. Enough to fill the screen many times over,
        /// and one request rather than several.
        /// </summary>
        private const int PageSize = 40;

        private readonly ObservableCollection<DialogItem> _dialogs =
            new ObservableCollection<DialogItem>();

        /// <summary>
        /// Every chat read so far, whichever tab is showing.
        ///
        /// The bound collection holds only what the selected tab shows, so the full
        /// set lives here and is filtered into it. Folders are rules over these
        /// chats rather than separate lists - only the archive is genuinely
        /// separate, and it is fetched rather than filtered.
        /// </summary>
        private readonly List<DialogItem> _all = new List<DialogItem>();

        private List<ChatFolder> _folders = new List<ChatFolder>();

        private bool _hasMore;
        private bool _loadingMore;

        private const int MainList = -1;
        private int _selectedFolder = MainList;
        private bool _archiveLoaded;

        public ChatsPage()
        {
            InitializeComponent();
            DialogList.ItemsSource = _dialogs;
        }

        /// <summary>
        /// How often to re-read the chat list while it is on screen.
        ///
        /// Longer than the interval inside a conversation: this fetches every chat,
        /// its unread count and its newest message, where a conversation fetches one
        /// history. Polling at all is a stand-in for the update stream, which is a
        /// piece of work in its own right - but a chat list that never changes while
        /// being looked at is wrong in a way anyone would notice.
        /// </summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        private DispatcherTimer _poll;
        private bool _refreshing;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            CallService.Incoming -= OnIncomingCall;
            CallService.Incoming += OnIncomingCall;

            // No chat is open here, so everything is worth announcing.
            Notifications.OpenPeerId = 0;
            Notifications.Banner -= OnBanner;
            Notifications.Banner += OnBanner;

            Load();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            Notifications.Banner -= OnBanner;

            // Nothing worth fetching for a list nobody is looking at.
            if (_poll != null) _poll.Stop();
        }

        /// <summary>
        /// Someone is calling.
        ///
        /// Only reachable while the app is open: there is no push and no background
        /// execution beyond the quarter-hourly check, so a call placed to a closed
        /// app rings nowhere and is missed.
        /// </summary>
        private void OnIncomingCall(CallInfo call)
        {
            Frame.Navigate(typeof(CallPage), new CallRequest { Call = call });
        }

        private void OnBanner(string title, string body, DialogEntry dialog)
        {
            _bannerPeerId = dialog != null ? dialog.PeerId : 0;

            BannerTitle.Text = title;
            BannerBody.Text = body;
            BannerPanel.Visibility = Visibility.Visible;

            if (_bannerTimer == null)
            {
                _bannerTimer = new DispatcherTimer();
                _bannerTimer.Interval = TimeSpan.FromSeconds(4);
                _bannerTimer.Tick += delegate
                {
                    _bannerTimer.Stop();
                    BannerPanel.Visibility = Visibility.Collapsed;
                };
            }

            // Restarted rather than left running, so a second message extends the
            // banner instead of inheriting the remainder of the first one's time.
            _bannerTimer.Stop();
            _bannerTimer.Start();
        }

        private DispatcherTimer _bannerTimer;
        private long _bannerPeerId;

        private void Banner_Tapped(object sender, TappedRoutedEventArgs e)
        {
            BannerPanel.Visibility = Visibility.Collapsed;
            if (_bannerTimer != null) _bannerTimer.Stop();

            // Only what is already in the list can be opened - a peer we have no
            // access hash for is not addressable, and the refresh that brings it in
            // is seconds away.
            DialogItem item = Find(_bannerPeerId);
            if (item == null) return;

            Frame.Navigate(typeof(ConversationPage), item);
        }

        private void StartPolling()
        {
            if (_poll != null) { _poll.Start(); return; }

            _poll = new DispatcherTimer();
            _poll.Interval = PollInterval;
            _poll.Tick += delegate { Refresh(); };
            _poll.Start();
        }

        /// <summary>
        /// Brings the list up to date without disturbing it.
        ///
        /// Rows already shown are matched by peer and updated in place, so an avatar
        /// already fetched stays put. The collection is rebuilt only when the order
        /// actually changes - which is what happens when a chat receives a message
        /// and moves to the top.
        /// </summary>
        private async void Refresh()
        {
            if (_refreshing) return;
            _refreshing = true;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, PageSize, 0, 0, null, TelegramService.Info);

                int now = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                                                               DateTimeKind.Utc)).TotalSeconds;

                var ordered = new List<DialogItem>();
                bool anyNew = false;

                foreach (DialogEntry d in page.Entries)
                {
                    DialogItem item = Find(d.PeerId);

                    if (item == null)
                    {
                        item = ItemFor(d, now);
                        anyNew = true;
                    }
                    else
                    {
                        item.Title = d.Title;
                        item.Preview = Shorten(d.LastText);
                        item.UnreadCount = d.UnreadCount;
                        item.Muted = d.IsMuted(now);
                    }

                    ordered.Add(item);
                }

                Notifications.Observe(page.Entries);
                CallService.KeepOnline(client);

                // The poll only ever sees the main list, so archived chats already
                // loaded are kept rather than dropped for being absent from it.
                foreach (DialogItem item in _all)
                    if (item.Archived && !ordered.Contains(item)) ordered.Add(item);

                if (anyNew || !SameOrderInAll(ordered))
                {
                    _all.Clear();
                    foreach (DialogItem item in ordered) _all.Add(item);

                    ApplyFolder();

                    if (anyNew) FetchAvatars(client);
                }
            }
            catch (RpcException ex) when (TelegramService.IsAuthGone(ex))
            {
                // Ending the session elsewhere is exactly how this surfaces: the
                // poll is refused, and without noticing, the app would keep showing
                // a chat list it can no longer read.
                await SignedOutAsync();
            }
            catch (Exception)
            {
                // A failed poll is not worth painting an error over a list that is
                // fine; the next one is ten seconds away.
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void Find_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(FindPage));
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SettingsPage));
        }

        /// <summary>
        /// Reads the folder list and draws the tabs.
        ///
        /// A failure is not reported: an account with no folders and one whose
        /// folders could not be read look the same, and either way the main list is
        /// there and usable.
        /// </summary>
        private async void LoadFolders(MtprotoClient client)
        {
            try
            {
                _folders = await Folders.GetAsync(client, TelegramService.Info);
            }
            catch (Exception)
            {
                _folders = new List<ChatFolder>();
            }

            BuildTabs();
        }

        private void BuildTabs()
        {
            FolderTabs.Children.Clear();

            AddTab("chats", MainList);
            foreach (ChatFolder folder in _folders) AddTab(folder.Title, folder.Id);

            // Last, because it is where things go to be out of the way.
            AddTab("archive", Folders.ArchiveFolderId);
        }

        private void AddTab(string title, int id)
        {
            int folderId = id;

            var button = new Button
            {
                Content = title,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 18,
                Opacity = folderId == _selectedFolder ? 1.0 : 0.5,
            };

            button.Click += delegate { SelectFolder(folderId); };
            FolderTabs.Children.Add(button);
        }

        private async void SelectFolder(int folderId)
        {
            _selectedFolder = folderId;
            BuildTabs();

            // The archive is a second list, so it has to be fetched the first time
            // it is opened rather than filtered out of what is already here.
            if (folderId == Folders.ArchiveFolderId && !_archiveLoaded) await LoadArchiveAsync();

            ApplyFolder();
        }

        private async Task LoadArchiveAsync()
        {
            SetBusy(true, "Loading archive...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, PageSize, 0, 0, null, TelegramService.Info,
                    Folders.ArchiveFolderId);

                int now = Now();
                foreach (DialogEntry d in page.Entries)
                {
                    if (Find(d.PeerId) != null) continue;
                    _all.Add(ItemFor(d, now));
                }

                _archiveLoaded = true;
                SetBusy(false, "");

                FetchAvatars(client);
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not load the archive: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>Rebuilds the visible list for whichever tab is selected.</summary>
        private void ApplyFolder()
        {
            int now = Now();

            _dialogs.Clear();
            foreach (DialogItem item in _all)
            {
                if (!Shows(item, now)) continue;
                _dialogs.Add(item);
            }

            // Paging belongs to the main list. A folder shows what is loaded, and
            // the archive is fetched whole when it is opened.
            LoadMoreButton.Visibility = _hasMore && _selectedFolder == MainList
                ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Appends the next page of chats.
        ///
        /// Continues from the last entry rather than asking for a longer list, so
        /// what is already loaded is not fetched and rebuilt a second time. The
        /// continuation point needs the date, the top message id and the peer
        /// together: chats are ordered by the time of their last message, and
        /// several can share one.
        /// </summary>
        private async void LoadMore_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingMore || _dialogs.Count == 0) return;

            _loadingMore = true;
            LoadMoreButton.IsEnabled = false;
            SetBusy(true, "Loading more chats...");

            try
            {
                DialogItem last = _dialogs[_dialogs.Count - 1];

                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client,
                    PageSize,
                    last.Entry != null ? last.Entry.TopMessageDate : 0,
                    last.Entry != null ? last.Entry.TopMessageId : 0,
                    Messages.InputPeerFor(last.Kind, last.PeerId, last.AccessHash),
                    TelegramService.Info);

                int now = Now();
                int added = 0;

                foreach (DialogEntry d in page.Entries)
                {
                    // The entry the offset was taken from comes back as the first of
                    // the next page.
                    if (Find(d.PeerId) != null) continue;

                    _all.Add(ItemFor(d, now));
                    added++;
                }

                // A page that adds nothing new is the end, whatever the server says.
                _hasMore = page.HasMore && added > 0;

                ApplyFolder();
                SetBusy(false, added == 0 ? "No more chats." : "");

                if (added > 0) FetchAvatars(client);
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not load more: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
            finally
            {
                _loadingMore = false;
                LoadMoreButton.IsEnabled = true;
            }
        }

        private bool Shows(DialogItem item, int now)
        {
            if (_selectedFolder == MainList) return !item.Archived;
            if (_selectedFolder == Folders.ArchiveFolderId) return item.Archived;

            foreach (ChatFolder folder in _folders)
            {
                if (folder.Id != _selectedFolder) continue;
                return Folders.Contains(folder, item.Entry, now);
            }

            return false;
        }

        private static int Now()
        {
            return (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                                                        DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>
        /// The long-tap menu, built for the chat it was opened on.
        ///
        /// Assembled in code rather than declared: the folder entries depend on the
        /// account, and which of mute/unmute or archive/unarchive belongs there
        /// depends on the chat.
        /// </summary>
        private void Dialog_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;

            var element = sender as FrameworkElement;
            if (element == null) return;

            var chat = element.DataContext as DialogItem;
            if (chat == null) return;

            var menu = new Windows.UI.Xaml.Controls.MenuFlyout();

            Add(menu, chat.Muted ? "unmute" : "mute", delegate { ToggleMute(chat); });
            Add(menu, chat.Archived ? "unarchive" : "archive", delegate { ToggleArchive(chat); });

            foreach (ChatFolder folder in _folders)
            {
                if (!folder.Editable) continue;

                ChatFolder which = folder;
                bool inside = folder.Include.Contains(chat.PeerId) ||
                              folder.Pinned.Contains(chat.PeerId);

                Add(menu, (inside ? "remove from " : "add to ") + folder.Title,
                    delegate { ToggleFolder(chat, which, !inside); });
            }

            Add(menu, "clear history", delegate { ClearOrDelete(chat, true); });
            Add(menu, "delete chat", delegate { ClearOrDelete(chat, false); });

            menu.ShowAt(element);
        }

        private void Add(Windows.UI.Xaml.Controls.MenuFlyout menu, string text, Action action)
        {
            var entry = new Windows.UI.Xaml.Controls.MenuFlyoutItem { Text = text };
            entry.Click += delegate { action(); };
            menu.Items.Add(entry);
        }

        private async void ToggleMute(DialogItem chat)
        {
            bool muted = !chat.Muted;

            // Changed here first so the list answers the tap at once; put back if
            // the server refuses, since a chat that looks muted and is not is worse
            // than one that appeared not to change.
            chat.Muted = muted;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Messages.SetMutedAsync(client, chat.Kind, chat.PeerId,
                                             chat.AccessHash, muted, TelegramService.Info);
            }
            catch (Exception ex)
            {
                chat.Muted = !muted;

                var rpc = ex as RpcException;
                SetBusy(false, "Could not change mute: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        private async void ToggleArchive(DialogItem chat)
        {
            bool archive = !chat.Archived;
            SetBusy(true, archive ? "Archiving..." : "Unarchiving...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Folders.SetArchivedAsync(
                    client, Messages.InputPeerFor(chat.Kind, chat.PeerId, chat.AccessHash),
                    archive, TelegramService.Info);

                chat.Archived = archive;
                if (chat.Entry != null) chat.Entry.Archived = archive;

                SetBusy(false, "");
                ApplyFolder();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not " + (archive ? "archive" : "unarchive") + ": " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        private async void ToggleFolder(DialogItem chat, ChatFolder folder, bool member)
        {
            SetBusy(true, (member ? "Adding to " : "Removing from ") + folder.Title + "...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Folders.SetMembershipAsync(
                    client, folder,
                    Messages.InputPeerFor(chat.Kind, chat.PeerId, chat.AccessHash),
                    chat.PeerId, member, TelegramService.Info);

                SetBusy(false, "");
                ApplyFolder();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not change the folder: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));

                // The copy in memory has changed and the server has not, so the two
                // now disagree; re-reading is the only way back to the truth.
                try
                {
                    MtprotoClient fresh = await TelegramService.ConnectAsync();
                    LoadFolders(fresh);
                }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Empties a chat, or removes it entirely.
        ///
        /// Both are asked about first. Neither can be undone, and the difference
        /// between them is not obvious from two adjacent menu entries.
        /// </summary>
        private async void ClearOrDelete(DialogItem chat, bool justClear)
        {
            var dialog = new Windows.UI.Popups.MessageDialog(
                justClear
                    ? "Delete all messages in " + chat.Title + "? This cannot be undone."
                    : "Delete " + chat.Title + " and all its messages? This cannot be undone.",
                justClear ? "Clear history" : "Delete chat");

            dialog.Commands.Add(new Windows.UI.Popups.UICommand("yes"));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand("cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;

            Windows.UI.Popups.IUICommand chosen = await dialog.ShowAsync();
            if (chosen == null || chosen.Label != "yes") return;

            SetBusy(true, justClear ? "Clearing..." : "Deleting...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Messages.DeleteHistoryAsync(
                    client, Messages.InputPeerFor(chat.Kind, chat.PeerId, chat.AccessHash),
                    justClear, false, TelegramService.Info);

                if (!justClear)
                {
                    _all.Remove(chat);
                    _dialogs.Remove(chat);
                }
                else
                {
                    chat.Preview = "";
                    chat.UnreadCount = 0;
                }

                SetBusy(false, "");
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not " + (justClear ? "clear" : "delete") + ": " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>Stops everything and returns to signing in.</summary>
        private async Task SignedOutAsync()
        {
            if (_poll != null) _poll.Stop();

            await TelegramService.AuthGoneAsync();

            SetBusy(false, "Signed out.");

            Frame.Navigate(typeof(QrLoginPage));
            Frame.BackStack.Clear();
        }

        /// <summary>
        /// Opens the chat a tapped toast asked for, if one did.
        ///
        /// Only a chat already in the list can be opened - without an access hash a
        /// peer is not addressable.
        /// </summary>
        /// <returns>
        /// False when the chat is not in the loaded list, so a caller that can
        /// reload knows it is worth doing. True means handled, including when there
        /// was nothing to open.
        /// </returns>
        internal bool OpenPending()
        {
            long peerId = Notifications.PendingPeerId;
            if (peerId == 0) return true;

            DialogItem item = Find(peerId);
            if (item == null) return false;

            Notifications.PendingPeerId = 0;
            Frame.Navigate(typeof(ConversationPage), item);
            return true;
        }

        private DialogItem Find(long peerId)
        {
            foreach (DialogItem item in _all)
                if (item.PeerId == peerId) return item;

            return null;
        }

        private bool SameOrderInAll(List<DialogItem> ordered)
        {
            if (ordered.Count != _all.Count) return false;

            for (int i = 0; i < ordered.Count; i++)
                if (!ReferenceEquals(ordered[i], _all[i])) return false;

            return true;
        }

        private DialogItem ItemFor(DialogEntry d, int now)
        {
            return new DialogItem
            {
                PeerId = d.PeerId,
                AccessHash = d.AccessHash,
                Kind = d.Kind,
                Title = d.Title,
                Preview = Shorten(d.LastText),
                UnreadCount = d.UnreadCount,
                Muted = d.IsMuted(now),
                PhotoId = d.PhotoId,
                ReadInboxMaxId = d.ReadInboxMaxId,
                Archived = d.Archived,
                Entry = d,
            };
        }

        private async void Load()
        {
            SetBusy(true, "Loading chats...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync(
                    delegate (string s) { SetBusy(true, s); });

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, PageSize, 0, 0, null, TelegramService.Info);

                _hasMore = page.HasMore;

                // The first read only sets the baseline; announcing everything
                // already waiting would be a wall of toasts for old messages.
                Notifications.Observe(page.Entries);

                int now = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0,
                                                               DateTimeKind.Utc)).TotalSeconds;

                _all.Clear();
                foreach (DialogEntry d in page.Entries) _all.Add(ItemFor(d, now));

                _archiveLoaded = false;
                ApplyFolder();

                SetBusy(false, _dialogs.Count == 0
                    ? "No chats."
                    : _dialogs.Count + " chats" + (page.HasMore ? " (more available)" : ""));

                // Nothing else subscribes, so without this an incoming call
                // arrives on the connection and is dropped on the floor.
                CallService.Listen(client);
                CallService.KeepOnline(client);

                FetchAvatars(client);
                LoadFolders(client);

                // The list is as complete as it is going to get, so a chat still
                // missing from it cannot be opened. Dropped rather than left parked,
                // or it would reopen itself on every later load.
                if (!OpenPending()) Notifications.PendingPeerId = 0;
                StartPolling();
            }
            catch (RpcException ex) when (TelegramService.IsAuthGone(ex))
            {
                await SignedOutAsync();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not load chats: " + (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>
        /// Fills in the pictures once the list is already usable.
        ///
        /// After the list is shown, never before it - an avatar is worth waiting no
        /// time at all for. One at a time rather than all at once, because this is a
        /// phone on a phone network and forty simultaneous downloads would compete
        /// with the messages the user actually came for.
        /// </summary>
        private async void FetchAvatars(MtprotoClient client)
        {
            var chats = new List<DialogItem>(_dialogs);

            foreach (DialogItem chat in chats)
            {
                if (chat.PhotoId == 0) continue;

                try
                {
                    Windows.UI.Xaml.Media.Imaging.BitmapImage image =
                        await AvatarCache.GetAsync(client, chat);

                    if (image != null) chat.Avatar = image;
                }
                catch (Exception)
                {
                    // A picture that will not come is not worth reporting.
                }
            }

            // Said once, at the end, rather than per chat. A whole kind of avatar
            // failing is worth knowing about; forty identical complaints are not.
            if (!string.IsNullOrEmpty(AvatarCache.LastError))
            {
                SetBusy(false, "some pictures unavailable - " + AvatarCache.LastError);
                AvatarCache.LastError = null;
            }
        }

        private void Dialog_Click(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as DialogItem;
            if (item == null) return;

            // The item itself is passed rather than its fields in a query string:
            // both pages are in this app, and reassembling a peer from text is a
            // chance to lose the access hash.
            Frame.Navigate(typeof(ConversationPage), item);
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
