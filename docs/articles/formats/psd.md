# PSD and PSB

Egami reads Adobe Photoshop documents (.psd) and large documents (.psb), written from Adobe's Photoshop File Format
Specification. `PsdFormat.Info` is registered in `EgamiContext.Default`; PSD is never written. Egami reads the flattened image
Photoshop stores after the layers, in every color mode and depth, and lists the layers as metadata without their pixels.

## Reading files

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-load)]

A file saved without Maximize Compatibility holds only a placeholder; `PsdDetails.HasRealComposite` says so, and Load then
flattens the layers instead (see [No real composite](#no-real-composite)).

| Color mode | 1-bit | 8-bit | 16-bit | 32-bit |
| --- | --- | --- | --- | --- |
| Bitmap | `Gray8`, 1 black | | | |
| Grayscale, duotone | | `Gray8`, `GrayAlpha8` | `Gray16`, `GrayAlpha16` | `GrayF32`; `RgbaF32` with alpha |
| Indexed | | `Rgb8`; `Rgba8` with a transparent index | | |
| RGB | | `Rgb8`, `Rgba8` | `Rgb16`, `Rgba16` | `RgbF32`, `RgbaF32` |
| CMYK | | `Cmyk8`; `Rgba8` with alpha | `Cmyk16`; `Rgba16` with alpha | |
| Lab | | `Rgb8`, `Rgba8`, converted to sRGB | `Rgb16`, `Rgba16` | |
| Multichannel | | `Gray8` of the first channel | `Gray16` | |

CMYK loads as ink amounts, with the file's ICC profile, so `ConvertColorTo` and `ConvertColor` turn it into RGB through the
profile as for a CMYK JPEG; with transparency it loads as RGBA by the plain formula. Lab is converted to sRGB from D50 by
Bradford, as for TIFF's CIELab. 32-bit files are linear light and say so in `RasterMetadata.ColorSpace`, or carry the file's
profile. A duotone file loads as its gray channel, its inks kept in `PsdDetails.ColorModeData`; a multichannel file has no color
model, so its first channel loads as gray with the `Psd.ChannelsDropped` warning.

Transparency is the channel after the color ones when the layer count is negative, as Photoshop marks it; Photoshop stores the
flattened image flattened onto white, and the white is divided back out, so colors under partial transparency come out as
painted. ImageMagick does the same for RGB files only, and Pillow not at all. Other extra channels (alpha and spot channels) are
not loaded; their names are in `PsdDetails.ExtraChannels`.

`PsdDetails` says what the file is: PSD or PSB (`Version`), its `PsdColorMode`, bits a channel, channel count and the
`PsdCompression` of the flattened image.

The image resources map to metadata: the ICC profile (1039), resolution (1005), EXIF (1058, whose orientation reaches
`RasterInfo.Orientation`), XMP (1060) and IPTC (1028). Every other resource, the thumbnail (1036) among them, is kept as it was
read in `PsdDetails.OtherResources`.

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-details)]

## No real composite

A file saved without Photoshop's Maximize Compatibility holds no real flattened image, only a placeholder, and
`PsdDetails.HasRealComposite` is false. By default Load, Probe and the row readers then flatten the layers as
`PsdDocument.Flatten` does, one layer at a time as they come in the file. `PsdReaderOptions.Composite` chooses:
`PsdComposite.Auto` (the default), `Stored` (always the stored image, the placeholder with the `Psd.NoComposite` warning) or
`Flatten` (always the layers flattened). A flattened image's type comes from the layer records before any pixel is read: with
alpha unless an opaque Normal layer covers the canvas at the top level. Lab, bitmap, indexed and multichannel files give their stored
image.

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-composite)]

## Layers

`PsdDetails.Layers` lists the layer records bottom to top as the file does: name (the Unicode name when there is one), bounds,
opacity (0 to 1, as `DrawImage` takes it), blend mode (Photoshop's key and Egami's `BlendMode` where there is one), visibility, clipping, and whether the record is a
layer or one end of a group, with its nesting level. Their pixels are not loaded; `LoadAll` returns the flattened image only.

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-layers)]

