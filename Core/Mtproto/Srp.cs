using System;
using System.Threading.Tasks;
using Lumigram.Tl;
using Lumigram.Crypto;

namespace Lumigram.Mtproto
{
    /// <summary>The client's half of an SRP exchange: what auth.checkPassword needs.</summary>
    public sealed class SrpProof
    {
        public byte[] A { get; set; }        // 256 bytes
        public byte[] M1 { get; set; }       // 32 bytes
    }

    /// <summary>
    /// Two-step verification, as
    /// passwordKdfAlgoSHA256SHA256PBKDF2HMACSHA512iter100000SHA256ModPow.
    ///
    /// SRP means the password itself never crosses the wire, and the server cannot
    /// derive it from what it stores. The client proves knowledge of the password by
    /// deriving a shared value the server can check.
    ///
    ///     x  = PH2(password, salt1, salt2)
    ///     v  = g^x mod p
    ///     A  = g^a mod p                    a is a fresh random secret
    ///     u  = H(A | B)
    ///     S  = (B - k*v)^(a + u*x) mod p
    ///     M1 = H(H(p) xor H(g) | H(salt1) | H(salt2) | A | B | H(S))
    ///
    /// Every large value is padded to 256 bytes before hashing. That padding is the
    /// easiest thing to get wrong: a value that happens to have a leading zero byte
    /// hashes differently unpadded, so it works most of the time and fails
    /// occasionally, which is far worse than failing always.
    /// </summary>
    public static class Srp
    {
        private const int Pbkdf2Iterations = 100000;
        private const int PadSize = 256;

        /// <summary>
        /// Signs in with a two-step verification password.
        ///
        /// Three parts, all of which have to happen together: ask the server for the
        /// current parameters, prove knowledge of the password against them, and
        /// send the proof. The parameters include a per-attempt value from the
        /// server, so they cannot be cached and reused - a proof computed against
        /// stale parameters is simply rejected.
        ///
        /// The password itself never leaves the device; what goes out is a proof
        /// derived from it. Expect this to take several seconds on phone hardware -
        /// the derivation is 100,000 rounds of PBKDF2 followed by a 2048-bit
        /// exponentiation, and that cost is the point of it.
        ///
        /// Lives here rather than in a page because it is protocol, and because
        /// having it in one place is what stops a second caller getting the order
        /// subtly wrong.
        /// </summary>
        public static async Task CheckPasswordAsync(MtprotoClient client, ICrypto crypto,
                                                    string password, ClientInfo info = null)
        {
            var q = new TlWriter(16);
            q.WriteConstructor(TlConstructors.AccountGetPassword);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            r.Expect(TlConstructors.AccountPassword, "account.password");

            int flags = r.ReadInt();
            if ((flags & 4) == 0)
                throw new MtprotoException("this account has no password set");

            uint algo = r.ReadConstructor();
            if (algo != TlConstructors.PasswordKdfAlgoSha256Pbkdf2)
                throw new MtprotoException("unsupported password method");

            byte[] salt1 = r.ReadBytes();
            byte[] salt2 = r.ReadBytes();
            int g = r.ReadInt();
            byte[] p = r.ReadBytes();
            byte[] srpB = r.ReadBytes();
            long srpId = r.ReadLong();

            // Off the calling thread. This is ten to fifteen seconds of solid
            // arithmetic on phone hardware - 100,000 rounds of PBKDF2 and a
            // 2048-bit exponentiation - and doing it inline freezes whatever UI
            // called it for the whole time, which looks exactly like a crash.
            SrpProof proof = await Task.Run(delegate
            {
                return ComputeProof(crypto, password, salt1, salt2, g, p, srpB);
            });

            var check = new TlWriter(600);
            check.WriteConstructor(TlConstructors.AuthCheckPassword)
                 .WriteConstructor(TlConstructors.InputCheckPasswordSrp)
                 .WriteLong(srpId)
                 .WriteBytes(proof.A)
                 .WriteBytes(proof.M1);

            TlReader result = await client.InvokeAsync(check.ToArray(), info);
            result.Expect(TlConstructors.AuthAuthorization, "auth.authorization");
        }

