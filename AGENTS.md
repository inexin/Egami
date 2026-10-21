# Instructions for AI coding agents

Egami is a fully managed, MIT-licensed 2D imaging library for .NET 10. Core Egami (`src/Egami`) holds rasters, pixel types,
codecs and operations; the packages Egami.Drawing, Egami.Fonts, Egami.Svg, Egami.Css, Egami.Metafiles, Egami.Barcodes and
Egami.Heic build on it (see [docs/articles/packages.md](docs/articles/packages.md)).

[CONTRIBUTING.md](CONTRIBUTING.md) is the guide for everyone who works on Egami: the clean-room rules, the build, test and
check commands, the repository layout, the docs rules and the pull request flow. Read it first and follow it. This file only
adds what an agent tends to get wrong.

## Hard rules

- **Clean room.** Write code from specifications, papers and the libraries listed in [docs/references.md](docs/references.md).
  Never copy, port, translate or paraphrase code, tests, test images or docs from a project that is not MIT-licensed or listed
  as permissive in docs/references.md, and never from GPL, LGPL or AGPL code or code without a license. If you translate code
  closely from a listed library, add its notice to `THIRD-PARTY-NOTICES.md` in the same change.
- **Managed .NET 10 only.** No P/Invoke, no native binaries, no wrappers around C or C++ libraries. .NET's own APIs (for example
  `ZLibStream`) are fine. Don't add a package reference that docs/references.md doesn't list as approved; ask first.
- **Branch and pull request.** Never commit to `main`, push to it or force-push it, and don't merge your own pull request. The
  maintainer reviews and merges.
- **Never publish.** Don't push to NuGet, push tags, create releases or change GitHub settings or Actions unless the
  maintainer asks for that.

## Working habits

- Before you say a change is done, build it and run the tests for the area, `dotnet format Egami.slnx --verify-no-changes` and
  `python tools/check-docs.py <package folder>`. Report failures as they are; never skip or disable a test to get green.
- Hot code (pixel conversion, codecs, operations) must not get slower or allocate more than `main`. Run the benchmarks and the
  allocation-budget tests (CONTRIBUTING.md has the commands) and put the before and after numbers in the pull request.
- Keep fuzz runs to about 10 minutes unless asked for longer: long runs load the machine and skew benchmark numbers.
- Regenerate reference outputs with their `tools/make-*-references.py` script, never by hand.
- Code shown in docs lives in `tests/Egami.DocSamples/` and runs with the tests; never paste untested code into a guide.
- Use Egami's own API vocabulary (`Raster<TPixel>`, `Rgba8`, `EgamiContext`, `Raster.Probe`, `Apply` and `Derive`), not names
  from other libraries.
- Stage explicit paths (never `git add -A`) and leave files, branches and worktrees you didn't create alone; other people and
  agents may be working in the same repository.
