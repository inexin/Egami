# Contributing to Egami

Thanks for helping build Egami, a fully managed, MIT-licensed imaging library for .NET.

## The three rules

1. **Only clean sources.** Write your own code from specifications and from the libraries listed in [docs/references.md](docs/references.md). Never copy, port, translate or paraphrase code, tests, test images, docs or API names from any other imaging library. If you have worked on the internals of another imaging library, say so in your PR.
2. **No wrapped native code.** Egami is C# all the way: no P/Invoke, no native binaries, no wrappers around C or C++ libraries. Using .NET's own APIs (for example `System.IO.Compression.ZLibStream`) is fine.
3. **Every commit is signed off.** Add `Signed-off-by: Your Name <you@example.com>` to each commit (`git commit -s`). This is the [Developer Certificate of Origin](https://developercertificate.org/): you certify that you wrote the change or have the right to contribute it under the MIT license.

## Where ideas may come from

- Format specifications (W3C PNG, ITU-T T.81 and JFIF, GIF89a, RFC 9649 and others), textbooks and papers.
- The permissively licensed libraries in [docs/references.md](docs/references.md), such as Skia, SkiaSharp, stb, Wuffs, libspng and the Rust image crates. Learn the ideas and write your own C#. If you translate code closely, add its license notice to `THIRD-PARTY-NOTICES.md` in the same PR.
- Never GPL, LGPL or AGPL code, and never code without a license.

## Building and testing

You need the .NET 10 SDK.

```
dotnet build
dotnet test
```

To test the SIMD fallback paths locally, run the tests with `DOTNET_EnableHWIntrinsic=0`.

The large third-party corpora (conformance suites and test vectors) are fetched into `.cache` by the `tools/make-*-references.py` scripts and never committed. A normal run skips them; set `EGAMI_CORPUS` to the codecs you are working on (`avif`, `exr`, `jbig2`, `jpeg2000`, `jxl`, `tiff`, `vp8`, comma-separated, or `all`) to check them too, for example `EGAMI_CORPUS=jxl dotnet test --filter FullyQualifiedName~Jxl`. The same switch takes a folder of `tests/images` (`avif`, `jpeg`, `pngsuite` and so on): the streaming tests that run over every file skip in a normal run and stream the files of the folders it names.

Benchmarks: `dotnet run -c Release --project benchmarks/Egami.Benchmarks`.

## Pull requests

- One topic per PR. Fill in the PR template, including the provenance section.
- New public API needs XML docs and an update to `PublicAPI.Unshipped.txt` (`python tools/update-public-api.py` adds what the build reports). `PublicAPI.Shipped.txt` is the frozen 1.0 API: changing or removing anything in it is a breaking change and needs a maintainer's agreement first.
- A public interface in a package at 1.0 or above (for example `IGlyphOutlineSink`, `IColorPaintVisitor`, `IFontFallbackResolver` and `ITextPaint` in Egami.Fonts, and `ICssElement` in Egami.Css) is frozen for the major version: a member added in 1.x comes as a default interface method, so code that implements the interface keeps compiling.
- Changes to hot code (pixel conversion, codecs, operations) need benchmark results. They must not be slower or allocate more than `main`.
- New test images must have a clear license and an entry in `tests/images/LICENSES`.

## Reporting security problems

See [SECURITY.md](SECURITY.md). Please don't open public issues for security bugs.
