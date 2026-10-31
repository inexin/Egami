# JPEG XL

Egami reads and writes JPEG XL (ISO/IEC 18181), written from the specification with no native code. `JxlFormat.Info` is
registered in `EgamiContext.Default`, so `Raster.Load` recognizes a bare codestream (FF 0A) or the box format from the first
bytes, and `Save("x.jxl")` picks the writer. `JxlDetails` describes the container.

`Raster.Probe` reads the headers alone: size, sample format, channels, orientation and frame count.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#probe)]

## Reading

Every kind of still decodes:

- **Lossless Modular**: every predictor, RCT, palette and squeeze, 1 to 31-bit and float samples, alpha.
- **Lossy VarDCT or Modular** in XYB: every transform size, Gaborish, the edge-preserving filter, LF frames, upsampling, patches,
  splines and noise.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#cookbook-load)]

The natural pixel type follows the bit depth: `Rgb8`, `Rgba8` or `Gray8`, their 16-bit forms, `RgbaF16` for 16-bit float, and
`RgbF32`, `RgbaF32` or `GrayF32` beyond. CMYK loads as `Cmyk8` or `Cmyk16`.

Color: lossy (XYB) files come out in the file's own color space, enumerated (as djxl gives it) or from its ICC profile through
Egami's color pipeline, and that space goes on the raster. PQ and HLG files keep their curve, named by H.273 code points, with
intensity_target as `HdrMetadata.MaxContentLightLevel`; converting to sRGB as they load tone maps them:

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#cookbook-sdr)]

A jhgm gain map loads into `RasterMetadata.GainMap`, and `ReaderOptions.Hdr` renders HDR from it as for Ultra HDR JPEGs (see the
[HDR guide](../guides/hdr.md)). Groups decode on several threads (`EgamiContext.MaxParallelism`), and `ReaderOptions.Region` on a still
decodes only the groups it touches.

`JxlReaderOptions` adds three choices on top of the general reader options:

| Option | Default | Does |
| --- | --- | --- |
| `MaxDownscale` | 1 | 8 or more decodes only a lossy still's LF image, an eighth of the size each way and far faster |
| `RenderSpotColors` | `true` | Mixes spot color channels into the color as djxl does; `false` leaves the color as coded |
| `LinearOutput` | `false` | Gives linear light in float, in the file's own primaries, instead of its encoded color space |

## Animations, layers and previews

