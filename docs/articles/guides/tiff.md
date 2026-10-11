# TIFF

Egami reads TIFF and BigTIFF, written from the TIFF 6.0 specification, Adobe's technical notes and supplements and the BigTIFF
design note, starting from the reader in Lars's ClassicMac. `TiffFormat.Info` is registered in `EgamiContext.Default`, so
`Raster.Load` recognizes TIFF from its first bytes. Every page of libtiff's own sample images and of Egami's corpus decodes to the
samples libtiff gives.

## Reading

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-load)]

`Load` reads one page: the first full-resolution page, or the one `TiffReaderOptions.Page` names. Reduced-resolution pages,
SubIFD previews and transparency masks are never counted as pages.

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-page)]

`LoadAll` returns every full-resolution page, each at its own size and pixel type; with `IncludeReducedPages` the
reduced-resolution pages and SubIFDs come too, each after the page it belongs to.

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-pages)]

| Compression | Read |
| --- | --- |
| None, LZW (and TIFF 5.0's bit-reversed LZW), Deflate, PackBits | Yes, with the horizontal and floating-point predictors |
| CCITT modified Huffman, Group 3 (1-D and 2-D), Group 4 | 1-bit pages |
| JPEG, old-style JPEG | 8-bit pages, through Egami's JPEG decoder; old-style JPEG is best effort |
| ThunderScan | 4-bit gray |
| SGILog, SGILog24 | LogL and LogLuv pages, as linear `GrayF32` or `RgbF32` |
| WebP | Lossless; lossy WebP comes with the WebP decoder's lossy stage |
| ZSTD, LZMA, LERC, JPEG XL | Not yet: `NotSupportedException` names the code. Added on real demand |

Each page loads in the pixel type that fits it. Depths Egami has no type for widen to the next: 2 and 4 bits to 8, 10 to 14 bits
to 16, scaled so the maximum stays the maximum; 24 and 32-bit integers and 16 to 64-bit floats load as float types.

| Photometric | 1 to 8 bits | 9 to 16 bits | Wider or float |
| --- | --- | --- | --- |
| Gray (either polarity) | `Gray8`, `GrayAlpha8` | `Gray16`, `GrayAlpha16` | `GrayF32`; `RgbaF32` with alpha |
| RGB | `Rgb8`, `Rgba8` | `Rgb16`, `Rgba16` | `RgbF32`, `RgbaF32`; `RgbaF16` for 16-bit float |
| Palette | `Rgb8`, or `Rgb16` when the color map needs 16 bits | | |
| CMYK | `Cmyk8`; `Rgba8` with alpha | `Cmyk16`; `Rgba16` with alpha | `RgbaF32` |
| YCbCr, at any subsampling | `Rgb8` | `Rgb16` | |
| CIE Lab, ICC Lab, ITU Lab | `Rgb8` | `Rgb16` | `RgbF32` |

Associated (premultiplied) alpha loads as straight alpha, and `TiffDetails.ExtraSamples` says which kind the file had; extra
samples that are not alpha are dropped with a warning. Signed samples are offset so 0 becomes the middle value. Lab is relative
to the page's white point, D50 when the file gives none (as Adobe's note and libtiff have it), and converted to sRGB with
Bradford adaptation, as ICC does. Float pages are linear light. CMYK pages keep their ICC profile, so `ConvertColorTo` converts
them through it.

## Format details and metadata

The tags EXIF defines (ImageDescription, Make, Model, Orientation, Software, DateTime, Artist, Copyright and the rest) and the
EXIF, GPS and Interoperability sub-IFDs become `RasterMetadata.Exif`, so converting a TIFF to JPEG keeps them. ICC profiles, XMP,
IPTC (or the IPTC in Photoshop's resources) and the resolution are read too. `TiffDetails` describes each page: its
`TiffCompression`, `TiffPredictor`, `TiffPhotometric`, `TiffSampleFormat`, `TiffByteOrder` and `TiffLayout`, strips or tiles, and
`TiffExtraSample` kinds. Tags Egami does not map, such as GeoTIFF's and private ones, are kept as `TiffTag` values in `OtherTags`.

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-details)]

## Memory and damaged files

Only the strips or tiles a read touches are decoded, a band at a time, and rows are converted from the band as they are asked
for, so a region or a reduced decode of a large tiled file reads only the tiles it needs. Uncompressed strips in memory are read
where they lie. A stream that cannot seek is read whole first, since a TIFF's index often comes last; the bytes count against
`SafetyLimits.MaxTotalBytes`. Every IFD offset is checked against the file and against loops, and a strip or tile that decodes
short is padded with zero samples and noted in one warning per page (`RecoveryMode.Strict` throws instead).
