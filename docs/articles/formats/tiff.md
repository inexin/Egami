# TIFF

Egami reads and writes TIFF and BigTIFF, written from the TIFF 6.0 specification, Adobe's technical notes and supplements and the BigTIFF
design note, starting from the reader in Lars's ClassicMac. `TiffFormat.Info` is registered in `EgamiContext.Default`, so
`Raster.Load` recognizes TIFF from its first bytes and `Save("x.tif")` picks the writer. Every page of libtiff's own sample images and of Egami's corpus decodes to the
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
| None, LZW (and TIFF 5.0's bit-reversed LZW), Deflate, PackBits, ZSTD, LZMA | Yes, with the horizontal and floating-point predictors |
| CCITT modified Huffman, Group 3 (1-D and 2-D), Group 4 | 1-bit pages |
| JPEG, old-style JPEG | 8-bit pages, through Egami's JPEG decoder; old-style JPEG is best effort |
| Lossless JPEG (code 7 with SOF3 or SOF11 units) | Gray, RGB and CMYK pages of 2 to 16 bits, each strip or tile decoded to its samples as they are, including DNG's layout of a row as two components half as wide |
| ThunderScan | 4-bit gray |
| SGILog, SGILog24 | LogL and LogLuv pages, as linear `GrayF32` or `RgbF32` |
| WebP | Lossless and lossy, through Egami's WebP decoder |
| JPEG XL (50002, and DNG 1.7's 52546) | Chunky gray, RGB and YCbCr pages of 8 or 16-bit or float samples, lossless and lossy, through Egami's JPEG XL decoder; each strip or tile as its codestream decodes alone. DNG's camera RAW pages (CFA, LinearRaw) are not read |
| LERC | Not yet: `NotSupportedException` names the code. Added on real demand |

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

## Writing

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-save)]

| Option | Default | Does |
| --- | --- | --- |
| `Compression` | the TIFF source's, else `Deflate` | `None`, `Lzw`, `Deflate`, `Zstd`, `Lzma`, `PackBits`, `CcittGroup3` and `CcittGroup4` (1-bit), `Jpeg` (8-bit gray, RGB and CMYK; lossless JPEG at `Quality` 100, 8 and 16-bit), `Webp` (lossless at `Quality` 100, else lossy) |
| `CompressionLevel` | 5 | Deflate level, 0 to 9 |
| `ZstdLevel` | 9 | Zstandard level, 1 to 22, as libtiff's `ZSTD_LEVEL` |
| `LzmaPreset` | 6 | LZMA preset, 0 to 9, with xz's meanings, as libtiff's `LZMA_PRESET` |
| `Predictor` | the source's, else `Horizontal` for integers and `FloatingPoint` for floats | For LZW, Deflate, Zstd and LZMA; `None` turns it off |
| `Quality` | 75 | JPEG and lossy WebP quality; 100 writes lossless JPEG or lossless WebP |
| `TileSize` | the source's, else strips of about 64 KB | A multiple of 16 writes square tiles; 0 forces strips |
| `Layout` | `Classic` | `Big` writes BigTIFF; `Auto` switches when the pixels pass 4 GB; `Classic` throws before writing when they would |
| `Reduce` | `false` | Writes the smallest exact form: 1 bit, gray, a palette of up to 256 colors, or 8 bits from 16 |
| `AssociatedAlpha` | `false` | Writes straight alpha premultiplied (ExtraSamples 1) |
| `ParallelCompression` | `false` | Compresses strips and tiles on several threads; the file is the same |

Every pixel type is written at its own depth: gray and RGB at 8 and 16 bits with or without alpha, `GrayF32`, `RgbF32`,
`RgbaF32` and `RgbaF16` as floats, `Cmyk8` and `Cmyk16` as separated, premultiplied types as associated alpha; packed types widen
to 8 or 16 bits. The writer is deterministic and little-endian, except that re-saving a big-endian TIFF keeps its byte order, as
it keeps the source's compression, predictor and tiling. A JPEG, WebP or CCITT source keeps its compression only while no
operation has changed the pixels, so a lossy file is never compressed twice by accident; a lossless JPEG source keeps it always. `Raster.SaveAll` writes several rasters
as pages.

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-fax)]

