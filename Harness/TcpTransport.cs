using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Lumigram.Mtproto;

namespace Lumigram.Harness
{
    /// <summary>
    /// Desktop transport over System.Net.Sockets. The phone gets an equivalent built
    /// on Windows.Networking.Sockets.StreamSocket; the protocol above sees neither.
    /// </summary>
    internal sealed class TcpTransport : ITransport
    {
        private TcpClient _client;
        private NetworkStream _stream;

        public bool IsConnected
        {
            get { return _client != null && _client.Connected; }
        }

        public async Task ConnectAsync(string host, int port)
        {
            _client = new TcpClient();
            _client.NoDelay = true;                 // MTProto packets are small and latency-sensitive
            await _client.ConnectAsync(host, port);
            _stream = _client.GetStream();
        }

        public async Task SendAsync(byte[] data)
        {
            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }

        public async Task<byte[]> ReceiveExactAsync(int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await _stream.ReadAsync(buffer, read, count - read);
                if (n <= 0)
                    throw new MtprotoException("connection closed after " + read + " of " + count + " bytes");
                read += n;
            }
            return buffer;
        }

        public void Dispose()
        {
            if (_stream != null) _stream.Dispose();
            if (_client != null) _client.Close();
            _stream = null;
            _client = null;
        }
    }
}