Animations load as their blended frames with delays and loop count. `JxlImage.ReadLayers` gives the frames before blending, each
a `JxlLayer` with its offset, duration and `JxlLayerBlend` mode, for an editor; `JxlImage.ReadPreview` gives the preview a file
may carry. Writing them is under [Writing](#animations).

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#layers)]

## Partly downloaded files

While a file arrives through `RasterDecoder.OpenIncremental`, `RenderPreview` draws what libjxl's progressive flush draws of the
bytes so far (djxl `--allow_partial_files`), within one level at 8 bits of it. Nothing shows until a frame's LF global section and
LF groups have arrived. Then a lossy file shows its LF image upsampled 8 times, each 256-pixel group sharpening as its passes
arrive (`Progressive` files in three steps). A lossless file fills in group by group, with the groups still missing black, or
level by level when it is squeezed (cjxl `-R 1`). Rows come out once the last byte has, and are the same pixels as a whole-file
load. Animations show nothing before the end.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#partial-display)]

## Extra channels

Alpha loads straight (premultiplied color is divided out). `JxlDetails.ExtraChannels` lists every channel as a
`JxlExtraChannel` with its `JxlExtraChannelKind` (alpha, depth, spot color, selection mask and others), and
`JxlImage.ReadExtraChannel` reads any one alone as gray floats.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#extra-channels)]

## Recompressed JPEGs

JPEG XL can hold a JPEG losslessly, about a fifth smaller. `JxlImage.RecompressJpeg` makes such a file from a JPEG, and
`JxlImage.ReconstructJpeg` gives the original back byte for byte. `JxlDetails.HasJpegReconstruction` says which files can.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#recompress)]

Baseline and progressive JPEGs with any Huffman tables, restart markers, ICC, Exif and XMP are taken; arithmetic-coded,
lossless, hierarchical, 12-bit and CMYK JPEGs, and 4:1:1 sampling, are refused.

Such files also load as pixels like any other:

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#reconstruct)]

## Writing

The writer makes lossless Modular files in the raster's own precision, or lossy VarDCT files in XYB, with ICC, EXIF, XMP and
orientation. `JxlWriterOptions` follows cjxl's own knobs, so the same numbers give about the same files:

| Option | Default | Does |
| --- | --- | --- |
| `Lossless` | `null` | `true` writes Modular, `false` VarDCT; `null` keeps the kind of the JPEG XL file the raster came from and writes lossy otherwise |
| `Effort` | 5 | 1 (fastest) to 10 (smallest), cjxl's `-e`; lossy files take 8 to 10 as 7 |
| `Quality` | 90 | Lossy quality, 0 to 100, cjxl's `-q` scale; 90 is distance 1.0, visually lossless |
| `Distance` | `null` | A distance as cjxl's `-d` instead of a quality, 0.1 to 25 |
| `AlphaDistance` | 0 | Lossy files: 0 keeps alpha exact, larger values code it lossy |
| `KeepTransparentColors` | `true` | Lossy files keep the color under fully transparent pixels; `false` gives smaller files |
| `Progressive` | `false` | Lossy files as cjxl `-p` makes them: a coarse image first that sharpens |
| `PhotonNoiseIso` | `null` | Film-like noise the decoder adds, as cjxl's `--photon_noise_iso` |
| `FirstFrameOnly` | `false` | Writes only the first frame of a multi-frame raster, as a still |
| `ExtraChannels` | `null` | Extra channels to write beside the pixels: depth, masks, thermal data, spot colors |

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#save)]

Lossless files give back every pixel exactly, in the raster's own precision: 8 or 16-bit, half or single float, gray or RGB,
with alpha when it is not opaque everywhere. The effort follows cjxl's: 1 is its fast path, 7 learns a context tree and tries
palettes and color transforms, 8 and 9 learn from more properties, try more color transforms and weighted predictor settings
and find LZ77 copies, and 10 tries every predictor under one context tree for the whole image, for files within a few bytes of
cjxl's at each effort. Effort 10 is slow, as cjxl's is (seconds a megapixel), and keeps the whole image in memory. From
effort 5, shapes that repeat, such as the letters of a screenshot, are coded once as patches. The ICC profile (or an
enumerated sRGB, linear or HDR space), EXIF, XMP and orientation are kept.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#save-lossy)]

Lossy files are VarDCT in XYB, as cjxl writes them for PNG input, and come out within 5% of cjxl's size and Butteraugli score at the
same settings (libjxl 0.12.0). Pick either a quality or a distance: `Quality` is cjxl's `-q` (90 by default, 100 lossless) and
`Distance` its `-d`, where 1.0 is visually lossless and each step up gives a smaller file with more visible loss; a distance,
when set, wins. `Effort` 6 adds the full block-size search and a per-block edge-preserving filter strength, 7 patches for
repeated shapes such as text. Large images are coded in 2048-pixel chunks, as cjxl does, so memory stays bounded.

A JPEG source is better recompressed than re-encoded: [recompressing](#recompressed-jpegs) keeps its pixels exactly and the
original file recoverable, where lossy encoding its decoded pixels loses detail a second time.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#save-lossy-alpha)]

Alpha is an extra channel coded apart from the color, exact by default, so cut-outs keep their edges (below distance 10;
from there cjxl and Egami code the whole image at half size). `AlphaDistance` above 0 codes it lossy as cjxl's
`--alpha_distance` does. Fully transparent pixels keep their color unless `KeepTransparentColors` is false, which lets the
encoder replace it, as libjxl does by default, for a smaller file.

A frame of more than eight groups (256-pixel squares) is coded group by group at lossless efforts 2, 8 and 9, each group with
its own context model, as libjxl does by default (screenshots and other images with flat areas come out smaller that way), and
then without patches, as with libjxl; effort 10 always codes the whole frame at once.

### Animations

A raster with more than one frame is saved as an animation, through the same `Save`, lossless or lossy, as cjxl makes one from
an APNG or GIF: each frame's `FrameMetadata.Delay` (in ticks of a millisecond, at least one) and the raster's `LoopCount`. Each
frame is cut to the rectangle that changed from the one before. A lossless rectangle whose changed pixels are all opaque is
blended over the canvas, its unchanged pixels made transparent; any other rectangle, and every lossy one, replaces the pixels,
so lossy errors cannot pile up from frame to frame. Effort 1 codes animation frames at effort 2, as cjxl does.
`FirstFrameOnly` writes the first frame alone. An animation is coded a whole frame at a time, so only `Save` writes one, not a
streamed encode.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#write-animation)]

### Layers

`JxlImage.WriteLayers` writes `JxlLayer`s as one frame each, for an editor: each at its own place on the canvas (offsets can be
negative or reach past it), with its `JxlLayerBlend` mode (replace, blend, add, multiply-add or multiply), duration and name,
blended onto the layers below. `ReadLayers` gives every field back, and `Load` gives the image they make. All layers take the
first one's pixel format, with alpha when any layer has it; the file is lossless unless the options ask for lossy, and
animated when a layer has a duration.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#write-layers)]

### Extra channels

`ExtraChannels` writes more channels beside the pixels: a `JxlExtraChannelImage` each, a `JxlExtraChannel` description (kind,
name, 1 to 24 integer bits or a 16 or 32-bit float, subsampling 0 to 3, and a spot color's color) with its samples as a
`Raster<GrayF32>` at the subsampled size. They are coded lossless in lossless and lossy files alike, after the raster's own
alpha, and `ReadExtraChannel` gives them back exactly. Still images only. A CMYK raster (`Cmyk8` or `Cmyk16` with a CMYK ICC
profile) keeps its black channel in lossless files too.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#write-extra-channels)]
