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

You need the .NET 10 SDK and Python 3 for the scripts in `tools/`.

```sh
dotnet build Egami.slnx
dotnet test Egami.slnx
dotnet format Egami.slnx --verify-no-changes      # formatting; CI fails if this reports changes
python tools/check-docs.py                         # docs are complete for src/Egami; pass a folder for another package
python tools/update-public-api.py                  # adds new public API to PublicAPI.Unshipped.txt
```

Warnings are errors, and the build checks the public API baseline (`PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` in
each package). While you work, run only the tests for your area, for example
`dotnet test Egami.slnx --filter FullyQualifiedName~Jxl`, and run the whole solution before you open the pull request.

To test the SIMD fallback paths locally, run the tests with `DOTNET_EnableHWIntrinsic=0`.

CI on a pull request builds and tests on Linux x64 and runs the format, public API, dependency, docs, provenance and sign-off
checks. Pushes to `main`, a nightly run and manual runs add Linux arm64, Windows and macOS, the tests with AVX-512, AVX2 or all
hardware intrinsics turned off, PNG against libpng and the Native AOT smoke app. If your change touches SIMD or
platform-specific code, run the tests with the intrinsics turned off before you open the pull request.

The large third-party corpora (conformance suites and test vectors) are fetched into `.cache` by the `tools/make-*-references.py` scripts and never committed. A normal run skips them; set `EGAMI_CORPUS` to the codecs you are working on (`avif`, `exr`, `heic`, `jbig2`, `jpeg2000`, `jxl`, `tiff`, `vp8`, comma-separated, or `all`) to check them too, for example `EGAMI_CORPUS=jxl dotnet test --filter FullyQualifiedName~Jxl`. The same switch takes a folder of `tests/images` (`avif`, `jpeg`, `pngsuite` and so on): the streaming tests that run over every file skip in a normal run and stream the files of the folders it names.

Reference outputs in `tests/reference` come from the `tools/make-*-references.py` scripts, most of which run the reference
library in Docker. Regenerate them with the script, never by hand.

The allocation-budget tests (managed allocations must not grow with the image, and warm calls allocate nothing) measure bytes
allocated by the test thread, so other tests running alongside can push them a few bytes over. They skip in a normal run and in
CI; run them on demand, alone, when you change hot code:

```
EGAMI_ALLOCATION_TESTS=1 dotnet test tests/Egami.Tests --filter Category=Allocation
```

In PowerShell: `$env:EGAMI_ALLOCATION_TESTS=1; dotnet test tests/Egami.Tests --filter Category=Allocation`.

Benchmarks: `dotnet run -c Release --project benchmarks/Egami.Benchmarks` (also `Egami.Drawing.Benchmarks` and
`Egami.Fonts.Benchmarks`).

Fuzzing: `tools/fuzz-afl.py` runs the readers under AFL++ in Docker; its header explains the usage. A 10-minute run of each
reader you changed is enough for a pull request.

