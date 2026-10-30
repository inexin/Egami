# Egami.Video: guide for coding agents

Egami.Video reads, writes, remuxes, trims and edits video and audio in managed code. Frames are Egami rasters, so Egami's
operations and drawing work on them. Containers: MP4 and QuickTime, Matroska and WebM, AVI, IVF, Ogg and the audio files.
Video codecs: AV1, VP9, VP8, FFV1 and Motion JPEG (decode and encode). Audio: PCM, FLAC, Opus, ALAC (decode and encode), Vorbis,
MP3, AC-3 and G.711 (decode). Royalty-free codecs only: H.264 and HEVC decoding are the separate packages Egami.Video.H264 and
Egami.Video.Hevc.

Namespaces: `Egami.Video` (readers, writers, `MediaFile`, `MediaContext`), `Egami.Video.Codecs`, `Egami.Video.Containers`,
`Egami.Video.Editing` (timelines), `Egami.Video.Audio`, with `Egami` and `Egami.Pixels`.

{{shared}}

## Entry points

- `MediaFile.Probe(path)`: container, duration, tracks, tags and chapters, without decoding.
- `VideoReader.Open(path)` and `reader.ReadFrames<TPixel>()`: decoded frames as `VideoFrame<TPixel>`, each with a `Raster` and
  a `Timestamp`.
- `VideoWriter.Create(path, VideoWriterOptions)` and `writer.WriteFrame(raster)`: the container from the extension, the codec
  from `Codec` (`VideoCodecs.Av1`, `Vp9`, `Vp8`, `Ffv1`, `MotionJpeg`).
- `MediaFile.Remux` and `MediaFile.Trim`: copy packets into another container, or cut, without decoding what need not be.
- `MediaFile.OpenDemuxer` and `MediaFile.CreateMuxer`: packet by packet.
- `VideoEdit` with `Clip`: joins, transitions, layers and titles, rendered to a file.
- `MediaContext`: like `EgamiContext`, for video; `MediaContext.SetDefault` at startup. The long-running calls take an
  `IProgress<MediaProgress>` and a `CancellationToken`.

## Samples

What a file holds:

{{sample Video.cs#video-probe}}

Read every frame:

{{sample Video.cs#video-read}}

Write a clip:

{{sample Video.cs#video-write}}

Encode VP9:

{{sample VideoEncode.cs#video-encode-vp9}}

Remux without decoding:

{{sample Video.cs#video-remux}}

Trim:

{{sample VideoTrim.cs#video-trim}}

Progress:

{{sample Video.cs#video-progress}}

Join clips:

{{sample Editing.cs#edit-join}}

## Common mistakes

- Not disposing frames. Each `VideoFrame` owns a raster from the pool: dispose it in the loop, as the samples do.
- Decoding and encoding again to change the container. `MediaFile.Remux` copies the packets, keeps the sound and loses no
  quality.
- Expecting H.264 (most MP4 files) or HEVC to decode with this package alone. Add Egami.Video.H264 or Egami.Video.Hevc and call
  `WithH264()` or `WithHevc()` on the `MediaContext`; without them their packets are only copied through.
- Expecting an H.264 or HEVC encoder. There is none; encode AV1, VP9 or VP8 for delivery and FFV1 for lossless archives.
- Converting every frame to RGB when YUV is enough. `reader.ReadFrames()` without a pixel type gives the decoder's planar YUV
  frames, with no conversion.
- Trimming at keyframes when frame accuracy matters: the default `MediaFile.Trim` mode is frame-exact; `TrimMode.Keyframe`
  only copies.

## More

- Video guide: https://inexin.github.io/Egami/v1/articles/guides/video.html
- Encoding: https://inexin.github.io/Egami/v1/articles/guides/video-encoding.html
- Editing: https://inexin.github.io/Egami/v1/articles/guides/video-editing.html
- Audio: https://inexin.github.io/Egami/v1/articles/guides/audio.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Video.html
