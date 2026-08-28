using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Lumigram.Mtproto;
using Lumigram.Qr;

namespace LumigramPlus.App
{
    /// <summary>
    /// Signing in by showing a code for another Telegram to scan.
    ///
    /// The only way in for this client, by choice: the Silverlight app already does
    /// codes by SMS, and a code that has to be typed is the worst part of testing a
    /// client repeatedly.
    ///
    /// The flow has two awkward parts, and both are here because leaving either out
    /// produces a login that appears to work and does not:
    ///
    ///   The token usually points somewhere else. Telegram answers the first export
    ///   with "this account lives on datacenter N", and the token has to be carried
    ///   there and imported. The old connection is kept open while the new one is
    ///   built, because the token expires in seconds and a handshake takes several -
    ///   tear the old one down first and the token is dead before it can be used.
    ///
    ///   Scanning is not signing in. The scan makes the server accept the token; the
    ///   client still has to call importLoginToken to collect the authorisation.
    ///   Without that the phone shows a second session in Telegram's device list and
    ///   is not actually logged in.
    /// </summary>
    public sealed partial class QrLoginPage : Page
    {
        /// <summary>
        /// How often to ask whether the code has been scanned.
        ///
        /// The server also pushes an update when it happens, but polling is what
        /// makes this reliable: dropping it once produced a login that only worked
        /// on the second attempt, every time.
        /// </summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        private MtprotoClient _client;
        private MtprotoClient _moved;
        private int _polls;
        private QrLoginStatus _lastStatus = QrLoginStatus.ShowToken;
        private bool _leaving;

        /// <summary>
        /// Shown on screen so it is never a guess which build is running. Two rounds
        /// of debugging were spent on a fix that was already deployed.
        /// </summary>
        private const string Build = "qr-3";

        public QrLoginPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Start();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _leaving = true;
        }

        private async void Start()
        {
            try
            {
                Log("build " + Build);
                Say("Connecting...");

                Log("[1] connect");
                _client = await TelegramService.ConnectAsync(Say);

                Log("[2] export token");

                QrLoginStep step = await QrLogin.ExportTokenAsync(
                    _client, Secrets.ApiId, Secrets.ApiHash, TelegramService.Info);

                Log("[3] status " + step.Status);

                if (step.Status == QrLoginStatus.Migrate)
                {
                    step = await MigrateAsync(step);
                    if (step == null) return;

                    Log("migrated -> " + step.Status +
                        (step.Token == null ? " (no token)" : ""));
                }

                if (step.Status == QrLoginStatus.Success) { await DoneAsync(); return; }
                if (step.Status == QrLoginStatus.PasswordNeeded) { AskForPassword(); return; }

                Log("[4] showing code, polling");
                Draw(step.Url);
                Say("Waiting for the code to be scanned...");

                await PollAsync(step);
            }
            catch (Exception ex)
            {
                // The type and the top of the stack, not just the message. A null
                // reference says nothing about where it happened, and guessing at
                // that from the flow has already cost two rounds.
                Say("Could not sign in: " + Describe(ex));

                Log(ex.GetType().Name);

                string stack = ex.StackTrace ?? "(no stack)";
                Log(stack.Length > 500 ? stack.Substring(0, 500) : stack);
            }
        }

        /// <summary>
        /// Asks repeatedly whether the token has been accepted, refreshing it before
        /// it expires.
        /// </summary>
        private async Task PollAsync(QrLoginStep step)
        {
            while (!_leaving)
            {
                await Task.Delay(PollInterval);
                if (_leaving) return;

                _polls++;

                QrLoginStep poll;
                try
                {
                    poll = await QrLogin.ImportTokenAsync(_client, step.Token,
                                                          TelegramService.Info);

                    // Any change in the answer is reported the moment it happens -
                    // that is the event being waited for, and averaging it into a
                    // periodic line would hide the one poll that matters. The
                    // periodic line exists only so that "waiting" looks different
                    // from "stopped".
                    if (poll.Status != _lastStatus)
                    {
                        Log("poll " + _polls + ": " + _lastStatus + " -> " + poll.Status);
                        _lastStatus = poll.Status;
                    }
                    else if (_polls % 10 == 0)
                    {
                        Log("poll " + _polls + ": " + poll.Status +
                            ", code valid " + step.SecondsRemaining + "s");
                    }
                }
                catch (RpcException ex)
                {
                    // The token has a life of about a minute; an expired one is
                    // ordinary and simply means asking for another.
                    if (ex.ErrorType != null && ex.ErrorType.Contains("AUTH_TOKEN_EXPIRED"))
                    {
                        step = await RefreshAsync();
                        if (await FinishedAsync(step)) return;
                        continue;
                    }
                    // Anything else stops the loop, but says what it was first: an
                    // unexpected error here is the interesting case, and the outer
                    // handler only reports the message.
                    Log("poll error: " + (ex.ErrorType ?? ex.Message));
                    throw;
                }

                if (poll.Status == QrLoginStatus.Migrate)
                {
                    QrLoginStep moved = await MigrateAsync(poll);
                    if (moved == null) return;

                    if (moved.Status == QrLoginStatus.Success) { await DoneAsync(); return; }
                    if (moved.Status == QrLoginStatus.PasswordNeeded) { AskForPassword(); return; }

                    step = moved;
                    Draw(step.Url);
                    continue;
                }

                if (poll.Status == QrLoginStatus.Success) { await DoneAsync(); return; }
                if (poll.Status == QrLoginStatus.PasswordNeeded) { AskForPassword(); return; }

                if (step.SecondsRemaining <= 5)
                {
                    step = await RefreshAsync();
                    if (await FinishedAsync(step)) return;
                }
            }
        }

