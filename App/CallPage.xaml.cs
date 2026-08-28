using System;
using Windows.Phone.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Lumigram.Mtproto;
using Lumigram.Voip;

namespace LumigramPlus.App
{
    /// <summary>What this page was opened to do.</summary>
    public sealed class CallRequest
    {
        /// <summary>Who is on the other end. Null for an incoming call.</summary>
        public DialogItem Peer;

        /// <summary>The call as the server described it, for an incoming one.</summary>
        public CallInfo Call;

        public bool Outgoing { get { return Peer != null; } }
    }

    /// <summary>
    /// A call in progress, in both directions.
    ///
    /// Deliberately plain. What is being tested here is whether the signalling
    /// reaches the other end and comes back - so the page shows the state machine
    /// rather than dressing it up, and says outright that there is no audio.
    /// </summary>
    public sealed partial class CallPage : Page
    {
        private CallRequest _request;
        private DateTime _started;
        private bool _finished;

        private DispatcherTimer _tick;
        private VoipTransport _transport;
        private VoicePlayer _player;
        private VoiceRecorder _recorder;
        private string _media = "";

        public CallPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _request = e.Parameter as CallRequest;
            if (_request == null)
            {
                Frame.GoBack();
                return;
            }

            // Back must not walk out of a live call: there is nothing behind this
            // page that can end it, so leaving would strand the call ringing.
            HardwareButtons.BackPressed += OnBackPressed;

            CallService.Changed += OnChanged;
            _started = DateTime.UtcNow;

            PeerTitle.Text = _request.Outgoing
                ? (_request.Peer.Title ?? "call")
                : "incoming call";

            // The counters below only move while media is flowing, and nothing
            // else on this page ticks - so it redraws itself.
            _tick = new DispatcherTimer();
            _tick.Interval = TimeSpan.FromSeconds(1);
            _tick.Tick += delegate { Describe(CallService.Current); };
            _tick.Start();

            if (_request.Outgoing) await PlaceAsync();
            else await RingAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            HardwareButtons.BackPressed -= OnBackPressed;
            CallService.Changed -= OnChanged;

            if (_tick != null)
            {
                _tick.Stop();
                _tick = null;
            }

            if (_transport != null)
            {
                _transport.Progress -= OnMediaProgress;
                _transport.Audio -= OnAudio;
                _transport.Established -= OnEstablished;
                _transport.Dispose();
                _transport = null;
            }

            if (_recorder != null)
            {
                _recorder.Frame -= OnCaptured;
                _recorder.Dispose();
                _recorder = null;
            }

            if (_player != null)
            {
                _player.Dispose();
                _player = null;
            }
        }

        private void OnBackPressed(object sender, BackPressedEventArgs e)
        {
            e.Handled = true;
            HangUp_Tapped(null, null);
        }

        private async System.Threading.Tasks.Task PlaceAsync()
        {
            StateText.Text = "Calling...";

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();
                CallService.Listen(client);

                CallInfo call = await CallService.PlaceAsync(client, _request.Peer);

                StateText.Text = "Ringing...";
                Describe(call);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private async System.Threading.Tasks.Task RingAsync()
        {
            StateText.Text = "Incoming call";
            AnswerButton.Visibility = Visibility.Visible;
            Describe(_request.Call);

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();
                await CallService.RingingAsync(client, _request.Call);
            }
            catch (Exception)
            {
                // Only affects what the caller hears while waiting.
            }
        }

        private async void Answer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnswerButton.Visibility = Visibility.Collapsed;
            StateText.Text = "Answering...";

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                CallInfo call = await CallService.AnswerAsync(client, _request.Call);

