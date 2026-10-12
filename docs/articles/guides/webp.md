# WebP

Egami reads and writes WebP: lossless (VP8L), lossy (VP8) with or without alpha, and animated, written from RFC 9649 and RFC 6386
with no native code. `WebpFormat.Info` is registered in `EgamiContext.Default`, so `Raster.Load` recognizes WebP from its first
bytes and `Save("x.webp")` picks the writer.

## Writing

[!code-csharp[](../../../tests/Egami.DocSamples/Webp.cs#webp)]

`WebpWriterOptions` follows libwebp's own knobs, so the same numbers give about the same files as `cwebp`:

| Option | Default | Does |
| --- | --- | --- |
| `Lossless` | `null` | `true` writes VP8L, `false` VP8; `null` keeps the kind of the WebP file the raster came from (`WebpDetails.Lossless`) and writes lossy otherwise |
| `Quality` | 75 | Lossy quality, 0 to 100, cwebp's `-q` scale |
| `Effort` | 4 | 0 (fastest) to 6 (smallest), cwebp's `-m`, for both kinds |
| `AlphaQuality` | 100 | Lossy files with transparency: 100 keeps alpha exact |
| `KeepTransparentColors` | `true` | Colors under fully transparent pixels are kept (exactly in lossless files); `false` lets the encoder change them for smaller files |
| `SharpYuv` | `false` | Lossy: a slower color conversion that keeps sharp colored edges |
| `NearLossless` | 100 | Lossless: below 100 pixel values may move a little before coding |
| `MixedFrames` | `false` | Animations: each frame is encoded both ways and the smaller kept |
| `FirstFrameOnly` | `false` | Writes only the first frame, as a still |

Lossy files with transparency carry the alpha in an ALPH chunk: compressed losslessly with the filter that gives the smallest
result (all four tried at effort 5 and 6), and with fewer levels when `AlphaQuality` is below 100, fully transparent and fully
opaque pixels always kept exact.

Lossless WebP holds sides up to 16,384 pixels and lossy up to 16,383; larger rasters throw before any work. Every pixel type can
be saved: it is converted to 8-bit RGBA first.

## Reading

`Raster.Load` gives `Rgba8` when the file says it has alpha (the VP8X flag, the VP8L hint or an ALPH chunk) and `Rgb8` otherwise.
Lossless files decode to exactly libwebp's pixels. Lossy files decode to libwebp's YUV planes sample for sample, and to RGB within
one level of libwebp's: chroma is brought to full resolution exactly as libwebp's default upsampler does, then converted with the
BT.601 kernel of the planar YUV guide. `YuvRaster.Load` returns a lossy file's planes untouched (4:2:0, BT.601, limited range, chroma
in the centre), with the alpha as a fourth plane. Every kind streams through `RasterDecoder`, with regions, reductions and
incremental decode. The same VP8 decoder reads video frames too, inter frames included: see [VP8 video frames](vp8.md).
`WebpReaderOptions` adds two choices on top of the general reader options:

[!code-csharp[](../../../tests/Egami.DocSamples/Webp.cs#webp-read)]

## Format details and metadata

ICC profiles, EXIF and XMP are read into `RasterMetadata` and written back unchanged. WebP has no resolution field, so the
resolution is read from the EXIF block and written into it, in a minimal EXIF block when the raster has none. IPTC has no place in
WebP and is not written. `WebpDetails` says what kind of file it was and keeps chunks Egami does not read, each a `WebpChunk` with
its `WebpChunkPosition`; animation frames carry `WebpFrameDetails`.

[!code-csharp[](../../../tests/Egami.DocSamples/Webp.cs#webp-details)]

## Animation

Animated WebP reads and writes through the same frames as GIF and APNG. Reading composes every frame on a canvas exactly as
libwebp's animation decoder does (its blending and key frames included), starting transparent unless
`WebpReaderOptions.UseBackgroundColor` asks for the ANIM background color; `ReaderOptions.Frames` and streaming through
`RasterDecoder` work as for GIF, holding one canvas whatever the frame count. Writing stores each frame's changed rectangle at
even offsets, blended over the canvas when its new pixels are opaque and replacing them otherwise, with delays rounded to whole
milliseconds and capped at 16,777,215.

[!code-csharp[](../../../tests/Egami.DocSamples/Webp.cs#webp-animation)]
