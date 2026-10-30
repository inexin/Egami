# Egami.Video.H264: guide for coding agents

Egami.Video.H264 decodes H.264 (AVC) video for Egami.Video, in managed code: Constrained Baseline, Main, High, High 10 and
High 4:2:2, 8 and 10 bit, progressive or interlaced, from MP4, MOV, Matroska, AVI and raw Annex B `.h264` files. Frames equal
FFmpeg's decode. It decodes only; H.264 is covered by patents, so read the package README before shipping it.

Namespaces: `Egami.Video` (`WithH264`), `Egami.Video.Containers` (`H264Format`, for raw files), with `Egami.Pixels`.

{{shared}}

## Entry points

- `MediaContext.Default.WithH264()`: a context that decodes H.264; `MediaContext.SetDefault(...)` makes it the default.
- Then Egami.Video's `VideoReader`, `MediaFile.Trim` and the editing timeline decode H.264 tracks.
- `VideoReaderOptions.Errors = DecodeErrorMode.Conceal`: hide broken slices instead of throwing.

## Samples

Register it:

{{sample VideoH264.cs#register}}

Decode:

{{sample VideoH264.cs#decode}}

## Common mistakes

- Referencing the package and expecting frames. Nothing registers it on its own: call `WithH264()`.
- Expecting 4:4:4, lossless or Extended profile streams to decode: they are not supported.
- Expecting an encoder. Encode with Egami.Video's AV1, VP9 or VP8 encoders instead.

## More

- H.264: https://inexin.github.io/Egami/v1/articles/guides/h264.html
- Video guide: https://inexin.github.io/Egami/v1/articles/guides/video.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
