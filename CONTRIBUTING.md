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

CI on a pull request builds and tests on Linux x64 and runs the format, public API, dependency, docs, provenance and sign-off
checks. Pushes to `main`, a nightly run and manual runs add Linux arm64, Windows and macOS, the tests with AVX-512, AVX2 or all
hardware intrinsics turned off, PNG against libpng and the Native AOT smoke app. If your change touches SIMD or
platform-specific code, run the tests with the intrinsics turned off before you open the pull request.

The large third-party corpora (conformance suites and test vectors) are fetched into `.cache` by the `tools/make-*-references.py` scripts and never committed. A normal run skips them; set `EGAMI_CORPUS` to the codecs you are working on (`avif`, `exr`, `jbig2`, `jpeg2000`, `jxl`, `tiff`, `vp8`, comma-separated, or `all`) to check them too, for example `EGAMI_CORPUS=jxl dotnet test --filter FullyQualifiedName~Jxl`. The same switch takes a folder of `tests/images` (`avif`, `jpeg`, `pngsuite` and so on): the streaming tests that run over every file skip in a normal run and stream the files of the folders it names.

Benchmarks: `dotnet run -c Release --project benchmarks/Egami.Benchmarks`.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/Egami/` | The core library |
| `src/Egami.Drawing/`, `src/Egami.Fonts/`, `src/Egami.Svg/`, `src/Egami.Css/`, `src/Egami.Metafiles/` | The other packages ([Packages](docs/articles/packages.md)) |
| `tests/Egami.Tests/` | Unit, conformance, round-trip and SIMD equivalence tests (xUnit) |
| `tests/Egami.Fuzz/` | Fuzzing harness (SharpFuzz and AFL++) |
| `tests/Egami.AotSmoke/` | Console app published with Native AOT in CI |
| `tests/Egami.DocSamples/` | The code the docs show, compiled and run with the tests |
| `samples/` | One small console app per guide; `dotnet run --project samples/Formats` |
| `tests/Egami.OutsideTests/` | Plugins written with only the public API: a pixel type, a memory source, operations, formats |
| `tests/images/`, `tests/reference/` | Test images for every format and their reference decodes, with licenses; large corpora are fetched into `.cache` |
| `docs/` | The documentation site (DocFX): guides, cookbook, API reference. Build it with `docfx docs/docfx.json` (output in `docs/_site`) |
| `benchmarks/Egami.Benchmarks/` | BenchmarkDotNet benchmarks |
| `branding/` | Package icons, logos and the favicon |
| `tools/` | Scripts that regenerate references and the public API baseline, check the docs are complete, and the libpng checker used in CI |

## Documentation versions

The docs site keeps one folder per major version (`v1/`, `v2/`, ...) with `versions.json` and an `index.html` that sends
readers to the newest version. `docs/versions.json` lists the major versions, newest first; `docs/template/public/main.js`
reads it to show a version picker on every page and a notice on pages of an older version. Minor versions share their
major's folder, because 1.x stays compatible. When a new major version starts, keep the old one on a `release/<major>.x`
branch, add the new version at the top of `docs/versions.json` and build each folder from its own branch.

## Releases

A tag `v<version>` (the version of `src/Egami`) runs `.github/workflows/release.yml`: it tests, packs Egami, Egami.Drawing,
Egami.Fonts, Egami.Svg and Egami.Css at the versions in their project files, and pushes them to nuget.org with trusted
publishing, after a maintainer approves the `nuget` environment. The repository holds no NuGet API key: a trusted publishing
policy on nuget.org names this repository, `release.yml` and the `nuget` environment, and the secret `NUGET_USER` holds the
nuget.org account name. A tag `<package>-v<version>`, for example `Egami.Metafiles-v0.1.1`, runs the same workflow for that one
package at that version, so a package can release on its own. Only maintainers tag releases.

## Pull requests

- One topic per PR. Fill in the PR template, including the provenance section.
- New public API needs XML docs and an update to `PublicAPI.Unshipped.txt` (`python tools/update-public-api.py` adds what the build reports). `PublicAPI.Shipped.txt` is the frozen 1.0 API: changing or removing anything in it is a breaking change and needs a maintainer's agreement first.
- A public interface in a package at 1.0 or above (for example `IGlyphOutlineSink`, `IColorPaintVisitor`, `IFontFallbackResolver` and `ITextPaint` in Egami.Fonts, and `ICssElement` in Egami.Css) is frozen for the major version: a member added in 1.x comes as a default interface method, so code that implements the interface keeps compiling.
- Changes to hot code (pixel conversion, codecs, operations) need benchmark results. They must not be slower or allocate more than `main`.
- New test images must have a clear license and an entry in `tests/images/LICENSES`.

## Reporting security problems

See [SECURITY.md](SECURITY.md). Please don't open public issues for security bugs.
