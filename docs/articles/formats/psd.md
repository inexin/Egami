# PSD and PSB

Egami reads Adobe Photoshop documents (.psd) and large documents (.psb), written from Adobe's Photoshop File Format
Specification. `PsdFormat.Info` is registered in `EgamiContext.Default`; PSD is never written. Egami reads the flattened image
Photoshop stores after the layers, in every color mode and depth, and lists the layers as metadata; `PsdImage.ReadLayers` reads
every layer with its pixels and mask (see [Reading layers](#reading-layers)).

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
alpha unless an opaque Normal layer covers the canvas at the top level. Lab files flatten in Lab and give sRGB; bitmap, indexed
and multichannel files give their stored image.

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
disabled ones), vector masks (filled with the non-zero rule and antialiased), the feather of both, clipping (the clipped layers
blend onto their base and keep its coverage), pass-through and isolated groups, shallow and deep knockout, blending ranges
("blend if"), fill layers (solid color, gradient and pattern), adjustment layers and layer effects. Noise gradients and
patterns the document does not hold are left out with one warning a kind naming the layers. 8 and 16-bit documents blend
their stored values and 32-bit ones linear light, as Photoshop does. The result has the type Load gives the file, with alpha
only when some pixel is not opaque. `PsdFlattenOptions.ApplyAdjustments` turns adjustment layers off and
`PsdFlattenOptions.ApplyEffects` layer effects. Bitmap, indexed and multichannel documents are not flattened.

- **Fill opacity** in Photoshop's eight special modes (Color Burn, Linear Burn, Color Dodge, Linear Dodge, Vivid Light,
  Linear Light, Hard Mix, Difference) goes into the formula, as in Photoshop: it pulls the layer's color toward the mode's
  neutral color, and the result covers the backdrop by the layer's coverage and opacity. Vivid Light keeps a source of pure
  black or white, as Photoshop does.
- **Masks.** When a layer has both a user and a vector mask, the combined mask Photoshop stores with them is used. A feathered
  mask is blurred by a Gaussian whose σ is the feather radius, which matches Photoshop's feathered masks within a level on
  average.
- **Knockout** shows, through the layers of a group set to knock out, the group's backdrop (shallow: what is under the group,
  or nothing in an isolated group) or the Background layer (deep, and shallow at the top level).
- **Gradient fills** are drawn afresh in all five styles (linear, radial, angle, reflected, diamond) with their angle,
  scale, offset, reverse and alignment: aligned with the layer, a gradient spans its vector mask's bounds, else the canvas.
  Color and transparency stops have midpoints (owned by the stop on the right) and the smoothness bends each segment. Of
  Photoshop 2022's methods, Classic, Smooth and Stripes mix in the document's space; Linear mixes in linear light and
  Perceptual in Oklab, in RGB documents only (as Classic in the others). Photoshop's dither is not copied.
- **Pattern fills** tile the pattern from the document's pattern table (gray, indexed, RGB, CMYK or Lab, 8 or 16-bit) from
  the canvas's origin moved by the phase, scaled and turned by the angle, sampled between pixels when scaled or turned.
- **Lab** documents blend their L\*, a\* and b\* values as stored, the separable modes as for RGB and Hue, Saturation, Color,
  Luminosity, Darker Color and Lighter Color on lightness, chroma and hue, then become sRGB as Load converts Lab. Adjustments
  work through sRGB, as CMYK's work through RGB.

Hard Mix at a fill opacity under 100 %, deep knockout, Lab's non-separable modes and Lab adjustments are written from the
specification and Photoshop's documentation but no Photoshop-saved file checks them yet.

The adjustment layers applied are levels, curves, invert, hue/saturation (and colorize), brightness/contrast, exposure,
vibrance, posterize, threshold, photo filter, channel mixer, color balance, black and white, selective color, gradient map and
color lookup (a CUBE table, through `Lut3D`). Gradient maps with a noise gradient, color lookups in another table format or
the abstract-profile form, and version 3 photo filters are left out with a warning. Levels, curves, invert, posterize,
threshold, channel mixer and gradient map follow Photoshop's documented formulas, and exposure matches a Photoshop-saved file
within PSNR 45 dB. The others are Egami's own formulas from Photoshop's documentation: hue/saturation, the current (not
legacy) brightness/contrast, vibrance, photo filter, color balance, black and white, selective color and color lookup have not
been checked against a Photoshop-saved file, since no public file has them over pixels it shows, so expect color differences
there.

These fill settings are not checked against Photoshop either: the Linear, Perceptual, Smooth and Stripes methods, gradients in
32-bit documents (fill colors there are not made linear), and a pattern fill linked to a layer that was moved (Photoshop then
starts the tiles elsewhere).

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-flatten)]

### Layer effects

The ten layer effects are drawn from a layer's effects (the lfx2 and lmfx blocks, a group's lfxs, and the old lrFX block
when a layer has none of those), with several of a kind in the newer files, the first on top:

- **Drop shadow** and **outer glow** go under the layer, **strokes** outside its shape too; the content then goes over them,
  in its own mode. A drop shadow is knocked out by the layer (Layer Knocks Out Drop Shadow) unless that is off.
- **Color, gradient and pattern overlays**, **satin**, **inner glow**, **inner shadow**, **bevel and emboss** and the part of
  a stroke inside the shape go over the content, within its shape, in that order from the bottom.
- The shape is the layer's coverage after its masks; for fill and shape layers, the vector mask's. The flags Photoshop keeps
  with a layer apply: Blend Interior Effects as Group, Transparency Shapes Layer, Layer Mask Hides Effects and Vector Mask
  Hides Effects. Fill opacity takes the content and leaves the effects; opacity takes both.
- Shadows and glows blur by a Gaussian of σ 0.4 (shadows, satin) and 0.3 (glows, grown by a fifth first) times the size,
  fitted to Photoshop's; the precise glow technique, strokes and bevels measure the distance to the shape's edge exactly
  (Felzenszwalb and Huttenlocher's transform), a shape that runs past the canvas included. Contours, the range, choke and
  spread, global light, noise and jitter (from a fixed seed, so a file flattens the same each time) apply. Scale Effects is
  only what the dialog shows: the sizes are stored already scaled.

Against the composites Photoshop stored, the drop shadow files reach 40 to 55 dB PSNR, the glows 38 dB, and the stroke
files 39 dB and over (several exactly). Bevel and emboss is close but not exact (about 30 to 34 dB), below the 38 dB the
other effects are held to: Photoshop's height profile is fitted, not known, so bevel results are recorded, not gated. These effect settings are not checked against a Photoshop-saved file: radial and other
non-linear gradient overlays (one file fits only with a smaller radius than the gradient fill's), pattern overlays and
pattern strokes linked to the layer, gradient strokes other than linear, the chisel techniques, emboss and pillow emboss,
stroke emboss, bevel textures and contours, satin, inner glow from the center, effects in CMYK, Lab and 32-bit documents, and
the old lrFX block's glows and bevels. Effects on knockout layers are left out with `Psd.EffectsSkipped`, as are effects
whose settings cannot be read.

[!code-csharp[](../../../tests/Egami.DocSamples/Psd.cs#psd-flatten-effects)]

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
