# Shared NAL code

Internal C# source that H.264 and HEVC share, compiled into each package that needs it as linked source rather than shipped
as a package of its own (H.264 and HEVC design, question 1). Today that is Egami.Heic; Egami.Video.H264 joins it. Neither
package depends on the other, so an H.264 user never loads HEVC code.

| File | What it holds |
| --- | --- |
| `NalBitReader.cs` | u(n), ue(v) and se(v) over an RBSP, with an optional syntax trace for comparing headers with the reference decoders |
| `CabacEngine.cs` | The CABAC arithmetic decoding engine both standards share; each codec keeps its own context tables |
| `NalFraming.cs` | Annex B start codes, length-prefixed units and emulation prevention |
| `NalSei.cs` | The SEI message loop; each codec reads the payloads it knows |
| `NalVui.cs` | The start of the VUI both standards share: aspect ratio, video signal type, colour description, chroma location |
| `NalAccessUnitScanner.cs` | Groups NAL units into access units for streams that do not mark them, from the codec's role for each unit |
| `NalBumpingBuffer.cs` | The output side of the decoded picture buffer: pictures wait for output and leave in picture order |
| `NalErrors.cs` | The errors the shared code throws |

Rules for the folder:

- Everything is `internal`, in the namespace `Egami.Nal`. A package that compiles the folder adds it to its project file with
  `<Compile Include="..\Shared\Nal\**\*.cs" Link="Shared\Nal\%(RecursiveDir)%(Filename)%(Extension)" />` and supplies the other
  part of the partial class `NalErrors`: the constants `FormatName` and `Prefix` its errors carry (see
  `src/Egami.Heic/Hevc/NalErrors.cs`).
- Code goes here only when both codecs use it bit for bit. What differs between H.264 and HEVC stays in the codec.
- The tests are in `tests/Egami.Nal.Tests`, which compiles the folder on its own. Egami.Heic's tests and HM comparisons must
  stay green after any change here, and its decode speed must not drop.
- A test project that can see the internals of two packages that both compile this folder sees two copies of each type; give it
  an `extern alias` for one of them.
