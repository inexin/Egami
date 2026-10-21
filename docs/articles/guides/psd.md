# PSD and PSB

Egami reads Adobe Photoshop documents (.psd) and large documents (.psb), written from Adobe's Photoshop File Format
Specification. `PsdFormat.Info` is registered in `EgamiContext.Default`; PSD is never written. Egami reads the flattened image
Photoshop stores after the layers, in every color mode and depth, and lists the layers as metadata without their pixels.

## Reading files

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-load)]

A file saved without Maximize Compatibility holds only a placeholder; `PsdDetails.HasRealComposite` says so.

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

A file saved without Photoshop's Maximize Compatibility holds no real flattened image, only a placeholder. Egami loads it with
the `Psd.NoComposite` warning and `PsdDetails.HasRealComposite` false; it does not composite the layers itself, which would need
Photoshop's blend modes, adjustment layers, effects and text. ImageMagick flattens the layers instead.

## Layers

`PsdDetails.Layers` lists the layer records bottom to top as the file does: name (the Unicode name when there is one), bounds,
opacity (0 to 1, as `DrawImage` takes it), blend mode (Photoshop's key and Egami's `BlendMode` where there is one), visibility, clipping, and whether the record is a
layer or one end of a group, with its nesting level. Their pixels are not loaded; `LoadAll` returns the flattened image only.

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-layers)]

## Streaming and speed

Rows come from the channel planes as they are asked for: raw rows at computed offsets and PackBits rows through their byte counts,
read where they lie on a seekable stream, so `Region` reads only the rows it needs and the layer data is skipped without being
read. A stream that cannot seek is read to its end first, and Deflate data is inflated whole. On 12-megapixel files Egami decodes
three to ten times faster than ImageMagick (Magick.NET).

## Damaged and hostile data

The header's size is checked against `SafetyLimits` before anything else; PSB's sides of up to 300,000 pixels stay behind the
default limits unless the caller raises them. Every section length is checked against the data that remains, the layer section
is skipped by its length, the image resources and layer records count against `MaxMetadataBytes`, and the PackBits row table is
bounded by the header before it is read. Damaged layer records or resources are warnings (`Psd.LayersDamaged`,
`Psd.ResourcesDamaged`), since the flattened image does not depend on them. Image data that ends early loads with the missing
pixels 0 and the `Psd.DataTruncated` warning, and throws `CorruptDataException` in strict mode. The reader is fuzzed with AFL++
through whole files and through the image data alone.
