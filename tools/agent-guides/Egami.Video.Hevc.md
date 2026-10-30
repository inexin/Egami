# Egami.Video.Hevc: guide for coding agents

Egami.Video.Hevc decodes HEVC (H.265) video for Egami.Video, in managed code, through Egami.Heic's HEVC decoder: Main, Main 10,
Main 12 and the range extensions to 12 bits, from MP4, MOV, Matroska and raw Annex B `.h265` files, with HDR10 and HDR10+
metadata on the frames. It decodes only; HEVC is covered by patents, so read the package README before shipping it.

Namespaces: `Egami.Video` (`WithHevc`), `Egami.Video.Containers` (`HevcFormat`, for raw files), with `Egami.Pixels`.

{{shared}}

## Entry points

- `MediaContext.Default.WithHevc()`: a context that decodes HEVC; chain `WithH264()` too for H.264 files.
  `MediaContext.SetDefault(...)` makes it the default.
- Then Egami.Video's `VideoReader`, `MediaFile.Trim` and the editing timeline decode HEVC tracks.

## Samples

Register it:

{{sample VideoHevc.cs#register}}

## Common mistakes

- Referencing the package and expecting frames. Nothing registers it on its own: call `WithHevc()`.
- Expecting the Dolby Vision enhancement layer, screen content coding or multi-layer HEVC: only the base layer of Dolby Vision
  profile 8 decodes.
- Reading HEIC photos through this package. Photos are images: use Egami.Heic with core Egami's `Raster.Load`.

## More

- HEVC: https://inexin.github.io/Egami/v1/articles/guides/hevc.html
- Video guide: https://inexin.github.io/Egami/v1/articles/guides/video.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
