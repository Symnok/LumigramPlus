using System;
using System.Threading.Tasks;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// A raw byte pipe to a datacenter. The desktop implements this over
    /// System.Net.Sockets, the phone over Windows.Networking.Sockets.StreamSocket -
    /// neither type exists on the other platform, so the protocol only ever sees
    /// this.
    /// </summary>
    public interface ITransport : IDisposable
    {
        Task ConnectAsync(string host, int port);

        Task SendAsync(byte[] data);

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes, or throws. Framing depends
        /// on being able to demand an exact number of bytes; a partial read that
        /// returns quietly would desynchronise the stream.
        /// </summary>
        Task<byte[]> ReceiveExactAsync(int count);

        bool IsConnected { get; }
    }

    /// <summary>
    /// What a transport that wraps the stream - an MTProto proxy's - needs from the
    /// framing above it.
    /// </summary>
    public interface IFramingOptions
    {
        /// <summary>The transport tag went in the transport's own opening bytes.</summary>
        bool TagSent { get; }

        /// <summary>Packets carry 0-15 random bytes after them ("padded intermediate").</summary>
        bool Padded { get; }

        byte[] NewPadding();
    }

    /// <summary>
    /// MTProto's "intermediate" TCP framing: a one-off 0xeeeeeeee handshake byte
    /// sequence, then every packet prefixed with its little-endian length.
    ///
    /// Chosen over the abridged format because the length field is a plain uint32
    /// in both directions, with no size-dependent special case to get wrong.
    ///
    /// Through an MTProto proxy the tag travels in the proxy's opening bytes
    /// instead, and a "dd" or "ee" proxy wants the padded variant: random bytes
    /// after each packet, counted by its length.
    /// </summary>
    public sealed class MtprotoFraming
    {
        private static readonly byte[] IntermediateTag = { 0xee, 0xee, 0xee, 0xee };

        private readonly ITransport _transport;
        private readonly IFramingOptions _options;
        private bool _tagSent;

        public MtprotoFraming(ITransport transport)
        {
            _transport = transport;
            _options = transport as IFramingOptions;
            _tagSent = _options != null && _options.TagSent;
        }

        private bool Padded { get { return _options != null && _options.Padded; } }

        public async Task SendPacketAsync(byte[] payload)
        {
            if (payload.Length % 4 != 0)
                throw new ArgumentException("MTProto packets are a whole number of 4-byte words");

            byte[] padding = Padded ? _options.NewPadding() : new byte[0];
            int tag = _tagSent ? 0 : 4;

            var frame = new byte[tag + 4 + payload.Length + padding.Length];
            if (!_tagSent)
            {
                Buffer.BlockCopy(IntermediateTag, 0, frame, 0, 4);
                _tagSent = true;
            }

            int len = payload.Length + padding.Length;
            frame[tag] = (byte)len;
            frame[tag + 1] = (byte)(len >> 8);
            frame[tag + 2] = (byte)(len >> 16);
            frame[tag + 3] = (byte)(len >> 24);
            Buffer.BlockCopy(payload, 0, frame, tag + 4, payload.Length);
            Buffer.BlockCopy(padding, 0, frame, tag + 4 + payload.Length, padding.Length);

            await _transport.SendAsync(frame);
        }

        public async Task<byte[]> ReceivePacketAsync()
        {
            byte[] header = await _transport.ReceiveExactAsync(4);
            int len = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);

            if (len < 0 || len > 16 * 1024 * 1024)
                throw new MtprotoException("implausible packet length " + len);

            byte[] packet = await _transport.ReceiveExactAsync(len);

            // The proxy's padding goes before anything tries to read the message.
            if (Padded)
            {
                int real = UnpaddedLength(packet);
                if (real < packet.Length)
                {
                    var trimmed = new byte[real];
                    Buffer.BlockCopy(packet, 0, trimmed, 0, real);
                    packet = trimmed;
                }
            }

            // A 4-byte body is an error code rather than a message: the server
            // reports transport-level rejections this way, as a negative int32.
            if (packet.Length == 4)
            {
                int code = packet[0] | (packet[1] << 8) | (packet[2] << 16) | (packet[3] << 24);
                throw new MtprotoException("transport error from server: " + code);
            }

            return packet;
        }

        /// <summary>
        /// How much of a padded packet is the message.
        ///
        /// The length counts 0-15 random bytes and nothing marks where they start,
        /// so the message has to say where it ends:
        ///   - auth_key_id 0 is an unencrypted message, which states its own length;
        ///   - otherwise it is auth_key_id(8) + msg_key(16) + whole AES blocks, and
        ///     since the padding is under one block, rounding down recovers it;
        ///   - anything shorter is a 4-byte error code.
        /// </summary>
        public static int UnpaddedLength(byte[] packet)
        {
            int size = packet.Length;

            bool unencrypted = size >= 8;
            for (int i = 0; i < 8 && unencrypted; i++) unencrypted = packet[i] == 0;

            if (unencrypted)
            {
                if (size < 20) return size;
                int inner = packet[16] | (packet[17] << 8) | (packet[18] << 16) | (packet[19] << 24);
                return inner >= 0 && 20 + inner <= size ? 20 + inner : size;
            }

            if (size < 24) return Math.Min(size, 4);

            int ciphertext = size - 24;
            return 24 + ciphertext - (ciphertext % 16);
        }
    }

    public class MtprotoException : Exception
    {
        public MtprotoException(string message) : base(message) { }
    }
}
