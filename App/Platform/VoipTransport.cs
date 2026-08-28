using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Voip;

namespace LumigramPlus.App
{
    /// <summary>
    /// The voice connection: UDP to a reflector, and the handshake across it.
    ///
    /// Signalling agrees who is calling and what key they share. This is the part
    /// that makes the two phones able to say anything to each other, and it happens
    /// entirely outside MTProto - a plain UDP socket to a relay whose address the
    /// server handed us, with every packet prefixed by the 16-byte peer tag that
    /// tells the relay which call it belongs to.
    ///
    /// Three steps, in order, because each depends on the last:
    ///
    ///   1  A plaintext ping to the relay. This is not a health check - it is how
    ///      the relay learns our address, since nothing can be forwarded to a phone
    ///      behind carrier NAT that has never spoken first. It answers with our own
    ///      address as it sees it.
    ///   2  An encrypted init, repeated until acknowledged, saying what protocol
    ///      and codec we speak.
    ///   3  Their init, which we acknowledge in turn.
    ///
    /// Only after both directions have exchanged init does either end consider the
    /// call up - which is why a call with perfect signalling still sits saying
    /// "connecting" until this runs.
    /// </summary>
    internal sealed class VoipTransport : IDisposable
    {
        /// <summary>How often the loop wakes.</summary>
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

        /// <summary>How often to repeat a handshake step that has not completed.</summary>
        private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// How often to send something once the call is up.
        ///
        /// Not a keep-alive for NAT, though it serves as one. Every packet carries
        /// what we have heard from them, and that is the only evidence the far end
        /// has that its audio is arriving. Sending once every two seconds left
        /// libtgvoip judging the link on a handful of acknowledgements and declaring
        /// a weak signal on a connection that was carrying everything perfectly.
        ///
        /// A real call sends a frame every 20 to 60 ms. Until this end has audio to
        /// send, something empty at a similar rate keeps the picture honest.
        /// </summary>
        private static readonly TimeSpan Filler = TimeSpan.FromMilliseconds(200);

        /// <summary>How often to ask for a round trip time.</summary>
        private static readonly TimeSpan Ping = TimeSpan.FromSeconds(2);

        private readonly byte[] _key;
        private readonly byte[] _peerTag;
        private readonly bool _outgoing;
        private readonly string _host;
        private readonly int _port;

        private DatagramSocket _socket;
        private DataWriter _writer;
        private readonly SemaphoreSlim _sending = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _stop;

        private uint _sequence;
        private uint _theirLastSequence;
        private readonly List<uint> _recent = new List<uint>();

        private bool _registered;
        private bool _sawTheirInit;
        private bool _sawInitAck;

        /// <summary>
        /// Whether the connection has already been declared up.
        ///
        /// The far end repeats its init until acknowledged, so init packets keep
        /// arriving after the handshake is complete. Without this, every one of them
        /// announced the call as newly established - and the listener starts a
        /// microphone, so a second recorder opened alongside the first and both fed
        /// the same connection, each stamping its own timeline. Twice the packets,
        /// two interleaved streams, and speech that arrives shredded.
        /// </summary>
        private bool _announced;
        private DateTime _lastStep = DateTime.MinValue;
        private DateTime _lastFiller = DateTime.MinValue;
        private DateTime _lastAudio = DateTime.MinValue;
        private DateTime _lastPing = DateTime.MinValue;

        /// <summary>Progress, in words, for the page to show.</summary>
        public event Action<string> Progress;

        /// <summary>Raised once when both directions have finished the handshake.</summary>
        public event Action Established;

        /// <summary>An arriving frame of speech, still Opus-encoded.</summary>
        public event Action<byte[], int> Audio;

        public bool IsEstablished { get { return _sawInitAck && _sawTheirInit; } }

        /// <summary>Counters, for a page that has to show what is happening.</summary>
        public int Sent;
        public int Received;
        public string LastError;

        public VoipTransport(CallConnection reflector, byte[] key, bool outgoing)
        {
            _key = key;
            _peerTag = reflector.PeerTag;
            _outgoing = outgoing;
            _host = string.IsNullOrEmpty(reflector.Ip) ? reflector.Ipv6 : reflector.Ip;
            _port = reflector.Port;
        }

