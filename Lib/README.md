# Lib

Third-party binaries. Everything else in this repository is written here; these
are the exceptions, and each one needs a reason.

## Concentus.dll

A pure managed C# port of libopus — https://github.com/lostromb/concentus,
NuGet package `Concentus` 1.1.7, BSD licensed (the Opus licence,
https://opus-codec.org/license/).

**Why a dependency at all.** Windows Phone 8.1 has no Opus support of any kind,
and Telegram voice messages are Opus in an OGG container. Everything else this
project needed and the platform lacked — AES, SHA-512, big integers, inflate, a
QR encoder — was written by hand. Opus is not in that class: it is a hybrid of
two codecs, SILK and CELT, and writing it would be a larger undertaking than the
rest of the client put together.

**Which build.** The `portable-net45+win+wpa81+wp80` one. That profile includes
Windows Phone Silverlight, so it drops straight into the phone project, and it
also loads in the desktop harness — which is how `voice` can test the demuxer
against a real file.

**What is ours.** Only the codec comes from here. The OGG container is parsed by
`Core/Audio/OggOpus.cs`, which is written in this repository: OGG paging is
small and well specified, where the codec is neither.

To update, take the same `lib/portable-net45+win+wpa81+wp80/Concentus.dll` out of
a newer `.nupkg` and re-run `Lumigram.Harness voice <file.opus>`.
