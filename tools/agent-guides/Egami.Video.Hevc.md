# Egami.Video.Hevc: guide for coding agents

Egami.Video.Hevc decodes and encodes HEVC (H.265) video for Egami.Video, in managed code, on Egami.Heic's HEVC core. It decodes
Main, Main 10, Main 12 and the range extensions to 12 bits, from MP4, MOV, Matroska and raw Annex B `.h265` files, with HDR10
and HDR10+ metadata on the frames, and encodes 4:2:0 Main and Main 10 (`HevcCodec.Instance` in `VideoWriterOptions.Codec`).
HEVC is covered by patents, so read the package README before shipping it.

Namespaces: `Egami.Video` (`WithHevc`), `Egami.Video.Containers` (`HevcFormat`, for raw files), with `Egami.Pixels`.

{{shared}}

## Entry points

- `MediaContext.Default.WithHevc()`: a context that decodes HEVC; chain `WithH264()` too for H.264 files.
  `MediaContext.SetDefault(...)` makes it the default.
- Then Egami.Video's `VideoReader`, `MediaFile.Trim` and the editing timeline decode HEVC tracks.
- `new VideoWriterOptions { Codec = HevcCodec.Instance, Quality = 60 }` with `VideoWriter.Create("out.mp4", options)` encodes;
  `Hevc = new HevcEncoderOptions { ... }` holds the expert settings (profile, level, QP limits, AQ, B-frames, Annex B).

## Samples

Register it:

{{sample VideoHevc.cs#register}}

Encode:

{{sample VideoHevc.cs#encode}}

Re-encode H.264 as HEVC:

{{sample VideoHevc.cs#cookbook-hevc-from-h264}}

## Common mistakes

- Referencing the package and expecting frames. Nothing registers it on its own: call `WithHevc()`.
- Expecting the Dolby Vision enhancement layer, screen content coding or multi-layer HEVC: only the base layer of Dolby Vision
  profile 8 decodes.
- Odd frame sizes: the encoder needs an even width and height (4:2:0); crop or pad first.
- Expecting B-frames with `LatencyMode.Realtime`: they need `LatencyMode.Quality`, the default.
- Measuring PSNR with adaptive quantization on: it lowers PSNR on purpose; set `AdaptiveQuantization = false` to compare.
- Reading HEIC photos through this package. Photos are images: use Egami.Heic with core Egami's `Raster.Load`.

## More

- HEVC: https://inexin.github.io/Egami/v1/articles/guides/hevc.html
- Video guide: https://inexin.github.io/Egami/v1/articles/guides/video.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
