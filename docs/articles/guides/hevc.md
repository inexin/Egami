# HEVC

Egami.Video.Hevc decodes HEVC (H.265), the codec of iPhone and Android video, GoPro footage, 4K streaming and HDR10. It is a
separate, opt-in package because HEVC is covered by patents (see [Patents](#patents)); Egami.Video itself contains no HEVC
decoder and copies HEVC tracks through untouched when this package is not registered. The decoder is Egami.Heic's, the same one
that reads HEIC photos, so Egami.Video.Hevc depends on Egami.Heic; apps can also drive it directly through Egami.Heic's
`HevcStreamDecoder` (see [HEIC](../formats/heic.md)).

- Profiles: Main, Main 10, Main 12 and the format range extensions up to 12 bits, 4:0:0, 4:2:0, 4:2:2 and 4:4:4. I, P and B
  pictures with merge, AMVP and temporal motion vector prediction, weighted prediction, asymmetric partitions, lossless and
  transform-skip blocks, scaling lists, tiles, wavefronts and several slices per picture.
- Containers: MP4 and MOV (hvc1 and hev1), Matroska through Egami.Video's readers, and raw Annex B `.h265` files through
  `HevcFormat`. Dolby Vision profile 8 files decode as their HEVC base layer, dvh1 and dvhe tracks included.
- Frames equal the reference decoder HM's sample for sample on the JCT-VC conformance bitstreams, and FFmpeg's on every clip
  in the test corpus but one, whose chroma FFmpeg decodes against the stream's own picture hashes there; that clip is checked
  against HM.
- Not decoded: the screen content coding extensions, which give `UnsupportedFormatException`, and layers above the base
  (multi-layer HEVC, Dolby Vision enhancement layers and RPU), which are skipped.

## Registering

`WithHevc()` (from `HevcMediaContextExtensions`) adds the decoder, `HevcCodec`, and the raw stream reader, `HevcFormat`, to a
`MediaContext`. Nothing registers them on its own; set the context as the default with `MediaContext.SetDefault` to use it
everywhere.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHevc.cs#register)]

## Decoding

Frames come out in display order through `VideoReader`, like any other codec, as `YuvVideoFrame`s with the stream's own
planes (8-bit samples in bytes, 10 and 12-bit in ushorts) or converted to a pixel type. The colour comes from the stream's
VUI, then the container, and otherwise BT.601 for 576 lines and fewer and BT.709 above; HDR10 mastering display and content
light level SEI become the frame's `Hdr`, and HDR10+ user data SEI its `Hdr10Plus`.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHevc.cs#decode)]

Broken data follows `VideoReaderOptions.Errors` and reports `Hevc.CorruptFrame`. With `Conceal` a picture whose slice data is
damaged keeps what decoded before the damage and comes out; with `Skip`, the default, and when concealing cannot help (a
broken slice header or parameter set), frames are dropped up to the next IRAP picture; `Throw` throws. Decoding that starts
in the middle of a stream waits for the next IRAP picture, and the leading pictures a CRA picture skips there are not output.

Tiles, wavefront rows and slices decode on `EgamiContext.MaxParallelism` threads, and so do the deblocking filter and SAO.

## Raw .h265 files

`HevcFormat` reads raw Annex B streams (`.h265`, `.265` and `.hevc`), as encoders and `ffmpeg -f hevc` write them. Such a
stream has no timestamps, so frames are timed by the stream's VUI frame rate, or 25 frames a second, unless `FrameRate` is
set. Seeking goes back to the IRAP picture before the target.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHevc.cs#raw)]

## Patents

HEVC is covered by patents. Egami's authors grant no patent rights and are not lawyers. Whether you need a patent licence
depends on your country and on how you use and distribute this package; commercial products may need one from the HEVC patent
pools or holders. Egami.Video does not depend on this package.
