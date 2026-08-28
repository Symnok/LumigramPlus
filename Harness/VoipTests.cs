using System;
using Lumigram.Crypto;
using Lumigram.Voip;

namespace Lumigram.Harness
{
    /// <summary>
    /// The voice packet format, checked without a network.
    ///
    /// Everything here fails the same way when it is wrong: the far end decrypts
    /// rubbish, drops the packet, and says nothing. There is no error to read and no
    /// reply to wait for - a call simply stays silent. So the wire format is worth
    /// pinning down here, where a mistake is a failing check rather than a fortnight
    /// of staring at a phone.
    ///
    /// The values are taken from libtgvoip's VoIPController::SendPacket, KDF2 and
    /// WritePacketHeader, which is what the other end of any real call is running.
    /// </summary>
    internal static class VoipTests
    {
        private static int _checks;
        private static int _failures;

        public static bool RunAll()
        {
            _checks = 0;
            _failures = 0;

            var crypto = new DesktopCrypto();
            byte[] key = Key();

            Section("a sealed packet opens at the other end");
            {
                var tag = new byte[16];
                for (int i = 0; i < tag.Length; i++) tag[i] = (byte)(0xa0 + i);

                byte[] body = VoipPackets.Init(1, 0, 0);

                // We placed the call, so we seal as outgoing; they answered, so they
                // open with the offsets the other way round.
                byte[] sealed_ = VoipCrypto.Seal(crypto, key, tag, body, true);

                Same("the peer tag leads, in the clear", tag, Slice(sealed_, 0, 16));
                Eq("length is a whole number of blocks", 0, (sealed_.Length - 32) % 16);

                byte[] opened = VoipCrypto.Open(crypto, key, sealed_, false);
                Same("the body survives the round trip", body, opened);
            }

            Section("a packet cannot be opened by the wrong end");
            {
                var tag = new byte[16];
                byte[] body = VoipPackets.Init(1, 0, 0);

                byte[] sealed_ = VoipCrypto.Seal(crypto, key, tag, body, true);

                // The offset x is what separates the two directions. Opening with
                // our own role rather than theirs means reading our own keystream,
                // and it has to fail rather than return plausible rubbish.
                Null("opened with the sender's own offset", VoipCrypto.Open(crypto, key, sealed_, true));
            }

            Section("a tampered packet is refused");
            {
                var tag = new byte[16];
                byte[] sealed_ = VoipCrypto.Seal(crypto, key, tag, VoipPackets.Init(1, 0, 0), true);

                byte[] bent = (byte[])sealed_.Clone();
                bent[bent.Length - 1] ^= 0x01;
                Null("a flipped ciphertext bit", VoipCrypto.Open(crypto, key, bent, false));

                bent = (byte[])sealed_.Clone();
                bent[20] ^= 0x01;
                Null("a flipped message key bit", VoipCrypto.Open(crypto, key, bent, false));

                Null("something far too short", VoipCrypto.Open(crypto, key, new byte[20], false));
            }

            Section("padding hides the length");
            {
                var tag = new byte[16];

                // Two bodies one byte apart must not produce packets one byte apart:
                // padding is at least a whole block, so the sizes step rather than
                // track.
                byte[] a = VoipCrypto.Seal(crypto, key, tag, new byte[40], true);
                byte[] b = VoipCrypto.Seal(crypto, key, tag, new byte[41], true);

                Eq("40 bytes", 0, (a.Length - 32) % 16);
                Eq("41 bytes", 0, (b.Length - 32) % 16);
                Eq("a whole block of padding at least", true, a.Length >= 32 + 40 + 16);
            }

            Section("the header is what libtgvoip expects");
            {
                byte[] init = VoipPackets.Init(7, 5, 0xdeadbeef);

                Eq("type is init", (byte)1, init[0]);
                Eq("their last sequence", 5, ReadInt(init, 1));
                Eq("our sequence", 7, ReadInt(init, 5));
                Eq("ack mask", unchecked((int)0xdeadbeef), ReadInt(init, 9));
                Eq("no extras", (byte)0, init[13]);

                Eq("protocol version", 9, ReadInt(init, 14));
                Eq("minimum version", 3, ReadInt(init, 18));
                Eq("flags", 0, ReadInt(init, 22));
                Eq("one audio codec", (byte)1, init[26]);
                Eq("codec is OPUS", 0x4F505553, ReadInt(init, 27));
            }

            Section("audio frames round-trip through the framing");
            {
                var opus = new byte[47];
                for (int i = 0; i < opus.Length; i++) opus[i] = (byte)(i * 3);

                VoipPacket small = VoipPackets.Read(
                    VoipPackets.StreamData(11, 10, 0, opus, 4000));

                Eq("type is stream data", (byte)4, small.Type);
                Eq("sequence", 11u, small.Sequence);
                Eq("timestamp", 4000, small.Timestamp);
                Same("the frame itself", opus, small.Audio);

                // Over 255 bytes the length becomes 16-bit and a flag says so. A
                // reader that misses the flag reads the length as one byte and
                // everything after it is garbage.
                var big = new byte[400];
                for (int i = 0; i < big.Length; i++) big[i] = (byte)i;

                VoipPacket large = VoipPackets.Read(
                    VoipPackets.StreamData(12, 11, 0, big, 4020));

                Eq("long frame length", 400, large.Audio.Length);
                Same("long frame contents", big, large.Audio);
                Eq("long frame timestamp", 4020, large.Timestamp);
            }

            Section("a truncated packet is refused rather than guessed at");
            {
                byte[] full = VoipPackets.StreamData(1, 0, 0, new byte[60], 20);

                for (int cut = 1; cut < 20; cut++)
                {
                    var short_ = new byte[full.Length - cut];
                    Buffer.BlockCopy(full, 0, short_, 0, short_.Length);

                    _checks++;
                    VoipPacket read = VoipPackets.Read(short_);

                    if (read != null && read.Audio != null && read.Audio.Length == 60)
                        Fail("cut by " + cut, "read a full frame out of a short packet");
                }
            }

            Console.WriteLine();
            Console.WriteLine("  {0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        private static byte[] Key()
        {
            var key = new byte[256];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 5 + 11);
            return key;
        }

        private static int ReadInt(byte[] data, int at)
        {
            return data[at] | (data[at + 1] << 8) | (data[at + 2] << 16) | (data[at + 3] << 24);
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("  [{0}]", title);
        }

        private static void Eq(string what, object expected, object actual)
        {
            _checks++;
            if (Equals(expected, actual)) return;

            Fail(what, "expected " + expected + ", got " + actual);
        }

        private static void Null(string what, object actual)
        {
            _checks++;
            if (actual == null) return;

            Fail(what, "expected nothing, got something");
        }

        private static void Same(string what, byte[] a, byte[] b)
        {
            _checks++;

            if (a != null && b != null && a.Length == b.Length)
            {
                bool same = true;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { same = false; break; }
                if (same) return;
            }

            Fail(what, "arrays differ");
        }

        private static void Fail(string what, string detail)
        {
            _failures++;
            if (_failures <= 8) Console.WriteLine("    FAIL {0}: {1}", what, detail);
        }
    }
}
