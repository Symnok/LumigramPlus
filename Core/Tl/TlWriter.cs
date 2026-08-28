using System;

namespace Lumigram.Tl
{
    /// <summary>
    /// Writes TL (Type Language) binary, the encoding MTProto carries every request
    /// and response in.
    ///
    /// Everything is little-endian and 4-byte aligned. The one irregular case is
    /// the byte string, which uses a short or long length prefix and then pads to
    /// the next 4-byte boundary - get that padding wrong and the server rejects the
    /// whole message with no useful diagnostic, so it is centralised here.
    /// </summary>
    public sealed class TlWriter
    {
        private byte[] _buf;
        private int _len;

        public TlWriter(int capacity = 256)
        {
            _buf = new byte[capacity < 16 ? 16 : capacity];
        }

        public int Length { get { return _len; } }

        public byte[] ToArray()
        {
            var r = new byte[_len];
            Buffer.BlockCopy(_buf, 0, r, 0, _len);
            return r;
        }

        private void Need(int extra)
        {
            if (_len + extra <= _buf.Length) return;
            int cap = _buf.Length * 2;
            while (cap < _len + extra) cap *= 2;
            var b = new byte[cap];
            Buffer.BlockCopy(_buf, 0, b, 0, _len);
            _buf = b;
        }

        public TlWriter WriteByte(byte v)
        {
            Need(1);
            _buf[_len++] = v;
            return this;
        }

        public TlWriter WriteInt(int v)
        {
            Need(4);
            _buf[_len++] = (byte)v;
            _buf[_len++] = (byte)(v >> 8);
            _buf[_len++] = (byte)(v >> 16);
            _buf[_len++] = (byte)(v >> 24);
            return this;
        }

        public TlWriter WriteUInt(uint v) { return WriteInt(unchecked((int)v)); }

        /// <summary>Constructor ids are written exactly like an int; named for readability.</summary>
        public TlWriter WriteConstructor(uint id) { return WriteUInt(id); }

        public TlWriter WriteLong(long v)
        {
            Need(8);
            for (int i = 0; i < 8; i++) _buf[_len++] = (byte)(v >> (8 * i));
            return this;
        }

        public TlWriter WriteDouble(double v)
        {
            return WriteLong(BitConverter.DoubleToInt64Bits(v));
        }

        /// <summary>Raw bytes with no length prefix and no padding - int128, int256, key material.</summary>
        public TlWriter WriteRaw(byte[] data)
        {
            Need(data.Length);
            Buffer.BlockCopy(data, 0, _buf, _len, data.Length);
            _len += data.Length;
            return this;
        }

        /// <summary>
        /// TL byte string. Lengths below 254 use a single length byte; longer ones
        /// use 0xFE followed by a 3-byte length. Either way the total is padded with
        /// zeros to a multiple of 4.
        /// </summary>
        public TlWriter WriteBytes(byte[] data)
        {
            if (data == null) data = new byte[0];
            int start = _len;

            if (data.Length <= 253)
            {
                WriteByte((byte)data.Length);
            }
            else
            {
                WriteByte(0xFE);
                WriteByte((byte)data.Length);
                WriteByte((byte)(data.Length >> 8));
                WriteByte((byte)(data.Length >> 16));
            }

            WriteRaw(data);

            int pad = (4 - ((_len - start) & 3)) & 3;
            for (int i = 0; i < pad; i++) WriteByte(0);
            return this;
        }

        public TlWriter WriteString(string s)
        {
            return WriteBytes(s == null ? new byte[0] : System.Text.Encoding.UTF8.GetBytes(s));
        }

        public TlWriter WriteBool(bool v)
        {
            return WriteConstructor(v ? TlConstructors.BoolTrue : TlConstructors.BoolFalse);
        }

        public TlWriter WriteVectorOfLong(long[] items)
        {
            WriteConstructor(TlConstructors.Vector);
            WriteInt(items.Length);
            for (int i = 0; i < items.Length; i++) WriteLong(items[i]);
            return this;
        }
    }
}
