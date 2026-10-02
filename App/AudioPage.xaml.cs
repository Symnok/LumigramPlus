using System;
using Windows.System.Display;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace LumigramPlus.App
{
    /// <summary>What to play, and what to call it.</summary>
    public sealed class AudioRequest
    {
        /// <summary>The cached file's name in the media folder, extension and all.</summary>
        public string CachedName;

        public string Title;
        public string Performer;
    }

    /// <summary>
    /// Plays one downloaded audio file.
    ///
    /// Takes a file already in the cache, like the picture and video viewers: by the
    /// time this opens the download is done, so the page needs no connection and
    /// knows nothing about messages.
    ///
    /// Plays only while it is open. Going back stops it, and so does leaving the
    /// app. Keeping music going behind other apps is a separate mechanism on this
    /// platform - a background audio task in a project of its own - and not what
    /// this page is.
    /// </summary>
    public sealed partial class AudioPage : Page
    {
        /// <summary>
        /// Keeps the screen on while something is playing.
        ///
        /// Windows Phone suspends an app when the screen locks, and a suspended app
        /// is a silent one - so without this, every track longer than the lock
        /// timeout stops partway through. Held only while actually playing, and
        /// let go on pause, at the end, and on the way out, because a screen held
        /// on for no reason is a flat battery.
        /// </summary>
        private DisplayRequest _awake;
        private bool _held;

        private readonly DispatcherTimer _tick = new DispatcherTimer();

        /// <summary>
        /// Set while the slider is being moved by the timer rather than by a finger.
        ///
        /// ValueChanged fires for both. Without telling them apart, every timer tick
        /// would seek to where the track already is - which on this player is a
        /// small stutter four times a second.
        /// </summary>
        private bool _updating;

        public AudioPage()
        {
            InitializeComponent();

            _tick.Interval = TimeSpan.FromMilliseconds(500);
            _tick.Tick += delegate { ShowPosition(); };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var request = e.Parameter as AudioRequest;
            if (request == null || string.IsNullOrEmpty(request.CachedName))
            {
                StatusText.Text = "Nothing to play.";
                return;
            }

            TrackTitle.Text = !string.IsNullOrEmpty(request.Title) ? request.Title
                                                                   : request.CachedName;
            TrackPerformer.Text = request.Performer ?? "";

            StatusText.Text = "Opening...";
            Player.Source = new Uri("ms-appdata:///local/media/" + request.CachedName);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            _tick.Stop();
            Release();

            // Stopped outright. The element would be torn down with the page anyway,
            // but not necessarily before the next page has started making sound of
            // its own.
            try { Player.Stop(); }
            catch (Exception) { }

            Player.Source = null;
        }

        private void Player_Opened(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "";

            TimeSpan length = Player.NaturalDuration.HasTimeSpan
                ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;

            _updating = true;
            Position.Maximum = Math.Max(1, length.TotalSeconds);
            Position.Value = 0;
            _updating = false;

            Total.Text = Clock(length);
            _tick.Start();
        }

        private void Player_Ended(object sender, RoutedEventArgs e)
        {
            _tick.Stop();
            ShowPosition();
            Release();
        }

        /// <summary>
        /// The file could not be played.
        ///
        /// The extension is the usual reason - this is why audio is now stored as
        /// .mp3 rather than .bin - and a file whose name says MP3 but whose bytes
        /// are something else is the other.
        /// </summary>
        private void Player_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            _tick.Stop();
            Release();
            StatusText.Text = "This file cannot be played here: " + e.ErrorMessage;
        }

        /// <summary>
        /// Keeps the button and the screen in step with what the player is doing,
        /// rather than with what was last asked of it - the two differ whenever
        /// the system pauses playback for a call.
        /// </summary>
        private void Player_StateChanged(object sender, RoutedEventArgs e)
        {
            bool playing = Player.CurrentState == MediaElementState.Playing;

            PlayButton.Icon = new SymbolIcon(playing ? Symbol.Pause : Symbol.Play);
            PlayButton.Label = playing ? "pause" : "play";

            if (playing) Hold();
            else Release();
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (Player.CurrentState == MediaElementState.Playing)
            {
                Player.Pause();
                return;
            }

            // At the end, play means from the start: a play button that does
            // nothing because the track is over looks broken.
            if (Player.NaturalDuration.HasTimeSpan &&
                Player.Position >= Player.NaturalDuration.TimeSpan)
                Player.Position = TimeSpan.Zero;

            Player.Play();
            _tick.Start();
        }

        private void Restart_Click(object sender, RoutedEventArgs e)
        {
            Player.Position = TimeSpan.Zero;
            Player.Play();
            _tick.Start();
        }

        /// <summary>A finger on the slider: go there.</summary>
        private void Position_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_updating) return;

            Player.Position = TimeSpan.FromSeconds(e.NewValue);
            Elapsed.Text = Clock(Player.Position);
        }

        private void ShowPosition()
        {
            _updating = true;
            Position.Value = Math.Min(Position.Maximum, Player.Position.TotalSeconds);
            _updating = false;

            Elapsed.Text = Clock(Player.Position);
        }

        private void Hold()
        {
            if (_held) return;

            try
            {
                if (_awake == null) _awake = new DisplayRequest();
                _awake.RequestActive();
                _held = true;
            }
            catch (Exception)
            {
                // Not fatal: the music plays, it just stops when the screen locks.
            }
        }

        /// <summary>
        /// Lets the screen go again. Guarded, because RequestRelease without a
        /// matching RequestActive throws, and every path out calls this.
        /// </summary>
        private void Release()
        {
            if (!_held) return;

            try { _awake.RequestRelease(); }
            catch (Exception) { }

            _held = false;
        }

        private static string Clock(TimeSpan t)
        {
            int seconds = (int)t.TotalSeconds;
            return (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }
    }
}