        public static SrpProof ComputeProof(ICrypto crypto, string password,
                                            byte[] salt1, byte[] salt2, int g, byte[] pBytes,
                                            byte[] srpB, Action<string> log = null)
        {
            log = log ?? delegate { };

            var p = BigInt.FromBytesBE(pBytes);
            var gBig = BigInt.FromUInt((uint)g);
            var b = BigInt.FromBytesBE(srpB);

            // The server chooses p and g here exactly as it does for the handshake,
            // so the same validation applies - an unchecked group would let a
            // malicious server learn the password verifier.
            DhValidation.ValidateParameters(g, p, b, crypto, log);

            byte[] pPad = p.ToBytesBE(PadSize);
            byte[] gPad = gBig.ToBytesBE(PadSize);
            byte[] bPad = b.ToBytesBE(PadSize);

            // x = PH2(password, salt1, salt2)
            byte[] passwordBytes = System.Text.Encoding.UTF8.GetBytes(password);
            byte[] ph1 = SH(crypto, SH(crypto, passwordBytes, salt1), salt2);
            byte[] pbkdf = Pbkdf2.DeriveSha512(crypto, ph1, salt1, Pbkdf2Iterations, 64);
            byte[] x = SH(crypto, pbkdf, salt2);

            var xBig = BigInt.FromBytesBE(x);

            // k = H(p | g), v = g^x mod p, k*v mod p
            var k = BigInt.FromBytesBE(crypto.Sha256(CryptoExtensions.Concat(pPad, gPad)));
            var v = BigInt.ModPow(gBig, xBig, p);
            var kv = BigInt.Mod(BigInt.Mul(k, v), p);

            // A = g^a mod p, retrying if u would be zero.
            BigInt a, aValue, u;
            byte[] aPad;
            int attempts = 0;
            do
            {
                if (++attempts > 100)
                    throw new MtprotoException("SRP: could not find a usable secret");

                a = BigInt.FromBytesBE(crypto.Random(256));
                aValue = BigInt.ModPow(gBig, a, p);
                aPad = aValue.ToBytesBE(PadSize);
                u = BigInt.FromBytesBE(crypto.Sha256(CryptoExtensions.Concat(aPad, bPad)));
            }
            while (u.IsZero);

            // t = (B - k*v) mod p, kept non-negative without a signed BigInt.
            BigInt t;
            if (BigInt.Compare(b, kv) >= 0)
            {
                t = BigInt.Sub(b, kv);
            }
            else
            {
                // B < k*v: add enough multiples of p to bring it back above.
                BigInt diff = BigInt.Sub(kv, b);
                BigInt reduced = BigInt.Mod(diff, p);
                t = reduced.IsZero ? BigInt.Zero : BigInt.Sub(p, reduced);
            }

            // S = t^(a + u*x) mod p
            BigInt exponent = BigInt.Add(a, BigInt.Mul(u, xBig));
            BigInt s = BigInt.ModPow(t, exponent, p);
            byte[] kA = crypto.Sha256(s.ToBytesBE(PadSize));

            byte[] hp = crypto.Sha256(pPad);
            byte[] hg = crypto.Sha256(gPad);
            var xored = new byte[hp.Length];
            for (int i = 0; i < hp.Length; i++) xored[i] = (byte)(hp[i] ^ hg[i]);

            byte[] m1 = crypto.Sha256(CryptoExtensions.Concat(
                xored,
                crypto.Sha256(salt1),
                crypto.Sha256(salt2),
                aPad,
                bPad,
                kA));

            return new SrpProof { A = aPad, M1 = m1 };
        }

        /// <summary>SH(data, salt) = SHA256(salt | data | salt).</summary>
        private static byte[] SH(ICrypto crypto, byte[] data, byte[] salt)
        {
            return crypto.Sha256(CryptoExtensions.Concat(salt, data, salt));
        }
    }
}