        /// <summary>
        /// True when this step ends the loop rather than continuing it.
        ///
        /// Asking for a fresh token can return a finished login instead of one - the
        /// code may have been scanned in the moment between the last poll and the
        /// refresh. Those results carry no token, so treating one as something to
        /// keep polling means importing a null token, which is exactly how this
        /// failed: a null reference from inside QrLogin, several frames away from
        /// the loop that caused it.
        /// </summary>
        private async Task<bool> FinishedAsync(QrLoginStep step)
        {
            if (step == null) return true;

            if (step.Status == QrLoginStatus.Success) { await DoneAsync(); return true; }
            if (step.Status == QrLoginStatus.PasswordNeeded) { AskForPassword(); return true; }

            if (step.Token == null)
            {
                Say("The sign-in code could not be refreshed.");
                return true;
            }

            return false;
        }

        private async Task<QrLoginStep> RefreshAsync()
        {
            Say("The code expired. Getting another...");

            QrLoginStep step = await QrLogin.ExportTokenAsync(_client, Secrets.ApiId, Secrets.ApiHash,
                                                          TelegramService.Info);

            if (step.Status == QrLoginStatus.Migrate)
            {
                step = await MigrateAsync(step);
                if (step == null) return null;
            }

            if (step.Token != null)
            {
                Draw(step.Url);
                Say("Waiting for the code to be scanned...");
            }

            return step;
        }

        /// <summary>
        /// Moves to the datacenter the account belongs to, carrying the token there.
        ///
        /// Both connections are alive at once on purpose. The token expires in
        /// seconds; building the new connection takes several, so a fresh token is
        /// fetched from the old datacenter only once the new one is ready.
        /// </summary>
        /// <summary>
        /// Makes the migrated connection the app's, once there is one to adopt.
        /// </summary>
        private async Task AdoptMovedAsync(QrLoginStep step)
        {
            if (_moved == null) return;

            await TelegramService.AdoptAsync(_moved, step.DcId);
            _client = _moved;
            _moved = null;
        }

        private async Task<QrLoginStep> MigrateAsync(QrLoginStep step)
        {
            try
            {
                Say("This account is on datacenter " + step.DcId + ". Moving...");

                MtprotoClient moved = await TelegramService.ConnectSeparateAsync(step.DcId, Say);
                _moved = moved;

                byte[] token = step.Token;

                // A token fetched before the new connection existed may already have
                // expired while it was being built.
                try
                {
                    QrLoginStep refreshed = await QrLogin.ExportTokenAsync(
                        _client, Secrets.ApiId, Secrets.ApiHash, TelegramService.Info);

                    if (refreshed.Token != null) token = refreshed.Token;
                }
                catch (Exception)
                {
                    // Keep the one already in hand.
                }

                QrLoginStep imported = await QrLogin.ImportTokenAsync(
                    moved, token, TelegramService.Info);

                _moved = moved;
                await AdoptMovedAsync(step);

                return imported;
            }
            catch (RpcException ex) when ((ex.ErrorType ?? "").Contains("SESSION_PASSWORD_NEEDED"))
            {
                // Belt and braces. Core turns this into a PasswordNeeded result, so
                // it should not reach here - but if any call on this path ever
                // raises it again, the move has still succeeded and the only thing
                // left is the password. Reporting it as a failed migration throws
                // away a login that is one step from done, and leaves the password
                // about to be checked against the datacenter we just left.
                await AdoptMovedAsync(step);
                return new QrLoginStep { Status = QrLoginStatus.PasswordNeeded };
            }
            catch (Exception ex)
            {
                Say("Could not move datacenter: " + Describe(ex));
                return null;
            }
        }

        /// <summary>
        /// Swaps the code for the password prompt.
        ///
        /// The code is hidden rather than left in place: it has done its job, and at
        /// roughly 270 pixels plus its instructions it pushed the password box off
        /// the bottom of the screen - present, focusable, and invisible.
        ///
        /// Marshalled explicitly because this is reached from the polling loop, and
        /// touching a control from the wrong thread throws rather than doing
        /// nothing - which would lose the prompt entirely.
        /// </summary>
        private void AskForPassword()
        {
            if (!Dispatcher.HasThreadAccess)
            {
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                                                  delegate { AskForPassword(); });
                return;
            }

            Say("This account has two-step verification.");