        public async Task StartAsync()
        {
            if (_key == null || _key.Length < VoipCrypto.KeyLength)
                throw new InvalidOperationException("the call has no key");

            if (_peerTag == null || _peerTag.Length != 16)
                throw new InvalidOperationException("the reflector gave no peer tag");

            _socket = new DatagramSocket();
            _socket.MessageReceived += OnMessage;

            // Any local port: the relay replies to whatever it sees, and asking for
            // a particular one is a way to fail on a phone that has it taken.
            await _socket.BindServiceNameAsync("");

            IOutputStream stream = await _socket.GetOutputStreamAsync(
                new HostName(_host), _port.ToString());

            _writer = new DataWriter(stream);
            _stop = new CancellationTokenSource();

            Report("finding the relay");

            var ignored = LoopAsync(_stop.Token);
        }

        /// <summary>
        /// Repeats whichever step has not completed yet.
        ///
        /// One loop rather than a timer per step: only one of them is ever the
        /// current one, and the packet to send is a function of what has been heard
        /// so far.
        /// </summary>
        private async Task LoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    DateTime now = DateTime.UtcNow;

                    if (!_registered || !_sawInitAck)
                    {
                        // Still shaking hands. Repeated at a human pace, because the
                        // far end has to answer and hammering it helps nothing.
                        if (now - _lastStep > Retry)
                        {
                            _lastStep = now;

                            if (!_registered) await SendRelayPingAsync();
                            else await SendAsync(VoipPackets.Init(Next(), _theirLastSequence, AckMask()));
                        }
                    }
                    else
                    {
                        // Audio carries acknowledgements as well as speech, so
                        // filler is only needed when nothing is being said.
                        if (now - _lastAudio > Filler && now - _lastFiller > Filler)
                        {
                            _lastFiller = now;
                            await SendPacketAsync(VoipPacketType.Nop);
                        }

                        if (now - _lastPing > Ping)
                        {
                            _lastPing = now;
                            await SendPacketAsync(VoipPacketType.Ping);
                        }
                    }

                    await Task.Delay(Tick, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Hung up.
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Report("connection lost: " + ex.Message);
            }
        }

        /// <summary>
        /// The relay's own protocol, in the clear: three words of -1, then -2 to ask
        /// it who we are, then an id it echoes back.
        ///
        /// Unencrypted because it is addressed to the relay rather than to the other
        /// phone, and the relay has no key - it forwards bytes without being able to
        /// read them.
        /// </summary>
        private async Task SendRelayPingAsync()
        {
            var w = new VoipPackets.Writer(48);

            w.Raw(_peerTag);
            w.Int(-1);
            w.Int(-1);
            w.Int(-1);
            w.Int(-2);
            w.Raw(TelegramService.Crypto.Random(8));

            await SendRawAsync(w.ToArray());
        }

        /// <summary>Sends one Opus frame.</summary>
        public async Task SendAudioAsync(byte[] opus, int timestamp)
        {
            if (!IsEstablished) return;

            _lastAudio = DateTime.UtcNow;

            await SendAsync(VoipPackets.StreamData(Next(), _theirLastSequence, AckMask(),
                                                   opus, timestamp));
        }

        private async Task SendPacketAsync(byte type)
        {
            var w = new VoipPackets.Writer(16);
            VoipPackets.WriteHeader(w, type, Next(), _theirLastSequence, AckMask());

            await SendAsync(w.ToArray());
        }

        private async Task SendAsync(byte[] body)
        {
            await SendRawAsync(VoipCrypto.Seal(TelegramService.Crypto, _key, _peerTag,
                                               body, _outgoing));
        }

        private async Task SendRawAsync(byte[] data)
        {
            // Datagrams are written one at a time: two overlapping StoreAsync calls
            // on one writer interleave their bytes into a single packet.
            await _sending.WaitAsync();

            try
            {
                _writer.WriteBytes(data);
                await _writer.StoreAsync();
                Sent++;
            }
            finally
            {
                _sending.Release();
            }
        }

