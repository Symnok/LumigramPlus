using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LumigramPlus.App
{
    /// <summary>
    /// Plays a downloaded video.
    ///
    /// Given the cached file name rather than the message: by the time a video can
    /// be played it has been downloaded, so this page needs no connection and no
    /// knowledge of the protocol.
    /// </summary>
    public sealed partial class VideoPage : Page
    {
        private string _fileName;
        private IRandomAccessStream _stream;
        private long _size;

        public VideoPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _fileName = e.Parameter as string;
            if (string.IsNullOrEmpty(_fileName)) return;

            await OpenAsync();
        }

        /// <summary>
        /// Hands the file to the player as an open stream with its type named.
        ///
        /// Not as an ms-appdata URI, which is what this did first and which left a
        /// black screen with no error: the player has to resolve the URI itself,
        /// then work out the format from the name, and when either step goes wrong
        /// it can end up waiting rather than failing. Opening the file here and
        /// saying "video/mp4" removes both guesses - and the same file played
        /// perfectly in the phone's own video player, which is what ruled out the
        /// codec and pointed here.
        ///
        /// The size is shown on failure because a truncated download and an
        /// unplayable format look identical on screen and are not the same problem.
        /// </summary>
        private async Task OpenAsync()
        {
            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .GetFolderAsync("media");

                StorageFile file = await folder.GetFileAsync(_fileName);

                Windows.Storage.FileProperties.BasicProperties basic =
                    await file.GetBasicPropertiesAsync();

                _size = (long)basic.Size;

                if (_size == 0)
                {
                    StatusText.Text = "The video did not download.";
                    return;
                }

                _stream = await file.OpenAsync(FileAccessMode.Read);

                Player.SetSource(_stream, "video/mp4");
                Player.Play();
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not open the video: " + ex.Message;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // Otherwise the sound carries on after the page is gone.
            Player.Stop();
            Player.Source = null;

            if (_stream != null)
            {
                try { _stream.Dispose(); }
                catch (Exception) { }
                _stream = null;
            }
        }

        /// <summary>
        /// Says why a video will not play.
        ///
        /// Telegram carries whatever the sender uploaded, and this phone decodes a
        /// particular set of formats. A silent black screen would be indisponible
        /// from a broken download, so the reason is put on screen.
        /// </summary>
        private void Player_Failed(object sender, ExceptionRoutedEventArgs e)
        {
            StatusText.Text = "This video cannot be played on this phone." +
                              Environment.NewLine + Environment.NewLine +
                              (e.ErrorMessage ?? "") +
                              Environment.NewLine + Environment.NewLine +
                              (_size / 1024) + " KB downloaded";
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_fileName)) return;

            SaveButton.IsEnabled = false;

            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .GetFolderAsync("media");

                StorageFile file = await folder.GetFileAsync(_fileName);

                // The camera roll, not the videos library: the latter cannot be
                // written to on this platform and refuses the copy.
                await file.CopyAsync(KnownFolders.CameraRoll, "lumigram-" + _fileName,
                                     NameCollisionOption.ReplaceExisting);

                SaveButton.Label = "saved";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Not saved: " + ex.Message;
            }
            finally
            {
                SaveButton.IsEnabled = true;
            }
        }
    }
}
