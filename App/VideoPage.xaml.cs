using System;
using Windows.Storage;
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

        public VideoPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _fileName = e.Parameter as string;
            if (string.IsNullOrEmpty(_fileName)) return;

            Player.Source = new Uri("ms-appdata:///local/media/" + _fileName);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // Otherwise the sound carries on after the page is gone.
            Player.Stop();
            Player.Source = null;
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
                              (e.ErrorMessage ?? "");
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
