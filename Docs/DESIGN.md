# Lumigram - design notes

A Telegram client for Windows Phone 8.1 Silverlight, speaking MTProto 2.0
directly to Telegram's servers.

## Scope (v1)

In:  login, text messages, contact/chat list.
Out: attachments, voice/video calls, notifications, background tasks,
     secret chats, channels-as-such (they may appear, but are not a target).

Notifications are not "deferred" - they are impossible. MPNS is retired and
WNS requires a Store registration that no longer exists for WP8.1. Nothing in
this design can bring them back.

## No server, ever

The app opens a TCP socket to Telegram's datacenters and speaks MTProto 2.0.
There is no bridge, proxy, or relay. The auth key is generated on the device
by Diffie-Hellman and never leaves it. This is the whole security argument for
the project: a bridge would hold the user's session key on someone else's
machine, and that was ruled out.

## What the platform gives us, and what it does not

Checked against the installed WP8.1 reference assemblies and Windows.winmd
(not assumed - see the table below; every "no" here shaped the code layout).

| Need           | Source                                              |
|----------------|-----------------------------------------------------|
| TCP            | WinRT `StreamSocket`                                |
| SHA-1/256/512  | WinRT `HashAlgorithmProvider`                       |
| HMAC-SHA512    | WinRT `MacAlgorithmProvider`                        |
| AES-256        | NOT USED from the platform - `Crypto/Aes256`        |
| AES-IGE        | NOT AVAILABLE - `Crypto/AesIge` over the above      |
| BigInteger     | NOT AVAILABLE - `Crypto/BigInt`                     |
| PBKDF2         | NOT AVAILABLE - `Crypto/Pbkdf2` (2FA needs SHA-512) |
| gzip inflate   | NOT AVAILABLE - `Tl/Inflate` (RFC 1951/1952)        |

AES ended up in managed code rather than behind the shim: IGE chains block to
block, so a platform block cipher would cost one interop call per 16 bytes.

Two of these were discovered by running the thing, not by reading docs.
Telegram compresses any sizeable response, so `account.getPassword` arrives
gzipped and a client without inflate cannot log in at all. And the desktop's
`Rfc2898DeriveBytes` is SHA-1 only in .NET 4.5, so even the *desktop* head
needed the portable PBKDF2.

WP8.1 Silverlight has no `System.Numerics` and no `System.Security.Cryptography`.
That is why the core carries its own big-integer arithmetic instead of the
one-liner every desktop MTProto implementation uses.

MTProto needs big integers in three places: the DH exchange, the RSA step
(which is just `x^65537 mod n` over a 255-byte block - no RSA API required),
and factoring `pq` during the handshake.

## Layout, and why

    Core/        protocol. Plain C#, no platform types, no external packages.
      Crypto/    BigInt, AES-IGE, and the ICrypto shim
      Tl/        TL serialisation (constructor ids, reader/writer)
      Mtproto/   handshake, session, encryption, transport
    Harness/     desktop console app - console head over the same Core
    Phone/       WP8.1 Silverlight app - AnyCPU / ARM (device) / x86 (emulator)

`Core` compiles unchanged into both heads. It must not reference WinRT or
Silverlight types; anything platform-specific goes behind `ICrypto`/`ITransport`,
implemented once per head. This is what lets the protocol be developed and
debugged on the desktop, where iteration takes seconds instead of an emulator
deploy cycle - and it is why `Core` uses its own BigInt even on desktop, where
`System.Numerics` exists. The phone is the constraint; the desktop follows it.

## API layer: 228, not 73

The original plan was to inherit Unigram's layer-73 definitions. That does not
work, and finding out took one afternoon rather than one rewrite:

    layer 73:   auth.sendCode -> 406 UPDATE_APP_TO_LOGIN
    layer 228:  auth.sendCode -> auth.sentCode { ... }

Telegram enforces a modern layer **for login specifically** - layer 73 is still
accepted for `help.getNearestDc`. So the client speaks layer 228, with method
definitions read from the TDLib schema at
`C:/projects/td/td/generate/scheme/telegram_api.tl` (layer number from
`td/telegram/Version.h`). Field layouts were read, not assumed: several changed
shape since layer 73 even where the constructor id did not.

MTProto 2.0 itself was unaffected - handshake, encryption and sessions all
worked unchanged at both layers. Only the API surface above it moved.

## State

Done, and verified against live Telegram:

- `BigInt`, AES-256, AES-IGE, TL, gzip inflate - all differential-tested
  against a reference implementation, ~15,000 checks
- auth-key handshake (DH, RSA, safe-prime validation)
- encrypted session layer (msg_key, KDF, salts, containers, rpc_result)
- login: `sendCode` -> `signIn` -> `checkPassword` -> `authorization`,
  including two-step verification over SRP

Next:

1. Messaging: `messages.getDialogs`, `getHistory`, `sendMessage`, updates.
2. The WP8.1 head: `ICrypto` over WinRT, `ITransport` over `StreamSocket`, UI.

## Measured on real hardware

Verified end to end on a **Lumia 521** - a 2013 dual-core device with 512 MB of
RAM, which is close to the slowest thing WP8.1 runs on. The local self test and
a full MTProto 2.0 handshake against Telegram both pass on it.

    2048-bit ModPow      915 ms   (desktop: 78 ms, so ~12x slower)

That single number sets the cost of everything expensive:

    handshake, cold        4.6 s   first login on a fresh install
    2FA proof             11.6 s   100,000 PBKDF2-HMAC-SHA512 iterations
    PBKDF2                 116 ms per 1,000 iterations

Both are per-login, not per-launch, and both need a progress indicator rather
than a frozen screen. Neither is fast, and neither has to be: the auth key is
permanent once created.

Two measurements changed the design:

**Safe-prime validation cost 35 s on first login.** Miller-Rabin over p and
(p-1)/2 is twenty-four exponentiations, and the in-process cache made a second
attempt look fast, hiding it. Fixed by recognising Telegram's standard prime
(`DhValidation.BuiltInGoodPrimeHex`) - a byte comparison in the normal case,
with full validation still applied to any prime we do not recognise, which is
the case actually worth being slow about.

**Two-step verification would have cost over half an hour.** PBKDF2 runs 100,000
HMAC-SHA512 iterations; through WinRT that measured ~20 ms per call, because
every iteration crossed the interop boundary and rebuilt the MAC key. Fixed by
implementing SHA-512 and HMAC in managed code (`Crypto/Sha512.cs`) so the loop
never leaves the CLR - the same call already made for AES.

Neither was visible from the desktop, where both paths are fast enough to look
fine. They only appeared on real hardware - which is the argument for putting a
self test in the app itself rather than reasoning about performance from a
development machine.

What remains in the 4.6 s cold connect is roughly: two 2048-bit exponentiations
(~1.8 s), factoring the server's pq challenge, and three network round trips.
The pq step uses shift-and-add modular multiplication because neither the
platform nor C# offers a 128-bit multiply, which is cheap on desktop and not on
ARM. It has not been profiled on the device; the figure is acceptable for a
once-per-install cost, so it has been left alone.

All of these are per-login, not per-launch: the auth key is permanent once
created.

## Credentials

`Secrets.cs` holds the api_id/api_hash and is gitignored; `Secrets.cs.template`
is committed in its place. The harness also writes `session.dat` next to its
executable - that file contains a full account credential in the clear. It is
gitignored, and the phone build must not copy that approach.
