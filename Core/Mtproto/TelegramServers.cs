using System;
using System.Collections.Generic;
using Lumigram.Crypto;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// Telegram's server public keys and datacenter addresses - the only constants a
    /// client needs to bootstrap. Everything else is negotiated.
    ///
    /// Both sets were taken from the shipped Unigram client
    /// (Telegram.Api.Native: DatacenterCryptography.cpp, ConnectionManager.cpp),
    /// which is a known-working source for them.
    /// </summary>
    public static class TelegramServers
    {
        /// <summary>
        /// Expected fingerprints, in the same order as <see cref="PublicKeyPems"/>.
        /// These are not used to build anything - <see cref="RsaKey"/> derives the
        /// fingerprint from the key itself. They are kept so that derivation can be
        /// checked against an independent source at startup.
        /// </summary>
        public static readonly long[] ExpectedFingerprints =
        {
            unchecked((long)0xc3b42b026ce86b21UL),
            unchecked((long)0x9a996a1db11c729bUL),
            unchecked((long)0xb05b2a6f70cdea78UL),
            unchecked((long)0x71e025b6c76033e3UL),
        };

        public static readonly string[] PublicKeyPems =
        {
            "-----BEGIN RSA PUBLIC KEY-----\n" +
            "MIIBCgKCAQEAwVACPi9w23mF3tBkdZz+zwrzKOaaQdr01vAbU4E1pvkfj4sqDsm6\n" +
            "lyDONS789sVoD/xCS9Y0hkkC3gtL1tSfTlgCMOOul9lcixlEKzwKENj1Yz/s7daS\n" +
            "an9tqw3bfUV/nqgbhGX81v/+7RFAEd+RwFnK7a+XYl9sluzHRyVVaTTveB2GazTw\n" +
            "Efzk2DWgkBluml8OREmvfraX3bkHZJTKX4EQSjBbbdJ2ZXIsRrYOXfaA+xayEGB+\n" +
            "8hdlLmAjbCVfaigxX0CDqWeR1yFL9kwd9P0NsZRPsmoqVwMbMu7mStFai6aIhc3n\n" +
            "Slv8kg9qv1m6XHVQY3PnEw+QQtqSIXklHwIDAQAB\n" +
            "-----END RSA PUBLIC KEY-----",

            "-----BEGIN RSA PUBLIC KEY-----\n" +
            "MIIBCgKCAQEAxq7aeLAqJR20tkQQMfRn+ocfrtMlJsQ2Uksfs7Xcoo77jAid0bRt\n" +
            "ksiVmT2HEIJUlRxfABoPBV8wY9zRTUMaMA654pUX41mhyVN+XoerGxFvrs9dF1Ru\n" +
            "vCHbI02dM2ppPvyytvvMoefRoL5BTcpAihFgm5xCaakgsJ/tH5oVl74CdhQw8J5L\n" +
            "xI/K++KJBUyZ26Uba1632cOiq05JBUW0Z2vWIOk4BLysk7+U9z+SxynKiZR3/xdi\n" +
            "XvFKk01R3BHV+GUKM2RYazpS/P8v7eyKhAbKxOdRcFpHLlVwfjyM1VlDQrEZxsMp\n" +
            "NTLYXb6Sce1Uov0YtNx5wEowlREH1WOTlwIDAQAB\n" +
            "-----END RSA PUBLIC KEY-----",

            "-----BEGIN RSA PUBLIC KEY-----\n" +
            "MIIBCgKCAQEAsQZnSWVZNfClk29RcDTJQ76n8zZaiTGuUsi8sUhW8AS4PSbPKDm+\n" +
            "DyJgdHDWdIF3HBzl7DHeFrILuqTs0vfS7Pa2NW8nUBwiaYQmPtwEa4n7bTmBVGsB\n" +
            "1700/tz8wQWOLUlL2nMv+BPlDhxq4kmJCyJfgrIrHlX8sGPcPA4Y6Rwo0MSqYn3s\n" +
            "g1Pu5gOKlaT9HKmE6wn5Sut6IiBjWozrRQ6n5h2RXNtO7O2qCDqjgB2vBxhV7B+z\n" +
            "hRbLbCmW0tYMDsvPpX5M8fsO05svN+lKtCAuz1leFns8piZpptpSCFn7bWxiA9/f\n" +
            "x5x17D7pfah3Sy2pA+NDXyzSlGcKdaUmwQIDAQAB\n" +
            "-----END RSA PUBLIC KEY-----",

            "-----BEGIN RSA PUBLIC KEY-----\n" +
            "MIIBCgKCAQEAwqjFW0pi4reKGbkc9pK83Eunwj/k0G8ZTioMMPbZmW99GivMibwa\n" +
            "xDM9RDWabEMyUtGoQC2ZcDeLWRK3W8jMP6dnEKAlvLkDLfC4fXYHzFO5KHEqF06i\n" +
            "qAqBdmI1iBGdQv/OQCBcbXIWCGDY2AsiqLhlGQfPOI7/vvKc188rTriocgUtoTUc\n" +
            "/n/sIUzkgwTqRyvWYynWARWzQg0I9olLBBC2q5RQJJlnYXZwyTL3y9tdb7zOHkks\n" +
            "WV9IMQmZmyZh/N7sMbGWQpt4NMchGpPGeJ2e5gHBjDnlIf2p1yZOYeUYrdbwcS0t\n" +
            "UiggS4UeE8TzIuXFQxw7fzEIlmhIaq3FnwIDAQAB\n" +
            "-----END RSA PUBLIC KEY-----",
        };

        public static List<RsaKey> LoadPublicKeys(ICrypto crypto)
        {
            var keys = new List<RsaKey>(PublicKeyPems.Length);
            foreach (var pem in PublicKeyPems) keys.Add(RsaKey.FromPem(pem, crypto));
            return keys;
        }

        /// <summary>Picks the key matching one of the fingerprints the server offered.</summary>
        public static RsaKey FindByFingerprint(List<RsaKey> keys, long[] offered)
        {
            for (int i = 0; i < offered.Length; i++)
                foreach (var k in keys)
                    if (k.Fingerprint == offered[i]) return k;
            return null;
        }

        // Datacenter addresses. The test cluster is deliberately listed first in
        // usage: an auth handshake can be exercised against it without touching a
        // real account.
        public const string TestDc2Host = "149.154.167.40";
        public const string ProductionDc2Host = "149.154.167.51";
        public const int DefaultPort = 443;

        /// <summary>
        /// The address of a datacenter by its id.
        ///
        /// Needed whenever the server says an account lives somewhere else - a QR
        /// login token that migrates, or a FILE_MIGRATE on a download. Hard-coded
        /// because a client has to be able to reach a datacenter before it can ask
        /// anything, including where the datacenters are.
        /// </summary>
        public static string HostFor(int dcId)
        {
            switch (dcId)
            {
                case 1: return "149.154.175.50";
                case 2: return "149.154.167.51";
                case 3: return "149.154.175.100";
                case 4: return "149.154.167.91";
                case 5: return "149.154.171.5";
                default: return ProductionDc2Host;
            }
        }
    }
}
