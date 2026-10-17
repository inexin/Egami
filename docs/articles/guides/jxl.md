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
[HDR guide](hdr.md)). Groups decode on several threads (`EgamiContext.MaxParallelism`), and `ReaderOptions.Region` on a still
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
may carry.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#layers)]

## Extra channels

Alpha loads straight (premultiplied color is divided out). `JxlDetails.ExtraChannels` lists every channel as a
`JxlExtraChannel` with its `JxlExtraChannelKind` (alpha, depth, spot color, selection mask and others), and
`JxlImage.ReadExtraChannel` reads any one alone as gray floats.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#extra-channels)]

## Recompressed JPEGs

JPEG XL can hold a JPEG losslessly, about a fifth smaller. `JxlImage.RecompressJpeg` makes such a file from a JPEG, and
`JxlImage.ReconstructJpeg` gives the original back byte for byte. `JxlDetails.HasJpegReconstruction` says which files can.

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#recompress)]

Such files also load as pixels like any other:

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#reconstruct)]

## Writing

The writer makes lossless Modular files in the raster's own precision, or lossy VarDCT files in XYB, with ICC, EXIF, XMP and
orientation. `JxlWriterOptions` follows cjxl's own knobs, so the same numbers give about the same files:

| Option | Default | Does |
| --- | --- | --- |
| `Lossless` | `null` | `true` writes Modular, `false` VarDCT; `null` keeps the kind of the JPEG XL file the raster came from and writes lossy otherwise |
| `Effort` | 5 | 1 (fastest) to 7 (smallest), cjxl's `-e`, for both kinds |
| `Quality` | 90 | Lossy quality, 0 to 100, cjxl's `-q` scale; 90 is distance 1.0, visually lossless |
| `Distance` | `null` | A distance as cjxl's `-d` instead of a quality, 0.1 to 25 |
| `AlphaDistance` | 0 | Lossy files: 0 keeps alpha exact, larger values code it lossy |
| `KeepTransparentColors` | `true` | Lossy files keep the color under fully transparent pixels; `false` gives smaller files |
| `Progressive` | `false` | Lossy files as cjxl `-p` makes them: a coarse image first that sharpens |
| `PhotonNoiseIso` | `null` | Film-like noise the decoder adds, as cjxl's `--photon_noise_iso` |

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#save)]

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#save-lossy)]

[!code-csharp[](../../../tests/Egami.DocSamples/Jxl.cs#save-lossy-alpha)]
