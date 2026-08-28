using System;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Phone.UI.Input;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LumigramPlus.App
{
    /// <summary>
    /// A recorded video, waiting to be sent.
    ///
    /// A static hand-off rather than a return value, because navigating back does
    /// not return anything. The same shape the file picker's continuation uses, for
    /// the same reason.
    /// </summary>
    internal static class VideoCapture
    {
        public static StorageFile Recorded;
    }

    /// <summary>
    /// Records a short video and hands it back to the conversation.
    ///
    /// Recording starts as soon as the camera is ready rather than waiting for a
    /// button. A video message is a thing people send in one motion, and a preview
    /// with a record button underneath is two decisions where there was one - the
    /// user already decided by choosing "video message".
    ///
    /// The front camera is preferred and the back one used when there is none,
    /// because a video message is nearly always of the person sending it.
    /// </summary>
    public sealed partial class VideoCapturePage : Page
    {
        /// <summary>
        /// The longest video this records.
        ///
        /// A minute is Telegram's own limit for a video message and about as much as
        /// anyone watches. It is also the difference between a few megabytes and a
        /// download somebody resents on a cellular connection.
        /// </summary>
        private const int MaxSeconds = 60;

        /// <summary>
        /// Bitrate, set rather than left to the platform.
        ///
        /// The default for VGA is generous enough that a minute runs to well over
        /// ten megabytes, which is a long upload on a phone and a long wait at the
        /// other end. Half a megabit looks fine at this size and is a quarter of it.
        /// </summary>
        private const uint Bitrate = 500000;

        private MediaCapture _capture;
        private StorageFile _file;
        private DispatcherTimer _timer;
        private DateTime _started;
        private bool _recording;
        private bool _finishing;

        public VideoCapturePage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            HardwareButtons.BackPressed += OnBackPressed;
            VideoCapture.Recorded = null;

            await StartAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            HardwareButtons.BackPressed -= OnBackPressed;
            Cleanup();
        }

        private void OnBackPressed(object sender, BackPressedEventArgs e)
        {
            e.Handled = true;
            Cancel_Click(null, null);
        }

        private async Task StartAsync()
        {
            try
            {
                StatusText.Text = "Starting the camera...";

                DeviceInformation camera = await ChooseCameraAsync();

                if (camera == null)
                {
                    StatusText.Text = "This phone has no camera.";
                    StopButton.IsEnabled = false;
                    return;
                }

                bool front = camera.EnclosureLocation != null &&
                             camera.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front;

                _capture = new MediaCapture();

                await _capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = camera.Id,
                    StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo,
                });

                // The sensor is mounted landscape whichever way the phone is held,
                // so a portrait recording has to say which way is up or it arrives
                // on its side. The front camera faces the other way, and turning it
                // the same direction stands the picture on its head.
                VideoRotation rotation = front
                    ? VideoRotation.Clockwise270Degrees
                    : VideoRotation.Clockwise90Degrees;

                _capture.SetPreviewRotation(rotation);
                _capture.SetRecordRotation(rotation);

                Preview.Source = _capture;
                await _capture.StartPreviewAsync();

                await RecordAsync();
            }
            catch (UnauthorizedAccessException)
            {
                StatusText.Text = "The camera or microphone is turned off for this app.";
                StopButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not start the camera: " + ex.Message;
                StopButton.IsEnabled = false;
            }
        }

        /// <summary>
        /// The front camera if there is one, otherwise whatever there is.
        /// </summary>
        private static async Task<DeviceInformation> ChooseCameraAsync()
        {
            DeviceInformationCollection cameras =
                await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            if (cameras.Count == 0) return null;

            foreach (DeviceInformation camera in cameras)
            {
                if (camera.EnclosureLocation != null &&
                    camera.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front)
                {
                    return camera;
                }
            }

            return cameras[0];
        }

        private async Task RecordAsync()
        {
            _file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                "message.mp4", CreationCollisionOption.ReplaceExisting);

            MediaEncodingProfile profile =
                MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga);

            profile.Video.Bitrate = Bitrate;

            await _capture.StartRecordToStorageFileAsync(profile, _file);

            _recording = true;
            _started = DateTime.UtcNow;
            StatusText.Text = "";

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(250);
            _timer.Tick += Tick;
            _timer.Start();
        }

        private void Tick(object sender, object e)
        {
            int seconds = (int)(DateTime.UtcNow - _started).TotalSeconds;

            ElapsedText.Text = (seconds / 60) + ":" + (seconds % 60).ToString("00");

            // Stopped for the user rather than left running past the limit: the
            // alternative is a recording that cannot be sent, discovered afterwards.
            if (seconds >= MaxSeconds) Stop_Click(null, null);
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (_finishing || !_recording) return;

            _finishing = true;
            StopButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            StatusText.Text = "Finishing...";

            try
            {
                await FinishAsync();

                // Handed over rather than sent from here: the conversation owns
                // sending, knows the peer, and already does this for a picked file.
                VideoCapture.Recorded = _file;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not finish the recording: " + ex.Message;
            }

            Leave();
        }

        private async void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_finishing) return;
            _finishing = true;

            try
            {
                await FinishAsync();

                if (_file != null) await _file.DeleteAsync();
            }
            catch (Exception)
            {
                // Nothing useful to say about a recording being thrown away.
            }

            VideoCapture.Recorded = null;
            Leave();
        }

        private async Task FinishAsync()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }

            if (_recording)
            {
                _recording = false;
                await _capture.StopRecordAsync();
            }
        }

        private void Cleanup()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }

            if (_capture == null) return;

            try { _capture.Dispose(); }
            catch (Exception) { }

            _capture = null;
            Preview.Source = null;
        }

        private void Leave()
        {
            Cleanup();

            if (Frame.CanGoBack) Frame.GoBack();
            else Frame.Navigate(typeof(ChatsPage));
        }
    }
}