Group 4 is the usual choice for scanned text; it takes 1-bit pages only, so threshold first or set `Reduce`.

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-zstd)]

ZSTD (code 50000, libtiff's and GDAL's) is lossless like Deflate. In Egami's benchmarks a TIFF loads about as fast as with
Deflate (the predictor and the pixel conversion take most of the time); level 3 writes about as fast as Deflate's default and
level 9 (the default) a little smaller than it, while on flat images such as screenshots the files are a third smaller. Higher
levels write smaller and slower. Egami's
Zstandard codec is its own managed code from RFC 8878: one frame a strip or tile, with the content size and no checksum, as
libtiff writes. Up to level 15 it follows libzstd's settings and stays within 5 % of its size; from 16, where libzstd switches to
optimal parsing, Egami's files are a few percent larger. libtiff and GDAL read Egami's ZSTD files and Egami reads theirs.

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-lossless-jpeg)]

Lossless JPEG (ITU-T T.81 annex H) keeps 8 and 16-bit gray, RGB and CMYK as they are: RGB is not turned into YCbCr, and inks are
stored as they are, not inverted. Each strip or tile is a whole SOF3 stream with its own Huffman tables and the predictor that
codes it smallest, so `ParallelCompression` applies and there are no JPEGTables. It is the coding DNG uses for camera RAW data;
libtiff's JPEG codec does not read it, so choose Deflate, ZSTD or LZMA for files other TIFF readers must open. Float pages are
written at 16 bits and alpha is refused, as with baseline JPEG.

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-lzma)]

LZMA (code 34925) writes the smallest lossless files, at a cost: on Egami's 2048 by 1536 photo benchmark, preset 6 is 18 %
smaller than Deflate and 16 % smaller than ZSTD 19, but loads about eight times slower and writes about twenty times slower than
Deflate. It suits archives that are written once and read rarely. Each strip or tile is an xz stream, as libtiff writes it: the
delta filter over whole samples, then LZMA2, with no check; Egami's codec is its own managed code from the xz format
specification. Presets 0 to 9 follow xz, with the dictionary cut to the strip, so a 64 KB strip never asks for a 64 MB
dictionary; Egami's files are within 4 % of liblzma's size at every preset. libtiff and GDAL read Egami's LZMA files and Egami
reads theirs. Some libtiff builds (Alpine's, for one) leave LZMA out and cannot open them.

Files are checked against libtiff (no tiffinfo warnings, identical samples), ImageMagick and Pillow, and open in Windows' WIC
except for BigTIFF and tiled gray-with-alpha or 16-bit CMYK, which WIC does not read from any writer.

## Format details and metadata

The tags EXIF defines (ImageDescription, Make, Model, Orientation, Software, DateTime, Artist, Copyright and the rest) and the
EXIF, GPS and Interoperability sub-IFDs become `RasterMetadata.Exif`, so converting a TIFF to JPEG keeps them. ICC profiles, XMP,
IPTC (or the IPTC in Photoshop's resources) and the resolution are read too. `TiffDetails` describes each page: its
`TiffCompression`, `TiffPredictor`, `TiffPhotometric`, `TiffSampleFormat`, `TiffByteOrder` and `TiffLayout`, strips or tiles, and
`TiffExtraSample` kinds. Tags Egami does not map, such as GeoTIFF's and private ones, are kept as `TiffTag` values in `OtherTags`
and written back by the TIFF writer, except tags that hold offsets to structures Egami cannot move.

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-details)]

## Memory and damaged files

Only the strips or tiles a read touches are decoded, a band at a time, and rows are converted from the band as they are asked
for, so a region or a reduced decode of a large tiled file reads only the tiles it needs. Uncompressed strips in memory are read
where they lie. A stream that cannot seek is read whole first, since a TIFF's index often comes last; the bytes count against
`SafetyLimits.MaxTotalBytes`. Every IFD offset is checked against the file and against loops, and a strip or tile that decodes
short is padded with zero samples and noted in one warning per page (`RecoveryMode.Strict` throws instead).

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### Split a multi-page scan into PNGs

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-split)]

Each page keeps its own size and pixel type; see [Reading](#reading).

### Combine pages into one TIFF

[!code-csharp[](../../../tests/Egami.DocSamples/Tiff.cs#tiff-save-all)]
