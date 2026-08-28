using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lumigram.Crypto;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>Where a call has got to.</summary>
    public enum CallState
    {
        /// <summary>Nothing, or a call that has been forgotten.</summary>
        Empty,

        /// <summary>We asked; the other side has not picked up.</summary>
        Waiting,

        /// <summary>Someone is calling us and we have not answered.</summary>
        Requested,

        /// <summary>They answered and sent g_b; the key can now be finished.</summary>
        Accepted,

        /// <summary>Both sides have the key and the connections are known.</summary>
        Ready,

        /// <summary>Over, one way or another.</summary>
        Discarded,
    }

    /// <summary>Why a call ended, as the server names it.</summary>
    public enum CallDiscardReason
    {
        Missed,
        Disconnect,
        Hangup,
        Busy,
    }

    /// <summary>A reflector to send voice through.</summary>
    public sealed class CallConnection
    {
        public long Id;
        public string Ip;
        public string Ipv6;
        public int Port;

        /// <summary>Identifies this call to the reflector; sent with every packet.</summary>
        public byte[] PeerTag;

        public bool Tcp;

        public override string ToString()
        {
            return (Ip ?? Ipv6 ?? "?") + ":" + Port + (Tcp ? " tcp" : " udp");
        }
    }

    /// <summary>
    /// A call, as the server describes it.
    ///
    /// One class for every phoneCall constructor rather than one per state: they are
    /// the same call at different points, the fields that matter change as it moves,
    /// and a caller holding "the call" should not have to swap types underneath
    /// itself when the other side picks up.
    /// </summary>
    public sealed class CallInfo
    {
        public CallState State;
        public long Id;
        public long AccessHash;
        public int Date;

        /// <summary>Who placed the call. Compare with your own id to know which end you are.</summary>
        public long AdminId;
        public long ParticipantId;

        /// <summary>SHA-256 of the caller's g_a, sent before g_a itself.</summary>
        public byte[] GaHash;

        /// <summary>The callee's public value, once they have accepted.</summary>
        public byte[] Gb;

        /// <summary>Whichever public value completes the exchange for this end.</summary>
        public byte[] GaOrB;

        public long KeyFingerprint;
        public bool P2pAllowed;

        public List<CallConnection> Connections = new List<CallConnection>();

        public CallDiscardReason? DiscardReason;
        public int Duration;

        public override string ToString()
        {
            return State + " call " + Id + " (" + Connections.Count + " connections)";
        }
    }

    /// <summary>
    /// Placing, answering and ending calls.
    ///
    /// Signalling only: this negotiates who is talking to whom and what key they
    /// share, and says nothing about carrying the voice. That is a separate protocol
    /// over UDP to the reflectors this hands back.
    ///
    /// The key exchange is Diffie-Hellman with a twist worth understanding, because
    /// the order of the calls only makes sense once you see it. The caller does not
    /// send g_a; it sends SHA-256 of g_a. Only after the callee has committed to
    /// their own g_b does the caller reveal g_a, and the callee checks it against the
    /// hash it was given. Without that, a malicious callee could choose g_b after
    /// seeing g_a and steer the shared key.
    /// </summary>
    public static class Calls
    {
        /// <summary>
        /// The voice protocol versions this client will speak.
        ///
        /// Sent to the other end so both sides can agree. These are libtgvoip's
        /// numbers because that is what every other client runs; claiming a version
        /// means being able to talk to whatever answers.
        /// </summary>
        public const int MinLayer = 65;
        public const int MaxLayer = 92;

        /// <summary>Length of a DH secret exponent, in bytes.</summary>
        private const int SecretBytes = 256;

        /// <summary>
        /// The group everyone agrees on: Telegram's 2048-bit safe prime and its
        /// generator, fetched rather than assumed.
        /// </summary>
        public sealed class DhConfig
        {
            public int G;
            public byte[] P;
            public int Version;

            /// <summary>Server entropy, mixed into our secret rather than trusted as it.</summary>
            public byte[] Random;
        }

        public static async Task<DhConfig> GetDhConfigAsync(MtprotoClient client,
                                                            ClientInfo info = null)
        {
            var q = new TlWriter(16);
            q.WriteConstructor(TlConstructors.MessagesGetDhConfig)
             .WriteInt(0)                  // version 0: we hold nothing cached
             .WriteInt(SecretBytes);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlObject result = TlSchema.ReadObject(r);

            if (result.Ctor != TlConstructors.MessagesDhConfig)
                throw new MtprotoException("the server sent no DH parameters");

            return new DhConfig
            {
                G = Int(result, "g"),
                P = result.Bytes("p"),
                Version = Int(result, "version"),
                Random = result.Has("random") ? result.Bytes("random") : null,
            };
        }

        /// <summary>
        /// A secret exponent, and the public value that goes with it.
        ///
        /// The server's random is mixed in rather than used directly: it is entropy
        /// from a party with an interest in the outcome, so it strengthens our own
        /// and is not allowed to replace it.
        /// </summary>
        public static byte[] Secret(ICrypto crypto, byte[] serverRandom)
        {
            byte[] ours = crypto.Random(SecretBytes);

            if (serverRandom != null)
            {
                for (int i = 0; i < ours.Length && i < serverRandom.Length; i++)
                    ours[i] ^= serverRandom[i];
            }

            return ours;
        }

        /// <summary>g^secret mod p, as 256 bytes.</summary>
        public static byte[] PublicValue(DhConfig config, byte[] secret)
        {
            BigInt p = BigInt.FromBytesBE(config.P);
            BigInt g = BigInt.FromUInt((uint)config.G);
            BigInt x = BigInt.FromBytesBE(secret);

            return BigInt.ModPow(g, x, p).ToBytesBE(SecretBytes);
        }

        /// <summary>
        /// The shared key: their public value raised to our secret.
        ///
        /// Their value is checked before it is used. A g_a of 0 or 1, or anything
        /// close to p, collapses the key to something an eavesdropper can guess -
        /// and it is the other end, not the server, choosing it.
        /// </summary>
        public static byte[] SharedKey(DhConfig config, byte[] secret, byte[] theirs)
        {
            BigInt p = BigInt.FromBytesBE(config.P);
            BigInt other = BigInt.FromBytesBE(theirs);

            DhValidation.ValidatePublicValue(other, p, "g_a_or_b");

            BigInt x = BigInt.FromBytesBE(secret);
            return BigInt.ModPow(other, x, p).ToBytesBE(SecretBytes);
        }

        /// <summary>
        /// The key's fingerprint, as both ends compute it.
        ///
        /// Bytes 12 to 19 of SHA-1 of the key, little-endian - not the first eight,
        /// and not big-endian. It is compared against a number the other side
        /// derived independently, so any difference in convention shows up as a call
        /// that rings and then fails on a mismatch neither side can explain.
        /// </summary>
        public static long Fingerprint(ICrypto crypto, byte[] key)
        {
            byte[] hash = crypto.Sha1(key);

            long value = 0;
            for (int i = 7; i >= 0; i--)
                value = (value << 8) | hash[12 + i];

            return value;
        }

        public static async Task<CallInfo> RequestAsync(MtprotoClient client,
                                                        byte[] inputUser, int randomId,
                                                        byte[] gaHash, bool video = false,
                                                        ClientInfo info = null)
        {
            var q = new TlWriter(128);
            q.WriteConstructor(TlConstructors.PhoneRequestCall)
             .WriteInt(video ? 1 : 0)
             .WriteRaw(inputUser)
             .WriteInt(randomId)
             .WriteBytes(gaHash);

            WriteProtocol(q);

            return await CallAsync(client, q, info);
        }

        public static async Task<CallInfo> AcceptAsync(MtprotoClient client, CallInfo call,
                                                       byte[] gb, ClientInfo info = null)
        {
            var q = new TlWriter(128);
            q.WriteConstructor(TlConstructors.PhoneAcceptCall)
             .WriteRaw(InputCall(call))
             .WriteBytes(gb);

            WriteProtocol(q);

            return await CallAsync(client, q, info);
        }

        public static async Task<CallInfo> ConfirmAsync(MtprotoClient client, CallInfo call,
                                                        byte[] ga, long fingerprint,
                                                        ClientInfo info = null)
        {
            var q = new TlWriter(128);
            q.WriteConstructor(TlConstructors.PhoneConfirmCall)
             .WriteRaw(InputCall(call))
             .WriteBytes(ga)
             .WriteLong(fingerprint);

            WriteProtocol(q);

            return await CallAsync(client, q, info);
        }

        /// <summary>
        /// Tells the server the phone is ringing.
        ///
        /// Without it the caller sees no ringing and the server eventually treats
        /// the call as missed, even though the callee's phone had it all along.
        /// </summary>
        public static async Task ReceivedAsync(MtprotoClient client, CallInfo call,
                                               ClientInfo info = null)
        {
            var q = new TlWriter(32);
            q.WriteConstructor(TlConstructors.PhoneReceivedCall)
             .WriteRaw(InputCall(call));

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlSchema.ReadObject(r);
        }

        public static async Task DiscardAsync(MtprotoClient client, CallInfo call,
                                              int duration, CallDiscardReason reason,
                                              long connectionId = 0,
                                              ClientInfo info = null)
        {
            var q = new TlWriter(48);
            q.WriteConstructor(TlConstructors.PhoneDiscardCall)
             .WriteInt(0)                  // not video
             .WriteRaw(InputCall(call))
             .WriteInt(duration)
             .WriteConstructor(ReasonConstructor(reason))
             .WriteLong(connectionId);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlSchema.ReadObject(r);        // Updates
        }

        private static async Task<CallInfo> CallAsync(MtprotoClient client, TlWriter q,
                                                      ClientInfo info)
        {
            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            TlObject result = TlSchema.ReadObject(r);

            if (!result.Has("phone_call"))
                throw new MtprotoException("the server returned no call");

            return Parse(result.Obj("phone_call"));
        }

        /// <summary>
        /// What we can speak, sent with every signalling call.
        ///
        /// Both transports are offered. Peer to peer is better when it works, and
        /// the reflector is what makes a call connect at all behind carrier NAT -
        /// which on a mobile network is most of the time.
        /// </summary>
        private static void WriteProtocol(TlWriter q)
        {
            q.WriteConstructor(TlConstructors.PhoneCallProtocol)
             .WriteInt(1 | 2)              // udp_p2p, udp_reflector
             .WriteInt(MinLayer)
             .WriteInt(MaxLayer)
             .WriteConstructor(TlConstructors.Vector)
             .WriteInt(2)
             .WriteString("2.4.4")
             .WriteString("2.7.7");
        }

        private static byte[] InputCall(CallInfo call)
        {
            var q = new TlWriter(24);
            q.WriteConstructor(TlConstructors.InputPhoneCall)
             .WriteLong(call.Id)
             .WriteLong(call.AccessHash);

            return q.ToArray();
        }

        private static uint ReasonConstructor(CallDiscardReason reason)
        {
            switch (reason)
            {
                case CallDiscardReason.Missed: return TlConstructors.PhoneCallDiscardReasonMissed;
                case CallDiscardReason.Disconnect: return TlConstructors.PhoneCallDiscardReasonDisconnect;
                case CallDiscardReason.Busy: return TlConstructors.PhoneCallDiscardReasonBusy;
                default: return TlConstructors.PhoneCallDiscardReasonHangup;
            }
        }

        /// <summary>
        /// Reads any of the phoneCall constructors into one shape.
        ///
        /// Returns null for anything that is not a call, so an update carrying
        /// something else does not have to be filtered by the caller.
        /// </summary>
        public static CallInfo Parse(TlObject call)
        {
            if (call == null) return null;

            // Read through Has rather than by name directly: the constructors
            // share a shape but not a field list, and asking phoneCallEmpty for an
            // access hash throws rather than returning nothing.
            var info = new CallInfo
            {
                Id = Long(call, "id"),
                AccessHash = Long(call, "access_hash"),
                Date = Int(call, "date"),
                AdminId = Long(call, "admin_id"),
                ParticipantId = Long(call, "participant_id"),
                KeyFingerprint = Long(call, "key_fingerprint"),
                Duration = Int(call, "duration"),
            };

            if (call.Has("g_a_hash")) info.GaHash = call.Bytes("g_a_hash");
            if (call.Has("g_b")) info.Gb = call.Bytes("g_b");
            if (call.Has("g_a_or_b")) info.GaOrB = call.Bytes("g_a_or_b");

            switch (call.Ctor)
            {
                case TlConstructors.PhoneCallEmpty:
                    info.State = CallState.Empty;
                    break;

                case TlConstructors.PhoneCallWaiting:
                    info.State = CallState.Waiting;
                    break;

                case TlConstructors.PhoneCallRequested:
                    info.State = CallState.Requested;
                    break;

                case TlConstructors.PhoneCallAccepted:
                    info.State = CallState.Accepted;
                    break;

                case TlConstructors.PhoneCall:
                    info.State = CallState.Ready;
                    info.P2pAllowed = call.Has("p2p_allowed");
                    ReadConnections(call, info);
                    break;

                case TlConstructors.PhoneCallDiscarded:
                    info.State = CallState.Discarded;
                    info.DiscardReason = ReadReason(call);
                    break;

                default:
                    return null;
            }

            return info;
        }

        private static void ReadConnections(TlObject call, CallInfo info)
        {
            if (!call.Has("connections")) return;

            foreach (object entry in call.Vec("connections"))
            {
                var connection = entry as TlObject;
                if (connection == null) continue;

                // Only the reflector shape is understood. A webRTC connection is
                // for a transport this client does not speak, and using its
                // addresses as though they were reflectors would fail obscurely.
                if (connection.Ctor != TlConstructors.PhoneConnection) continue;

                info.Connections.Add(new CallConnection
                {
                    Id = Long(connection, "id"),
                    Ip = Str(connection, "ip"),
                    Ipv6 = Str(connection, "ipv6"),
                    Port = Int(connection, "port"),
                    PeerTag = connection.Has("peer_tag") ? connection.Bytes("peer_tag") : null,
                    Tcp = connection.Has("tcp"),
                });
            }
        }

        private static long Long(TlObject o, string name)
        {
            return o.Has(name) ? o.Long(name) : 0;
        }

        private static int Int(TlObject o, string name)
        {
            return o.Has(name) ? o.Int(name) : 0;
        }

        private static string Str(TlObject o, string name)
        {
            return o.Has(name) ? o.Str(name) : null;
        }

        private static CallDiscardReason? ReadReason(TlObject call)
        {
            if (!call.Has("reason")) return null;

            TlObject reason = call.Obj("reason");
            if (reason == null) return null;

            switch (reason.Ctor)
            {
                case TlConstructors.PhoneCallDiscardReasonMissed: return CallDiscardReason.Missed;
                case TlConstructors.PhoneCallDiscardReasonDisconnect: return CallDiscardReason.Disconnect;
                case TlConstructors.PhoneCallDiscardReasonBusy: return CallDiscardReason.Busy;
                case TlConstructors.PhoneCallDiscardReasonHangup: return CallDiscardReason.Hangup;
                default: return null;
            }
        }
    }
}