Each record also has `FillOpacity` and a `PsdLayerType`: pixels, text, shape, smart object, fill, adjustment or group.

## Reading layers

`PsdImage.ReadLayers` reads every layer with its pixels into a `PsdDocument`: its `Layers` are `PsdLayerImage`s, bottom to top,
a group after its contents, each naming its group in `ParentIndex`. A layer's `Pixels` are at its own size, placed on the
canvas at `X` and `Y`, in the type Load would use, always with straight alpha: `Rgba8`, `Rgba16` or `RgbaF32` for RGB and Lab
(converted to sRGB), `GrayAlpha8`, `GrayAlpha16` or `RgbaF32` for gray. CMYK layers come as an `InkRaster` in `Ink`.
`ReadLayers<TPixel>` converts every layer to one type. A layer's pixel mask is a `PsdMask`: a gray raster of its own size and
place, the value outside it, and whether it is disabled. The channels are read in file order, so the stream need not seek.

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-read-layers)]

## Flattening

`PsdDocument.Flatten` composites the layers as Photoshop does, with each layer's `Info` as it is now, so a copy changed with
`with` (a layer hidden, another opacity or blend mode) flattens differently. It covers visibility, opacity and fill opacity, all
of Photoshop's blend modes (Soft Light with Photoshop's own formula), pixel masks (density, the default color outside them,
disabled ones), vector masks (filled with the non-zero rule and antialiased), clipping (the clipped layers blend onto their base
and keep its coverage), pass-through and isolated groups, blending ranges ("blend if") and solid color fill layers. Layer
effects, gradient and pattern fills, knockout and mask feathering are left out with one warning a kind naming the layers.
8 and 16-bit documents blend their stored values and 32-bit ones linear light, as Photoshop does. The result has the type Load
gives the file, with alpha only when some pixel is not opaque. `PsdFlattenOptions.ApplyAdjustments` turns adjustment layers off.
Lab, bitmap, indexed and multichannel documents are not flattened.

Levels, curves and invert follow Photoshop's documented formulas. Hue/saturation and the current (not legacy)
brightness/contrast are close approximations that have not been checked against Photoshop yet, so expect small color
differences there. Hard Mix and Vivid Light can differ from Photoshop at pure black and white, and Photoshop's fill opacity in
its eight special modes (Color Burn, Linear Burn, Color Dodge, Linear Dodge, Vivid Light, Linear Light, Hard Mix, Difference)
works like opacity here.

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-flatten)]

## Streaming and speed

Rows come from the channel planes as they are asked for: raw rows at computed offsets and PackBits rows through their byte counts,
read where they lie on a seekable stream, so `Region` reads only the rows it needs and the layer data is skipped without being
read. A stream that cannot seek is read to its end first, and Deflate data is inflated whole. On 12-megapixel files Egami decodes
three to ten times faster than ImageMagick (Magick.NET). Flattening reads and composites one layer at a time, in float
canvases from the shared pool, so its managed allocations do not grow with the image: a 12-megapixel file with three layers
flattens in about 2.2 seconds, against 2.9 for ImageMagick.

## Damaged and hostile data

The header's size is checked against `SafetyLimits` before anything else; PSB's sides of up to 300,000 pixels stay behind the
default limits unless the caller raises them. Every section length is checked against the data that remains, the layer section
is skipped by its length, the image resources and layer records count against `MaxMetadataBytes`, and the PackBits row table is
bounded by the header before it is read. Damaged layer records or resources are warnings (`Psd.LayersDamaged`,
`Psd.ResourcesDamaged`), since the stored flattened image does not depend on them; damaged or short layer channel data leaves
the missing pixels 0 with `Psd.LayerDataDamaged` or `Psd.LayerDataTruncated`, and throws `CorruptDataException` in strict
mode. Each layer and mask is checked against `SafetyLimits` before it is decoded, and the layers and flattening canvases
together against `MaxTotalBytes`. Image data that ends early loads with the missing
pixels 0 and the `Psd.DataTruncated` warning, and throws `CorruptDataException` in strict mode. The reader is fuzzed with AFL++
through whole files and through the image data alone.
