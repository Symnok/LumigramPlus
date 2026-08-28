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
    /// MTProto's "intermediate" TCP framing: a one-off 0xeeeeeeee handshake byte
    /// sequence, then every packet prefixed with its little-endian length.
    ///
    /// Chosen over the abridged format because the length field is a plain uint32
    /// in both directions, with no size-dependent special case to get wrong.
    /// </summary>
    public sealed class MtprotoFraming
    {
        private static readonly byte[] IntermediateTag = { 0xee, 0xee, 0xee, 0xee };

        private readonly ITransport _transport;
        private bool _tagSent;

        public MtprotoFraming(ITransport transport)
        {
            _transport = transport;
        }

        public async Task SendPacketAsync(byte[] payload)
        {
            if (payload.Length % 4 != 0)
                throw new ArgumentException("MTProto packets are a whole number of 4-byte words");

            byte[] frame;
            int offset = 0;

            if (!_tagSent)
            {
                frame = new byte[4 + 4 + payload.Length];
                Buffer.BlockCopy(IntermediateTag, 0, frame, 0, 4);
                offset = 4;
                _tagSent = true;
            }
            else
            {
                frame = new byte[4 + payload.Length];
            }

            int len = payload.Length;
            frame[offset] = (byte)len;
            frame[offset + 1] = (byte)(len >> 8);
            frame[offset + 2] = (byte)(len >> 16);
            frame[offset + 3] = (byte)(len >> 24);
            Buffer.BlockCopy(payload, 0, frame, offset + 4, payload.Length);

            await _transport.SendAsync(frame);
        }

        public async Task<byte[]> ReceivePacketAsync()
        {
            byte[] header = await _transport.ReceiveExactAsync(4);
            int len = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);

            if (len < 0 || len > 16 * 1024 * 1024)
                throw new MtprotoException("implausible packet length " + len);

            // A 4-byte body is an error code rather than a message: the server
            // reports transport-level rejections this way, as a negative int32.
            if (len == 4)
            {
                byte[] err = await _transport.ReceiveExactAsync(4);
                int code = err[0] | (err[1] << 8) | (err[2] << 16) | (err[3] << 24);
                throw new MtprotoException("transport error from server: " + code);
            }

            return await _transport.ReceiveExactAsync(len);
        }
    }

    public class MtprotoException : Exception
    {
        public MtprotoException(string message) : base(message) { }
    }
}
