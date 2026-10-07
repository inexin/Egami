# Egami

Egami ("image" backwards) is an open source 2D imaging library for .NET that loads, edits and saves images.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke.
- **Safe on untrusted files**: size limits are checked before allocating, and readers are fuzz-tested.
- **Trimming and Native AOT clean**, on every OS and CPU .NET 10 supports.

> **Status:** early development. Nothing is published to NuGet yet. The first milestone is the core types plus a PNG reader and writer.

## Building

Requires the .NET 10 SDK.

```sh
dotnet build Egami.slnx
dotnet test Egami.slnx
```

## Repository layout

| Path | Contents |
| --- | --- |
| `src/Egami/` | The library, the only shipped project |
| `tests/Egami.Tests/` | Unit, conformance and round-trip tests (xUnit) |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Every change is written from specifications and the libraries in [docs/references.md](docs/references.md), and every commit is signed off.

## License

[MIT](LICENSE)
