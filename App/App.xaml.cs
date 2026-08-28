using System;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LumigramPlus.App
{
    /// <summary>
    /// Bare WinRT application shell.
    ///
    /// No session restore, no navigation, no state: this exists to host one page
    /// that answers two questions and is then thrown away.
    /// </summary>
    public sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // Without this the hardware Back button closes the app from any page.
            // WinRT does not wire it to navigation for you - a Silverlight app gets
            // that behaviour by default, which is why its absence here reads as the
            // app crashing on Back rather than as a missing handler.
            Windows.Phone.UI.Input.HardwareButtons.BackPressed += OnBackPressed;

            // WinRT closes an app's sockets while it is suspended and hands back
            // something that still looks connected. Letting go here means the next
            // use builds a new connection instead of failing on the old one.
            Suspending += OnSuspending;
        }

        private void OnSuspending(object sender,
                                  Windows.ApplicationModel.SuspendingEventArgs e)
        {
            Windows.ApplicationModel.SuspendingDeferral deferral =
                e.SuspendingOperation.GetDeferral();

            try { TelegramService.Disconnect(); }
            catch (Exception) { }

            deferral.Complete();
        }

        private void OnBackPressed(object sender,
                                   Windows.Phone.UI.Input.BackPressedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null || !frame.CanGoBack) return;

            // Unhandled means "close the app", so this has to be claimed explicitly
            // whenever there is somewhere to go back to.
            e.Handled = true;
            frame.GoBack();
        }

        /// <summary>
        /// Where a file picker comes back to.
        ///
        /// The picker does not return a value - it suspends the app and activates it
        /// again with the answer, so this is the only path by which a chosen file
        /// arrives. The page that asked may have been rebuilt in the meantime, which
        /// is why the current one is asked rather than a remembered reference.
        /// </summary>
        protected override async void OnActivated(IActivatedEventArgs args)
        {
            var frame = Window.Current.Content as Frame;

            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }

            // The app may have been shut down while the picker was up, in which
            // case there is no page behind this activation at all. Landing on an
            // empty frame leaves the user stuck on a blank screen with nothing to go
            // back to, which is worse than losing the file they picked.
            if (frame.Content == null)
            {
                SessionStore session = null;

                try { session = await SessionStore.LoadAsync(); }
                catch (Exception) { }

                if (session != null && session.SignedIn)
                {
                    TelegramService.Session = session;
                    frame.Navigate(typeof(ChatsPage));
                }
                else
                {
                    frame.Navigate(typeof(QrLoginPage));
                }
            }

            var target = frame.Content as IFileContinuation;

            if (args.Kind == ActivationKind.PickFileContinuation && target != null)
            {
                target.FilePicked((FileOpenPickerContinuationEventArgs)args);
            }
            else if (args.Kind == ActivationKind.PickSaveFileContinuation && target != null)
            {
                target.SaveLocationPicked((FileSavePickerContinuationEventArgs)args);
            }

            Window.Current.Activate();
        }

        /// <summary>
        /// Opens the app where the user can actually do something.
        ///
        /// Straight to the chats when there is an authorisation, straight to sign-in
        /// when there is not. There used to be a menu in between; a screen whose only
        /// purpose is to be got past is not worth the tap.
        /// </summary>
        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;

            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }

            // A tapped toast launches the app with the argument it was given. It is
            // parked rather than acted on here: opening a conversation needs the
            // access hash, which only the loaded chat list has.
            long tapped = Notifications.PeerFromArgument(e.Arguments);
            if (tapped != 0) Notifications.PendingPeerId = tapped;

            // The app was already running, so nothing below would run and the tap
            // would do nothing at all.
            if (frame.Content != null && tapped != 0)
            {
                // Asked first, because the chat is usually already in the list the
                // app was showing when it was put away. Navigating unconditionally
                // rebuilt the whole page - a chat list, the folders, and then the
                // conversation - all at once on resume, which is what tripped
                // FLOOD_WAIT on a tapped notification.
                var chats = frame.Content as ChatsPage;

                if (chats == null || !chats.OpenPending())
                {
                    frame.Navigate(typeof(ChatsPage));
                    frame.BackStack.Clear();
                }
            }

            if (frame.Content == null)
            {
                SessionStore session = null;

                try { session = await SessionStore.LoadAsync(); }
                catch (Exception) { }

                if (session != null && session.SignedIn)
                {
                    TelegramService.Session = session;
                    frame.Navigate(typeof(ChatsPage));
                }
                else
                {
                    frame.Navigate(typeof(QrLoginPage));
                }
            }

            Window.Current.Activate();

            // Registrations outlive the app, and a control channel does not survive
            // being closed. Reapplying on every launch is what makes the setting
            // mean the same thing after an update, a reinstall, or a swipe out of
            // the task switcher.
            await BackgroundNotifications.ApplyAsync(AppSettings.BackgroundMode);
        }

    }
}