                AnswerButton.Visibility = Visibility.Collapsed;
                Describe(call);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private async void HangUp_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_finished)
            {
                Leave();
                return;
            }

            _finished = true;
            StateText.Text = "Hanging up...";

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                int seconds = (int)(DateTime.UtcNow - _started).TotalSeconds;

                // Hangup rather than missed, even while it is still ringing: we
                // chose to end it, and the other end should hear that rather than
                // that we never noticed.
                await CallService.HangUpAsync(client, seconds, CallDiscardReason.Hangup);
            }
            catch (Exception)
            {
                // Leaving matters more than reporting.
            }

            Leave();
        }

        private void OnChanged(CallInfo call)
        {
            if (call == null) return;

            switch (call.State)
            {
                case CallState.Waiting:
                    StateText.Text = "Ringing...";
                    break;

                case CallState.Accepted:
                    StateText.Text = "Answered, exchanging keys...";
                    break;

                case CallState.Ready:
                    StateText.Text = CallService.Key != null
                        ? "Connected - bringing up media"
                        : "Connected, but the key exchange failed";
                    AnswerButton.Visibility = Visibility.Collapsed;

                    StartMedia(call);
                    break;

                case CallState.Discarded:
                    _finished = true;
                    StateText.Text = "Call ended" +
                        (call.DiscardReason.HasValue ? " (" + call.DiscardReason + ")" : "");
                    MuteButton.Visibility = Visibility.Collapsed;
                    break;
            }

            Describe(call);
        }

        /// <summary>
        /// Opens the voice connection to the reflector.
        ///
        /// Only once, and only with a key: without one every packet we sent would
        /// be noise to the far end, and it would tell us nothing about why.
        /// </summary>
        private async void StartMedia(CallInfo call)
        {
            if (_transport != null) return;
            if (CallService.Key == null) return;
            if (call.Connections.Count == 0) return;

            try
            {
                _player = new VoicePlayer();

                // Attached before the transport starts, so the first frame to arrive
                // has somewhere to go.
                Speaker.MediaFailed += delegate (object s, ExceptionRoutedEventArgs a)
                {
                    _media = "playback failed: " + a.ErrorMessage;
                };

                Speaker.SetMediaStreamSource(_player.Source);

                Route();

                _transport = new VoipTransport(call.Connections[0], CallService.Key,
                                               CallService.WeCalled);

                _transport.Progress += OnMediaProgress;
                _transport.Audio += OnAudio;
                _transport.Established += OnEstablished;

                await _transport.StartAsync();
            }
            catch (Exception ex)
            {
                _media = "media failed: " + ex.Message;
                Describe(CallService.Current);
            }
        }

        /// <summary>
        /// Starts the microphone, once there is somewhere for its audio to go.
        ///
        /// Not before: recording into a connection that has not finished its
        /// handshake would spend battery on frames nobody can decode, and asking for
        /// the microphone is the point at which the user is prompted.
        /// </summary>
        private async void OnEstablished()
        {
            if (_recorder != null) return;

            try
            {
                var recorder = new VoiceRecorder();
                recorder.Frame += OnCaptured;

                await recorder.StartAsync();
                _recorder = recorder;

                MuteButton.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                _media = "microphone failed: " + ex.Message;
            }
        }

        /// <summary>One frame of our speech, on its way out.</summary>
        private async void OnCaptured(byte[] opus, int timestamp)
        {
            VoipTransport transport = _transport;
            if (transport == null) return;

            try
            {
                await transport.SendAudioAsync(opus, timestamp);
            }
            catch (Exception)
            {
                // A dropped frame is a dropped frame. The next one is 20 ms away.
            }
        }

        /// <summary>
        /// A frame of their speech, straight into the jitter buffer.
        ///
        /// Not decoded here. This runs on the socket's thread and the decode belongs
        /// on the one asking for samples, where it can be done in step with playback
        /// rather than in step with the network.
        /// </summary>
        private void OnAudio(byte[] opus, int timestamp)
        {
            VoicePlayer player = _player;
            if (player != null) player.Receive(opus, timestamp);
        }

        /// <summary>
        /// Sends the call to the earpiece.
        ///
        /// A phone call out of the loudspeaker is the wrong default: it is held to
        /// the ear, and the speaker also gives the microphone far more of itself to
        /// cancel.
        /// </summary>
        private static void Route()
        {
            try
            {
                Windows.Phone.Media.Devices.AudioRoutingManager.GetDefault()
                    .SetAudioEndpoint(
                        Windows.Phone.Media.Devices.AudioRoutingEndpoint.Earpiece);
            }
            catch (Exception)
            {
                // Routing is a preference, not a requirement. A device without an
                // earpiece to route to still has a call to carry.
            }
        }

        private void OnMediaProgress(string what)
        {
            _media = what;

            // The transport runs off the UI thread, so this comes back to it before
            // touching anything on screen.
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                delegate
                {
                    if (_transport != null && _transport.IsEstablished)
                        StateText.Text = "Connected";

                    Describe(CallService.Current);
                });
        }

        /// <summary>
        /// The call's innards, on screen.
        ///
        /// This is a page for finding out whether the protocol works, so what it
        /// shows is what would otherwise need a debugger: which end we are, whether
        /// the key was derived, and what reflectors the server handed back.
        /// </summary>
        private void Describe(CallInfo call)
        {
            if (call == null) return;

            var text = new System.Text.StringBuilder();

            text.AppendLine("id " + call.Id);
            text.AppendLine("state " + call.State + (CallService.WeCalled ? " (we called)" : " (they called)"));

            if (CallService.Key != null)
            {
                text.AppendLine("key derived, fingerprint " +
                                call.KeyFingerprint.ToString("x16"));
            }
            else if (call.State == CallState.Ready)
            {
                text.AppendLine("no key");
            }

            if (!string.IsNullOrEmpty(CallService.LastError))
                text.AppendLine(CallService.LastError);

            if (_transport != null)
            {
                text.AppendLine("media: " + _media +
                                " (sent " + _transport.Sent +
                                ", received " + _transport.Received + ")");

                if (!string.IsNullOrEmpty(_transport.LastError))
                    text.AppendLine("media error: " + _transport.LastError);
            }

            if (_recorder != null)
            {
                text.AppendLine("capture: " + _recorder.Format +
                                ", " + _recorder.Processing);

                text.AppendLine("level: " + _recorder.Level + "%" +
                                ", silent frames " + _recorder.SilentFrames);

                text.AppendLine("mic: " + (_recorder.Running ? "on" : "off") +
                                ", encoded " + _recorder.Frames +
                                ", sent " + _recorder.Sent +
                                ", queued " + _recorder.Queued +
                                ", dropped " + _recorder.Dropped +
                                ", restarts " + _recorder.Restarts);

                if (!string.IsNullOrEmpty(_recorder.LastError))
                    text.AppendLine("mic error: " + _recorder.LastError);
            }

            if (_player != null)
            {
                // The player's own state is worth more than any of our counters when
                // sound stops: it says whether it is still trying.
                text.AppendLine("player: " + Speaker.CurrentState +
                                ", asked " + _player.Requests);

                text.AppendLine("audio: played " + _player.Played +
                                ", concealed " + _player.Concealed +
                                ", late " + _player.Late +
                                ", waiting " + _player.Waiting +
                                ", delay " + _player.DelayMs + " ms");

                if (!string.IsNullOrEmpty(_player.LastError))
                    text.AppendLine("audio error: " + _player.LastError);
            }

            foreach (CallConnection connection in call.Connections)
                text.AppendLine("reflector " + connection);

            DetailText.Text = text.ToString().TrimEnd();
        }

        private void Fail(Exception ex)
        {
            _finished = true;

            var rpc = ex as RpcException;
            StateText.Text = "Failed: " + (rpc != null ? rpc.ErrorType : ex.Message);

            MuteButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Stops sending, without stopping the microphone.
        ///
        /// Encoding carries on so the timeline keeps counting; only the sending
        /// stops. Unmuting then resumes in the right place rather than however far
        /// behind the mute lasted.
        /// </summary>
        private void Mute_Tapped(object sender, TappedRoutedEventArgs e)
        {
            VoiceRecorder recorder = _recorder;
            if (recorder == null) return;

            recorder.Muted = !recorder.Muted;

            MuteCircle.Fill = new SolidColorBrush(recorder.Muted
                ? Windows.UI.Color.FromArgb(0xFF, 0xD6, 0x45, 0x41)
                : Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

            MuteGlyph.Text = recorder.Muted ? "\uE1D7" : "\uE1D6";
        }

        private void Leave()
        {
            if (Frame.CanGoBack) Frame.GoBack();
            else Frame.Navigate(typeof(ChatsPage));
        }
    }
}
