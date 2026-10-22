# Video

Egami.Video reads and writes video with Egami rasters as frames, so resizing, color and drawing from Egami work on video with
no copying. It is fully managed and ships only royalty-free codecs:

- Containers: WebM and Matroska, AVI (with OpenDML for files over 1 GB), IVF and YUV4MPEG2, read and written, and headerless raw
  frames.
- Codecs: AV1 (8, 10 and 12 bit, 4:0:0 to 4:4:4, film grain, equal to libaom frame for frame), VP9 (all four profiles, 8, 10 and 12 bit, equal to libvpx frame for frame), VP8 (with WebM alpha) and Motion JPEG
  decode; Motion JPEG and uncompressed encode. FLAC and PCM audio decode and encode (see [Audio](audio.md)); other tracks
  are copied through untouched.

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

Each codec is a `VideoCodec`: `Av1Codec`, `Vp9Codec`, `Vp8Codec`, `MotionJpegCodec` and `RawVideoCodec`.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-codecs)]

An `IVideoEncoder` takes frames and gives packets, with `VideoEncoderOptions` for the size, frame rate and quality.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-encode)]

`CodecHeaders.Describe` lists a packet's header fields as `CodecHeaderField`s under the codec's own names, for VP8, VP9 and
Motion JPEG.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-headers)]

## AV1

`Av1Codec` decodes AV1 from WebM, Matroska, MP4 and IVF with Egami's AV1 decoder: every profile, 8, 10 and 12 bit, 4:0:0, 4:2:0,
4:2:2 and 4:4:4, film grain, scaled references and scalable streams. Every frame equals libaom's. A packet is a temporal unit;
hidden frames give no frame, and a frame shown later with show_existing_frame comes out then. Frames come out at their own size,
8-bit planes for 8-bit streams and 16-bit planes above, 4:0:0 for monochrome.

- The color comes from the sequence header; the container's fills only what the header leaves unspecified.
- `VideoDecoderOptions.ApplyFilmGrain = false` gives the frames as coded, without the film grain the stream asks for.
- A keyframe is a temporal unit that starts with a shown key frame. A forward key frame, hidden and shown later, is not one, as
  FFmpeg flags them too. When a container's keyframe flag disagrees, the stream wins and `Av1.KeyframeFlagMismatch` is reported.
- A track's av1C (its `CodecPrivate`) gives the sequence header when the first unit has none. The IVF reader builds av1C from the
  first sequence header, and so does the Matroska writer for a track that has none, so IVF files remux to WebM, Matroska and MP4.
- Broken data follows `VideoReaderOptions.Errors`: `Skip` (the default) reports `Av1.CorruptFrame`, or `Av1.MissingReference` for
  a unit that needs frames from before the first key frame, and resumes at the next shown key frame; `Conceal` repeats the last
  good frame in between.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-av1)]

`Av1Packets` splits a temporal unit into its frames and merges them back, and builds the av1C record from a sequence header.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-av1-packets)]

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

## MP4 and QuickTime

`Mp4Format` reads .mp4, .m4v, .m4a and .mov files, flat or fragmented (CMAF and DASH segments, phone recordings), and writes
ISO MP4. Every track is read: AV1, VP9, VP8, Motion JPEG, Opus, FLAC, PCM and mov_text with their settings, and H.264, HEVC,
AAC or any other codec passed through, so a remux keeps them byte for byte. Opening a long file reads only its index.

- Edit lists are applied, as FFmpeg shows them: a delayed track starts later, and packets before the start are marked
  `EncodedPacket.IsDiscardable` (the reader decodes them and drops their frames). `new Mp4Format { IgnoreEditLists = true }`
  gives the media's own times.
- The display matrix is `VideoTrackInfo.Orientation`. Frames come out as coded; `VideoReaderOptions.AutoOrient` turns them.
- A timecode track gives `MediaInfo.Timecode`. Tags, cover art (as an attachment named cover.jpg or cover.png) and chapters
  (Nero chpl or a QuickTime chapter track) are read, and written back.
- `MovTextSample` reads and writes mov_text subtitles with their style runs (`MovTextStyle`: the characters each covers, `MovTextFace` bold, italic or underline, size and color).
- Writing puts moov before the media, so the file plays while it downloads. On a stream that can seek and read the media is
  moved forward at the end; otherwise it waits in a temporary file. `new Mp4Format { MoovAtEnd = true }` writes moov last,
  straight to a seekable stream. A QuickTime file is written as MP4.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-mp4)]

Broken files are read up to the damage, with a warning through `MediaContext.Warnings`: `mp4-truncated` (the file was cut
off), `mp4-bad-track`, `mp4-bad-box` and `mp4-bad-fragment` (broken boxes, skipped), `mp4-sample-table` (tables that disagree),
`mp4-edit-list` (an edit list with several media edits or another rate, not applied), `mp4-sample-descriptions` (a track that
changes its sample description), `mp4-fragment-track` and `mp4-track-id`. Writers report `mp4-timestamps` (a packet decoding
before the one before it) and `metadata-dropped`. A file whose moov follows its media needs a stream that can seek.

## Containers

Each container is a `ContainerFormat`: `MatroskaFormat` (WebM and Matroska), `Mp4Format` (MP4 and QuickTime), `AviFormat`,
`IvfFormat`, `Y4mFormat`, and for audio `WavFormat` and `FlacFormat`.
`RawVideo` reads and writes headerless frames in a layout you give.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-containers)]
