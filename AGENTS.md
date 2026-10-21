# Instructions for AI coding agents

Egami is a fully managed, MIT-licensed 2D imaging library for .NET 10, plus the packages Egami.Drawing, Egami.Fonts,
Egami.Svg, Egami.Css and Egami.Metafiles (see [docs/articles/packages.md](docs/articles/packages.md)). Read
[CONTRIBUTING.md](CONTRIBUTING.md) first; this file adds what an agent needs on top of it.

## Hard rules

- **Clean room.** Write code from specifications, papers and the libraries listed in [docs/references.md](docs/references.md).
  Never copy, port, translate or paraphrase code, tests, test images or docs from a project that is not MIT-licensed (or listed
  as permissive in docs/references.md). Never use GPL, LGPL or AGPL code, or code without a license. If you translate code
  closely from a listed library, add its notice to `THIRD-PARTY-NOTICES.md` in the same change.
- **Managed .NET 10 only.** No P/Invoke, no native binaries, no wrappers around C or C++ libraries. .NET's own APIs (for example
  `ZLibStream`) are fine. Core Egami (`src/Egami`) takes no package references other than the build-time
  `Microsoft.CodeAnalysis.PublicApiAnalyzers`. A satellite package (Egami.Drawing, Egami.Fonts, Egami.Svg and the others)
  may also reference a managed MIT, BSD or Apache-2.0 package that the maintainer approved by name; the approved packages
  are listed in [docs/references.md](docs/references.md) with their licence and minimum version. Ask before adding any other.
- **Never publish.** Don't push to NuGet, push tags, create releases or change GitHub Actions settings, unless the maintainer explicitly
  asks for that.
- **Every change goes through a branch and a pull request.** Never commit to `main` or push to it, and never force-push it. The
  maintainer reviews and merges; don't merge your own pull request unless asked (see "Commits and pull requests" below).

## Build, test and check

```sh
dotnet build Egami.slnx
dotnet test Egami.slnx
dotnet format Egami.slnx --verify-no-changes      # must pass before anything lands
python tools/check-docs.py                         # docs complete for src/Egami; pass a folder for another package
python tools/update-public-api.py                  # adds new public API to PublicAPI.Unshipped.txt
```

- Warnings are errors, and the public API baseline (`PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt` in each package) is
  checked by the build. Every new public member needs XML docs and an entry in `PublicAPI.Unshipped.txt`.
- Run only the tests for the area you change while working (`dotnet test --filter FullyQualifiedName~Jxl`), then the whole
  solution before landing. Large corpora are fetched into `.cache` and run only when `EGAMI_CORPUS` names them (see
  CONTRIBUTING.md).
- Hot code (pixel conversion, codecs, operations) must not get slower or allocate more than `main`; run the benchmarks in
  `benchmarks/Egami.Benchmarks` and include the before and after numbers. Also run the allocation-budget tests, which skip in a
  normal run: `EGAMI_ALLOCATION_TESTS=1 dotnet test tests/Egami.Tests --filter Category=Allocation` (see CONTRIBUTING.md).
- **Fuzzing:** runs are 10 minutes (`tools/fuzz-afl.py`). Longer runs only when the maintainer asks: they load the machine and
  skew other sessions' speed measurements.
- Reference outputs come from the `tools/make-*-references.py` scripts, most of them in Docker; regenerate them with the script,
  never by hand.

## Documentation

Every user-visible change updates the docs in the same commit:

- The guide in `docs/articles/guides/` for the area (add new guides to `docs/articles/toc.yml`, then run
  `python tools/make-llms-txt.py` to update `docs/llms.txt`), and a recipe under the guide's `## Recipes` heading where it helps. A guide explains an
  area (options, defaults, limits); a recipe is one short task with its code. New guides go into their package's section of `docs/articles/toc.yml` and
  `docs/articles/guides/index.md`, in the same order.
- Code shown in docs lives in `tests/Egami.DocSamples/` as `#region` blocks and is compiled and run with the tests; guides
  include it with `[!code-csharp[](../../../tests/Egami.DocSamples/File.cs#region)]`. XML `<example>` tags point at the same
  regions. Never paste untested code into a guide.
- An entry in `CHANGELOG.md` under the package's next version (`## Egami.Fonts 1.1.0 (unreleased)`; add that heading if it is
  not there yet), grouped by feature.
- A new package also goes into `docs/docfx.json` (API reference) and `docs/articles/packages.md`.
- `python tools/check-docs.py <package folder>` must pass for every package you touched.

Write docs in plain, short sentences that say what the user gets. Describe behavior, not the planning history (no phase or task
numbers in docs).

## Code style

Match the surrounding code: its naming, comment density and idioms. Egami uses its own API vocabulary (`Raster<TPixel>`,
`Rgba8`, `EgamiContext`, `Raster.Probe`, `Apply` and `Derive`); keep to it. `.editorconfig` and `dotnet format` decide
formatting.

## Commits and pull requests

- Start every change on a new branch from an up-to-date `main`. Several agents often work at once, each in its own worktree
  and branch; leave other worktrees alone.
- Commit messages: `Area: what changed`, for example `JPEG XL lossy: faster block-size search`. Sign off every commit
  (`git commit -s`; CONTRIBUTING.md explains the DCO). Stage explicit paths, never `git add -A`.
- Before opening the pull request, rebase the branch onto `main` and run `dotnet format Egami.slnx --verify-no-changes`, the
  tests and `check-docs` for each package you touched. Then push the branch (`git push -u origin <branch>`) and open the pull
  request with `gh pr create`, filling in `.github/PULL_REQUEST_TEMPLATE.md`, provenance section included. CI on a pull
  request only builds and tests on Linux x64; list your local results in the description (the benchmark numbers for hot code,
  and the tests with `DOTNET_EnableHWIntrinsic=0` when you touch SIMD code).
- If `main` moves on, rebase the branch onto it, run the tests again and push the branch with `--force-with-lease`. That is
  the only force-push allowed, and only on your own branch.
- Breaking changes to the 1.0 API are still allowed while nothing is released, but say so in the commit and the changelog.
