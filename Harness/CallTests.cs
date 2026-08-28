using System;
using Lumigram.Crypto;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace Lumigram.Harness
{
    /// <summary>
    /// The call key exchange, checked without a network.
    ///
    /// Everything here is the part of a call that fails silently. A shared key
    /// derived correctly on one side and not the other does not throw - it produces
    /// two different 256-byte arrays, the fingerprints disagree, and the call is
    /// refused by the far end with no clue as to which of the two is wrong. A
    /// fingerprint assembled from the wrong eight bytes, or the right eight in the
    /// wrong order, fails exactly the same way.
    ///
    /// So the properties worth pinning are the ones both ends have to agree on
    /// independently: that the exchange is symmetric, and that the fingerprint is
    /// byte-for-byte what every other client computes.
    /// </summary>
    internal static class CallTests
    {
        private static int _checks;
        private static int _failures;

        public static bool RunAll()
        {
            _checks = 0;
            _failures = 0;

            var crypto = new DesktopCrypto();

            Section("Diffie-Hellman is symmetric");
            {
                Calls.DhConfig config = Group();

                // Short exponents, because this runs on every build. The property
                // being checked - that both ends land on the same key - does not
                // depend on the length, and a full 2048-bit exponent costs seconds.
                byte[] a = Exponent(crypto, 0x11);
                byte[] b = Exponent(crypto, 0x22);

                byte[] ga = Calls.PublicValue(config, a);
                byte[] gb = Calls.PublicValue(config, b);

                Eq("g_a is 256 bytes", 256, ga.Length);
                Eq("g_b is 256 bytes", 256, gb.Length);

                byte[] callerKey = Calls.SharedKey(config, a, gb);
                byte[] calleeKey = Calls.SharedKey(config, b, ga);

                Same("both ends derive the same key", callerKey, calleeKey);
                Eq("key is 256 bytes", 256, callerKey.Length);

                Eq("fingerprints agree",
                   Calls.Fingerprint(crypto, callerKey),
                   Calls.Fingerprint(crypto, calleeKey));
            }

            Section("the fingerprint is bytes 12..19 of SHA-1, little-endian");
            {
                // Assembled here from the hash by hand, so a change to the packing
                // in Calls fails rather than being mirrored by the test.
                var key = new byte[256];
                for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 7 + 3);

                byte[] hash = crypto.Sha1(key);

                long expected = 0;
                for (int i = 7; i >= 0; i--)
                    expected = (expected << 8) | hash[12 + i];

                Eq("fingerprint", expected, Calls.Fingerprint(crypto, key));

                // The failure this guards against: taking the first eight bytes, or
                // the right eight big-endian. Both are plausible and both are wrong.
                long firstEight = 0;
                for (int i = 7; i >= 0; i--) firstEight = (firstEight << 8) | hash[i];

                NotEq("not the first eight bytes", firstEight, Calls.Fingerprint(crypto, key));
            }

            Section("a public value the other end could weaken is refused");
            {
                Calls.DhConfig config = Group();
                byte[] secret = Exponent(crypto, 0x33);

                BigInt p = BigInt.FromBytesBE(config.P);

                Throws("g_b of 0", delegate { Calls.SharedKey(config, secret, new byte[256]); });

                var one = new byte[256];
                one[255] = 1;
                Throws("g_b of 1", delegate { Calls.SharedKey(config, secret, one); });

                Throws("g_b of p - 1", delegate
                {
                    Calls.SharedKey(config, secret, Subtract(p, 1).ToBytesBE(256));
                });

                Throws("g_b of p", delegate
                {
                    Calls.SharedKey(config, secret, p.ToBytesBE(256));
                });
            }

            Section("calls are parsed into one shape");
            {
                CallInfo requested = Calls.Parse(Requested());
                Eq("requested state", CallState.Requested, requested.State);
                Eq("requested id", 12345L, requested.Id);
                Eq("requested admin", 777L, requested.AdminId);
                Eq("g_a_hash carried", 32, requested.GaHash.Length);

                CallInfo ready = Calls.Parse(Ready());
                Eq("ready state", CallState.Ready, ready.State);
                Eq("key fingerprint", 0x0102030405060708L, ready.KeyFingerprint);
                Eq("one reflector", 1, ready.Connections.Count);
                Eq("reflector address", "149.154.175.50", ready.Connections[0].Ip);
                Eq("reflector port", 555, ready.Connections[0].Port);

                CallInfo discarded = Calls.Parse(Discarded());
                Eq("discarded state", CallState.Discarded, discarded.State);
                Eq("reason", CallDiscardReason.Hangup, discarded.DiscardReason);

                // phoneCallEmpty carries an id and nothing else. Reading it must not
                // throw looking for fields the constructor does not have.
                CallInfo empty = Calls.Parse(Empty());
                Eq("empty state", CallState.Empty, empty.State);
                Eq("empty access hash", 0L, empty.AccessHash);
            }

            Console.WriteLine();
            Console.WriteLine("  {0} checks, {1} failures", _checks, _failures);
            return _failures == 0;
        }

        /// <summary>Telegram's group, as the server serves it.</summary>
        private static Calls.DhConfig Group()
        {
            return new Calls.DhConfig
            {
                G = 3,
                P = Hex(
                    "c71caeb9c6b1c9048e6c522f70f13f73980d40238e3e21c14934d037563d930f" +
                    "48198a0aa7c14058229493d22530f4dbfa336f6e0ac925139543aed44cce7c37" +
                    "20fd51f69458705ac68cd4fe6b6b13abdc9746512969328454f18faf8c595f64" +
                    "2477fe96bb2a941d5bcd1d4ac8cc49880708fa9b378e3c4f3a9060bee67cf9a4" +
                    "a4a695811051907e162753b56b0f6b410dba74d8a84b2a14b3144e0ef1284754" +
                    "fd17ed950d5965b4b9dd46582db1178d169c6bc465b0d6ff9ca3928fef5b9ae4" +
                    "e418fc15e83ebea0f87fa9ff5eed70050ded2849f47bf959d956850ce929851f" +
                    "0d8115f635b105ee2e4e15d04b2454bf6f4fadf034b10403119cd8e3b92fcc5b"),
                Version = 2,
                Random = null,
            };
        }

        private static byte[] Exponent(ICrypto crypto, byte seed)
        {
            var secret = new byte[256];
            secret[254] = seed;
            secret[255] = (byte)(seed ^ 0x5a);
            return secret;
        }

        private static BigInt Subtract(BigInt value, uint amount)
        {
            return BigInt.Sub(value, BigInt.FromUInt(amount));
        }

        // ---- server objects, built the way the server would send them --------

        private static TlObject Requested()
        {
            var hash = new byte[32];
            for (int i = 0; i < hash.Length; i++) hash[i] = (byte)i;

            TlWriter w = new TlWriter()
                .WriteConstructor(TlConstructors.PhoneCallRequested)
                .WriteInt(0)
                .WriteLong(12345)
                .WriteLong(67890)
                .WriteInt(1700000000)
                .WriteLong(777)
                .WriteLong(888)
                .WriteBytes(hash);

            Protocol(w);
            return Read(w);
        }

        private static TlObject Ready()
        {
            var ga = new byte[256];
            for (int i = 0; i < ga.Length; i++) ga[i] = (byte)(i + 1);

            var tag = new byte[16];

            TlWriter w = new TlWriter()
                .WriteConstructor(TlConstructors.PhoneCall)
                .WriteInt(1 << 5)                       // p2p_allowed
                .WriteLong(12345)
                .WriteLong(67890)
                .WriteInt(1700000000)
                .WriteLong(777)
                .WriteLong(888)
                .WriteBytes(ga)
                .WriteLong(0x0102030405060708L);

            Protocol(w);

            w.WriteConstructor(TlConstructors.Vector)
                .WriteInt(1)
                .WriteConstructor(TlConstructors.PhoneConnection)
                .WriteInt(0)
                .WriteLong(99)
                .WriteString("149.154.175.50")
                .WriteString("")
                .WriteInt(555)
                .WriteBytes(tag)
                .WriteInt(1700000005);

            return Read(w);
        }

        private static TlObject Discarded()
        {
            return Read(new TlWriter()
                .WriteConstructor(TlConstructors.PhoneCallDiscarded)
                .WriteInt(1 | 2)                        // reason and duration present
                .WriteLong(12345)
                .WriteConstructor(TlConstructors.PhoneCallDiscardReasonHangup)
                .WriteInt(42));
        }

        private static TlObject Empty()
        {
            return Read(new TlWriter()
                .WriteConstructor(TlConstructors.PhoneCallEmpty)
                .WriteLong(12345));
        }

        private static TlWriter Protocol(TlWriter w)
        {
            return w.WriteConstructor(TlConstructors.PhoneCallProtocol)
                    .WriteInt(1 | 2)
                    .WriteInt(Calls.MinLayer)
                    .WriteInt(Calls.MaxLayer)
                    .WriteConstructor(TlConstructors.Vector)
                    .WriteInt(0);
        }

        private static TlObject Read(TlWriter writer)
        {
            return TlSchema.ReadObject(new TlReader(writer.ToArray()));
        }

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        // ---- reporting -------------------------------------------------------

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

        private static void NotEq(string what, object unexpected, object actual)
        {
            _checks++;
            if (!Equals(unexpected, actual)) return;

            Fail(what, "got the value it must not be: " + actual);
        }

        private static void Same(string what, byte[] a, byte[] b)
        {
            _checks++;
            if (a.Length == b.Length)
            {
                bool same = true;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { same = false; break; }
                if (same) return;
            }

            Fail(what, "arrays differ");
        }

        private static void Throws(string what, Action action)
        {
            _checks++;
            try
            {
                action();
                Fail(what, "expected a refusal, none came");
            }
            catch (MtprotoException) { }
            catch (Exception ex)
            {
                Fail(what, "unexpected " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Fail(string what, string detail)
        {
            _failures++;
            Console.WriteLine("    FAIL {0}: {1}", what, detail);
        }
    }
}