        private void OnMessage(DatagramSocket socket, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                DataReader reader = args.GetDataReader();

                var data = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(data);

                Received++;
                Handle(data);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        private void Handle(byte[] data)
        {
            if (data.Length < 16) return;

            // Everything from the relay carries our peer tag back. Anything else
            // arrived at this port by accident or by someone probing it.
            if (!CryptoExtensions.ConstantTimeEquals(Slice(data, 0, 16), _peerTag)) return;

            if (IsRelayReply(data))
            {
                if (_registered) return;

                _registered = true;
                Report("relay reachable, saying hello");
                return;
            }

            byte[] body = VoipCrypto.Open(TelegramService.Crypto, _key, data, _outgoing);
            if (body == null) return;

            // A packet that decrypts is proof the relay is forwarding, whether or
            // not its own reply ever arrived.
            _registered = true;

            VoipPacket packet = VoipPackets.Read(body);
            if (packet == null) return;

            Remember(packet.Sequence);

            switch (packet.Type)
            {
                case VoipPacketType.Init:
                    _sawTheirInit = true;

                    var ack = VoipPackets.InitAck(Next(), _theirLastSequence, AckMask());
                    var ignored = SendAsync(ack);

                    Settle();
                    break;

                case VoipPacketType.InitAck:
                    _sawInitAck = true;
                    Settle();
                    break;

                case VoipPacketType.Ping:
                    var pong = SendPacketAsync(VoipPacketType.Pong);
                    break;

                case VoipPacketType.StreamData:
                    Action<byte[], int> audio = Audio;
                    if (audio != null && packet.Audio != null)
                        audio(packet.Audio, packet.Timestamp);
                    break;
            }
        }

        /// <summary>
        /// A reply from the relay rather than from the other phone.
        ///
        /// Recognised by the twelve bytes of 0xFF that follow the tag, which no
        /// encrypted packet can begin with - its message key is a hash.
        /// </summary>
        private static bool IsRelayReply(byte[] data)
        {
            if (data.Length < 32) return false;

            for (int i = 16; i < 28; i++)
                if (data[i] != 0xff) return false;

            return true;
        }

        private void Settle()
        {
            if (!IsEstablished)
            {
                Report(_sawInitAck ? "they acknowledged us" : "they said hello");
                return;
            }

            if (_announced) return;
            _announced = true;

            Report("media connected");

            Action handler = Established;
            if (handler != null) handler();
        }

        private uint Next()
        {
            return ++_sequence;
        }

        /// <summary>
        /// Remembers a sequence number so it can be acknowledged.
        ///
        /// Only the last thirty-two matter - that is all the ack mask can carry -
        /// so the list is kept to that and the rest forgotten.
        /// </summary>
        private void Remember(uint sequence)
        {
            if (sequence > _theirLastSequence) _theirLastSequence = sequence;

            _recent.Add(sequence);
            if (_recent.Count > 64) _recent.RemoveRange(0, _recent.Count - 64);
        }

        /// <summary>
        /// Which of the thirty-two packets before their newest we actually received.
        ///
        /// Bit 31 is the one immediately before, counting down - the far end reads
        /// it that way round, and reversing it turns a clean connection into one
        /// that looks like it is losing everything.
        /// </summary>
        private uint AckMask()
        {
            uint mask = 0;

            for (int i = 0; i < 32; i++)
            {
                if (_recent.Contains(_theirLastSequence - (uint)(i + 1)))
                    mask |= 1u << (31 - i);
            }

            return mask;
        }

        private void Report(string what)
        {
            Action<string> handler = Progress;
            if (handler == null) return;

            try { handler(what); }
            catch (Exception) { }
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            System.Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        public void Dispose()
        {
            if (_stop != null)
            {
                _stop.Cancel();
                _stop = null;
            }

            if (_writer != null)
            {
                try { _writer.Dispose(); }
                catch (Exception) { }
                _writer = null;
            }

            if (_socket != null)
            {
                try { _socket.Dispose(); }
                catch (Exception) { }
                _socket = null;
            }
        }
    }
}
