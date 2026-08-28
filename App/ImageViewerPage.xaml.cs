using System;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace LumigramPlus.App
{
    /// <summary>
    /// One picture, full screen and zoomable.
    ///
    /// Takes the cached file rather than the message: by the time a picture can be
    /// opened it has already been downloaded, and passing the file means this page
    /// needs no connection, no protocol and no knowledge of what a message is.
    /// </summary>
    public sealed partial class ImageViewerPage : Page
    {
        private string _fileName;

        public ImageViewerPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _fileName = e.Parameter as string;
            if (string.IsNullOrEmpty(_fileName)) return;

            Picture.Source = new BitmapImage(
                new Uri("ms-appdata:///local/media/" + _fileName));
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

                await file.CopyAsync(KnownFolders.SavedPictures, "lumigram-" + _fileName,
                                     NameCollisionOption.ReplaceExisting);

                SaveButton.Label = "saved";
            }
            catch (Exception)
            {
                SaveButton.Label = "not saved";
            }
            finally
            {
                SaveButton.IsEnabled = true;
            }
        }
    }
}
