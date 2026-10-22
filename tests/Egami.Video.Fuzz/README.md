# Egami.Video.Fuzz

Fuzzing harnesses for Egami.Video's readers, run with [SharpFuzz](https://github.com/Metalnem/sharpfuzz) and AFL++, as in
Egami. A reader must either read an input or throw an `EgamiException`; any other exception is a crash, and AFL++ also
reports hangs. Limits are lowered so a valid but huge file cannot exhaust memory.

| Target | Input | Seeds |
|---|---|---|
| `avi` | Any container (detected): probed, every packet read and Motion JPEG packets decoded, seeks to 0.5 s and 0, then read again as a non-seekable stream | `tests/video/fuzz-seeds/avi/*` |
| `mkv` | The same path, seeded with Matroska files (seekable, and live with unknown sizes) | `tests/video/fuzz-seeds/mkv/*` |
| `y4m` | The same path, seeded with YUV4MPEG2 files; uncompressed frames decoded through RawVideoCodec | `tests/video/fuzz-seeds/y4m/*` |
| `ivf` | The same path, seeded with VP8 IVF files: packets read, VP8 decoded and headers described | `tests/video/fuzz-seeds/ivf/*` |
| `webm` | The same path, seeded with VP8 WebM files, one with alpha (BlockAdditions) | `tests/video/fuzz-seeds/webm/*` |
| `vp8` | A VP8 IVF or WebM file read through VideoReader with Skip, Conceal and Throw: broken frames must never throw in Skip or Conceal; then an exact seek. Instruments Egami.dll too, where the VP8 decoder is | `tests/video/fuzz-seeds/vp8/*` |
| `vp9` | A VP9 IVF, WebM or Matroska file read through VideoReader with Skip, Conceal and Throw, the first frame converted to RGB; then an exact seek | `tests/video/fuzz-seeds/vp9/*` |
| `vp9raw` | VP9 packets without a container (a CodecPrivate, then length-prefixed packets): each described, split and joined as a superframe, then decoded on one thread and on two | `tests/video/fuzz-seeds/vp9raw/*` |
| `obu` | The container path, seeded with AV1 low-overhead OBU and Annex B files | `tests/video/fuzz-seeds/obu/*` |
| `av1` | An AV1 IVF, WebM, Matroska, OBU or Annex B file read through VideoReader with Skip, Conceal and Throw, as for VP9. Instruments Egami.dll too, where the AV1 decoder is | `tests/video/fuzz-seeds/av1/*` |
| `av1raw` | AV1 temporal units without a container (an av1C, then length-prefixed units): each described with and without the references, split into frames and merged back, its av1C extracted, then decoded on one thread and on two | `tests/video/fuzz-seeds/av1raw/*` |
| `mjpeg` | One Motion JPEG packet: decoded at full and 1/4 scale (DHT splicing, AVI1 field weaving, junk trimming), converted to RGB, encoded back | `tests/video/fuzz-seeds/mjpeg/*` |

The seeds are written by `dotnet run --project tests/Egami.Video.Fuzz -- seeds tests/video/fuzz-seeds`.

## AFL++ in Docker (any OS)

```sh
docker build -t egami-fuzz -f tests/Egami.Fuzz/Dockerfile tests/Egami.Fuzz
python tools/video/fuzz-afl.py 10m avi mkv y4m mjpeg
```

Findings land in `.cache/video/afl/<target>`. Triage them with
`dotnet run --project tests/Egami.Video.Fuzz -c Release -- replay avi .cache/video/afl/avi/default/crashes`, and reproduce one with
`dotnet run --project tests/Egami.Video.Fuzz -- avi <file>`. Every fixed crash gets a regression test in
`tests/Egami.Video.Tests/FuzzRegressionTests.cs`.
