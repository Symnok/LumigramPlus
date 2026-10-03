using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Documents;
using Lumigram.Audio;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace LumigramPlus.App
{
    /// <summary>
    /// How far one of our own messages has got.
    ///
    /// Two signals exist, not three. Telegram has no delivery receipt a client can
    /// read: a message is on the server or it is not, and separately the dialog
    /// carries the highest of our messages the other side has read. So the middle
    /// state of the usual three-mark scheme has nothing to drive it, and what is
    /// shown is the pair Telegram's own clients show - on its way, sent, read.
    /// </summary>
    public enum MessageTicks
    {
        /// <summary>Put on screen, not yet acknowledged by the server.</summary>
        Sending = 0,

        /// <summary>The server has it and gave it an id.</summary>
        Sent = 1,

        /// <summary>The other side's read marker has passed it.</summary>
        Read = 2,
    }

    /// <summary>
    /// One chat in the forward list.
    ///
    /// Its own class because the list binds to it, and binding on this platform
    /// reads properties only. DialogEntry carries its title as a plain field, so the
    /// list it used to be bound to drew every row empty: the chats were there and
    /// tappable, with nothing on them to say which was which.
    /// </summary>
    public sealed class ForwardTarget
    {
        public string Title { get; set; }
        public DialogEntry Entry { get; set; }
    }

    /// <summary>One reaction as drawn on a bubble.</summary>
    public sealed class ReactionChip
    {
        /// <summary>The bubble it sits on, so a tap knows which message it is for.</summary>
        public MessageItem Owner { get; set; }

        /// <summary>Exactly as Telegram spells it - this is what a tap sends.</summary>
        public string Emoticon { get; set; }

        public int Count { get; set; }
        public bool Mine { get; set; }

        /// <summary>What is drawn, which for the heart is not what is sent.</summary>
        public string Display { get { return ReactionSet.Display(Emoticon); } }

        public Brush EmojiBrush { get { return ReactionSet.BrushFor(Emoticon); } }

        /// <summary>
        /// Ours stands out: a lighter chip and a bold count. Both rather than one,
        /// because either alone is easy to miss on a phone held at arm's length.
        /// </summary>
        public Brush ChipBackground { get { return Mine ? MineBrush : OtherBrush; } }

        public Windows.UI.Text.FontWeight CountWeight
        {
            get { return Mine ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.Normal; }
        }

        private static readonly Brush MineBrush =
            new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));

        private static readonly Brush OtherBrush =
            new SolidColorBrush(Color.FromArgb(55, 255, 255, 255));
    }

    /// <summary>One message in a conversation.</summary>
    public sealed class MessageItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        public int Id { get; set; }
        private string _text;

        /// <summary>
        /// The message's words.
        ///
        /// Raises changes, because it can change on screen - an edit, or a send
        /// that failed - and the alternative used to be rebuilding the whole list
        /// to pick the new text up, which threw the reader back to the top.
        ///
        /// The body itself is built from inlines rather than bound, so a change here
        /// does not redraw it on its own; ConversationPage.RefreshBody does that.
        /// </summary>
        public string Text
        {
            get { return _text; }
            set
            {
                _text = value;
                Raise("Text");
                Raise("TextVisibility");
                Raise("CopyVisibility");
                Raise("EditVisibility");
            }
        }
        public string Time { get; set; }
        public bool Out { get; set; }

        /// <summary>The message this one answers, or 0. See TextMessage.ReplyToId.</summary>
        public int ReplyToId { get; set; }

        /// <summary>The part of the original the sender quoted, or null.</summary>
        public string ReplyQuote { get; set; }

        /// <summary>The original is in another chat, so there is nothing to look up.</summary>
        public bool ReplyElsewhere { get; set; }

        private string _replyText;

        /// <summary>
        /// The quotation line, worked out by the page - which is the one that knows
        /// the other messages and who wrote them. Raises changes, because it starts
        /// as "reply" for an original not yet loaded and is filled in when it is.
        /// </summary>
        public string ReplyText
        {
            get { return _replyText; }
            set
            {
                if (_replyText == value) return;

                _replyText = value;
                Raise("ReplyText");
                Raise("ReplyVisibility");
            }
        }

        public Visibility ReplyVisibility
        {
            get { return string.IsNullOrEmpty(_replyText) ? Visibility.Collapsed : Visibility.Visible; }
        }

        private List<MessageReaction> _reactions = new List<MessageReaction>();
        private List<ReactionChip> _chips = new List<ReactionChip>();

        /// <summary>
        /// The reactions on this message.
        ///
        /// Raises changes, and the chips are rebuilt from it each time, because it
        /// moves while the message is on screen: our own tap, and other people's
        /// reactions arriving with the refresh.
        /// </summary>
        public List<MessageReaction> Reactions
        {
            get { return _reactions; }
            set
            {
                _reactions = value ?? new List<MessageReaction>();

                var chips = new List<ReactionChip>();
                foreach (MessageReaction r in _reactions)
                {
                    if (r.Count <= 0) continue;

                    chips.Add(new ReactionChip
                    {
                        Owner = this,
                        Emoticon = r.Emoticon,
                        Count = r.Count,
                        Mine = r.Mine,
                    });
                }

                _chips = chips;
                Raise("Reactions");
                Raise("ReactionChips");
                Raise("ReactionsVisibility");
            }
        }

        public List<ReactionChip> ReactionChips { get { return _chips; } }

        public Visibility ReactionsVisibility
        {
            get { return _chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>The reaction this account has on it, or null.</summary>
        public string MyReaction
        {
            get
            {
                foreach (MessageReaction r in _reactions)
                    if (r.Mine) return r.Emoticon;

                return null;
            }
        }

        /// <summary>
        /// A short fingerprint of the reactions, so the refresh can tell whether
        /// anything changed without rebuilding every bubble's chips every few
        /// seconds.
        /// </summary>
        public static string Signature(List<MessageReaction> reactions)
        {
            if (reactions == null || reactions.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            foreach (MessageReaction r in reactions)
                sb.Append(r.Emoticon).Append(r.Count).Append(r.Mine ? "*" : "").Append('|');

            return sb.ToString();
        }

        private MessageTicks _ticks;

        /// <summary>
        /// How far this message has got, for our own messages.
        ///
        /// Raises changes, because it moves while the message is on screen: twice
        /// for one sent here - acknowledged, then read - and once for an older one
        /// when the other side catches up.
        /// </summary>
        public MessageTicks Ticks
        {
            get { return _ticks; }
            set
            {
                if (_ticks == value) return;

                _ticks = value;
                Raise("Ticks");
                Raise("TickVisibility");
                Raise("SingleTickVisibility");
                Raise("DoubleTickVisibility");
                Raise("TickBrush");
            }
        }

        /// <summary>
        /// Only on our own messages. What the other side has read of *their* own
        /// messages is not something we are told, and would be no use if we were.
        /// </summary>
        public Visibility TickVisibility
        {
            get { return Out ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>One mark: on its way, or sent.</summary>
        public Visibility SingleTickVisibility
        {
            get
            {
                return Out && Ticks != MessageTicks.Read ? Visibility.Visible
                                                         : Visibility.Collapsed;
            }
        }

        /// <summary>Two marks: read.</summary>
        public Visibility DoubleTickVisibility
        {
            get
            {
                return Out && Ticks == MessageTicks.Read ? Visibility.Visible
                                                         : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Green once read, white once sent, and faint until then.
        ///
        /// The colours are chosen against the outgoing bubble, which is the only
        /// background these are ever drawn on.
        /// </summary>
        public Brush TickBrush
        {
            get
            {
                if (Ticks == MessageTicks.Read) return ReadTickBrush;
                return Ticks == MessageTicks.Sent ? SentTickBrush : SendingTickBrush;
            }
        }

        private static readonly Brush SendingTickBrush =
            new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));

        private static readonly Brush SentTickBrush =
            new SolidColorBrush(Colors.White);

        // A light green rather than a dark one: these sit on the blue outgoing
        // bubble, and a saturated green against that blue is two strong colours
        // of similar darkness, which is hard to read at this size.
        private static readonly Brush ReadTickBrush =
            new SolidColorBrush(Color.FromArgb(255, 124, 252, 138));
        public string SenderName { get; set; }

        /// <summary>The attachment, when there is one.</summary>
        public MediaInfo Media { get; set; }

        private string _mediaNote;
        public string MediaNote
        {
            get { return _mediaNote; }
            set { _mediaNote = value; Raise("MediaNote"); Raise("MediaNoteVisibility"); }
        }

        private ImageSource _picture;
        public ImageSource Picture
        {
            get { return _picture; }
            set
            {
                _picture = value;
                Raise("Picture");
                Raise("PictureVisibility");
                Raise("PlayMarkVisibility");
                Raise("MediaNoteVisibility");
            }
        }

        /// <summary>Shown over a video preview, so a still reads as playable.</summary>
        public Visibility PlayMarkVisibility
        {
            get
            {
                bool video = Media != null && Media.Kind == MediaKind.Video;
                return video && _picture != null ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public Visibility PictureVisibility
        {
            get { return _picture == null ? Visibility.Collapsed : Visibility.Visible; }
        }

        /// <summary>
        /// The caption describing an attachment, shown only while there is no
        /// picture to show instead.
        /// </summary>
        /// <summary>
        /// Whether the caption under an attachment is shown.
        ///
        /// A photo speaks for itself once drawn, so its caption goes. Everything else
        /// keeps one: a video preview is not the video, and hiding the caption once a
        /// thumbnail appeared is what made every message about downloading, saving
        /// and failing invisible.
        /// </summary>
        public Visibility MediaNoteVisibility
        {
            get
            {
                if (string.IsNullOrEmpty(_mediaNote)) return Visibility.Collapsed;

                bool photo = Media != null && Media.Kind == MediaKind.Photo;
                if (photo && _picture != null) return Visibility.Collapsed;

                return Visibility.Visible;
            }
        }

        /// <summary>True once the attachment itself - not its preview - is on disk.</summary>
        public bool Downloaded { get; set; }

        /// <summary>Hidden when a message is only an attachment.</summary>
        public Visibility TextVisibility
        {
            get { return string.IsNullOrEmpty(Text) ? Visibility.Collapsed : Visibility.Visible; }
        }

        /// <summary>
        /// Whether the gallery is a sensible destination for this attachment.
        ///
        /// Pictures and videos have a place there; a document does not, and offering
        /// to put one in the gallery is offering something that cannot work.
        /// </summary>
        public Visibility GalleryVisibility
        {
            get
            {
                bool media = Media != null &&
                             (Media.Kind == MediaKind.Photo || Media.Kind == MediaKind.Video);

                return media ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Whether this message can be acted on at all.
        ///
        /// A bubble put on screen before the server answered has no id yet, and
        /// every one of these actions names the message by its id. Offering them
        /// would be offering something that cannot be carried out.
        /// </summary>
        public Visibility ActionVisibility
        {
            get { return Id != 0 ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>Same, for the one action that reaches the other side too.</summary>
        public Visibility RevokeVisibility
        {
            get { return ActionVisibility; }
        }

        /// <summary>
        /// Whether this message can be edited.
        ///
        /// Our own, sent, and with words in it. The server also has a time limit and
        /// will refuse an old one - that is left to the server rather than guessed
        /// at here, because the limit differs between a chat and a channel and
        /// hiding the item on a wrong guess is worse than a refusal that says why.
        ///
        /// Attachments are left out: this edits text, and offering "edit" on a
        /// photo would promise something that does not happen.
        /// </summary>
        public Visibility EditVisibility
        {
            get
            {
                return Out && Id != 0 && !string.IsNullOrEmpty(Text)
                    ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Whether this message has an address worth copying.
        ///
        /// A property of the chat rather than of the message - only a channel or
        /// supergroup gives its messages t.me addresses - so the page works it out
        /// once and stamps it on each message as it is added. A one-to-one
        /// conversation has no links to its messages at all, and offering one that
        /// quietly is not a link is worse than offering none.
        /// </summary>
        public bool CanLink { get; set; }

        public Visibility LinkVisibility
        {
            get
            {
                return CanLink && Id != 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Whether there is anything to copy.
        ///
        /// The same condition as showing the text at all, which includes an
        /// attachment's caption - that is text too, and the only text a picture
        /// message has. An id is not needed: copying never reaches the server, so
        /// it works on a message still on its way out.
        /// </summary>
        public Visibility CopyVisibility
        {
            get { return TextVisibility; }
        }

        /// <summary>
        /// Whether there is a file behind this message.
        ///
        /// Plain text used to offer "save as", which was the whole menu at the time
        /// - a long press on a sentence proposed writing it to a file.
        /// </summary>
        public Visibility SaveVisibility
        {
            get { return Media != null ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>Guards against a second fetch while one is already running.</summary>
        public bool Loading { get; set; }

        /// <summary>True for the oldest message that had not been read.</summary>
        public bool FirstUnread { get; set; }

        public Visibility UnreadMarkVisibility
        {
            get { return FirstUnread ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility SenderVisibility
        {
            get
            {
                return string.IsNullOrEmpty(SenderName) ? Visibility.Collapsed
                                                        : Visibility.Visible;
            }
        }

        /// <summary>Ours on the right, theirs on the left - the usual convention.</summary>
        public HorizontalAlignment Align
        {
            get { return Out ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
        }

        /// <summary>
        /// Leaves room on the opposite side so a bubble cannot run the full width,
        /// which is what makes the two directions readable at a glance.
        /// </summary>
        public Thickness Margin
        {
            get { return Out ? new Thickness(48, 3, 0, 3) : new Thickness(0, 3, 48, 3); }
        }

        public Brush Background
        {
            get
            {
                return Out
                    ? new SolidColorBrush(Color.FromArgb(255, 0, 120, 215))
                    : new SolidColorBrush(Color.FromArgb(255, 60, 60, 60));
            }
        }
    }

    /// <summary>
    /// One conversation: what has been said, and saying something.
    ///
    /// History only, with no live updates yet - a message sent from elsewhere will
    /// not appear until the page is reopened. That needs the update machinery, which
    /// is a piece of work in its own right and not a reason to withhold a
    /// conversation that can be read and replied to.
    /// </summary>
    public sealed partial class ConversationPage : Page, IFileContinuation
    {
        /// <summary>How much history to fetch. A screenful several times over.</summary>
        private const int HistoryCount = 30;

        /// <summary>
        /// Whether there is anything before the oldest message on screen.
        ///
        /// The server's own answer, read off each page - see History.HasOlder -
        /// rather than assumed from the count, so the button goes away at the real
        /// beginning of a conversation and not at an arbitrary one.
        /// </summary>
        private bool _hasOlder;

        /// <summary>A page of older messages is on its way; a second tap waits for it.</summary>
        private bool _loadingOlder;

        private readonly ObservableCollection<MessageItem> _messages =
            new ObservableCollection<MessageItem>();

        /// <summary>
        /// Who has written here, by id.
        ///
        /// A message names its sender by id and nothing else, so without the users
        /// vector that arrives alongside the history, every group message is
        /// anonymous.
        /// </summary>
        private readonly Dictionary<long, PeerInfo> _senders = new Dictionary<long, PeerInfo>();

        private DialogItem _peer;
        private byte[] _inputPeer;

        /// <summary>
        /// The forum topic being read, or 0 for an ordinary chat.
        ///
        /// A topic is not a peer, so this does not go into _inputPeer: the peer is
        /// still the whole forum, and the topic narrows what is asked of it. Every
        /// use of it is a branch rather than a different value, which is why it is
        /// kept beside the peer rather than folded into it.
        /// </summary>
        private int _topicId;

        /// <summary>
        /// The newest message already read here, whether "here" is a chat or one
        /// topic of a forum.
        /// </summary>
        private int _readTo;

        /// <summary>
        /// A message to open at, set when the page was reached by following a link.
        ///
        /// Changes which history is fetched rather than only where the list sits:
        /// the message may be thousands back, and the newest thirty would not
        /// contain it.
        /// </summary>
        private int _focusId;

        /// <summary>Whether messages here have t.me addresses at all.</summary>
        private bool _canLink;

        /// <summary>
        /// The newest message of ours the other side has read.
        ///
        /// Comes from the chat list on the way in, and is refreshed from the dialog
        /// poll that already runs - rather than by asking for it, which would be a
        /// second request every few seconds for one integer.
        /// </summary>
        private int _readOutbox;

        public ConversationPage()
        {
            InitializeComponent();
            MessageList.ItemsSource = _messages;

            // Take charge of what happens when the keyboard appears.
            //
            // By default the system resizes the window to keep the focused box in
            // view, and that resize is why the first tap on Send did nothing: the
            // tap dismisses the keyboard, the page re-lays out, and the button moves
            // out from under the finger before the press is delivered. Saying the
            // element is already in view stops the automatic resize; the compose bar
            // is lifted above the keyboard here instead, so nothing moves when it
            // goes away.
            InputPane pane = InputPane.GetForCurrentView();
            pane.Showing += OnKeyboardShowing;
            pane.Hiding += OnKeyboardHiding;

            ArmBarButtons();
        }

        /// <summary>
        /// Makes the buttons on the reply and edit bars answer the first tap.
        ///
        /// They sit inside the page, just above the message box, which is exactly
        /// where the note on the app bar says a button cannot be: with the keyboard
        /// up, a tap takes focus from the box, the keyboard goes, the page drops
        /// back down, and the button has moved out from under the finger before it
        /// is released - so Click never comes and the first press only closes the
        /// keyboard.
        ///
        /// So these act when the finger lands rather than when it lifts. That is
        /// before focus moves and before anything re-lays out, which makes it
        /// immune to the whole sequence rather than racing it.
        ///
        /// handledEventsToo, because a Button marks its own PointerPressed handled
        /// and a handler attached the ordinary way would never be called.
        /// </summary>
        private void ArmBarButtons()
        {
            CancelReplyButton.AddHandler(UIElement.PointerPressedEvent,
                new Windows.UI.Xaml.Input.PointerEventHandler(
                    delegate { ClearReply(); }), true);

            CancelEditButton.AddHandler(UIElement.PointerPressedEvent,
                new Windows.UI.Xaml.Input.PointerEventHandler(
                    delegate { ClearEdit(true); }), true);

            // Send rather than EditAsync directly: the send path already knows to
            // edit while the bar is up, and has the guard against a second press.
            ConfirmEditButton.AddHandler(UIElement.PointerPressedEvent,
                new Windows.UI.Xaml.Input.PointerEventHandler(
                    delegate { Send(); }), true);
        }

        private void OnKeyboardShowing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            args.EnsuredFocusedElementInView = true;
            Root.Margin = new Thickness(0, 0, 0, args.OccludedRect.Height);

            // The list has just lost half its height. Editing means looking at one
            // message, so that is the one kept in view rather than whatever the
            // shrink happened to leave there.
            if (_editingItem != null) BringIntoView(_editingItem);
        }

        private void OnKeyboardHiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            args.EnsuredFocusedElementInView = true;
            Root.Margin = new Thickness(0);
        }

        /// <summary>
        /// How often to look for new messages while the chat is on screen.
        ///
        /// Polling, not the update stream. The proper machinery keeps a connection
        /// listening and applies pushed updates; this is a fraction of the work and
        /// makes the difference between a conversation that is live and one that has
        /// to be reopened to see a reply. It runs only while the page is showing.
        /// </summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        /// <summary>Ticks between reads of the chat list, for notifications.</summary>
        private const int ObserveEvery = 4;

        private int _ticks;

        private DispatcherTimer _poll;

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            Notifications.OpenPeerId = 0;
            Notifications.Banner -= OnBanner;

            if (_poll != null) _poll.Stop();
        }

        /// <summary>
        /// Shows an arriving message from another chat.
        ///
        /// Reading one conversation is exactly when a message elsewhere is worth
        /// knowing about, and the toast raised for it is not shown to the app that
        /// raised it - so without this the case would be silent.
        /// </summary>
        private void OnBanner(string title, string body, DialogEntry dialog)
        {
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

            _bannerTimer.Stop();
            _bannerTimer.Start();
        }

        private DispatcherTimer _bannerTimer;

        /// <summary>
        /// Keeps the notifier fed while a conversation is open.
        ///
        /// The chat list is not polling now, so nothing else is watching the other
        /// chats - and those are the ones worth announcing.
        /// </summary>
        private async void ObserveOtherChats()
        {
            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, 20, 0, 0, null, TelegramService.Info);

                Notifications.Observe(page.Entries);

                // This chat is in that list, and its entry carries how much of ours
                // the other side has read - so the marks follow a request already
                // being made rather than one of their own.
                foreach (DialogEntry d in page.Entries)
                {
                    if (d.PeerId != _peer.PeerId || d.Kind != _peer.Kind) continue;

                    AdvanceReadMarks(d.ReadOutboxMaxId);
                    break;
                }
            }
            catch (Exception)
            {
                // The next tick will try again.
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Two shapes, because an ordinary chat has nothing to say beyond which
            // chat it is. Navigating with a bare DialogItem still means "all of it".
            var request = e.Parameter as ConversationRequest;

            _peer = request != null ? request.Peer : e.Parameter as DialogItem;
            _topicId = request != null ? request.TopicId : 0;

            if (_peer == null)
            {
                SetBusy(false, "No chat to open.");
                return;
            }

            _readTo = request != null ? request.ReadInboxMaxId : _peer.ReadInboxMaxId;
            _focusId = request != null ? request.FocusMessageId : 0;

            // Only a channel or supergroup. A basic group cannot be made public, and
            // a one-to-one conversation has no addressable messages at all.
            _canLink = _peer.Kind == "channel";
            _readOutbox = _peer.ReadOutboxMaxId;

            // Messages arriving for the chat on screen must not announce themselves.
            Notifications.OpenPeerId = _peer.PeerId;
            Notifications.Banner -= OnBanner;
            Notifications.Banner += OnBanner;

            CallButton.Visibility = _peer.Kind == "user"
                ? Visibility.Visible : Visibility.Collapsed;

            CollectRecordedVideo();

            // The topic's name, not the forum's: the forum's is one tap back, and
            // which thread this is is the thing that is not otherwise on screen.
            PeerTitle.Text = _topicId != 0
                ? (request.TopicTitle ?? "topic")
                : (_peer.Title ?? "chat");
            _inputPeer = Messages.InputPeerFor(_peer.Kind, _peer.PeerId, _peer.AccessHash);

            Load();
        }

        private async void Load()
        {
            SetBusy(true, "Loading messages...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.History history = await ReadHistoryAsync(client, _focusId);

                foreach (KeyValuePair<long, PeerInfo> pair in history.Senders)
                    _senders[pair.Key] = pair.Value;

                _messages.Clear();

                // getHistory returns newest first; a conversation reads oldest first.
                for (int i = history.Messages.Count - 1; i >= 0; i--)
                    Add(history.Messages[i]);

                ShowOlder(history.HasOlder(HistoryCount));

                SetBusy(false, _messages.Count == 0 ? "No messages yet." : "");

                if (_focusId != 0) ShowFocused();
                else OpenWhereReadingStopped();

                MarkRead(client, history.Messages);
                FetchMissingReplies();
                StartPolling();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not load: " + (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>
        /// The newest messages, from the whole chat or from one topic.
        ///
        /// getHistory cannot express a topic: the topic is not a peer, so asking for
        /// the forum's history returns every thread at once. getReplies asks for the
        /// thread hanging off one message, and a topic *is* that message.
        /// </summary>
        private async Task<Messages.History> ReadHistoryAsync(MtprotoClient client)
        {
            return await ReadHistoryAsync(client, 0);
        }

        /// <summary>
        /// The messages to show: the newest, or a window around one in particular.
        ///
        /// <paramref name="around"/> is set only on the first load after following a
        /// link. The refresh that follows every few seconds asks for the newest, as
        /// always - a poll that kept re-centring on an old message would drag the
        /// conversation back there every tick.
        /// </summary>
        private async Task<Messages.History> ReadHistoryAsync(MtprotoClient client, int around)
        {
            if (_topicId != 0)
            {
                if (around != 0)
                    return await Topics.GetHistoryAroundAsync(
                        client, _inputPeer, _topicId, around, HistoryCount,
                        TelegramService.Info);

                return await Topics.GetHistoryAsync(client, _inputPeer, _topicId,
                                                    HistoryCount, TelegramService.Info);
            }

            if (around != 0)
                return await Messages.GetHistoryAroundAsync(
                    client, _inputPeer, around, HistoryCount, TelegramService.Info);

            return await Messages.GetHistoryAsync(client, _inputPeer, HistoryCount,
                                                  TelegramService.Info);
        }

        /// <summary>
        /// Tells the server the chat has been read.
        ///
        /// The unread count belongs to the server, not to this app. Zeroing the badge
        /// locally makes it come back on the next chat list load, and leaves the chat
        /// showing unread on every other device the account is signed in on.
        ///
        /// Channels take a different method to everything else, which Core handles -
        /// sending the wrong one fails silently and the badge simply never clears.
        /// </summary>
        private async void MarkRead(MtprotoClient client, List<TextMessage> history)
        {
            int maxId = 0;
            foreach (TextMessage m in history) if (m.Id > maxId) maxId = m.Id;

            if (maxId == 0) return;

            try
            {
                // readHistory on a forum clears every topic in it at once, so
                // reading one thread would silence the badges on all the others.
                if (_topicId != 0)
                {
                    await Topics.ReadAsync(client, _inputPeer, _topicId, maxId,
                                           TelegramService.Info);
                }
                else
                {
                    await Messages.MarkReadAsync(client, _peer.Kind, _peer.PeerId,
                                                 _peer.AccessHash, maxId,
                                                 TelegramService.Info);

                    // So the list shows the change on the way back without a round
                    // trip. Not done for a topic: the count on the chat list row is
                    // the forum's, and one topic's worth of it is not all of it.
                    _peer.UnreadCount = 0;
                }
            }
            catch (Exception)
            {
                // Not worth interrupting reading for; the badge stays until next time.
            }
        }

        /// <summary>
        /// Positions the conversation on the first message not yet read.
        ///
        /// The read marker comes from the chat list and is read before the chat is
        /// marked read, which is the only moment it still means anything - opening
        /// the chat is what moves it.
        ///
        /// Falls back to the newest message: with nothing unread, or with the unread
        /// run older than the history fetched, the bottom is where a reader wants to
        /// be anyway.
        /// </summary>
        private void OpenWhereReadingStopped()
        {
            int readTo = _readTo;

            MessageItem first = null;
            foreach (MessageItem item in _messages)
            {
                if (item.Out || item.Id == 0 || item.Id <= readTo) continue;

                first = item;
                break;
            }

            if (first == null) { ScrollToEnd(); return; }

            first.FirstUnread = true;

            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low,
                delegate
                {
                    MessageList.UpdateLayout();
                    MessageList.ScrollIntoView(first);
                });
        }

        private void StartPolling()
        {
            if (_poll != null) { _poll.Start(); return; }

            _poll = new DispatcherTimer();
            _poll.Interval = PollInterval;
            // The history is read on every tick; the other chats are not. This
            // one exists only so notifications keep arriving while a conversation is
            // open, and doing it five seconds apart was two requests every five
            // seconds from one screen - enough to trip FLOOD_WAIT on its own.
            _poll.Tick += delegate
            {
                Refresh();

                if (++_ticks % ObserveEvery == 0) ObserveOtherChats();
            };
            _poll.Start();
        }

        /// <summary>
        /// Adds anything that has arrived since the last look.
        ///
        /// Matched by message id rather than by count or by text: the same history
        /// is fetched every time, so anything else either re-adds what is already
        /// shown or merges two identical messages that were genuinely sent twice.
        /// </summary>
        private async void Refresh()
        {
            // Parked on a message somebody linked to. The window on screen is from
            // the middle of the conversation, and the refresh asks for the newest -
            // so adding what it returns would staple the last few messages onto the
            // end of an old screenful with everything in between missing. Reading
            // here stays still until the reader asks to go back to the end.
            if (_focusId != 0) return;

            if (_refreshing) return;
            _refreshing = true;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.History history = await ReadHistoryAsync(client);

                foreach (KeyValuePair<long, PeerInfo> pair in history.Senders)
                    _senders[pair.Key] = pair.Value;

                bool added = false;

                for (int i = history.Messages.Count - 1; i >= 0; i--)
                {
                    TextMessage m = history.Messages[i];
                    if (m.Id == 0) continue;

                    if (Contains(m.Id))
                    {
                        // Already on screen; only its reactions can have moved.
                        UpdateReactions(m);
                        continue;
                    }

                    if (Adopt(m)) continue;

                    Add(m);
                    added = true;
                }

                if (added)
                {
                    ScrollToEnd();
                    MarkRead(client, history.Messages);
                    FetchMissingReplies();
                }
            }
            catch (RpcException ex) when (TelegramService.IsAuthGone(ex))
            {
                if (_poll != null) _poll.Stop();

                await TelegramService.AuthGoneAsync();

                Frame.Navigate(typeof(QrLoginPage));
                Frame.BackStack.Clear();
            }
            catch (Exception)
            {
                // A failed poll is not worth reporting: the next one is five seconds
                // away, and a connection blip would otherwise paint an error over a
                // conversation that is fine.
            }
            finally
            {
                _refreshing = false;
            }
        }

        private bool _refreshing;

        /// <summary>
        /// Gives an arriving copy of our own message to the bubble already waiting
        /// for it.
        ///
        /// Covers the gap between showing a sent message at once and learning what
        /// id the server gave it: a poll landing in between finds a message with an
        /// id that matches nothing on screen, and draws it a second time. Only our
        /// own messages with no id yet are considered, and each is claimed once, so
        /// the same text sent twice on purpose still shows as two.
        /// </summary>
        private bool Adopt(TextMessage m)
        {
            if (!m.Out || m.Id == 0) return false;

            foreach (MessageItem item in _messages)
            {
                if (!item.Out || item.Id != 0) continue;
                if (item.Text != (m.Text ?? "")) continue;

                // An attachment is not adopted into a bubble that has none: the
                // placeholder cannot show a picture, so claiming the message would
                // quietly swallow it.
                if (m.Media != null && item.Media == null) return false;

                item.Id = m.Id;
                return true;
            }

            return false;
        }

        private bool Contains(int id)
        {
            foreach (MessageItem item in _messages)
                if (item.Id == id) return true;

            return false;
        }

        private void Add(TextMessage m)
        {
            Add(m, -1);
        }

        /// <summary>
        /// Puts a message on screen at <paramref name="index"/>, or at the end when
        /// that is negative.
        ///
        /// The end is where new messages go; the front is where older ones go. Both
        /// need everything else Add does - the sender, the media caption, fetching a
        /// preview - so it is one method with a place to put it rather than two.
        /// </summary>
        private void Add(TextMessage m, int index)
        {
            // Worked out once per chat rather than per message; see
            // MessageItem.CanLink.
            var item = new MessageItem
            {
                Id = m.Id,
                Text = m.Text ?? "",
                Time = m.DateUtc.ToLocalTime().ToString("HH:mm"),
                Out = m.Out,
                SenderName = SenderFor(m),
                Media = m.Media,
                CanLink = _canLink,
                Ticks = m.Out && m.Id != 0 && m.Id <= _readOutbox
                    ? MessageTicks.Read : MessageTicks.Sent,
                Reactions = m.Reactions,
                ReplyToId = m.ReplyToId,
                ReplyQuote = m.ReplyQuote,
                ReplyElsewhere = m.ReplyElsewhere,
            };

            if (m.Media != null)
            {
                // Photos are cheap enough to fetch on sight when the setting allows
                // it. Everything else waits to be asked for: a document can be any
                // size at all, and this is a phone on a phone network.
                // Audio says what a tap will do, which is more than loading: it
                // downloads and then plays.
                item.MediaNote = m.Media.Describe() +
                    (m.Media.Kind == MediaKind.Audio ? " - tap to play" : " - tap to load");
            }

            if (index < 0 || index > _messages.Count) _messages.Add(item);
            else _messages.Insert(index, item);

            // From what is on screen now. One whose original is further back is
            // filled in by FetchMissingReplies once the page is in.
            ResolveReply(item);

            // Anything already fetched is shown without asking; anything else is
            // fetched now or on tap, depending on the setting.
            if (m.Media != null && m.Media.Kind == MediaKind.Photo)
            {
                if (AppSettings.AutoLoadPhotos) LoadMedia(item);
                else ShowIfCached(item);
            }
            else if (m.Media != null && m.Media.Kind == MediaKind.Video)
            {
                // Whether the video itself is already here decides what a tap does,
                // and the preview says nothing about that.
                MarkIfDownloaded(item);
                // The preview always, whatever the setting says about photos: it is
                // a few kilobytes, and without it a video is a line of text.
                LoadThumb(item);
            }
        }

        /// <summary>
        /// Fetches the small still that stands in for a video.
        ///
        /// Not gated on the photo setting: this is kilobytes rather than megabytes,
        /// and the alternative is a video that looks like a sentence.
        /// </summary>
        // ---- links in a message ----------------------------------------------

        /// <summary>
        /// Builds a message's body out of plain runs and tappable links.
        ///
        /// Called on Loaded and again on every DataContextChanged, because the list
        /// recycles its containers: a container that has already been Loaded once is
        /// handed the next message without being Loaded again, and a body built only
        /// on Loaded would be whatever message happened to be there first.
        /// </summary>
        private void MessageText_Loaded(object sender, RoutedEventArgs e)
        {
            var block = sender as TextBlock;
            if (block == null) return;

            // Removed first: Loaded can run more than once for one container, and
            // subscribing twice would rebuild the body twice on every reuse.
            block.DataContextChanged -= MessageText_DataContextChanged;
            block.DataContextChanged += MessageText_DataContextChanged;

            FillInlines(block);
        }

        private void MessageText_DataContextChanged(FrameworkElement sender,
                                                    DataContextChangedEventArgs args)
        {
            FillInlines(sender as TextBlock);
        }

        private void FillInlines(TextBlock block)
        {
            if (block == null) return;

            var item = block.DataContext as MessageItem;

            block.Inlines.Clear();
            if (item == null || string.IsNullOrEmpty(item.Text)) return;

            foreach (TextPart part in Links.Split(item.Text))
            {
                if (!part.IsLink)
                {
                    block.Inlines.Add(new Run { Text = part.Text });
                    continue;
                }

                var hyperlink = new Hyperlink();
                hyperlink.Inlines.Add(new Run { Text = part.Text });

                // A light blue that reads on both bubble colours. The accent the
                // platform would use by default is the same blue as an outgoing
                // bubble, which makes a link in one of those invisible.
                hyperlink.Foreground = LinkBrush;

                // NavigateUri is deliberately not set. Setting it hands the address
                // to the browser before this gets a say, which is the whole thing
                // being avoided for t.me links.
                string url = part.Url;
                hyperlink.Click += delegate { OpenLink(url); };

                block.Inlines.Add(hyperlink);
            }
        }

        private static readonly Brush LinkBrush =
            new SolidColorBrush(Color.FromArgb(255, 160, 212, 255));

        /// <summary>
        /// Follows a tapped link: inside the app when it points into Telegram, and
        /// out to the browser when it does not.
        /// </summary>
        private async void OpenLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return;

            Lumigram.Tl.TelegramLink link = TelegramLinks.Parse(url);

            if (link == null)
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri(url)); }
                catch (Exception) { SetBusy(false, "That link could not be opened."); }

                return;
            }

            SetBusy(true, "Opening...");

            try
            {
                string trouble = await LinkRouter.OpenAsync(Frame, link);
                SetBusy(false, trouble ?? "");
            }
            catch (RpcException ex) when (TelegramService.IsAuthGone(ex))
            {
                if (_poll != null) _poll.Stop();

                await TelegramService.AuthGoneAsync();
                Frame.Navigate(typeof(QrLoginPage));
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not open that link: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>
        /// Puts the message a link pointed at on screen, and marks it.
        ///
        /// The marker matters: the window is centred on the message, so it arrives
        /// in the middle of a screenful of other messages with nothing to say which
        /// one was meant. The same bar the unread line uses, for the same reason.
        /// </summary>
        private void ShowFocused()
        {
            MessageItem target = null;
            foreach (MessageItem item in _messages)
            {
                if (item.Id != _focusId) continue;

                target = item;
                break;
            }

            if (target == null)
            {
                // The server answered with a window that does not contain it - a
                // deleted message, most often. The conversation is still the right
                // place to be, so this says so rather than failing.
                SetBusy(false, "That message is no longer there.");
                ScrollToEnd();
                return;
            }

            target.FirstUnread = true;

            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low,
                delegate
                {
                    MessageList.UpdateLayout();
                    MessageList.ScrollIntoView(target);
                });
        }

        private async void LoadThumb(MessageItem item)
        {
            try
            {
                Uri uri = await MediaCache.GetThumbAsync(item.Media);
                if (uri == null) return;

                item.Picture = new Windows.UI.Xaml.Media.Imaging.BitmapImage(uri);
                item.MediaNote = item.Media.Describe() + " - double tap to play";
            }
            catch (Exception)
            {
                // No preview; the caption still says what it is.
            }
        }

        /// <summary>Notes that the file is already cached, without drawing anything.</summary>
        private async void MarkIfDownloaded(MessageItem item)
        {
            try
            {
                Windows.Storage.StorageFile file = await MediaCache.FindAsync(item.Media);
                if (file == null) return;

                item.Downloaded = true;
                item.MediaNote = item.Media.Describe() + " - double tap to play";
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Shows a picture that is already on disk, without fetching.</summary>
        private async void ShowIfCached(MessageItem item)
        {
            try
            {
                Windows.Storage.StorageFile file = await MediaCache.FindAsync(item.Media);
                if (file == null) return;

                item.Downloaded = true;
                item.Picture = new Windows.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appdata:///local/media/" + file.Name));
            }
            catch (Exception)
            {
                // Nothing to show; the caption stays.
            }
        }

        /// <summary>
        /// Fetches the picture behind a message and shows it.
        ///
        /// Progress is reported into the caption rather than a bar: on a slow
        /// connection a photo is tens of seconds, and a message that says nothing
        /// for that long reads as broken.
        /// </summary>
        private void LoadMedia(MessageItem item)
        {
            var ignored = LoadMediaAsync(item);
        }

        /// <summary>
        /// The awaitable form.
        ///
        /// Needed because a double tap on a video that has not been downloaded has
        /// to fetch it and then play it, and an async void cannot be waited for.
        /// </summary>
        private async Task LoadMediaAsync(MessageItem item)
        {
            if (item.Media == null || item.Loading) return;

            // Guarded on having the file, not on having something to look at. A
            // video shows its thumbnail in Picture, so treating that as "already
            // downloaded" meant the video itself could never be fetched - the tap
            // that should have started it returned here instead, and every path
            // behind it went quiet.
            if (item.Downloaded) return;

            item.Loading = true;
            string caption = item.MediaNote;

            try
            {
                Uri uri = await MediaCache.GetAsync(item.Media,
                    delegate (long got, long total)
                    {
                        if (total <= 0) return;

                        var ignored = Dispatcher.RunAsync(
                            Windows.UI.Core.CoreDispatcherPriority.Low,
                            delegate
                            {
                                // Checked here rather than before dispatching. These
                                // run at low priority and queue up, so the last of
                                // them lands after the download has finished and
                                // overwrites whatever the finished state said - the
                                // note stuck at "loading 100%", and with it the only
                                // thing on screen saying the video could be played.
                                if (item.Downloaded) return;

                                item.MediaNote = "loading " + (got * 100 / total) + "%";
                            });
                    });

                if (uri == null)
                {
                    item.MediaNote = caption;
                    return;
                }

                item.Downloaded = true;

                if (item.Media.Kind == MediaKind.Photo)
                {
                    item.Picture = new Windows.UI.Xaml.Media.Imaging.BitmapImage(uri);
                    return;
                }

                if (item.Media.Kind == MediaKind.Video)
                {
                    item.MediaNote = item.Media.Describe() + " - double tap to play";
                    return;
                }

                if (item.Media.Kind == MediaKind.Audio)
                {
                    item.MediaNote = item.Media.Describe() + " - tap to play";
                    return;
                }

                // Nothing to draw for a document, so say it is here and what can be
                // done with it - otherwise a finished download looks like nothing
                // happened.
                item.MediaNote = item.Media.Describe() + " - hold to save";
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                item.MediaNote = "could not load: " + (rpc != null ? rpc.ErrorType : ex.Message);
            }
            finally
            {
                item.Loading = false;
            }
        }

        /// <summary>
        /// Copies a picture into the phone's own gallery.
        ///
        /// SavedPictures rather than the app's storage: a picture kept where only
        /// this app can see it has not really been saved. Requires the pictures
        /// library capability, without which the folder simply is not there.
        /// </summary>
        private async void SavePicture(MessageItem item)
        {
            if (item == null || item.Media == null) return;

            bool photo = item.Media.Kind == MediaKind.Photo;
            bool video = item.Media.Kind == MediaKind.Video;

            if (!photo && !video)
            {
                // Should not be reachable - the menu entry is hidden for documents -
                // but a guess about where a file belongs is worth refusing rather
                // than acting on.
                item.MediaNote = "use save as... for this file";
                return;
            }

            try
            {
                Windows.Storage.StorageFile file = await MediaCache.FindAsync(item.Media);

                if (file == null)
                {
                    item.MediaNote = "load it first, then save";
                    return;
                }

                item.MediaNote = "saving...";

                // The camera roll for video, not the videos library.
                //
                // VideosLibrary is readable and not writable on this platform, so a
                // copy into it is refused outright - the same wall the Silverlight
                // client hit. The camera roll is where the phone itself puts video,
                // and it accepts one from an app.
                Windows.Storage.StorageFolder target = photo
                    ? Windows.Storage.KnownFolders.SavedPictures
                    : Windows.Storage.KnownFolders.CameraRoll;

                await file.CopyAsync(target, "lumigram-" + file.Name,
                                     Windows.Storage.NameCollisionOption.ReplaceExisting);

                item.MediaNote = "saved to the gallery";
            }
            catch (Exception ex)
            {
                // Naming the way out, not only the failure: "save as..."
                // writes wherever the user chooses and is not subject to the
                // library permissions this path depends on.
                item.MediaNote = "not saved (" + ex.Message.Trim() + ") - try save as...";
            }
        }

        /// <summary>
        /// Opens the message menu on a press and hold.
        ///
        /// An attached flyout does not show itself - something has to ask - and the
        /// Started phase is the moment to do it: waiting for Completed means the
        /// menu appears only after the finger lifts.
        /// </summary>
        private void Message_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;

            var element = sender as FrameworkElement;
            if (element == null) return;

            Windows.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(element);
        }

        /// <summary>Opens a downloaded picture full screen, where it can be zoomed.</summary>
        /// <summary>
        /// Opens an attachment full screen: a video plays, a picture zooms.
        ///
        /// A video that has not been downloaded is fetched first - the preview says
        /// nothing about whether the file itself is here, so a double tap on one
        /// would otherwise do nothing at all.
        /// </summary>
        private async void Media_DoubleTap(object sender,
                                           Windows.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            var item = element.DataContext as MessageItem;
            if (item == null || item.Media == null) return;

            // Without this an MP3 fell through to the picture viewer below.
            if (item.Media.Kind == MediaKind.Audio)
            {
                PlayAudio(item);
                return;
            }

            Windows.Storage.StorageFile file = await MediaCache.FindAsync(item.Media);

            if (file == null)
            {
                if (item.Media.Kind != MediaKind.Video) return;

                item.MediaNote = "loading video...";
                await LoadMediaAsync(item);

                file = await MediaCache.FindAsync(item.Media);
                if (file == null) return;
            }

            if (item.Media.Kind == MediaKind.Video)
            {
                Frame.Navigate(typeof(VideoPage), file.Name);
                return;
            }

            Frame.Navigate(typeof(ImageViewerPage), file.Name);
        }

        /// <summary>The message the next send will answer, or 0 for none.</summary>
        private int _replyTo;

        /// <summary>
        /// The message being edited, or 0 when the box sends a new one.
        ///
        /// What makes the send button mean two different things, which is why it is
        /// cleared on every path out of an edit - cancelled, sent, or failed.
        /// </summary>
        private int _editingId;

        /// <summary>
        /// The bubble being edited, so it can be kept on screen above the bar.
        /// </summary>
        private MessageItem _editingItem;

        private void ReplyMenu_Click(object sender, RoutedEventArgs e)
        {
            MessageItem item = MenuItem(sender);
            if (item == null) return;

            _replyTo = item.Id;

            // The bubble may be an attachment with no words in it, in which case the
            // note is the only description of it there is.
            string summary = !string.IsNullOrEmpty(item.Text) ? item.Text
                           : !string.IsNullOrEmpty(item.MediaNote) ? item.MediaNote
                           : "message";

            ReplyText.Text = summary;
            ReplyBar.Visibility = Visibility.Visible;

            ComposeBox.Focus(FocusState.Programmatic);
        }

        // ---- reply quotations -------------------------------------------------

        /// <summary>
        /// Originals fetched by id because they were not among the loaded messages.
        /// Kept for as long as the conversation is open, so scrolling back past a
        /// reply does not ask for its original again.
        /// </summary>
        private readonly Dictionary<int, TextMessage> _originals =
            new Dictionary<int, TextMessage>();

        /// <summary>
        /// Ids already asked for, found or not. One that came back deleted, or not
        /// at all, is not asked for again on every refresh.
        /// </summary>
        private readonly HashSet<int> _askedFor = new HashSet<int>();

        /// <summary>How long a quotation line is allowed to be before it is cut.</summary>
        private const int QuoteLength = 60;

        /// <summary>
        /// Works out a message's quotation line from whatever is known about the
        /// original: on screen, fetched, or not yet either.
        ///
        /// What the sender quoted wins over the start of the original, because it is
        /// the part they meant.
        /// </summary>
        private void ResolveReply(MessageItem item)
        {
            if (item == null) return;

            if (item.ReplyElsewhere)
            {
                item.ReplyText = "reply to another chat" +
                    (string.IsNullOrEmpty(item.ReplyQuote) ? "" : ": " + Snip(item.ReplyQuote));
                return;
            }

            if (item.ReplyToId == 0)
            {
                item.ReplyText = null;
                return;
            }

            MessageItem shown = FindItem(item.ReplyToId);
            if (shown != null)
            {
                item.ReplyText = Who(shown.Out, shown.SenderName) + ": " +
                                 Snip(item.ReplyQuote ?? Body(shown.Text, shown.Media));
                return;
            }

            TextMessage fetched;
            if (_originals.TryGetValue(item.ReplyToId, out fetched))
            {
                // messageEmpty comes back for a message that has been deleted.
                if (fetched.Note == "empty")
                {
                    item.ReplyText = "reply to a deleted message";
                    return;
                }

                item.ReplyText = Who(fetched.Out, SenderFor(fetched)) + ": " +
                                 Snip(item.ReplyQuote ?? Body(fetched.Text, fetched.Media));
                return;
            }

            // Not here yet. What the sender quoted, if anything, is still better
            // than a bare "reply" while the original is fetched.
            item.ReplyText = string.IsNullOrEmpty(item.ReplyQuote)
                ? "reply"
                : "reply: " + Snip(item.ReplyQuote);
        }

        /// <summary>
        /// Fetches, in one request, every original that replies on screen are still
        /// waiting for - and fills in any that have since turned up on screen.
        ///
        /// One request per page rather than one per reply, and never the same id
        /// twice: this runs after every refresh, and a conversation full of replies
        /// to old messages would otherwise ask for the same ones every few seconds.
        /// </summary>
        private async void FetchMissingReplies()
        {
            var missing = new List<int>();

            foreach (MessageItem item in _messages)
            {
                if (item.ReplyToId == 0 || item.ReplyElsewhere) continue;

                if (FindItem(item.ReplyToId) != null || _originals.ContainsKey(item.ReplyToId))
                {
                    ResolveReply(item);
                    continue;
                }

                if (_askedFor.Contains(item.ReplyToId) || missing.Contains(item.ReplyToId))
                    continue;

                missing.Add(item.ReplyToId);
            }

            if (missing.Count == 0 || _peer == null) return;

            foreach (int id in missing) _askedFor.Add(id);

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.History found = await Messages.GetByIdAsync(
                    client, _peer.Kind, _peer.PeerId, _peer.AccessHash, missing,
                    TelegramService.Info);

                foreach (KeyValuePair<long, PeerInfo> pair in found.Senders)
                    _senders[pair.Key] = pair.Value;

                foreach (TextMessage m in found.Messages)
                    if (m.Id != 0) _originals[m.Id] = m;

                foreach (MessageItem item in _messages)
                    if (missing.Contains(item.ReplyToId)) ResolveReply(item);
            }
            catch (Exception)
            {
                // The line stays "reply". Not worth interrupting reading for, and
                // the ids are not asked for again until the conversation is reopened.
            }
        }

        /// <summary>
        /// Goes to the original of a reply, when it is on screen.
        ///
        /// When it is not, says where it is rather than doing nothing: further back,
        /// where "older messages" reaches, or gone.
        /// </summary>
        private void ReplyQuote_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var item = element == null ? null : element.DataContext as MessageItem;
            if (item == null || item.ReplyToId == 0) return;

            // Kept from the bubble, whose own taps are for pictures and files.
            e.Handled = true;

            MessageItem original = FindItem(item.ReplyToId);
            if (original != null)
            {
                BringIntoView(original);
                return;
            }

            SetBusy(false, _hasOlder
                ? "The original is further back - \"older messages\" at the top reaches it."
                : "The original is no longer in this chat.");
        }

        /// <summary>"You" for our own, the sender's name in a group, the chat's in one-to-one.</summary>
        private string Who(bool own, string senderName)
        {
            if (own) return "You";
            if (!string.IsNullOrEmpty(senderName)) return senderName;

            return _peer != null && _peer.Title != null ? _peer.Title : "";
        }

        /// <summary>The words of a message, or what it is when it has none.</summary>
        private static string Body(string text, MediaInfo media)
        {
            if (!string.IsNullOrEmpty(text)) return text;
            if (media != null) return media.Describe();

            return "message";
        }

        /// <summary>One line of at most QuoteLength characters.</summary>
        private static string Snip(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            string one = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return one.Length <= QuoteLength ? one : one.Substring(0, QuoteLength - 3) + "...";
        }

        // ---- reactions --------------------------------------------------------

        /// <summary>The message the picker is open for.</summary>
        private MessageItem _reactingTo;

        /// <summary>
        /// Messages with a reaction on its way to the server.
        ///
        /// The refresh leaves these alone until the answer comes back. Otherwise a
        /// poll that happened to land between the tap and the reply would put the
        /// old reactions back, and the chip would flicker off and on again.
        /// </summary>
        private readonly HashSet<int> _reacting = new HashSet<int>();

        private async void ReactMenu_Click(object sender, RoutedEventArgs e)
        {
            MessageItem item = MenuItem(sender);
            if (item == null || item.Id == 0) return;

            _reactingTo = item;
            ReactGrid.Children.Clear();
            RemoveReactionButton.Visibility = item.MyReaction != null
                ? Visibility.Visible : Visibility.Collapsed;
            ReactPanel.Visibility = Visibility.Visible;

            List<string> offered;
            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();
                offered = await ReactionSet.OfferedAsync(client);
            }
            catch (Exception)
            {
                offered = new List<string>(ReactionSet.Old);
            }

            // The panel may have been closed while the list was being fetched.
            if (_reactingTo != item) return;

            string mine = item.MyReaction;

            foreach (string emoji in offered)
            {
                string chosen = emoji;

                var face = new TextBlock
                {
                    Text = ReactionSet.Display(emoji),
                    Foreground = ReactionSet.BrushFor(emoji),
                    IsColorFontEnabled = true,
                    FontSize = 30,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var button = new Button
                {
                    Content = face,
                    Width = 60,
                    Height = 60,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(emoji == mine ? 2 : 0),
                };

                button.Click += delegate { CloseReact(); React(item, chosen); };
                ReactGrid.Children.Add(button);
            }
        }

        private void RemoveReaction_Click(object sender, RoutedEventArgs e)
        {
            MessageItem item = _reactingTo;
            CloseReact();

            if (item != null && item.MyReaction != null) React(item, item.MyReaction);
        }

        private void CancelReact_Click(object sender, RoutedEventArgs e)
        {
            CloseReact();
        }

        private void CloseReact()
        {
            _reactingTo = null;
            ReactPanel.Visibility = Visibility.Collapsed;
            ReactGrid.Children.Clear();
        }

        /// <summary>A tap on a chip in a bubble: the same as choosing it in the picker.</summary>
        private void ReactionChip_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var chip = element == null ? null : element.DataContext as ReactionChip;
            if (chip == null || chip.Owner == null || chip.Owner.Id == 0) return;

            // Kept from reaching the bubble, whose own tap handling is for pictures.
            e.Handled = true;

            React(chip.Owner, chip.Emoticon);
        }

        /// <summary>
        /// Puts a reaction on, moves it, or takes it off.
        ///
        /// Choosing the reaction already ours takes it off - Telegram's own rule,
        /// and the only way off for anyone who reaches it by tapping the chip.
        /// Choosing another moves ours there: without Premium an account has one
        /// reaction per message, so a second does not join the first, it replaces
        /// it.
        ///
        /// Shown at once and sent after, then corrected by what the server says
        /// the reactions now are. If it refuses, the old ones are put back and the
        /// reason is said - most often a chat that has limited which reactions it
        /// takes.
        /// </summary>
        private async void React(MessageItem item, string emoticon)
        {
            if (item == null || item.Id == 0 || string.IsNullOrEmpty(emoticon)) return;
            if (_reacting.Contains(item.Id)) return;

            List<MessageReaction> before = item.Reactions;
            string mine = item.MyReaction;
            string sending = emoticon == mine ? null : emoticon;

            item.Reactions = Toggled(before, mine, sending);
            _reacting.Add(item.Id);

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                List<MessageReaction> now = await Reactions.SendAsync(
                    client, _inputPeer, item.Id, sending, TelegramService.Info);

                if (now != null) item.Reactions = now;
                SetBusy(false, "");
            }
            catch (Exception ex)
            {
                item.Reactions = before;

                var rpc = ex as RpcException;
                string trouble = rpc != null ? rpc.ErrorType : ex.Message;

                if (trouble != null && trouble.Contains("REACTION_INVALID"))
                    trouble = "This chat does not take that reaction.";
                else if (trouble != null && trouble.Contains("REACTION_EMPTY"))
                    trouble = "";
                else
                    trouble = "Reaction not sent: " + trouble;

                SetBusy(false, trouble);
            }
            finally
            {
                _reacting.Remove(item.Id);
            }
        }

        /// <summary>
        /// What the reactions will be once ours has moved, worked out here so the
        /// bubble can show it before the server answers.
        /// </summary>
        private static List<MessageReaction> Toggled(List<MessageReaction> before,
                                                     string removing, string adding)
        {
            var after = new List<MessageReaction>();
            bool added = false;

            foreach (MessageReaction r in before)
            {
                var copy = new MessageReaction { Emoticon = r.Emoticon, Count = r.Count, Mine = r.Mine };

                if (removing != null && copy.Emoticon == removing && copy.Mine)
                {
                    copy.Count--;
                    copy.Mine = false;
                }

                if (adding != null && copy.Emoticon == adding)
                {
                    copy.Count++;
                    copy.Mine = true;
                    added = true;
                }

                if (copy.Count > 0) after.Add(copy);
            }

            if (adding != null && !added)
                after.Add(new MessageReaction { Emoticon = adding, Count = 1, Mine = true });

            return after;
        }

        /// <summary>
        /// Brings an on-screen message's reactions up to date from the refresh.
        ///
        /// Compared first, so a poll that changes nothing - nearly all of them -
        /// touches nothing, and the chips of a bubble are only rebuilt when
        /// somebody actually reacted.
        /// </summary>
        private void UpdateReactions(TextMessage m)
        {
            if (_reacting.Contains(m.Id)) return;

            MessageItem item = FindItem(m.Id);
            if (item == null) return;

            if (MessageItem.Signature(item.Reactions) == MessageItem.Signature(m.Reactions)) return;

            item.Reactions = m.Reactions;
        }

        /// <summary>
        /// Puts a message into the box to be changed.
        ///
        /// Replying is cancelled on the way in. The box can only do one thing at a
        /// time, and an armed reply left showing while an edit is in progress would
        /// be a promise about where the next text goes that is no longer true.
        /// </summary>
        private void EditMenu_Click(object sender, RoutedEventArgs e)
        {
            MessageItem item = MenuItem(sender);
            if (item == null || item.Id == 0 || string.IsNullOrEmpty(item.Text)) return;

            ClearReply();

            _editingId = item.Id;
            _editingItem = item;
            EditText.Text = item.Text;
            EditBar.Visibility = Visibility.Visible;

            // The keyboard is about to take half the screen, and without this the
            // message being changed is usually underneath it. Done again once the
            // keyboard is up - see OnKeyboardShowing - because that is when the
            // space it has to fit in is actually known.
            BringIntoView(item);

            ComposeBox.Text = item.Text;
            ComposeBox.Focus(FocusState.Programmatic);

            // At the end rather than selected: an edit is usually an addition or a
            // typo, and arriving with the whole message selected means the first
            // keystroke throws it away.
            ComposeBox.SelectionStart = ComposeBox.Text.Length;
            ComposeBox.SelectionLength = 0;
        }

        /// <summary>
        /// Leaves edit mode.
        ///
        /// <paramref name="emptyBox"/> is false when the text has already been taken
        /// and sent: clearing it then would be clearing a box the send path has
        /// already emptied, and doing it twice is how a retry loses the message.
        /// </summary>
        private void ClearEdit(bool emptyBox)
        {
            _editingId = 0;
            _editingItem = null;
            EditBar.Visibility = Visibility.Collapsed;

            if (emptyBox) ComposeBox.Text = "";
        }

        private void ClearReply()
        {
            _replyTo = 0;
            ReplyBar.Visibility = Visibility.Collapsed;
        }

        private async void DeleteMenu_Click(object sender, RoutedEventArgs e)
        {
            await DeleteAsync(MenuItem(sender), false);
        }

        private async void DeleteAllMenu_Click(object sender, RoutedEventArgs e)
        {
            await DeleteAsync(MenuItem(sender), true);
        }

        /// <summary>
        /// Removes a message here and, when asked, everywhere.
        ///
        /// The bubble goes only after the server agrees. "Delete for everyone" is
        /// refused for messages that are too old or were sent by someone else, and a
        /// message that vanished locally but not remotely would come back on the
        /// next refresh with no explanation.
        /// </summary>
        private async Task DeleteAsync(MessageItem item, bool revoke)
        {
            if (item == null || item.Id == 0) return;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Messages.DeleteMessagesAsync(
                    client, _peer.Kind, _peer.PeerId, _peer.AccessHash,
                    new List<int> { item.Id }, revoke, TelegramService.Info);

                _messages.Remove(item);

                // A reply aimed at a message that no longer exists would be refused
                // by the server, so it is dropped with it.
                if (_replyTo == item.Id) ClearReply();

                SetBusy(false, revoke ? "Deleted for everyone." : "Deleted.");
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not delete: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        /// <summary>The message a menu item belongs to.</summary>
        private static MessageItem MenuItem(object sender)
        {
            var element = sender as FrameworkElement;
            return element == null ? null : element.DataContext as MessageItem;
        }

        /// <summary>
        /// Puts this message's t.me address where it can be copied.
        ///
        /// Through the same panel as copying text, because the platform has no
        /// clipboard and the system's text selection is the only way to reach one -
        /// see the panel's own note in the XAML.
        /// </summary>
        private void CopyLinkMenu_Click(object sender, RoutedEventArgs e)
        {
            MessageItem item = MenuItem(sender);
            if (item == null || item.Id == 0 || _peer == null) return;

            // A channel with a username gets the readable form; one without gets the
            // /c/ form, which only its own members can open - that is a property of
            // the chat, and it is still the link its members would share.
            string url = TelegramLinks.ForMessage(
                _peer.Username,
                _peer.Kind == "channel" ? _peer.PeerId : 0,
                item.Id,
                _topicId);

            if (url == null)
            {
                SetBusy(false, "Messages in this chat have no link.");
                return;
            }

            ShowCopyPanel(url);
        }

        // ---- copying ---------------------------------------------------------

        /// <summary>
        /// Hands a message's text to the system's text selection, which is the only
        /// thing on this platform that can put anything on the clipboard.
        ///
        /// Windows Phone 8.1 has no Clipboard class - see the panel's own comment in
        /// the XAML. So there is nothing to copy *to* here: the text goes into a box,
        /// the box is selected, and the copy button the system draws over a selection
        /// does the work. The other half of the pair needs no code at all, because
        /// pasting into the message box is the keyboard's own paste key.
        /// </summary>
        private void CopyMenu_Click(object sender, RoutedEventArgs e)
        {
            MessageItem item = MenuItem(sender);
            if (item == null || string.IsNullOrEmpty(item.Text)) return;

            ShowCopyPanel(item.Text);
        }

        /// <summary>
        /// Shows text selected and ready for the system's copy button.
        ///
        /// Shared by copying a message and copying its link: the two differ only in
        /// what goes in the box.
        /// </summary>
        private void ShowCopyPanel(string text)
        {
            CopyBox.Text = text ?? "";
            CopyPanel.Visibility = Visibility.Visible;

            // Focus first: a selection in an unfocused box draws no handles and no
            // copy button, so selecting before focusing selects nothing visible.
            CopyBox.Focus(FocusState.Programmatic);
            CopyBox.SelectAll();
        }

        private void CloseCopy_Click(object sender, RoutedEventArgs e)
        {
            CloseCopy();
        }

        private void CloseCopy()
        {
            CopyPanel.Visibility = Visibility.Collapsed;

            // Emptied rather than left: the box holds the last message copied, and
            // the page outlives the panel.
            CopyBox.Text = "";
        }

        // ---- forwarding ------------------------------------------------------

        /// <summary>The message waiting for a destination.</summary>
        private MessageItem _forwarding;

        private async void ForwardMenu_Click(object sender, RoutedEventArgs e)
        {
            MessageItem item = MenuItem(sender);
            if (item == null || item.Id == 0) return;

            _forwarding = item;

            ForwardList.ItemsSource = null;
            ForwardPanel.Visibility = Visibility.Visible;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                // The recent chats, which is what a forward is nearly always aimed
                // at. A full searchable list is a larger feature than this menu.
                Messages.DialogPage page = await Messages.GetDialogPageAsync(
                    client, 40, 0, 0, null, TelegramService.Info);

                var targets = new List<ForwardTarget>();
                foreach (DialogEntry d in page.Entries)
                    targets.Add(new ForwardTarget { Title = d.Title, Entry = d });

                ForwardList.ItemsSource = targets;
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not list chats: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));

                CloseForward();
            }
        }

        private void CancelForward_Click(object sender, RoutedEventArgs e)
        {
            CloseForward();
        }

        private void CloseForward()
        {
            _forwarding = null;
            ForwardPanel.Visibility = Visibility.Collapsed;
            ForwardList.ItemsSource = null;
        }

        private async void ForwardTarget_Click(object sender, ItemClickEventArgs e)
        {
            var chosen = e.ClickedItem as ForwardTarget;
            DialogEntry target = chosen == null ? null : chosen.Entry;
            MessageItem item = _forwarding;

            CloseForward();

            if (target == null || item == null) return;

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Messages.ForwardMessagesAsync(
                    client, TelegramService.Crypto, _inputPeer,
                    new List<int> { item.Id },
                    Messages.InputPeerFor(target), TelegramService.Info);

                SetBusy(false, "Forwarded to " + (target.Title ?? "chat") + ".");
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not forward: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        private void SaveMenu_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            SavePicture(element.DataContext as MessageItem);
        }

        private void Media_Tap(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            var item = element.DataContext as MessageItem;
            if (item == null || item.Media == null) return;

            // A location has nothing to fetch - the coordinates arrived with the
            // message - so the tap goes straight to showing it.
            if (item.Media.Kind == MediaKind.Location)
            {
                ShowOnMap(item.Media);
                return;
            }

            // Audio has nothing to preview, so there is no accident to guard
            // against: one tap downloads it if need be, and then plays it.
            if (item.Media.Kind == MediaKind.Audio)
            {
                PlayAudio(item);
                return;
            }

            // A single tap fetches. Playing is on the double tap, so that tapping a
            // preview to see it bigger never starts a video by accident.
            LoadMedia(item);
        }

        /// <summary>
        /// Opens a received location in the phone's own maps app.
        ///
        /// No map is drawn here. Rendering one would mean tiles, a network budget
        /// and a control this app does not have, where the phone already ships
        /// something better - with the user's own maps, their downloaded regions and
        /// directions from where they are standing. Handing the coordinates over is
        /// one line.
        ///
        /// The invariant culture matters more than it looks: the URI wants a decimal
        /// point, and on a phone set to a language that writes 55,75 the default
        /// formatting produces a pair of coordinates the maps app reads as four.
        /// </summary>
        private async void ShowOnMap(MediaInfo location)
        {
            try
            {
                string at = location.Latitude.ToString(
                                System.Globalization.CultureInfo.InvariantCulture) +
                            "~" +
                            location.Longitude.ToString(
                                System.Globalization.CultureInfo.InvariantCulture);

                var uri = new Uri("bingmaps:?cp=" + at + "&lvl=16");

                if (!await Windows.System.Launcher.LaunchUriAsync(uri))
                    SetBusy(false, "Nothing on this phone opens maps.");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Cannot open maps: " + ex.Message);
            }
        }

        /// <summary>
        /// Plays a video that is already here, or fetches whatever is not.
        ///
        /// One tap does the obvious thing at each stage: the first fetches, and the
        /// next plays. Two gestures for the two halves of one action would be
        /// something to learn rather than something to use.
        /// </summary>
        /// <summary>
        /// Downloads an audio file if it is not here yet, and then plays it.
        ///
        /// The player is only opened if this conversation is still what is on
        /// screen when the download finishes. A long track on a slow connection can
        /// take a minute, and by then the reader may be somewhere else entirely -
        /// pulling them into a player from wherever they went would be the app
        /// acting on a tap they have forgotten making.
        /// </summary>
        private async void PlayAudio(MessageItem item)
        {
            if (item == null || item.Media == null) return;

            Windows.Storage.StorageFile file = await MediaCache.FindAsync(item.Media);

            if (file == null)
            {
                await LoadMediaAsync(item);
                file = await MediaCache.FindAsync(item.Media);

                // The caption already says why - see LoadMediaAsync.
                if (file == null) return;

                if (Frame == null || Frame.Content != this) return;
            }

            Frame.Navigate(typeof(AudioPage), new AudioRequest
            {
                CachedName = file.Name,
                // The title on one line and the artist on the next, so not the
                // combined "Artist - Title" the bubble uses.
                Title = !string.IsNullOrEmpty(item.Media.Title) ? item.Media.Title
                                                                : (item.Media.FileName ?? "audio"),
                Performer = item.Media.Performer,
            });
        }

        private async void PlayOrLoad(MessageItem item)
        {
            if (item.Media != null && item.Media.Kind == MediaKind.Video)
            {
                Windows.Storage.StorageFile file = await MediaCache.FindAsync(item.Media);

                if (file != null)
                {
                    Frame.Navigate(typeof(VideoPage), file.Name);
                    return;
                }
            }

            LoadMedia(item);
        }

        private string SenderFor(TextMessage m)
        {
            if (m.Out || _peer.Kind == "user") return "";
            if (m.FromId == 0) return "";

            PeerInfo sender;
            if (_senders.TryGetValue(m.FromId, out sender)) return sender.Name;

            return "user " + m.FromId;
        }

        private void Compose_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;

            e.Handled = true;
            Send();
        }

        private void Call_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(CallPage), new CallRequest { Peer = _peer });
        }

        private void Send_Click(object sender, RoutedEventArgs e)
        {
            Send();
        }

        /// <summary>
        /// Offers any file to send.
        ///
        /// Every type, not only pictures: sending arbitrary files is one of the two
        /// reasons this client exists. The picker suspends the app, so the answer
        /// arrives in FilePicked rather than here.
        /// </summary>
        // ---- location --------------------------------------------------------

        /// <summary>
        /// Sends where the phone is.
        ///
        /// A high accuracy fix with a short patience: a location message is worth
        /// waiting a few seconds for and not worth waiting a minute for, and a fix
        /// good to a street is good enough to say where you are.
        /// </summary>
        private async void AttachLocation_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true, "Finding your location...");

            try
            {
                var locator = new Windows.Devices.Geolocation.Geolocator();
                locator.DesiredAccuracy =
                    Windows.Devices.Geolocation.PositionAccuracy.High;

                Windows.Devices.Geolocation.Geoposition position =
                    await locator.GetGeopositionAsync(
                        TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(20));

                Windows.Devices.Geolocation.Geocoordinate at = position.Coordinate;

                int accuracy = (int)at.Accuracy;

                MtprotoClient client = await TelegramService.ConnectAsync();

                await Upload.SendLocationAsync(
                    client, TelegramService.Crypto, _inputPeer,
                    at.Point.Position.Latitude, at.Point.Position.Longitude,
                    accuracy, TelegramService.Info, _topicId);

                SetBusy(false, "");
                Refresh();
            }
            catch (UnauthorizedAccessException)
            {
                // The one failure worth naming: it is fixed in the phone's settings
                // and nowhere in this app.
                SetBusy(false, "Location is turned off for this app. "
                             + "Settings, then location.");
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not send your location: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        // ---- video messages --------------------------------------------------

        private void AttachVideo_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VideoCapturePage));
        }

        /// <summary>
        /// Sends a video recorded on the capture page, if one came back.
        ///
        /// Picked up on the way in rather than handed over, because navigating back
        /// returns nothing. Taken and cleared in one step, so a failure to send does
        /// not leave the same recording waiting on every later visit.
        /// </summary>
        private async void CollectRecordedVideo()
        {
            Windows.Storage.StorageFile file = VideoCapture.Recorded;
            if (file == null) return;

            VideoCapture.Recorded = null;

            await SendFileAsync(file);
        }

        // ---- voice messages --------------------------------------------------

        private VoiceMessageRecorder _voiceRecorder;
        private DispatcherTimer _recordingTimer;

        private async void AttachVoice_Click(object sender, RoutedEventArgs e)
        {
            if (_voiceRecorder != null) return;

            try
            {
                var recorder = new VoiceMessageRecorder();
                await recorder.StartAsync();

                _voiceRecorder = recorder;

                RecordingPanel.Visibility = Visibility.Visible;
                ComposeBox.Visibility = Visibility.Collapsed;

                if (_recordingTimer == null)
                {
                    _recordingTimer = new DispatcherTimer();
                    _recordingTimer.Interval = TimeSpan.FromMilliseconds(200);
                    _recordingTimer.Tick += RecordingTick;
                }

                _recordingTimer.Start();
            }
            catch (Exception ex)
            {
                _voiceRecorder = null;
                SetBusy(false, "Could not start recording: " + ex.Message);
            }
        }

        private void RecordingTick(object sender, object e)
        {
            VoiceMessageRecorder recorder = _voiceRecorder;
            if (recorder == null) return;

            int seconds = recorder.Seconds;

            RecordingTime.Text = (seconds / 60) + ":" + (seconds % 60).ToString("00");
            RecordingLevel.Width = recorder.TakeLevel() * 110 / 100;

            // Stopped for the user rather than left running: everything is held in
            // memory until it is sent.
            if (seconds >= VoiceMessageRecorder.MaxSeconds)
                StopRecording_Click(null, null);
        }

        /// <summary>
        /// Ends the recording, encodes it and sends it.
        ///
        /// Encoding happens off the UI thread. It is a second or two of Opus at
        /// whatever complexity the phone can manage, and doing it on the thread that
        /// draws would freeze the app for exactly as long as the message is.
        /// </summary>
        private async void StopRecording_Click(object sender, RoutedEventArgs e)
        {
            VoiceMessageRecorder recorder = _voiceRecorder;
            if (recorder == null) return;

            _voiceRecorder = null;

            if (_recordingTimer != null) _recordingTimer.Stop();

            RecordingPanel.Visibility = Visibility.Collapsed;
            ComposeBox.Visibility = Visibility.Visible;

            try
            {
                short[] pcm = await recorder.StopAsync();
                recorder.Dispose();

                // Under a second is a slip of the finger rather than a message.
                if (pcm.Length < VoiceMessageRecorder.Rate / 2)
                {
                    SetBusy(false, "Too short to send.");
                    return;
                }

                SetBusy(true, "Encoding...");

                EncodedVoice encoded = await Task.Run(delegate
                {
                    return VoiceEncoder.Encode(pcm, pcm.Length, VoiceMessageRecorder.Rate);
                });

                SetBusy(true, "Sending voice message...");

                MtprotoClient client = await TelegramService.ConnectAsync();

                int at = 0;
                byte[] file = encoded.File;

                UploadedFile uploaded = await Upload.SendFileAsync(
                    client, TelegramService.Crypto, "voice.ogg", file.Length,
                    delegate (byte[] part)
                    {
                        int taken = Math.Min(part.Length, file.Length - at);
                        Buffer.BlockCopy(file, at, part, 0, taken);
                        at += taken;
                        return taken;
                    },
                    null, TelegramService.Info);

                await Upload.SendVoiceAsync(
                    client, TelegramService.Crypto, _inputPeer, uploaded,
                    encoded.DurationSeconds, encoded.Waveform, TelegramService.Info,
                    _topicId);

                SetBusy(false, "");
                Refresh();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Could not send the voice message: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        private void AttachFile_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
            picker.SuggestedStartLocation =
                Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;

            // The picker refuses to open with no filter at all; * is how "anything"
            // is spelled.
            picker.FileTypeFilter.Add("*");

            picker.PickSingleFileAndContinue();
        }

        /// <summary>The chosen file comes back here, after the app has been reactivated.</summary>
        public async void FilePicked(Windows.ApplicationModel.Activation.FileOpenPickerContinuationEventArgs args)
        {
            if (args == null || args.Files == null || args.Files.Count == 0) return;

            Windows.Storage.StorageFile file = args.Files[0];
            await SendFileAsync(file);
        }

        /// <summary>
        /// Uploads a file and sends it.
        ///
        /// Sent as a photo when it is one and as a document otherwise - the
        /// difference is what other clients show inline versus offer as a download,
        /// and getting it wrong makes a picture arrive as an attachment nobody can
        /// see without saving it first.
        /// </summary>
        private async Task SendFileAsync(Windows.Storage.StorageFile file)
        {
            var pending = new MessageItem
            {
                Id = 0,
                Text = "",
                Time = DateTime.Now.ToString("HH:mm"),
                Out = true,
                MediaNote = "sending " + file.Name + "...",
                CanLink = _canLink,
                Ticks = MessageTicks.Sending,
            };

            _messages.Add(pending);
            ScrollToEnd();

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                // Read from the file as the upload consumes it. Loading it whole
                // first works for a photo and is exactly what makes sending a video
                // fail on a phone with little memory to spare.
                using (System.IO.Stream stream = await file.OpenStreamForReadAsync())
                {
                    UploadedFile uploaded = await Upload.SendFileAsync(
                        client, TelegramService.Crypto, file.Name, stream.Length,
                        delegate (byte[] buffer) { return stream.Read(buffer, 0, buffer.Length); },
                        delegate (long done, long total)
                        {
                            if (total <= 0) return;

                            var ignored = Dispatcher.RunAsync(
                                Windows.UI.Core.CoreDispatcherPriority.Low,
                                delegate
                                {
                                    pending.MediaNote = "sending " + (done * 100 / total) + "%";
                                });
                        },
                        TelegramService.Info);

                    string type = file.ContentType ?? "";

                    if (type.StartsWith("image/"))
                    {
                        pending.Id = await Upload.SendPhotoAsync(
                            client, TelegramService.Crypto, _inputPeer, uploaded, "",
                            TelegramService.Info, _topicId);
                    }
                    else if (type.StartsWith("video/"))
                    {
                        // Size and duration are what make a video arrive as one.
                        // Sent as a plain document it is offered as a file to
                        // download rather than something to play, on every client.
                        Windows.Storage.FileProperties.VideoProperties video =
                            await file.Properties.GetVideoPropertiesAsync();

                        pending.Id = await Upload.SendVideoAsync(
                            client, TelegramService.Crypto, _inputPeer, uploaded, "",
                            type, (int)video.Duration.TotalSeconds,
                            (int)video.Width, (int)video.Height, TelegramService.Info,
                            _topicId);
                    }
                    else
                    {
                        pending.Id = await Upload.SendDocumentAsync(
                            client, TelegramService.Crypto, _inputPeer, uploaded, "",
                            type.Length > 0 ? type : "application/octet-stream",
                            TelegramService.Info, _topicId);
                    }
                }

                // The placeholder goes, and the real message takes its place.
                //
                // A pending bubble carries no MediaInfo - there is nothing to
                // download until the server has the file - so leaving it on screen
                // means the picture never appears. Dropping it lets the refresh add
                // the message properly, with the attachment attached.
                _messages.Remove(pending);
                Refresh();
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                pending.MediaNote = "not sent: " + (rpc != null ? rpc.ErrorType : ex.Message);
            }
        }

        /// <summary>
        /// Offers to save any attachment wherever the user likes.
        ///
        /// The gallery is right for a picture and useless for anything else, so this
        /// is the answer for documents - and it is the other half of what WinRT was
        /// ported for.
        /// </summary>
        private async void SaveAsMenu_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;

            var item = element.DataContext as MessageItem;
            if (item == null || item.Media == null) return;

            Windows.Storage.StorageFile cached = await MediaCache.FindAsync(item.Media);
            if (cached == null)
            {
                item.MediaNote = "load it first, then save";
                return;
            }

            _saving = item;

            var picker = new Windows.Storage.Pickers.FileSavePicker();

            string name = item.Media.FileName;
            if (string.IsNullOrEmpty(name)) name = cached.Name;

            // The extension comes from the file's own name, and the cached copy's
            // only when it has none. Taking it from the cache is what produced
            // "song.mp3.bin" and "report.pdf.bin": every document is cached as
            // ".bin", and the picker appends the extension it is given to a
            // suggested name that already had the real one.
            string extension = System.IO.Path.GetExtension(name);
            if (string.IsNullOrEmpty(extension))
                extension = System.IO.Path.GetExtension(cached.Name);

            picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(name);
            picker.DefaultFileExtension = extension;

            picker.FileTypeChoices.Add("file", new List<string> { extension });

            picker.PickSaveFileAndContinue();
        }

        private MessageItem _saving;

        /// <summary>The chosen destination comes back here.</summary>
        public async void SaveLocationPicked(Windows.ApplicationModel.Activation.FileSavePickerContinuationEventArgs args)
        {
            MessageItem item = _saving;
            _saving = null;

            if (args == null || args.File == null || item == null) return;

            try
            {
                Windows.Storage.StorageFile cached = await MediaCache.FindAsync(item.Media);
                if (cached == null) return;

                Windows.Storage.Streams.IBuffer read =
                    await Windows.Storage.FileIO.ReadBufferAsync(cached);

                await Windows.Storage.FileIO.WriteBufferAsync(args.File, read);

                item.MediaNote = "saved as " + args.File.Name;
            }
            catch (Exception ex)
            {
                item.MediaNote = "not saved: " + ex.Message;
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        /// <summary>
        /// Guards against the same message being sent twice - a fast double tap, or
        /// Enter and the button together.
        /// </summary>
        private bool _sending;

        private async void Send()
        {
            if (_sending) return;

            string text = (ComposeBox.Text ?? "").Trim();
            if (text.Length == 0) return;

            _sending = true;

            // An edit, not a send. Taken and cleared before anything else, so a
            // failure cannot leave the box still pointed at a message the user has
            // moved on from.
            if (_editingId != 0)
            {
                int editing = _editingId;
                ClearEdit(false);

                ComposeBox.Text = "";
                SendButton.IsEnabled = false;

                await EditAsync(editing, text);

                _sending = false;
                SendButton.IsEnabled = true;
                return;
            }

            // Taken before the bar is cleared, so a failure below does not leave the
            // next message answering something the user has moved on from.
            int replyTo = _replyTo;
            ClearReply();

            ComposeBox.Text = "";
            SendButton.IsEnabled = false;

            // Shown at once. Waiting for the server makes the app feel broken on a
            // slow connection, and a failure is reported below rather than hidden.
            var pending = new MessageItem
            {
                Id = 0,
                Text = text,
                Time = DateTime.Now.ToString("HH:mm"),
                Out = true,
                CanLink = _canLink,
                Ticks = MessageTicks.Sending,
                ReplyToId = replyTo,
            };

            _messages.Add(pending);
            ResolveReply(pending);
            ScrollToEnd();

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                // Taking the id is what stops the message being shown twice: the
                // bubble put on screen a moment ago carries no id, so when the same
                // message comes back from the server there is nothing to recognise
                // it by and it is added a second time.
                pending.Id = await Messages.SendTextAsync(
                    client, TelegramService.Crypto, _inputPeer, text, replyTo, _topicId);

                // The id is the acknowledgement, so this is the moment the first
                // mark is earned. The second waits on the other side.
                pending.Ticks = MessageTicks.Sent;

                SetBusy(false, "");
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                SetBusy(false, "Not sent: " + (rpc != null ? rpc.ErrorType : ex.Message));

                pending.Text = text + "  (not sent)";
                RefreshBody(pending);
            }
            finally
            {
                _sending = false;
                SendButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Moves to the newest message.
        ///
        /// Two things had to be right here, and neither is obvious. The layout has to
        /// be brought up to date first, or ScrollableHeight still describes the list
        /// as it was before the messages were added - which is why opening a chat
        /// left it at the top. And the target is deliberately larger than any real
        /// extent rather than ScrollableHeight itself: the value is clamped, so
        /// asking to go far too far always lands exactly at the bottom, whereas
        /// asking for a number read a moment too early lands short.
        /// </summary>
        /// <summary>
        /// Jumps to the newest message.
        ///
        /// Reading back through a long conversation leaves a lot of scrolling
        /// between the reader and the bottom, and the page returns there by itself
        /// only when something new arrives. This is the way back without waiting for
        /// somebody else to say something.
        /// </summary>
        private void Older_Click(object sender, RoutedEventArgs e)
        {
            LoadOlder();
        }

        /// <summary>
        /// Shows or hides the ways to go further back.
        ///
        /// The menu item too, not only the button: once the start of the conversation
        /// is on screen, a menu entry promising older messages would load nothing.
        /// </summary>
        private void ShowOlder(bool hasOlder)
        {
            _hasOlder = hasOlder;

            Visibility shown = hasOlder && !_loadingOlder ? Visibility.Visible
                                                          : Visibility.Collapsed;
            OlderButton.Visibility = shown;
            OlderMenuItem.Visibility = hasOlder ? Visibility.Visible : Visibility.Collapsed;
            OlderBusy.Visibility = _loadingOlder ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Fetches the page before the oldest message on screen, and puts it above.
        ///
        /// The reader stays where they were. The message that was at the top before
        /// is scrolled back to the top afterwards, so the new page arrives above it,
        /// out of sight, to be scrolled up into - rather than the view jumping to the
        /// start of a page they have not read the end of.
        ///
        /// A message already on screen is not added twice. Pages are asked for by id
        /// and do not overlap, but a message arriving while the request is out can
        /// shift what the server counts as "before".
        /// </summary>
        private async void LoadOlder()
        {
            if (_loadingOlder || !_hasOlder) return;

            MessageItem firstShown = null;
            int oldest = 0;

            foreach (MessageItem item in _messages)
            {
                if (item.Id <= 0) continue;

                firstShown = item;
                oldest = item.Id;
                break;
            }

            if (oldest == 0) return;

            _loadingOlder = true;
            ShowOlder(_hasOlder);

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                Messages.History page = _topicId != 0
                    ? await Topics.GetHistoryBeforeAsync(client, _inputPeer, _topicId,
                                                         oldest, HistoryCount,
                                                         TelegramService.Info)
                    : await Messages.GetHistoryBeforeAsync(client, _inputPeer, oldest,
                                                           HistoryCount, TelegramService.Info);

                foreach (KeyValuePair<long, PeerInfo> pair in page.Senders)
                    _senders[pair.Key] = pair.Value;

                // Newest first from the server, so walking it backwards and inserting
                // each at the next position down keeps the oldest at the very top.
                int at = 0;
                for (int i = page.Messages.Count - 1; i >= 0; i--)
                {
                    TextMessage m = page.Messages[i];
                    if (m.Id == 0 || Contains(m.Id)) continue;

                    Add(m, at);
                    at++;
                }

                _loadingOlder = false;
                ShowOlder(page.HasOlder(HistoryCount));

                // A page further back can hold originals that newer replies on
                // screen were waiting for.
                FetchMissingReplies();

                if (at == 0) SetBusy(false, "That is the beginning of the conversation.");
                else SetBusy(false, "");

                // Back to where the reader was. Low priority, so it runs once the new
                // rows have been laid out and there is a position to go back to.
                if (at > 0 && firstShown != null)
                {
                    MessageItem keep = firstShown;
                    var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low,
                        delegate
                        {
                            MessageList.UpdateLayout();
                            MessageList.ScrollIntoView(keep, ScrollIntoViewAlignment.Leading);
                        });
                }
            }
            catch (RpcException ex) when (TelegramService.IsAuthGone(ex))
            {
                _loadingOlder = false;
                if (_poll != null) _poll.Stop();

                await TelegramService.AuthGoneAsync();
                Frame.Navigate(typeof(QrLoginPage));
            }
            catch (Exception ex)
            {
                _loadingOlder = false;
                ShowOlder(_hasOlder);

                var rpc = ex as RpcException;
                SetBusy(false, "Could not load older messages: " +
                               (rpc != null ? rpc.ErrorType : ex.Message));
            }
        }

        private void SkipToEnd_Click(object sender, RoutedEventArgs e)
        {
            if (_focusId == 0)
            {
                ScrollToEnd();
                return;
            }

            // Parked on a linked message, so the end is not on screen to scroll to -
            // it is a different screenful that has to be fetched. Clearing this is
            // also what lets the poll start adding new messages again.
            _focusId = 0;
            Load();
        }

        /// <summary>
        /// Sends the changed text, and puts it on screen.
        ///
        /// The bubble is updated here rather than waiting for the refresh: the poll
        /// matches messages by id and the id has not changed, so an edited message
        /// is not something it would notice. Rebuilding the collection is how the
        /// new text is picked up, because MessageItem.Text raises no change.
        /// </summary>
        private async Task EditAsync(int messageId, string text)
        {
            SetBusy(true, "Saving...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                await Messages.EditTextAsync(client, _inputPeer, messageId, text,
                                             TelegramService.Info);

                MessageItem edited = FindItem(messageId);
                if (edited != null)
                {
                    edited.Text = text;
                    RefreshBody(edited);

                    // Left where it is. This used to rebuild the whole list to pick
                    // the new text up, which reset the scroll and threw the reader
                    // to the top of the conversation.
                    BringIntoView(edited);
                }

                SetBusy(false, "");
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                string trouble = rpc != null ? rpc.ErrorType : ex.Message;

                // The two the server actually answers with, said in words. Anything
                // else is reported as it came.
                if (trouble != null && trouble.Contains("MESSAGE_EDIT_TIME_EXPIRED"))
                    trouble = "Too late to edit that message.";
                else if (trouble != null && trouble.Contains("MESSAGE_NOT_MODIFIED"))
                    trouble = "";
                else if (trouble != null && trouble.Contains("MESSAGE_AUTHOR_REQUIRED"))
                    trouble = "Only the author can edit that message.";
                else
                    trouble = "Not saved: " + trouble;

                SetBusy(false, trouble);

                // Armed again, not merely put back in the box. Restoring the text
                // alone would leave the box looking like an edit in progress while
                // the send button had quietly gone back to meaning "send", so the
                // obvious second attempt would post a new message instead.
                if (trouble.Length > 0) RearmEdit(messageId, text);
            }
        }

        private MessageItem FindItem(int messageId)
        {
            foreach (MessageItem item in _messages)
                if (item.Id == messageId) return item;

            return null;
        }

        /// <summary>
        /// Redraws one message's body after its text has changed.
        ///
        /// The body is built from runs and links in code, not bound, so changing the
        /// text does not redraw it. Only the one bubble is touched: the list, its
        /// scroll position and every other bubble stay exactly as they were.
        ///
        /// A bubble that is not on screen has no container and nothing to redraw -
        /// it will be built from the new text when it is scrolled back to, by the
        /// same DataContextChanged path that builds every recycled bubble.
        /// </summary>
        private void RefreshBody(MessageItem item)
        {
            var container = MessageList.ContainerFromItem(item) as DependencyObject;
            if (container == null) return;

            FillInlines(FindNamed<TextBlock>(container, "MessageText"));
        }

        private static T FindNamed<T>(DependencyObject parent, string name)
            where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                var match = child as T;
                if (match != null && match.Name == name) return match;

                T deeper = FindNamed<T>(child, name);
                if (deeper != null) return deeper;
            }

            return null;
        }

        /// <summary>
        /// Scrolls one message into view, once layout has caught up.
        ///
        /// Low priority so it runs after whatever is changing the layout right now -
        /// the keyboard arriving, or a bubble growing by a line - and scrolls to
        /// where the message ends up rather than where it was.
        /// </summary>
        private void BringIntoView(MessageItem item)
        {
            if (item == null) return;

            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low,
                delegate
                {
                    MessageList.UpdateLayout();
                    MessageList.ScrollIntoView(item);
                });
        }

        /// <summary>Puts a failed edit back the way it was, ready to try again.</summary>
        private void RearmEdit(int messageId, string text)
        {
            _editingId = messageId;
            _editingItem = FindItem(messageId);
            EditBar.Visibility = Visibility.Visible;

            ComposeBox.Text = text;
            ComposeBox.SelectionStart = ComposeBox.Text.Length;
            ComposeBox.SelectionLength = 0;
        }

        /// <summary>
        /// Moves the read marker forward, and marks the messages it has passed.
        ///
        /// Only ever forward. The dialog poll and the chat list can disagree for a
        /// moment, and a marker that went backwards would un-read messages on
        /// screen - which looks like a bug whichever way it is actually wrong.
        /// </summary>
        private void AdvanceReadMarks(int readOutboxMaxId)
        {
            if (readOutboxMaxId <= _readOutbox) return;

            _readOutbox = readOutboxMaxId;

            foreach (MessageItem item in _messages)
            {
                if (!item.Out || item.Id == 0 || item.Id > _readOutbox) continue;

                item.Ticks = MessageTicks.Read;
            }
        }

        private void ScrollToEnd()
        {
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low,
                delegate
                {
                    MessageList.UpdateLayout();

                    if (_messages.Count > 0)
                        MessageList.ScrollIntoView(_messages[_messages.Count - 1]);
                });
        }

        private void SetBusy(bool busy, string status)
        {
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = status ?? "";
        }
    }
}
