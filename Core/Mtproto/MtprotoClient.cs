using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>An error returned by the server in place of a result.</summary>
    public sealed class RpcException : Exception
    {
        public int Code { get; private set; }
        public string ErrorType { get; private set; }

        public RpcException(int code, string type)
            : base("RPC error " + code + ": " + type)
        {
            Code = code;
            ErrorType = type;
        }

        /// <summary>
        /// How long the server wants us to wait, or 0 if it did not say.
        ///
        /// FLOOD_WAIT_N is not a refusal - it is the server saying "not yet, try
        /// in N seconds". Treating it as an error surfaces a number the user can
        /// do nothing with, when the right response is to wait that long.
        /// </summary>
        public int FloodWaitSeconds
        {
            get
            {
                const string prefix = "FLOOD_WAIT_";

                if (ErrorType == null) return 0;
                if (!ErrorType.StartsWith(prefix, StringComparison.Ordinal)) return 0;

                int seconds;
                return int.TryParse(ErrorType.Substring(prefix.Length), out seconds)
                    ? seconds : 0;
            }
        }
    }

    /// <summary>
    /// Raised when a request has to be sent again because the condition that made
    /// the server refuse it has now been corrected locally.
    /// </summary>
    public class RetryRequestException : Exception
    {
    }

    /// <summary>Raised when the server rejects a salt; the request must be re-sent.</summary>
    public sealed class BadSaltException : RetryRequestException
    {
    }

    /// <summary>
    /// A connection to one datacenter: handshake, encrypted session, request
    /// correlation, and incoming updates.
    ///
    /// The shape here is driven by how MTProto actually behaves. Replies are not a
    /// stream of answers to questions: a packet may hold a container of several
    /// messages, the server interjects salt corrections and session notices, and it
    /// pushes updates that nobody asked for - at any time, including in the middle
    /// of a request.
    ///
    /// So there is one receive loop that owns the socket. Requests park a
    /// TaskCompletionSource keyed by message id and wait; the loop completes them
    /// when the matching rpc_result arrives, and raises everything else as an
    /// update. Trying to read replies only while a request is outstanding - the
    /// obvious design - drops every update that arrives at any other moment.
    /// </summary>
    public sealed class MtprotoClient : IDisposable
    {
        private readonly ICrypto _crypto;
        private readonly ITransport _transport;
        private readonly Action<string> _log;

        private readonly Dictionary<long, TaskCompletionSource<TlReader>> _pending =
            new Dictionary<long, TaskCompletionSource<TlReader>>();
        private readonly object _sessionLock = new object();
        private readonly object _pendingLock = new object();

        private MtprotoFraming _framing;
        private MtprotoSession _session;
        private AuthKey _authKey;
        private bool _connectionInitialised;
        private volatile bool _running;

        /// <summary>How long a single request waits before giving up.</summary>
        public TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// How often to ping. Telegram closes idle connections, and an idle client
        /// receives no updates - so a client that only ever waits is a client that
        /// silently stops working after a minute or so.
        /// </summary>
        public TimeSpan PingInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// What this client tells the server about itself.
        ///
        /// Set once, and used for initConnection whichever request happens to be
        /// first on the connection. Relying on each call site to pass it was a bug
        /// waiting to happen: most did not, so the first request fell back to
        /// ClientInfo.Default with api_id 0, and Telegram answered
        /// CONNECTION_API_ID_INVALID. Which call went first depended on whether the
        /// stored update position was empty, so it failed only sometimes.
        /// </summary>
        public ClientInfo Info;

        /// <summary>
        /// Raised for every Update the server pushes. Handlers run on the receive
        /// loop, so anything slow or UI-bound must marshal elsewhere.
        /// </summary>
        public event Action<TlObject> UpdateReceived;

        /// <summary>Raised when the receive loop stops because the connection failed.</summary>
        public event Action<Exception> Faulted;

        public MtprotoClient(ICrypto crypto, ITransport transport, Action<string> log = null)
        {
            _crypto = crypto;
            _transport = transport;
            _log = log ?? delegate { };
        }

        public AuthKey AuthKey { get { return _authKey; } }

        public long ServerSalt
        {
            get { lock (_sessionLock) return _session == null ? 0 : _session.ServerSalt; }
        }

        public async Task ConnectAsync(string host, int port)
        {
            await _transport.ConnectAsync(host, port);

            // One framing instance for the whole connection: the intermediate-mode
            // tag is sent once, by whichever packet goes out first.
            _framing = new MtprotoFraming(_transport);

            var handshake = new AuthKeyHandshake(_crypto, _framing, _log);
            _authKey = await handshake.RunAsync();

            _session = new MtprotoSession(_crypto, _authKey);
            StartReceiving();
            StartKeepAlive();
        }

        /// <summary>
        /// Connects using an authorisation key established earlier, skipping the
        /// handshake entirely.
        ///
        /// Auth keys are permanent, not per-connection - that is the point of paying
        /// for the DH exchange once. Reusing a stored key is the normal path after
        /// first launch, and it is also what lets a multi-step login work:
        /// auth.sendCode and auth.signIn must share one authorisation, or the second
        /// call invalidates the first one's code.
        /// </summary>
        public async Task ConnectWithKeyAsync(string host, int port, AuthKey key)
        {
            await _transport.ConnectAsync(host, port);
            _framing = new MtprotoFraming(_transport);
            _authKey = key;
            _session = new MtprotoSession(_crypto, key);
            StartReceiving();
            StartKeepAlive();
        }

        /// <summary>
        /// Pings on a timer for as long as the connection lives.
        ///
        /// Sent as a bare (not content-related) message so it does not consume a
        /// sequence number. disconnect_delay asks the server to drop us if we go
        /// quiet, which converts a silently dead connection into a detectable one.
        /// </summary>
        private void StartKeepAlive()
        {
            Task.Factory.StartNew(async delegate
            {
                var rng = new Random();
                while (_running)
                {
                    await Task.Delay(PingInterval);
                    if (!_running) return;

                    try
                    {
                        var q = new TlWriter(24);
                        q.WriteConstructor(TlConstructors.PingDelayDisconnect)
                         .WriteLong(((long)rng.Next() << 32) | (uint)rng.Next())
                         .WriteInt((int)(PingInterval.TotalSeconds * 2.5));

                        long msgId;
                        byte[] packet;
                        lock (_sessionLock) packet = _session.Encrypt(q.ToArray(), false, out msgId);
                        await _framing.SendPacketAsync(packet);
                    }
                    catch (Exception)
                    {
                        // The receive loop will see the same failure and report it.
                        return;
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        private void StartReceiving()
        {
            _running = true;
            Task.Factory.StartNew(async delegate { await ReceiveLoopAsync(); },
                                  TaskCreationOptions.LongRunning);
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                while (_running)
                {
                    byte[] raw = await _framing.ReceivePacketAsync();

                    long msgId;
                    int seqNo;
                    byte[] body;
                    lock (_sessionLock) body = _session.Decrypt(raw, out msgId, out seqNo);

                    try
                    {
                        Dispatch(new TlReader(body), msgId);
                    }
                    catch (Exception ex)
                    {
                        // One malformed message must not take the connection down;
                        // the next one may be the reply someone is waiting for.
                        _log("   dispatch error: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_running)
                {
                    _running = false;
                    FailAllPending(ex);
                    Action<Exception> handler = Faulted;
                    if (handler != null) handler(ex);
                }
            }
        }

        /// <summary>
        /// Sends a TL request and waits for its result.
        ///
        /// The first call is wrapped in invokeWithLayer/initConnection, telling the
        /// server what layer we speak and what client we are; later calls go bare.
        /// </summary>
        /// <summary>
        /// The longest FLOOD_WAIT this will sit through rather than report.
        ///
        /// Short waits are ordinary - a burst of polling trips them and they pass in
        /// a moment. Anything longer is worth telling the caller about.
        /// </summary>
        private const int MaxFloodWait = 10;

        public async Task<TlReader> InvokeAsync(byte[] body, ClientInfo info = null)
        {
            byte[] payload = _connectionInitialised
                ? body
                : WrapInitConnection(body, info ?? Info ?? ClientInfo.Default);

            // Server salts expire, and a stored session will usually carry a stale
            // one. The server answers bad_server_salt with the correct value and
            // *discards* the request, so the only sensible response is to re-send.
            // Leaving that to callers means every call site must remember, and
            // forgetting looks like a random failure.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    TlReader result = await SendOnceAsync(payload);
                    _connectionInitialised = true;
                    return result;
                }
                catch (RetryRequestException)
                {
                    // The session has been corrected - salt, clock or sequence -
                    // so the same request is simply sent again.
                }
                catch (RpcException ex)
                {
                    // Waited out rather than reported, but only when the wait is
                    // short. A long one means the account is genuinely rate limited
                    // and the caller should hear about it instead of the app going
                    // silent for a quarter of an hour.
                    int seconds = ex.FloodWaitSeconds;
                    if (seconds <= 0 || seconds > MaxFloodWait) throw;

                    // One second over what was asked, because the server measures
                    // from when it saw the request and we are measuring from now.
                    await Task.Delay(TimeSpan.FromSeconds(seconds + 1));
                }
            }

            throw new MtprotoException("the server refused the request three times");
        }

        private async Task<TlReader> SendOnceAsync(byte[] payload)
        {
            long msgId;
            byte[] packet;
            lock (_sessionLock) packet = _session.Encrypt(payload, true, out msgId);

            var tcs = new TaskCompletionSource<TlReader>();
            lock (_pendingLock) _pending[msgId] = tcs;

            try
            {
                await _framing.SendPacketAsync(packet);

                Task delay = Task.Delay(RequestTimeout);
                Task finished = await Task.WhenAny(tcs.Task, delay);

                if (finished != tcs.Task)
                    throw new MtprotoException("request timed out after " +
                                               (int)RequestTimeout.TotalSeconds + "s");

                return await tcs.Task;
            }
            finally
            {
                lock (_pendingLock) _pending.Remove(msgId);
            }
        }

        private byte[] WrapInitConnection(byte[] query, ClientInfo info)
        {
            if (info.ApiId == 0)
                throw new MtprotoException(
                    "no api_id set - initConnection would be rejected as " +
                    "CONNECTION_API_ID_INVALID");

            var init = new TlWriter(query.Length + 128);
            init.WriteConstructor(TlConstructors.InitConnection)
                .WriteInt(0)                       // flags: no proxy, no params
                .WriteInt(info.ApiId)
                .WriteString(info.DeviceModel)
                .WriteString(info.SystemVersion)
                .WriteString(info.AppVersion)
                .WriteString(info.SystemLangCode)
                .WriteString(info.LangPack)
                .WriteString(info.LangCode)
                .WriteRaw(query);

            var outer = new TlWriter(init.Length + 8);
            outer.WriteConstructor(TlConstructors.InvokeWithLayer)
                 .WriteInt(TlConstructors.Layer)
                 .WriteRaw(init.ToArray());
            return outer.ToArray();
        }

        /// <summary>
        /// Seconds of disagreement tolerated before the session re-bases its clock.
        ///
        /// The server accepts message ids within roughly -300..+30 seconds of its
        /// own time, so this leaves a wide margin while ignoring the ordinary
        /// second-or-two of jitter.
        /// </summary>
        private const int ClockToleranceSeconds = 15;

        /// <summary>
        /// Raised when the session's clock has been re-based, with the resulting
        /// offset from the device clock in seconds.
        /// </summary>
        public event Action<int> ClockAdjusted;

        /// <summary>
        /// Learns the server's clock from the id of a message it sent.
        ///
        /// Every server message id carries the server's timestamp, so the time is
        /// already arriving with the traffic and costs nothing to read. Waiting for
        /// the server to complain first works, but only after a request has already
        /// failed - and a stored offset goes stale the moment the user corrects the
        /// device clock, which is the case that produced a wall of
        /// bad_msg_notification 16.
        /// </summary>
        private void ObserveServerTime(long msgId)
        {
            int serverTime = (int)((ulong)msgId >> 32);
            if (serverTime <= 0) return;

            int offset;
            lock (_sessionLock)
            {
                if (Math.Abs(_session.DriftFrom(serverTime)) < ClockToleranceSeconds) return;
                _session.SyncTime(serverTime);
                offset = _session.TimeOffset;
            }

            _log("   device clock is " + (-offset) + " s off; compensating");

            Action<int> handler = ClockAdjusted;
            if (handler != null)
            {
                try { handler(offset); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// The live session.
        ///
        /// Exposed so a test can disturb it deliberately: a wrong device clock
        /// cannot be reproduced any other way without changing the machine's own
        /// time, and the recovery path is worth having a real test for.
        /// </summary>
        public MtprotoSession Session
        {
            get { lock (_sessionLock) return _session; }
        }

        /// <summary>Unwraps one incoming message and routes it.</summary>
        private void Dispatch(TlReader r, long msgId)
        {
            ObserveServerTime(msgId);

            uint type = r.ReadConstructor();

            switch (type)
            {
                case TlConstructors.MsgContainer:
                {
                    int count = r.ReadInt();
                    for (int i = 0; i < count; i++)
                    {
                        long innerId = r.ReadLong();
                        r.ReadInt();                       // seqno
                        int len = r.ReadInt();
                        int end = r.Position + len;

                        try { Dispatch(r, innerId); }
                        catch (Exception ex) { _log("   inner dispatch error: " + ex.Message); }

                        r.Position = end;                  // resync regardless
                    }
                    return;
                }

                case TlConstructors.RpcResult:
                {
                    long reqId = r.ReadLong();

                    TaskCompletionSource<TlReader> tcs;
                    lock (_pendingLock)
                    {
                        if (!_pending.TryGetValue(reqId, out tcs)) tcs = null;
                    }

                    int mark = r.Position;
                    uint inner = r.ReadConstructor();

                    if (inner == TlConstructors.RpcError)
                    {
                        int code = r.ReadInt();
                        string message = r.ReadString();
                        if (tcs != null) tcs.TrySetException(new RpcException(code, message));
                        else _log("   rpc_error for an unknown request: " + message);
                        return;
                    }

                    TlReader payload;
                    if (inner == TlConstructors.GzipPacked)
                    {
                        // Telegram compresses any sizeable result. The decompressed
                        // bytes are a complete TL object, so callers see no
                        // difference.
                        byte[] packed = r.ReadBytes();
                        payload = new TlReader(Inflate.Gunzip(packed));
                    }
                    else
                    {
                        r.Position = mark;
                        payload = r;
                    }

                    if (tcs != null) tcs.TrySetResult(payload);
                    else _log("   result for an unknown request " + reqId.ToString("x16"));
                    return;
                }

                case TlConstructors.GzipPacked:
                {
                    // A pushed message can be compressed too, and until now only
                    // results were unpacked - so anything the server thought worth
                    // compressing was quietly dropped instead of being handled.
                    //
                    // That is most of the interesting ones. A small update like a
                    // status change arrives raw and worked; an updates container
                    // carrying a message or a call brings user objects with it, is
                    // large enough to compress, and vanished.
                    byte[] packed = r.ReadBytes();
                    Dispatch(new TlReader(Inflate.Gunzip(packed)), msgId);
                    return;
                }

                case TlConstructors.NewSessionCreated:
                {
                    r.ReadLong();                          // first_msg_id
                    r.ReadLong();                          // unique_id
                    long salt = r.ReadLong();
                    lock (_sessionLock) _session.ServerSalt = salt;
                    _log("   new_session_created, salt now " + salt.ToString("x16"));
                    return;
                }

                case TlConstructors.BadServerSalt:
                {
                    long badMsgId = r.ReadLong();
                    r.ReadInt();                           // bad_msg_seqno
                    int errorCode = r.ReadInt();
                    long newSalt = r.ReadLong();

                    lock (_sessionLock) _session.ServerSalt = newSalt;
                    _log("   bad_server_salt (" + errorCode + "), corrected");

                    TaskCompletionSource<TlReader> tcs;
                    lock (_pendingLock)
                    {
                        if (!_pending.TryGetValue(badMsgId, out tcs)) tcs = null;
                    }
                    if (tcs != null) tcs.TrySetException(new BadSaltException());
                    return;
                }

                case TlConstructors.BadMsgNotification:
                {
                    long badMsgId = r.ReadLong();
                    r.ReadInt();                           // bad_msg_seqno
                    int code = r.ReadInt();

                    // 16 and 17 mean our message id sits outside the window around
                    // the server's clock, 32 and 33 that the sequence numbering has
                    // diverged. Both are recoverable, and both were previously
                    // reported as a permanent failure - which on a phone with a
                    // drifting clock meant every single request failed forever.
                    Exception failure = null;
                    if (code == 16 || code == 17)
                    {
                        // The id of the message carrying this notification is the
                        // server's own, so its timestamp is the authoritative clock.
                        int serverTime = (int)(msgId >> 32);
                        lock (_sessionLock) _session.SyncTime(serverTime);
                        _log("   bad_msg_notification " + code + ", clock re-synced to the server");
                    }
                    else if (code == 32 || code == 33)
                    {
                        lock (_sessionLock) _session.Renew();
                        // A new session has to introduce itself again.
                        _connectionInitialised = false;
                        _log("   bad_msg_notification " + code + ", started a new session");
                    }
                    else
                    {
                        failure = new MtprotoException("bad_msg_notification code " + code);
                    }

                    TaskCompletionSource<TlReader> tcs;
                    lock (_pendingLock)
                    {
                        if (!_pending.TryGetValue(badMsgId, out tcs)) tcs = null;
                    }

                    if (tcs != null) tcs.TrySetException(failure ?? new RetryRequestException());
                    else if (failure != null) _log("   " + failure.Message);
                    return;
                }

                case TlConstructors.MsgsAck:
                case TlConstructors.Pong:
                    return;

                default:
                    // Anything else the server sends unprompted is an update.
                    RaiseUpdate(r, type);
                    return;
            }
        }

        private void RaiseUpdate(TlReader r, uint type)
        {
            Action<TlObject> handler = UpdateReceived;
            if (handler == null) return;

            if (!TlSchema.IsKnown(type))
            {
                _log("   unknown pushed message 0x" + type.ToString("x8"));
                return;
            }

            TlObject obj = TlSchema.ReadBody(r, type);
            handler(obj);
        }

        private void FailAllPending(Exception ex)
        {
            List<TaskCompletionSource<TlReader>> waiting;
            lock (_pendingLock)
            {
                waiting = new List<TaskCompletionSource<TlReader>>(_pending.Values);
                _pending.Clear();
            }
            foreach (var tcs in waiting) tcs.TrySetException(ex);
        }

        public void Dispose()
        {
            _running = false;
            _transport.Dispose();
        }
    }

    /// <summary>What the client reports about itself in initConnection.</summary>
    public sealed class ClientInfo
    {
        public int ApiId { get; set; }
        public string ApiHash { get; set; }
        public string DeviceModel { get; set; }
        public string SystemVersion { get; set; }
        public string AppVersion { get; set; }
        public string SystemLangCode { get; set; }
        public string LangPack { get; set; }
        public string LangCode { get; set; }

        /// <summary>
        /// A fresh set of defaults, with no api_id - the caller supplies that.
        ///
        /// A property rather than a static field, because every caller treats this
        /// as a starting point and assigns over it. As a shared field those writes
        /// landed on one instance that everything else was reading, so the
        /// credentials one call site set silently became every call site's, and a
        /// path that ran before any of them saw api_id 0.
        /// </summary>
        public static ClientInfo Default
        {
            get
            {
                return new ClientInfo
                {
                    DeviceModel = "Windows Phone",
                    SystemVersion = "8.1",
                    AppVersion = "Lumigram 0.1",
                    SystemLangCode = "en",
                    LangPack = "",
                    LangCode = "en",
                };
            }
        }
    }
}