            ScanText.Visibility = Visibility.Collapsed;
            QrPlate.Visibility = Visibility.Collapsed;

            PasswordPanel.Visibility = Visibility.Visible;
            PasswordBox.Focus(FocusState.Programmatic);
        }

        private void Password_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;

            e.Handled = true;
            Submit();
        }

        private void Password_Click(object sender, RoutedEventArgs e)
        {
            Submit();
        }

        private async void Submit()
        {
            string password = PasswordBox.Password ?? "";

            Log("password entered, " + password.Length + " characters");

            if (password.Length == 0)
            {
                // Said rather than silently ignored: a button that does nothing and
                // a button that is disabled look identical from the outside.
                Say("Enter the password first.");
                return;
            }

            PasswordButton.IsEnabled = false;

            try
            {
                // Several seconds of arithmetic on this hardware: SRP needs a
                // 2048-bit exponentiation and the key derivation runs 100,000
                // rounds of PBKDF2.
                Log("checking password (this takes several seconds)");
                Say("Checking the password...");

                await Srp.CheckPasswordAsync(_client, TelegramService.Crypto, password,
                                             TelegramService.Info);

                Log("password accepted");
                await DoneAsync();
            }
            catch (Exception ex)
            {
                Say("Password rejected: " + Describe(ex));
                Log("password failed: " + ex.GetType().Name + ": " + Describe(ex));
                PasswordButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// Records the authorisation and says so.
        ///
        /// It used to navigate straight to the main page, which has a sign-in button
        /// of its own - so a successful login looked like being thrown back to the
        /// start. Success is worth stating plainly, especially after a wait of half
        /// a minute with no way to tell what happened.
        /// </summary>
        private async Task DoneAsync()
        {
            await TelegramService.SignedInAsync();

            Log("signed in, session saved");

            _leaving = true;            // nothing left to poll for

            // Straight to the chats. There used to be a "signed in - continue"
            // step here, from when the destination was a menu with a sign-in button
            // on it and arriving there looked like being sent back to the start.
            // The destination is the chat list now, so the confirmation is one tap
            // between the user and the app.
            Frame.Navigate(typeof(ChatsPage));

            // Signing in is not somewhere to come back to.
            Frame.BackStack.Clear();
        }

        /// <summary>
        /// Draws the code into a bitmap at whole-pixel scale.
        ///
        /// The scale has to be an integer: a fractional one blurs the module edges
        /// and a camera reads the result far less reliably.
        /// </summary>
        private void Draw(string url)
        {
            bool[,] modules = QrCode.Encode(url);
            int size = modules.GetLength(0);
            const int quiet = 4;

            int scale = Math.Max(1, 280 / (size + 2 * quiet));
            int pixels = (size + 2 * quiet) * scale;

            // WinRT has no Pixels array - the buffer is written directly, and it is
            // BGRA rather than the ARGB integers the Silverlight version used.
            var bitmap = new WriteableBitmap(pixels, pixels);
            var buffer = new byte[pixels * pixels * 4];

            for (int y = 0; y < pixels; y++)
            {
                int row = y / scale - quiet;
                for (int x = 0; x < pixels; x++)
                {
                    int col = x / scale - quiet;
                    bool dark = row >= 0 && col >= 0 && row < size && col < size &&
                                modules[row, col];

                    byte value = dark ? (byte)0 : (byte)255;
                    int at = (y * pixels + x) * 4;

                    buffer[at] = value;         // B
                    buffer[at + 1] = value;     // G
                    buffer[at + 2] = value;     // R
                    buffer[at + 3] = 255;       // A
                }
            }

            using (System.IO.Stream stream = bitmap.PixelBuffer.AsStream())
                stream.Write(buffer, 0, buffer.Length);

            bitmap.Invalidate();

            QrImage.Source = bitmap;
            QrImage.Width = pixels;
            QrImage.Height = pixels;
        }

        private static string Describe(Exception ex)
        {
            var rpc = ex as RpcException;
            return rpc != null ? rpc.ErrorType : ex.Message;
        }

        /// <summary>How many trace lines are kept. Enough to see the last few steps.</summary>
        private const int LogLines = 6;

        private void Log(string line)
        {
            if (Dispatcher.HasThreadAccess)
            {
                string text = LogText.Text;
                text += (text.Length > 0 ? Environment.NewLine : "") + line;

                // Only the tail is kept. The whole history is of no use on a phone
                // screen, and letting it grow is what pushed the code out of view.
                string[] lines = text.Split(new[] { Environment.NewLine },
                                            StringSplitOptions.None);

                if (lines.Length > LogLines)
                    text = string.Join(Environment.NewLine,
                                       lines, lines.Length - LogLines, LogLines);

                LogText.Text = text;
                return;
            }

            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                                              delegate { Log(line); });
        }

        private void Say(string text)
        {
            if (Dispatcher.HasThreadAccess)
            {
                StatusText.Text = text ?? "";
                return;
            }

            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                                              delegate { Say(text); });
        }
    }
}
