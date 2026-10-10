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

## Code points

`ColorCodePoints` holds the ITU-T H.273 code points that PNG's cICP chunk, AVIF and JPEG XL use, exactly as stored, so codes Egami
does not know round-trip.

[!code-csharp[](../../../tests/Egami.DocSamples/ColorManagement.cs#code-points)]
