# Security

Egami is built to open files from people you do not trust: uploads, email attachments, crawled images.

## Limits

[!code-csharp[](../../../tests/Egami.DocSamples/MemoryAndSecurity.cs#untrusted)]

| Limit | Default | Guards against |
| --- | --- | --- |
| `MaxWidth`, `MaxHeight` | 65,535 | Absurd dimensions in a header |
| `MaxPixels` | 268 million | Decompression bombs: a tiny file that claims a huge image |
| `MaxTotalBytes` | 2 GB | Everything one load or chain holds at once, frames and scratch included |
| `MaxFrames` | 10,000 | Animations with endless frames |
| `MaxMetadataBytes` | 16 MB | Oversized EXIF, XMP, ICC and text blocks |

Every reader checks sizes from the header before it allocates pixel memory; a breached limit throws `LimitExceededException`
with the limit's name, before any large allocation.

## Damaged files

By default readers are lenient: a truncated or damaged file returns the rows decoded so far, the rest transparent black (or
black), and lists what happened in `Metadata.Warnings`, each with a stable id; a [diagnostics](../diagnostics.md) handler gets the
same list as each load ends. A header that makes no sense always throws. `RecoveryMode.Strict` throws
`CorruptDataException` on any problem instead, with the byte offset.

All three exceptions derive from `EgamiException`; anything else thrown by a reader is a bug.

## Fuzzing

Every reader is fuzzed with AFL++ through SharpFuzz, and every input a fuzzer ever found is kept as a regression test. The
harness runs with `MaxTotalBytes` at 256 MB and treats any exception other than `EgamiException`, a hang or an out-of-memory as a
finding.

## Reporting a vulnerability

Please report security issues privately, as [SECURITY.md](https://github.com/inexin/Egami/blob/main/SECURITY.md) describes.

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### Use from ASP.NET Core

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#upload)]

Egami has no ASP.NET Core dependency: it reads and writes any `Stream`. The async calls read the upload into a pooled buffer,
counted against `MaxTotalBytes`, so a slow client never holds a thread.

## Run it

[`samples/Security`](https://github.com/inexin/Egami/tree/main/samples/Security) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Security` writes its results to `./output`.
