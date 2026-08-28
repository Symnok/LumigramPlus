using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace LumigramPlus.App
{
    /// <summary>
    /// One call at a time, and everything that happens to it.
    ///
    /// The signalling is a conversation held partly through requests we make and
    /// partly through updates the server pushes at us, and the two halves have to be
    /// tracked together. The caller asks and then waits to be told the other side
    /// picked up; the callee is told there is a call and then answers. Neither side's
    /// half makes sense without the other's, so both live here.
    ///
    /// This is the state machine only. Nothing here carries voice - there is no
    /// audio and no media transport yet - so a connected call is silent. That is
    /// deliberate: making it ring end to end against real Telegram is the thing the
    /// harness cannot check, and it is where the previous attempt at this stalled.
    /// </summary>
    internal static class CallService
    {
        /// <summary>The call in progress, or null.</summary>
        public static CallInfo Current;

        /// <summary>Raised on the UI thread whenever the call moves.</summary>
        public static event Action<CallInfo> Changed;

        /// <summary>Raised when someone calls us and nothing is in progress.</summary>
        public static event Action<CallInfo> Incoming;

        private static Calls.DhConfig _config;
        private static byte[] _secret;
        private static byte[] _ourPublic;
        private static byte[] _key;
        private static MtprotoClient _hooked;
        private static bool _subscribed;
        private static CoreDispatcher _dispatcher;

        /// <summary>
        /// What went wrong last, or null.
        ///
        /// A call that stalls looks the same whichever step failed, and every one of
        /// those steps used to be inside a catch that said nothing. This is what
        /// turns "stuck exchanging keys" into a sentence naming the reason.
        /// </summary>
        public static string LastError;

        /// <summary>
        /// The last call update the server pushed, and how many have arrived.
        ///
        /// For the case where nothing happens at all: an incoming call that never
        /// rings is either an update that never came or one that came and was not
        /// acted on, and those need telling apart before either can be fixed.
        /// </summary>
        public static string LastUpdate;
        public static int UpdateCount;

        /// <summary>
        /// Every pushed message, not only the ones about calls.
        ///
        /// Tells a server that is pushing nothing from one that is pushing plenty
        /// and none of it relevant. Those look identical from a call's point of
        /// view and have nothing in common as problems.
        /// </summary>
        public static int PushedCount;

        /// <summary>The last few pushed constructors, for working out what arrives.</summary>
        public static string Pushed = "";

        /// <summary>Whether we placed the call rather than answered it.</summary>
        public static bool WeCalled;

        /// <summary>The shared key, once both ends have finished the exchange.</summary>
        public static byte[] Key { get { return _key; } }

        /// <summary>
        /// Starts watching for call updates.
        ///
        /// Called once the app is signed in. Until something subscribes, an incoming
        /// call arrives on the connection and is dropped on the floor.
        /// </summary>
        public static void Listen(MtprotoClient client)
        {
            if (Window.Current != null) _dispatcher = Window.Current.Dispatcher;

            // Follow the connection rather than binding to whichever one happened to
            // exist first. This was the whole of the incoming-call bug: the app
            // rebuilds its connection on resume, and the handler stayed on the dead
            // one, so a call placed to this phone arrived on a socket nobody was
            // listening to.
            if (!_subscribed)
            {
                _subscribed = true;
                TelegramService.ClientCreated += Hook;
            }

            Hook(client ?? TelegramService.Current);
        }

        private static void Hook(MtprotoClient client)
        {
            if (client == null || ReferenceEquals(client, _hooked)) return;

            if (_hooked != null) _hooked.UpdateReceived -= OnUpdate;

            _hooked = client;
            client.UpdateReceived += OnUpdate;
        }

        /// <summary>
        /// Places a call.
        ///
        /// The hash goes first and the value itself only later: committing to g_a
        /// without revealing it is what stops the other end choosing their g_b after
        /// seeing ours and steering the shared key.
        /// </summary>
        public static async Task<CallInfo> PlaceAsync(MtprotoClient client, DialogItem peer)
        {
            _config = await Calls.GetDhConfigAsync(client, TelegramService.Info);

            _secret = Calls.Secret(TelegramService.Crypto, _config.Random);
            _ourPublic = Calls.PublicValue(_config, _secret);
            _key = null;
            WeCalled = true;

            byte[] hash = TelegramService.Crypto.Sha256(_ourPublic);

            // requestCall wants an InputUser rather than an InputPeer: the same two
            // fields under a different constructor.
            byte[] inputUser = InputUserFrom(peer);

            int randomId = BitConverter.ToInt32(TelegramService.Crypto.Random(4), 0);

            CallInfo call = await Calls.RequestAsync(client, inputUser, randomId, hash,
                                                     false, TelegramService.Info);

            Current = call;
            return call;
        }

        /// <summary>
        /// Answers a call someone placed to us.
        ///
        /// Our g_b goes out in the clear here, which is safe in this direction: the
        /// caller is already committed to a g_a they cannot change.
        /// </summary>
        public static async Task<CallInfo> AnswerAsync(MtprotoClient client, CallInfo call)
        {
            _config = await Calls.GetDhConfigAsync(client, TelegramService.Info);

            _secret = Calls.Secret(TelegramService.Crypto, _config.Random);
            _ourPublic = Calls.PublicValue(_config, _secret);
            _key = null;
            WeCalled = false;

            Current = await Calls.AcceptAsync(client, call, _ourPublic, TelegramService.Info);
            return Current;
        }

        /// <summary>When we last told the server someone is holding this phone.</summary>
        private static DateTime _online = DateTime.MinValue;

        /// <summary>
        /// Reports this client as online, at most every so often.
        ///
        /// Throttled rather than sent on every poll: the server only needs to know
        /// often enough not to let the status lapse, and the chat list ticks far
        /// more often than that.
        /// </summary>
        public static async void KeepOnline(MtprotoClient client)
        {
            if (client == null) return;
            if ((DateTime.UtcNow - _online).TotalSeconds < 45) return;

            _online = DateTime.UtcNow;

            try
            {
                await Messages.SetOnlineAsync(client, true, TelegramService.Info);
            }
            catch (Exception)
            {
                // Presence is not worth reporting; the next tick tries again.
                _online = DateTime.MinValue;
            }
        }

        /// <summary>Tells the caller their call is ringing here.</summary>
        public static async Task RingingAsync(MtprotoClient client, CallInfo call)
        {
            try
            {
                await Calls.ReceivedAsync(client, call, TelegramService.Info);
            }
            catch (Exception)
            {
                // The call still works without it; the caller just hears nothing
                // until we answer.
            }
        }

        public static async Task HangUpAsync(MtprotoClient client, int duration,
                                             CallDiscardReason reason)
        {
            CallInfo call = Current;
            Current = null;
            _key = null;
            _secret = null;

            if (call == null || call.Id == 0) return;

            try
            {
                await Calls.DiscardAsync(client, call, duration, reason, 0,
                                         TelegramService.Info);
            }
            catch (Exception)
            {
                // Already gone, most likely. Nothing useful to do about it.
            }
        }

        /// <summary>
        /// Every pushed update, filtered down to the ones about a call.
        ///
        /// The server wraps updates in several shapes, and an updatePhoneCall can
        /// arrive alone or inside a container, so the search is recursive rather
        /// than a check of the outermost constructor.
        /// </summary>
        private static void OnUpdate(TlObject pushed)
        {
            PushedCount++;

            if (pushed != null)
            {
                // The constructor ids of what actually arrives. Whether an incoming
                // call is missing because nothing is pushed, or because plenty is
                // pushed and none of it is a call, are different problems.
                Pushed = pushed.Ctor.ToString("x8") + " " + Pushed;
                if (Pushed.Length > 80) Pushed = Pushed.Substring(0, 80);
            }

            var calls = new List<TlObject>();
            Collect(pushed, calls);

            if (calls.Count > 0)
            {
                UpdateCount += calls.Count;

                CallInfo first = Calls.Parse(calls[0].Obj("phone_call"));
                LastUpdate = DateTime.Now.ToString("HH:mm:ss") + " " +
                             (first == null ? "unparsed" : first.State.ToString());
            }

            foreach (TlObject update in calls)
            {
                CallInfo call = Calls.Parse(update.Obj("phone_call"));
                if (call == null) continue;

                Handle(call);
            }
        }

        private static void Collect(TlObject o, List<TlObject> found)
        {
            if (o == null) return;

            if (o.Ctor == TlConstructors.UpdatePhoneCall)
            {
                found.Add(o);
                return;
            }

            if (o.Has("update")) Collect(o.Obj("update"), found);

            if (o.Has("updates"))
            {
                foreach (object entry in o.Vec("updates"))
                    Collect(entry as TlObject, found);
            }
        }

        /// <summary>
        /// Whether a call is genuinely in progress.
        ///
        /// A finished call stays in Current so the page can show how it ended, which
        /// is not the same as being busy. Reading the field alone made every call
        /// after the first one look like an interruption, so the second incoming
        /// call of a session was silently ignored and the phone never rang again.
        /// </summary>
        private static bool Busy
        {
            get
            {
                return Current != null &&
                       Current.State != CallState.Discarded &&
                       Current.State != CallState.Empty;
            }
        }

        private static async void Handle(CallInfo call)
        {
            // An update about some other call - one answered on another device, say.
            if (Busy && call.Id != Current.Id && call.State != CallState.Requested)
                return;

            if (call.State == CallState.Requested)
            {
                if (Busy) return;

                Current = call;
                WeCalled = false;
                Raise(Incoming, call);
                return;
            }

            // The other end picked up and sent g_b, so the key can be finished and
            // g_a finally revealed. This is the caller's half of the exchange.
            if (call.State == CallState.Accepted && WeCalled && _secret != null)
            {
                await ConfirmAsync(call);
                return;
            }

            // Both ends are ready. The callee learns the caller's g_a here, checks
            // it against the hash it was promised, and derives the same key.
            if (call.State == CallState.Ready && !WeCalled && _secret != null && _key == null)
            {
                Complete(call);
            }

            Current = call;
            Raise(Changed, call);

            // Let go of the key material the moment the call is over, rather than
            // carrying it until something else happens to replace it.
            if (call.State == CallState.Discarded)
            {
                _key = null;
                _secret = null;
            }
        }

        private static async Task ConfirmAsync(CallInfo call)
        {
            try
            {
                LastError = null;
                _key = Calls.SharedKey(_config, _secret, call.Gb);
                long fingerprint = Calls.Fingerprint(TelegramService.Crypto, _key);

                MtprotoClient client = await TelegramService.ConnectAsync();

                CallInfo confirmed = await Calls.ConfirmAsync(
                    client, call, _ourPublic, fingerprint, TelegramService.Info);

                Current = confirmed;
                Raise(Changed, confirmed);
            }
            catch (Exception ex)
            {
                // Named rather than swallowed. Confirming is where the caller's half
                // of the exchange either completes or does not, and a silent failure
                // here is indistinguishable from the other end never answering.
                var rpc = ex as RpcException;
                LastError = "confirm failed: " + (rpc != null ? rpc.ErrorType : ex.Message);

                Current = call;
                Raise(Changed, call);
            }
        }

        private static void Complete(CallInfo call)
        {
            try
            {
                // The promise made at the start, checked. A g_a that does not match
                // the hash means the caller changed their mind after seeing our g_b,
                // which is the whole attack the commitment exists to prevent.
                if (Current != null && Current.GaHash != null)
                {
                    byte[] hash = TelegramService.Crypto.Sha256(call.GaOrB);

                    if (!CryptoExtensions.ConstantTimeEquals(hash, Current.GaHash))
                        throw new MtprotoException("g_a does not match the hash we were given");
                }

                _key = Calls.SharedKey(_config, _secret, call.GaOrB);

                long ours = Calls.Fingerprint(TelegramService.Crypto, _key);
                if (ours != call.KeyFingerprint)
                    throw new MtprotoException("key fingerprints disagree");
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                LastError = "key failed: " + (rpc != null ? rpc.ErrorType : ex.Message);

                _key = null;
            }
        }

        private static byte[] InputUserFrom(DialogItem peer)
        {
            var q = new TlWriter(24);
            q.WriteConstructor(TlConstructors.InputUser)
             .WriteLong(peer.PeerId)
             .WriteLong(peer.AccessHash);

            return q.ToArray();
        }

        /// <summary>
        /// Raises an event on the UI thread.
        ///
        /// Updates arrive on the connection's receive loop, and a page handler that
        /// touches XAML from there throws on a thread it cannot see.
        /// </summary>
        private static void Raise(Action<CallInfo> handler, CallInfo call)
        {
            if (handler == null) return;

            CoreDispatcher dispatcher = _dispatcher;

            if (dispatcher == null)
            {
                try { handler(call); }
                catch (Exception) { }
                return;
            }

            var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                try { handler(call); }
                catch (Exception) { }
            });
        }
    }
}
