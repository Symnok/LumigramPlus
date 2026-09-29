using System;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
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
        /// <summary>
        /// How far a double tap zooms in.
        ///
        /// Not the maximum. A double tap is for looking closer at something, and
        /// landing at eight times on one of them puts the thing tapped somewhere off
        /// the screen; pinching is there for anyone who wants more.
        /// </summary>
        private const float ZoomStep = 3f;

        private string _fileName;

        public ImageViewerPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _fileName = e.Parameter as string;

            if (!string.IsNullOrEmpty(_fileName))
                Picture.Source = new BitmapImage(
                    new Uri("ms-appdata:///local/media/" + _fileName));

            // The clock and signal strength are not what anyone opened a photograph
            // to look at, and on a 4-inch screen they are a real fraction of it.
            try { await StatusBar.GetForCurrentView().HideAsync(); }
            catch (Exception) { }
        }

        protected override async void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // Put back on the way out. The status bar belongs to the app, not to
            // this page, so leaving it hidden would hide it on every other screen.
            try { await StatusBar.GetForCurrentView().ShowAsync(); }
            catch (Exception) { }
        }

        /// <summary>
        /// Keeps the picture the size of the screen.
        ///
        /// The ScrollViewer would otherwise lay its content out at the bitmap's own
        /// size - see the note in the XAML. Handled on every size change rather than
        /// once at load, because turning the phone is a size change and a picture
        /// fitted to the old orientation is exactly the problem this is fixing.
        /// </summary>
        private void Zoomer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // e.NewSize rather than ViewportWidth/Height: the viewport is a measured
            // value and is still zero on the first pass, which would leave the
            // picture unsized exactly when it is first shown. With the scrollbars
            // hidden they take no room, so the two are the same number anyway.
            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;

            Stage.Width = e.NewSize.Width;
            Stage.Height = e.NewSize.Height;
        }

        /// <summary>
        /// Double tap zooms in on what was tapped, and again to fit.
        ///
        /// ScrollViewer gives pinch-zoom for nothing, but not this - and a phone
        /// photograph is usually looked at with one hand, which does not pinch.
        ///
        /// The zoom is set to an absolute factor rather than multiplied by one, so
        /// the two states are fit and close-up whatever a pinch left behind, and so
        /// nothing goes wrong if the platform ever decides to handle the gesture as
        /// well.
        /// </summary>
        private void Zoomer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            bool zoomedIn = Zoomer.ZoomFactor > 1.01f;

            if (zoomedIn)
            {
                Zoomer.ChangeView(0.0, 0.0, 1f);
                return;
            }

            // Keeping the tapped point under the finger, rather than zooming into
            // the middle: on a photograph the interesting part is rarely the centre,
            // and zooming away from what was pointed at is its own small annoyance.
            Point at = e.GetPosition(Zoomer);
            double scale = ZoomStep / Zoomer.ZoomFactor;

            Zoomer.ChangeView(
                (Zoomer.HorizontalOffset + at.X) * scale - at.X,
                (Zoomer.VerticalOffset + at.Y) * scale - at.Y,
                ZoomStep);
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
