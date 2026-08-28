using System;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// TL serialisation tests, plus the RSA key fingerprint check.
    ///
    /// The fingerprint check is the valuable one. Telegram's key fingerprints are
    /// the low 64 bits of SHA1 over the TL serialisation of (modulus, exponent), so
    /// deriving them from the PEMs and matching the published values exercises the
    /// PEM decoder, the DER parser, TL byte-string encoding *including its padding*,
    /// and SHA1 - all against an external source of truth, before any of it is
    /// pointed at a live server.
    /// </summary>
    internal static class TlTests
    {
        private static int _checks;
        private static int _failures;

        public static bool RunAll()
        {
            var rng = new Random(20260825);
            var crypto = new DesktopCrypto();

            Section("primitives round-trip");
            for (int i = 0; i < 500; i++)
            {
                int a = rng.Next(int.MinValue, int.MaxValue);
                long b = ((long)rng.Next() << 32) ^ rng.Next();
                double d = rng.NextDouble() * 1e9;

                var w = new TlWriter();
                w.WriteInt(a).WriteLong(b).WriteDouble(d).WriteBool(true).WriteBool(false);

                var r = new TlReader(w.ToArray());
                Eq("int", a, r.ReadInt());
                Eq("long", b, r.ReadLong());
                Eq("double", d, r.ReadDouble());
                Eq("true", true, r.ReadBool());
                Eq("false", false, r.ReadBool());
                Eq("consumed", 0, r.Remaining);
            }

            Section("sendMessage and forwardMessages");
            {
                byte[] peer = Messages.InputPeerFor("user", 1234567, 0x1122334455667788L);
                byte[] other = Messages.InputPeerFor("channel", 998877, 0x0102030405060708L);

                // A plain send: flags clear, and nothing between the peer and the
                // text. A stray reply_to here would be read as the message body.
                {
                    var r = new TlReader(Messages.SendTextBody(peer, "hello", 42L, 0));

                    Eq("send ctor", TlConstructors.MessagesSendMessage, r.ReadConstructor());
                    Eq("send flags", 0, r.ReadInt());
                    Eq("send peer", TlConstructors.InputPeerUser, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("send text", "hello", r.ReadString());
                    Eq("send random", 42L, r.ReadLong());
                    Eq("send consumed", 0, r.Remaining);
                }

                // A reply: flags.0 set, and a boxed InputReplyTo before the text.
                // The message id is the second field of that box, after its own
                // flags - written first, the server sees a reply to message 0.
                {
                    var r = new TlReader(Messages.SendTextBody(peer, "hi", 7L, 91011));

                    Eq("reply ctor", TlConstructors.MessagesSendMessage, r.ReadConstructor());
                    Eq("reply flags", 1, r.ReadInt());
                    Eq("reply peer", TlConstructors.InputPeerUser, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("reply box", TlConstructors.InputReplyToMessage, r.ReadConstructor());
                    Eq("reply box flags", 0, r.ReadInt());
                    Eq("reply to id", 91011, r.ReadInt());
                    Eq("reply text", "hi", r.ReadString());
                    Eq("reply random", 7L, r.ReadLong());
                    Eq("reply consumed", 0, r.Remaining);
                }

                // Forwarding: two vectors of the same length, ids then random ids,
                // and the destination last. Swapping the peers sends the copy back
                // where it came from.
                {
                    var ids = new int[] { 5, 6, 7 };
                    var randoms = new long[] { 100L, 200L, 300L };

                    var r = new TlReader(Messages.ForwardBody(peer, ids, randoms, other));

                    Eq("fwd ctor", TlConstructors.MessagesForwardMessages, r.ReadConstructor());
                    Eq("fwd flags", 0, r.ReadInt());
                    Eq("fwd from", TlConstructors.InputPeerUser, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();

                    Eq("fwd ids vector", TlConstructors.Vector, r.ReadConstructor());
                    Eq("fwd id count", 3, r.ReadInt());
                    for (int i = 0; i < ids.Length; i++)
                        Eq("fwd id " + i, ids[i], r.ReadInt());

                    Eq("fwd random vector", TlConstructors.Vector, r.ReadConstructor());
                    Eq("fwd random count", 3, r.ReadInt());
                    for (int i = 0; i < randoms.Length; i++)
                        Eq("fwd random " + i, randoms[i], r.ReadLong());

                    Eq("fwd to", TlConstructors.InputPeerChannel, r.ReadConstructor());
                    r.ReadLong(); r.ReadLong();
                    Eq("fwd consumed", 0, r.Remaining);
                }
            }

            Section("byte strings and padding");
            {
                // Lengths around every boundary that changes the encoding.
                int[] lengths = { 0, 1, 2, 3, 4, 5, 15, 16, 17, 252, 253, 254, 255, 256, 257, 1000, 65535, 65536 };
                foreach (int len in lengths)
                {
                    var data = new byte[len];
                    rng.NextBytes(data);

                    var w = new TlWriter();
                    w.WriteBytes(data);
                    byte[] wire = w.ToArray();

                    _checks++;
                    if (wire.Length % 4 != 0)
                        Fail("padding", "len " + len + " produced " + wire.Length + " bytes, not a multiple of 4");

                    var back = new TlReader(wire).ReadBytes();
                    _checks++;
                    if (!Same(data, back)) Fail("bytes", "round-trip failed at length " + len);
                }
            }

            Section("strings and vectors");
            {
                var w = new TlWriter();
                w.WriteString("hello é世界");        // non-ASCII must survive
                w.WriteVectorOfLong(new long[] { 1, -2, long.MaxValue, long.MinValue });

                var r = new TlReader(w.ToArray());
                Eq("string", "hello é世界", r.ReadString());
                var v = r.ReadVectorOfLong();
                Eq("vector len", 4, v.Length);
                Eq("vector[3]", long.MinValue, v[3]);
            }

            Section("reader rejects malformed input");
            {
                ExpectThrow("short buffer", delegate { new TlReader(new byte[2]).ReadInt(); });
                ExpectThrow("bad length prefix", delegate { new TlReader(new byte[] { 0xFF, 0, 0, 0 }).ReadBytes(); });
                ExpectThrow("length past end", delegate { new TlReader(new byte[] { 0x40, 1, 2, 3 }).ReadBytes(); });
                ExpectThrow("wrong constructor", delegate { new TlReader(new byte[] { 1, 2, 3, 4 }).Expect(TlConstructors.ResPQ, "resPQ"); });
            }

            Section("RSA keys: derived fingerprints vs published");
            {
                var keys = TelegramServers.LoadPublicKeys(crypto);
                Eq("key count", TelegramServers.ExpectedFingerprints.Length, keys.Count);

                for (int i = 0; i < keys.Count; i++)
                {
                    long expected = TelegramServers.ExpectedFingerprints[i];
                    _checks++;
                    if (keys[i].Fingerprint != expected)
                    {
                        Fail("fingerprint[" + i + "]",
                             "derived " + keys[i].Fingerprint.ToString("x16") +
                             ", published " + expected.ToString("x16"));
                    }
                    else
                    {
                        Console.WriteLine("    key {0}: {1:x16}  ({2}-bit modulus)",
                                          i, keys[i].Fingerprint, keys[i].Modulus.BitLength);
                    }
                }
            }

            Section("RSA exponentiation shape");
            {
                var keys = TelegramServers.LoadPublicKeys(crypto);
                var key = keys[0];

                var block = new byte[255];
                rng.NextBytes(block);
                byte[] enc = key.Encrypt(block);

                Eq("output size", 256, enc.Length);

                // The public exponent is 65537 for all of Telegram's keys.
                Eq("exponent", "010001", key.Exponent.ToBytesBE().ToHex());

                // A block that is not smaller than the modulus must be refused
                // rather than silently reduced.
                var tooBig = new byte[256];
                for (int i = 0; i < 256; i++) tooBig[i] = 0xFF;
                ExpectThrow("oversized block", delegate { key.Encrypt(tooBig); });
            }

            Section("gzip inflate vs framework");
            {
                // Round-trip data the framework compressed. Hand-written inflate is
                // exactly the kind of code that works on simple input and fails on
                // real payloads, so the cases below cover the three block types:
                // highly compressible (dynamic Huffman), random (stored/fixed), and
                // repetitive runs that exercise overlapping back-references.
                foreach (int size in new[] { 1, 2, 100, 1000, 50000 })
                {
                    Check("random", RandomData(rng, size));
                    Check("compressible", CompressibleData(size));
                    Check("repetitive", RepetitiveData(size));
                }
            }

            Console.WriteLine();
            Console.WriteLine("{0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        private static byte[] RandomData(Random rng, int n)
        {
            var b = new byte[n];
            rng.NextBytes(b);
            return b;
        }

        private static byte[] CompressibleData(int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)('a' + (i % 4));
            return b;
        }

        private static byte[] RepetitiveData(int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = 0x5A;
            return b;
        }

        private static void Check(string what, byte[] original)
        {
            byte[] gz = GzipCompress(original);
            byte[] back;
            try
            {
                back = Lumigram.Tl.Inflate.Gunzip(gz);
            }
            catch (Exception ex)
            {
                _checks++;
                Fail("inflate " + what + "/" + original.Length, ex.GetType().Name + ": " + ex.Message);
                return;
            }

            _checks++;
            if (!Same(original, back))
                Fail("inflate " + what + "/" + original.Length,
                     "got " + back.Length + " bytes, expected " + original.Length);
        }

        private static byte[] GzipCompress(byte[] data)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms,
                           System.IO.Compression.CompressionMode.Compress, true))
                {
                    gz.Write(data, 0, data.Length);
                }
                return ms.ToArray();
            }
        }

        private static void Section(string name)
        {
            Console.WriteLine();
            Console.WriteLine("  [{0}]", name);
        }

        private static void Eq(string what, object expected, object actual)
        {
            _checks++;
            if (!Equals(expected, actual)) Fail(what, expected + " != " + actual);
        }

        private static void ExpectThrow(string what, Action action)
        {
            _checks++;
            try
            {
                action();
                Fail(what, "expected an exception, none thrown");
            }
            catch (TlParseException) { }
            catch (ArgumentException) { }
            catch (FormatException) { }
            catch (Exception ex)
            {
                Fail(what, "unexpected " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Fail(string what, string detail)
        {
            _failures++;
            if (_failures <= 6) Console.WriteLine("    FAIL {0}: {1}", what, detail);
            else if (_failures == 7) Console.WriteLine("    ... further failures suppressed");
        }
    }
}
