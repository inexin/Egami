# Color management

Egami reads color information from every file and keeps it, but changes no pixel unless you ask: a file's numbers load as
stored, exactly as in 1.0. Phase 2.1.2 adds the types that say what those numbers mean, and the conversions between them.

## Profiles

A `ColorProfile` says what a raster's numbers mean: the color model (`ColorModel.Rgb`, `Gray` or `Cmyk`), the primaries and
white point, and the transfer function. The built-ins cover sRGB, linear sRGB, Display P3, Adobe RGB, ProPhoto RGB, Rec. 2020,
the two Rec. 2100 HDR encodings and gray; `ColorProfile.Create` makes any other RGB encoding.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#profiles)]

## Primaries and white points

`ColorPrimaries` holds the CIE 1931 `Chromaticity` of red, green, blue and white. The named sets are BT.709 (sRGB's), Display P3,
BT.2020, Adobe RGB and ProPhoto, with the D65 and D50 white points.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#primaries)]

## Transfer functions

A `TransferFunction` decodes stored values to linear light and back: sRGB, linear, a pure gamma, BT.709, the ICC parametric curve,
a sampled table, and the HDR curves PQ and HLG. Single values use exact double arithmetic; rows use a vectorized form within
1e-5 of it, the same on every CPU. Linear 1.0 is reference white, which for PQ and HLG is 203 cd/m² (ITU-R BT.2408), so HDR
highlights are values above 1.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#transfer)]

## ICC profiles

`IccData` parses an ICC profile (versions 2 and 4): the header fields (version, `IccProfileClass`, data and connection spaces,
the suggested `RenderingIntent`, creation date), the description and copyright text, and every tag with its bytes. Like `ExifData`, it never throws: offsets or sizes outside the profile, a missing `acsp` signature or more bytes than
`SafetyLimits.MaxMetadataBytes` mark it damaged, with `Warnings` saying why, and what could be read stays available.

`ColorProfile.FromIcc` turns a profile into an encoding. Egami converts with RGB, gray and CMYK profiles of the input, display,
output and color space classes, whether they convert through a matrix and curves or through `lut8`, `lut16`, `lutAtoB` and
`lutBtoA` tables; a damaged tag that conversion needs throws `CorruptDataException`, and other classes or data spaces throw
`UnsupportedFormatException`. A matrix profile gets its primaries and curve, so `IsEquivalentTo` recognizes an ICC profile written
for one of the built-ins, and a version 4.4 `cicp` tag naming PQ or HLG wins over the rest.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#icc)]

## Converting

Nothing converts unless asked. `RasterMetadata.GetColorProfile` says what a raster's pixels mean, from its ICC profile, its sRGB
or linear tag or its gamma, and sRGB when the file said nothing. `RasterOps.ConvertColor` converts in place to another encoding of
the same model and records the result: no profile for sRGB, the target's ICC profile otherwise.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#convert)]

What a file says is kept in the metadata: the 1.0 `IccProfile`, `ColorSpace` hint and `Gamma` as before, and now
`CodePoints` (PNG cICP), `Primaries` (PNG cHRM, BMP calibrated endpoints) and `Hdr` (PNG mDCV and cLLI, as `HdrMetadata`).
`GetColorProfile` resolves them in one order for every format: code points, then the ICC profile, then an sRGB tag, then a gamma
or linear tag with the primaries, then a camera's EXIF Adobe RGB marker, and sRGB when the file said nothing. PNG's gAMA 45455
(1/2.2) without other chromaticities than sRGB's counts as sRGB, which is what such files mean.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#hdr-metadata)]

`Raster.ConvertColor<TTo>` converts into a new raster of another pixel type, which is how a CMYK image becomes RGB through its
profile. `ColorTransformOptions` choose the rendering intent (perceptual by default) and black point compensation (off by
default, as in Little CMS and Pillow).

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#convert-type)]

`ReaderOptions.ConvertColorTo` converts while loading. In the natural pixel type the type is kept when the model stays the same;
a CMYK file converted to sRGB loads as `Rgb8`.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#convert-on-load)]

## Transforms on pixel spans

`ColorTransform` compiles a conversion once and applies it to spans of any pixel type, in place or into another type. Transforms
are cached per `EgamiContext`. Matrix encodings (sRGB, Display P3, Adobe RGB, matrix ICC profiles, gray) run as curves and one
matrix; table profiles and CMYK are sampled onto a 33 or 17 point grid and interpolated. 8-bit pixels take a fast path; float
and half types keep values outside 0..1.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#transform)]

`ColorProfile.ToIcc` writes an encoding as a small version 4 ICC profile, the one embedded after a conversion: version 4.3 with
parametric curves, or 4.4 with a cicp tag for PQ and HLG. Little CMS, Pillow and ImageMagick read them.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#to-icc)]

## Writing color tags

Writers take color tags from the generic metadata, so they survive a change of format: PNG writes cICP from `CodePoints`, cHRM
from `Primaries` when there is no ICC profile, and mDCV and cLLI from `Hdr`; QOI marks a raster linear when its profile is linear.
`WriterOptions.OmitSrgbProfile` leaves out an embedded profile that means sRGB (PNG writes its sRGB chunk instead).

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#omit-srgb)]

## Linear light

Operations with `ColorSpace.Linear` (resize, blur, sharpen, grayscale) decode with the raster's own transfer function, from
`GetColorProfile` or the last `ConvertColor` before them in the chain, so a gamma 1.8 file or a PQ image is linearized correctly and
a raster tagged linear is used as it is. Untagged and sRGB rasters give the same bytes as before.

## HDR

PQ and HLG decode to linear light with reference white (203 cd/m², ITU-R BT.2408) at 1.0, so highlights are values above 1, kept
by float and half pixel types. Converting an HDR encoding to an SDR one tone maps by default: `ToneMapping.Bt2390` applies the
EETF of ITU-R BT.2390 to max(R, G, B), which keeps hue, and then clips what is still outside the target's gamut toward each
pixel's luminance. The source peak comes from `ColorTransformOptions.SourcePeakNits`, else the raster's `Hdr` (MaxCLL, then the
mastering display's peak), else 1,000 cd/m². `ToneMapping.Reinhard` is a simpler curve; `ToneMapping.Clip` turns mapping off.
`RasterOps.ToneMap` maps linear HDR pixels in place.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#tone-map)]

## Color values

Nine small structs hold colors in the models apps ask for: `HslColor` and `HsvColor` (on encoded sRGB, as CSS and color pickers),
`XyzColor` (D65, CSS Color 4's XYZ), `LabColor` and `LchColor` (D50, as ICC and CSS Color 4), `OklabColor` and `OklchColor`
(Ottosson's perceptual space), `YCbCrColor` (BT.601 full range by default, or any `YuvMatrix` and `YuvRange`) and `CmykColor`
(the plain formula, or a print profile). Each implements `IColorValue<TSelf>`, so it converts from and to `Rgba8` and `RgbaF32`; `ColorConvert.Convert` converts between
any two through XYZ, and `ColorConvert.FromPixels` and `ToPixels` convert whole rows, from pixels in any encoding.
`LabColor.DeltaE2000` and `OklabColor.Distance` measure how different two colors look.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#color-values)]

Real inks come from a print profile:

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#cmyk-ink)]

## Code points

`ColorCodePoints` holds the ITU-T H.273 code points that PNG's cICP chunk, AVIF and JPEG XL use, exactly as stored, so codes Egami
does not know round-trip.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#code-points)]
