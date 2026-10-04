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
| clipboard      | NOT AVAILABLE - see below                           |

AES ended up in managed code rather than behind the shim: IGE chains block to
block, so a platform block cipher would cost one interop call per 16 bytes.

Two of these were discovered by running the thing, not by reading docs.
Telegram compresses any sizeable response, so `account.getPassword` arrives
gzipped and a client without inflate cannot log in at all. And the desktop's
`Rfc2898DeriveBytes` is SHA-1 only in .NET 4.5, so even the *desktop* head
needed the portable PBKDF2.

## There is no clipboard

`Windows.ApplicationModel.DataTransfer` is present on WP8.1 and ships
`DataPackage`, `DataTransferManager`, `StandardDataFormats` and the whole share
contract - and no `Clipboard` class at all. Checked by disassembling
`Windows.winmd` from the Windows Phone Kits 8.1 reference set: zero occurrences
of the name in 277,000 lines of IL. The SDK's own `Windows.xml` documents
`Clipboard` anyway, because that file is shared with the Windows 8.1 SDK, so
reading the docs rather than the metadata gives the wrong answer.

So an app cannot put text on the clipboard. What the platform does give is text
selection - `TextBox.SelectAll`/`Select`/`SelectedText`, and the copy button the
system draws over a selection - which is the only route there is. Copying a
message therefore means putting its text in a box and selecting it, and letting
the user tap the system's own button.

Pasting needs no code for the same reason from the other side: the paste key
belongs to the keyboard, so the message box already accepts one.

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

## Forum topics

A topic is not a chat. It is a thread hanging off one message in the supergroup,
and that message's id is the topic's name everywhere:

    list topics    messages.getForumTopics(peer)        - peer, not channel:
                                                          the older
                                                          channels.getForumTopics
                                                          is gone at layer 228
    read a topic   messages.getReplies(peer, msg_id=T)  - getHistory cannot say
                                                          which thread, so it
                                                          returns all of them
    mark it read   messages.readDiscussion(peer, T, m)  - readHistory clears
                                                          every topic at once
    send into it   reply_to = inputReplyToMessage {
                       top_msg_id      = T              - the thread
                       reply_to_msg_id = T, or the
                                         message being
                                         answered
                   }

The General topic, id 1, is the exception in the last of these: its id is not a
message anyone can reply to, so it is named only as the thread and the reply
target stays zero. This follows TDLib, whose `MessageTopic` makes the same
distinction.

Nothing here needed the schema regenerating - every constructor involved was
already in the layer-228 table. What it needed was the `forum` bit, flags.30 of
`channel`, which is a true-flag and so appears in no generated field list: with
it a supergroup opens as a list of topics, and without it as one interleaved
stream where everything sent back lands in General.

## Files are not all on one datacenter

An account is signed in on one datacenter, but its files are spread over all of
them: a photo is stored where the phone that sent it was, so a chat list of
people in three countries refers to files on three datacenters. Asking the
signed-in one for a file it does not hold answers `FILE_MIGRATE_4`, which reads
like an error and is an address.

This surfaced as "some pictures unavailable - FILE_MIGRATE_4" on the chat list,
and it is not a rare case: it is every contact who joined from abroad.

Reaching datacenter 4 is a whole second connection - its own handshake, its own
auth key - and that key is a stranger's until the account is put on it:

    auth.exportAuthorization(4)     on the datacenter already signed in
    auth.importAuthorization(id, bytes)  on the new one

The credential is short-lived and single-use, so it is fetched per datacenter as
that datacenter is first needed. `FileDcPool` keeps the resulting connections for
as long as the app runs, because the handshake costs seconds on a phone and a
chat list is dozens of pictures from a handful of datacenters - paid once each,
then not again. They are not stored across launches: each is a live credential
for the account, and writing three more of them to disk to save a few seconds
once per launch is not a trade worth making silently.

The file's own `dc_id` is treated as a hint and never as a decision. It saves a
round trip to the wrong place, but the server's answer settles it - so the
signed-in connection is always tried first unless a connection to that
datacenter is already open. Building one on the strength of the hint alone would
mean paying for a handshake to arrive back where it started.

## Links back into Telegram

A message's text is drawn as runs and hyperlinks rather than as a string, and the
links that point at Telegram are followed in the app instead of the browser.
Sending one of those to a browser lands the user on a page telling them to
install Telegram, which is the one thing they demonstrably already have.

Four shapes are recognised, which is what people actually paste:

    @name                        a mention
    https://t.me/name            a peer
    https://t.me/name/391        one message in it
    https://t.me/c/<id>/391      one message in a chat with no username

A mention is given the t.me address it is shorthand for, so everything above the
splitter treats the two alike. Everything t.me serves that is *not* a peer -
invite links, sticker sets, proxies, share dialogs - is left as a web address on
purpose: following one as though it were a username looks up somebody who does
not exist and loses the user the link.

The /c/ form carries the channel's own id, with no -100 in front, which is the
same number this client uses everywhere else. It can only be followed by somebody
already in that chat, because an id addresses nothing without the access hash
that comes with it, and the only place this client holds hashes is the chat list.
That is not a shortcoming of the client: it is what the link means.

Opening at a particular message needs history centred on it rather than the
newest - `add_offset` negative, which is the only way getHistory will return
anything *newer* than the message named. While a conversation is parked there the
five-second poll is held off, or it would staple the newest messages onto the end
of a window from the middle of the conversation with everything between missing.
"skip to end" is how the reader leaves that state.

## SOCKS5 proxy

For networks that block Telegram. `Socks5Transport` (Core) wraps any
`ITransport`: it connects to the proxy, runs the RFC 1928 handshake (with RFC 1929
username/password login), and then passes bytes straight through, so MTProto never
knows. Ported from Symbigram's hand-written handshake.

Because it is a wrapper in Core, the phone's socket and the desktop harness's both
go inside it unchanged - the harness tests the code the phone runs. Verified against
a local logging proxy with a full auth-key exchange against a production datacenter
(`Lumigram.Harness socks 127.0.0.1:PORT [user pass]`).

Every connection on the phone is made through `PhoneTransport.Open()`, which applies
the setting: the main connection, the per-datacenter file connections and the
background task's. None can go around the proxy - on a blocking network one that did
would fail, and on a watching one it would show the user's real address.

The proxy page is reachable from the sign-in screen as well as Settings, because on a
blocking network there is no signing in without it. Calls are not proxied: they are
UDP, which this kind of SOCKS5 setup does not carry.

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
