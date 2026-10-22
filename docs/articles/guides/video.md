# Video

Egami.Video reads and writes video with Egami rasters as frames, so resizing, color and drawing from Egami work on video with
no copying. It is fully managed and ships only royalty-free codecs:

- Containers: WebM and Matroska, AVI (with OpenDML for files over 1 GB), IVF and YUV4MPEG2, read and written, and headerless raw
  frames.
- Codecs: VP9 (all four profiles, 8, 10 and 12 bit, equal to libvpx frame for frame), VP8 (with WebM alpha) and Motion JPEG
  decode; Motion JPEG and uncompressed encode. Audio and other tracks are copied through untouched.

## Probing a file

`MediaFile.Probe` reads the headers and returns a `MediaInfo`: the container's `FormatName`, the `Duration`, and the
`Tracks`. A `TrackInfo` gives a track's `Number`, `Kind` (a `TrackKind`) and `CodecId` (see `CodecIds`); a `VideoTrackInfo`
adds its size, bit depth, chroma format, `FieldOrder`, frame rate, color and HDR metadata, and an `AudioTrackInfo` its sample
rate and channels. `MediaMetadata` holds the tags, the chapters (`ChapterInfo`) and the attachments (`AttachmentInfo`).

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-probe)]

## Reading frames

`VideoReader` opens a file and decodes its first video track. `ReadFrames<TPixel>` gives `VideoFrame<TPixel>` frames of RGB
pixels, each with its timestamp, duration and color; dispose each frame when done with it.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-read)]

`ReadFrame` and `ReadFrames` without a pixel type give a `YuvVideoFrame`: the codec's own planes as a `YuvRaster`, at the
stream's subsampling and bit depth, with no conversion. Both kinds of frame are a `VideoFrame`, which is what encoders and
writers take.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-planes)]

`VideoReaderOptions` picks the track, the codec's `VideoDecoderOptions` (such as `DecodeAlpha` for WebM alpha), and what a
broken frame does, as a `DecodeErrorMode`: `Skip` (the default) reports it and drops frames up to the next keyframe, `Conceal`
repeats the last good frame, and `Throw` throws.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-options)]

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-open-async)]

## Seeking

`Seek` takes a `MediaTime` and lands on the frame shown at that time, decoding from the keyframe before it; it is exact over
VP9's hidden and show-existing frames too. `SeekMode.Keyframe` stops at the keyframe instead, which is faster.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-seek)]

## Writing a clip

`VideoWriter` encodes images or frames into a file or stream; `VideoWriterOptions` sets the codec, the frame rate (a
`Rational`), the quality, the chroma format, the metadata and other tracks to copy in.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-write)]

## Remuxing

`MediaFile.Remux` copies a file's packets into another container without decoding. For more control, an `IDemuxer` reads
`EncodedPacket`s (data, timestamps, the keyframe flag and `PacketAddition`s such as WebM alpha) and an `IMuxer` writes them.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-remux)]

## Codecs by hand

`VideoCodecs` finds the codec for a track and creates an `IVideoDecoder`: send packets, receive frames. `VideoCodecs.CreateDecoder<TPixel>`
gives an `IVideoDecoder<TPixel>` that hands out RGB frames.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-decode)]

Each codec is a `VideoCodec`: `Vp9Codec`, `Vp8Codec`, `MotionJpegCodec` and `RawVideoCodec`.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-codecs)]

An `IVideoEncoder` takes frames and gives packets, with `VideoEncoderOptions` for the size, frame rate and quality.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-encode)]

`CodecHeaders.Describe` lists a packet's header fields as `CodecHeaderField`s under the codec's own names, for VP8, VP9 and
Motion JPEG.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-headers)]

## HDR

A frame's `Color` and `Hdr` carry its color code points and static HDR metadata. `YuvVideoFrame.ToRaster` with a target
`ColorProfile` converts through Egami's color pipeline and tone maps PQ and HLG to SDR. VP9 frames in WebM carry HDR10+
dynamic metadata as `Hdr10PlusMetadata`, with its `Hdr10PlusPercentile` distribution and `Hdr10PlusToneCurve`.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-hdr)]

## Measuring quality

`VideoQuality` sums PSNR and SSIM over frames, plane by plane, as `PlaneScores`, and hashes frames the way libvpx's tests do.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-quality)]

## Limits and warnings

A `MediaContext` holds the containers and codecs, the Egami context (memory, threads, safety limits), the `MediaLimits` for
files from outside (packet, header and index sizes, tracks, nesting) and a handler for `MediaWarning`s: what a reader
recovered from. VP9 decodes tile columns and loop filter rows on up to `MaxParallelism` threads.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-limits)]

## Containers

Each container is a `ContainerFormat`: `MatroskaFormat` (WebM and Matroska), `AviFormat`, `IvfFormat` and `Y4mFormat`.
`RawVideo` reads and writes headerless frames in a layout you give.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-containers)]
