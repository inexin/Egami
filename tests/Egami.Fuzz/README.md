# Egami.Fuzz

Fuzzing harnesses for Egami's readers, run with [SharpFuzz](https://github.com/Metalnem/sharpfuzz) and AFL++.
A reader must either load an input or throw an `EgamiException`; any other exception is a crash, and AFL++ also
reports hangs. CI runs each target for 30 minutes every night (`.github/workflows/fuzz.yml`).

Targets:

| Target | Input | Seeds |
|---|---|---|
| `png` | PNG files: loaded leniently and strictly, converted, probed | `tests/images/pngsuite/*.png` |
| `jpeg` | JPEG files: probed and loaded leniently, strictly, with fast upsampling and at reduced scale, then auto-oriented and saved as JPEG | `tests/images/jpeg/*.jpg` |
| `exif` | Raw EXIF (TIFF) blocks: parsed, every value read, edited and rebuilt; parsing must never throw at all | `tests/images/fuzz/exif-seeds/*.tiff` |

## Running locally (Linux or WSL)

```sh
sudo apt-get install afl++
dotnet tool install --global SharpFuzz.CommandLine
dotnet publish tests/Egami.Fuzz -c Release -o out/fuzz
sharpfuzz out/fuzz/Egami.dll
mkdir -p findings
mkdir -p seeds && cp tests/images/pngsuite/*.png seeds/
AFL_SKIP_BIN_CHECK=1 afl-fuzz -i seeds -o findings -t 5000 -m none -- out/fuzz/Egami.Fuzz png
```

## Without AFL++ (any OS)

A simple mutation fuzzer runs in process: random edits of the seeds for the given seconds, with crashes and inputs that take
over 10 seconds saved to the findings directory. It is not coverage-guided, so it complements AFL++ rather than replacing it.

```sh
dotnet run --project tests/Egami.Fuzz -c Release -- mutate jpeg 3600 seeds-dir findings-dir
```

## Reproducing a crash

```sh
dotnet run --project tests/Egami.Fuzz -c Release -- png path/to/crash
```

Every crash found becomes a regression test: add the file under `tests/images/fuzz/` and a test that loads it.