Egami.Video keeps its tools in `tools/video/`: `make-corpus.py`, `make-vp8-corpus.py` and `make-vp9-corpus.py` make the clips
in `tests/video/corpus` and fetch the VP8 and VP9 test vectors into `.cache/video`; `fuzz-afl.py` fuzzes its readers and
codecs; `profile-vp9.py` profiles VP9 against libvpx; `make-subtitle-tracks.py`, `check-subtitle-tracks.py`,
`make-subtitle-references.py` and `time-subtitles.py` make and check the subtitle files against FFmpeg, MKVToolNix and MP4Box;
`make-h264-corpus.py` makes the H.264 clips and their FFmpeg frame hashes, and `make-h264-tables.py` regenerates Egami.Video.H264's
CAVLC and CABAC tables from OpenH264; `count-instructions.py` counts a decoder's instructions, branches and cycles per frame
from the CPU's counters against FFmpeg's, repeatably even on a busy machine. Its gates against FFmpeg, libvpx and the Matroska tools live in the
egami-compare repository (`video/`). Benchmarks: `dotnet run -c Release --project benchmarks/Egami.Video.Benchmarks`.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/Egami/` | The core library |
| `src/Egami.Drawing/`, `src/Egami.Fonts/`, `src/Egami.Fonts.Substitutes/`, `src/Egami.Svg/`, `src/Egami.Css/`, `src/Egami.Metafiles/`, `src/Egami.Barcodes/`, `src/Egami.Heic/`, `src/Egami.Video/`, `src/Egami.Video.H264/` | The other packages ([Packages](docs/articles/packages.md)) |
| `src/Shared/` | Internal source compiled into more than one package as linked files: `src/Shared/Nal/` is the NAL code H.264 and HEVC share, `src/Shared/GdiPlus/` the GDI+ semantics Egami.Metafiles and Egami.SystemDrawing share (their READMEs say how) |
| `tests/Egami.Tests/` | Unit, conformance, round-trip and SIMD equivalence tests (xUnit) for core Egami |
| `tests/Egami.<Package>.Tests/` | The tests of each other package |
| `tests/Egami.Nal.Tests/`, `tests/Egami.GdiPlus.Tests/` | The tests of the shared code in `src/Shared/Nal/` and `src/Shared/GdiPlus/` |
| `tests/Egami.Fuzz/` | Fuzzing harness (SharpFuzz and AFL++) |
| `tests/Egami.Video.Fuzz/`, `tests/video/` | Egami.Video's fuzzing harness, and its clips, ffprobe dumps, fuzz seeds and regressions |
| `tests/Egami.AotSmoke/` | Console app published with Native AOT in CI |
| `tests/Egami.DocSamples/` | The code the docs show, compiled and run with the tests |
| `samples/` | One small console app per guide; `dotnet run --project samples/Formats` |
| `tests/Egami.OutsideTests/` | Plugins written with only the public API: a pixel type, a memory source, operations, formats |
| `tests/images/`, `tests/reference/` | Test images for every format and their reference decodes, with licenses; large corpora are fetched into `.cache` |
| `docs/` | The documentation site (DocFX): guides with recipes, API reference. Build it with `docfx docs/docfx.json` (output in `docs/_site`) |
| `benchmarks/` | BenchmarkDotNet benchmarks for Egami, Egami.Drawing and Egami.Fonts |
| `branding/` | Package icons, logos and the favicon |
| `tools/` | Scripts that regenerate references and the public API baseline, check the docs are complete, and the libpng checker used in CI |

## Code style

Match the surrounding code: its naming, comment density and idioms. `.editorconfig` and `dotnet format` decide formatting.
Egami has its own API vocabulary: `Raster<TPixel>` for an image, pixel types such as `Rgba8`, `Rgb8`, `Gray8` and `RgbaF32`,
`EgamiContext` for configuration, `Raster.Probe` to read a header without decoding, and `Apply` (change in place) and `Derive`
(return a new raster) for operations. Use these names rather than ones borrowed from other libraries.

## Documentation

Every user-visible change updates the docs in the same pull request:

- The guide in `docs/articles/guides/` for the area. A guide explains an area (options, defaults, limits); a recipe under its
  `## Recipes` heading is one short task with its code. A new guide goes into its package's section of
  `docs/articles/toc.yml` and `docs/articles/guides/index.md`, in the same order; then run `python tools/make-llms-txt.py` to
  update `docs/llms.txt`.
- The package's agent guide in `tools/agent-guides/<PackageId>.md` when its main entry points or common mistakes change: a short
  guide for coding agents, packed into the NuGet package as `AGENTS.md`. Its samples are `{{sample File.cs#region}}` lines from
  `tests/Egami.DocSamples/`. `python tools/make-llms-txt.py` writes `docs/agents/<PackageId>.md` from it; rerun it after changing
  a guide or a sample region it uses.
- Code shown in the docs lives in `tests/Egami.DocSamples/` as `#region` blocks, so it is compiled and run with the tests.
  Guides include it with `[!code-csharp[](../../../tests/Egami.DocSamples/File.cs#region)]`, and XML `<example>` tags point at
  the same regions. Don't paste code into a guide that the tests don't run.
- An entry in `CHANGELOG.md` under the package's next version (for example `## Egami.Fonts 1.1.0 (unreleased)`, at the shared
  version for the release train; add the heading if it is not there yet), grouped by feature.
- A new package also goes into `docs/docfx.json` (API reference) and `docs/articles/packages.md`.
- `python tools/check-docs.py <package folder>` must pass for every package you touched.

Write docs in plain, short sentences that say what the user gets. Describe behavior, not how the change was planned.

## Documentation versions

The docs site keeps one folder per major version (`v1/`, `v2/`, ...) with `versions.json` and an `index.html` that sends
readers to the newest version. `docs/versions.json` lists the major versions, newest first; `docs/template/public/main.js`
reads it to show a version picker on every page and a notice on pages of an older version. Minor versions share their
major's folder, because 1.x stays compatible. When a new major version starts, keep the old one on a `release/<major>.x`
branch, add the new version at the top of `docs/versions.json` and build each folder from its own branch.

## Releases

Egami, Egami.Drawing, Egami.Fonts, Egami.Svg, Egami.Css, Egami.Heic and Egami.Cli form one release train: they share one
version, `EgamiVersion` in `Directory.Build.props`, and release together, so never set a version in their project files. A
tag `v<version>` (that version) runs `.github/workflows/release.yml`: it tests, packs the seven packages and pushes them to
nuget.org with trusted publishing, after a maintainer approves the `nuget` environment. The repository holds no NuGet API
key: a trusted publishing policy on nuget.org names this repository, `release.yml` and the `nuget` environment, and the secret `NUGET_USER` holds the
nuget.org account name. Egami.Barcodes, Egami.Metafiles, Egami.Fonts.Substitutes and Egami.Video keep their own version in
their project file: a tag `<package>-v<version>`, for example `Egami.Barcodes-v1.0.1`, runs the same workflow for that one
package at that version, so it releases on its own. Only maintainers tag releases. A package that only other Egami packages
or tools use sets `<EgamiUnlisted>true</EgamiUnlisted>` in its project file (Egami.Css and Egami.Cli.Host do): nuget.org
cannot take a package unlisted, so `release.yml` unlists that version right after the push. It stays out of search but
still restores as a dependency.

## Pull requests

All changes, including the maintainer's, go through a branch and a pull request to `main`; the maintainer reviews and merges.

- Start from an up-to-date `main` on a branch of your own. One topic per pull request.
- Commit messages read `Area: what changed`, for example `JPEG XL lossy: faster block-size search`. Sign off every commit.
- Before you open the pull request, rebase onto `main` and run `dotnet format Egami.slnx --verify-no-changes`, the tests and
  `python tools/check-docs.py` for each package you touched. If `main` moves on, rebase again and push with
  `--force-with-lease`; never force-push a branch someone else works on.
- Fill in the PR template, including the provenance section. List your local results: benchmark numbers for hot code, and the
  tests with `DOTNET_EnableHWIntrinsic=0` when you touch SIMD code.
- New public API needs XML docs and an update to `PublicAPI.Unshipped.txt` (`python tools/update-public-api.py` adds what the build reports). `PublicAPI.Shipped.txt` is the frozen 1.0 API: changing or removing anything in it is a breaking change and needs a maintainer's agreement first; say so in the commit and the changelog.
- A public interface in a package at 1.0 or above (for example `IGlyphOutlineSink`, `IColorPaintVisitor`, `IFontFallbackResolver` and `ITextPaint` in Egami.Fonts, and `ICssElement` in Egami.Css) is frozen for the major version: a member added in 1.x comes as a default interface method, so code that implements the interface keeps compiling.
- Core Egami takes no package references except build-time analyzers. A satellite package may also use a managed MIT, BSD or
  Apache-2.0 package the maintainer approved by name, listed in [docs/references.md](docs/references.md#approved-packages).
  Propose any new one in an issue first.
- Changes to hot code (pixel conversion, codecs, operations) need benchmark results. They must not be slower or allocate more than `main`.
- New test images must have a clear license and an entry in `tests/images/LICENSES`.

## Reporting security problems

See [SECURITY.md](SECURITY.md). Please don't open public issues for security bugs.
