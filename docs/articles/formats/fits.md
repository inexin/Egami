# FITS

FITS (Flexible Image Transport System) is astronomy's image format. A file is a chain of header and data units (HDUs). Each
header is a list of 80-character keyword cards in 2880-byte blocks. Its data is an array of big-endian 8, 16, 32 or 64-bit
integers or 32 or 64-bit floats, and BSCALE and BZERO turn the stored numbers into physical values such as counts or flux.
FITS reads and writes through the Egami.Formats.Extended package: register it with `WithFits()` or `WithExtendedFormats()`
([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | The FITS Standard 4.0: the primary HDU and every `IMAGE` extension, BITPIX 8, 16, 32, 64, -32 and -64, BSCALE, BZERO and BLANK, 2D images and cubes (each plane an image); tile-compressed images (the tiled image convention) in RICE_1, GZIP_1, GZIP_2, PLIO_1 and NOCOMPRESS, with quantized floats and their dither; headers with CONTINUE long strings and HIERARCH keywords. Tables are listed, not read as images |
| Writes | One primary HDU: BITPIX 8, 16 (unsigned, BZERO 32768) or -32; gray as a 2D image, colour as a cube of three planes; the header cards of a raster read from FITS, and cards of your own |
| Natural pixel type | `GrayF32`, or `RgbF32` with `FitsReaderOptions.Rgb` |
| `FormatInfo` | `FitsFormat.Info`: name `FITS`, extensions `.fits`, `.fit` and `.fts`, MIME types `image/fits` and `application/fits` |

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#fits-load)]

The first stored row is the bottom of the picture, as the standard's axes run and astronomy viewers show it. Load turns it
upright, so the first row of the raster is the last row of the file. `FitsFile.ReadValues` keeps the file's order.

## Images and planes

Every image HDU is an image, and so is every plane of a cube: an HDU with NAXIS3 of 4 gives four images, one after the other.
`RasterInfo.ImageCount` counts them, LoadAll reads them all and `FitsReaderOptions.Image` picks one by that count. An empty
primary HDU (NAXIS 0, common when the images are in extensions) is skipped. Tables, random groups and extensions Egami does
not know are skipped too. A file with no image throws `UnsupportedFormatException`.

`FitsReaderOptions.Rgb` reads a cube of exactly three planes as one RGB image, the planes as red, green and blue, stretched
alike.

## Stretching

The values of a FITS image have no fixed range: a 16-bit image may hold counts up to a few hundred, and a float image may be
negative. Load maps them into the picture's 0 to 1 with `FitsReaderOptions.Stretch`:

| `FitsStretch` | Black and white | Use |
| --- | --- | --- |
| `MinMax` (the default) | `DATAMIN` and `DATAMAX` when the header has both, otherwise the smallest and largest finite value | A faithful view of the whole range |
| `ZScale` | IRAF's zscale limits: a line fitted to a sorted sample of up to 1000 pixels, contrast 0.25 | Astronomers' usual view: the sky and faint detail show, bright stars clip |
| `Asinh` | As MinMax, then `asinh(x / 0.1) / asinh(1 / 0.1)` | Faint detail lifted while bright cores keep their shape, as in Lupton's colour images |
| `None` | No mapping: the physical values themselves | Measuring |

Missing pixels (BLANK in an integer image, NaN in a float one) load black, or as NaN with `None`.
`FitsDetails.StretchLow` and `FitsDetails.StretchHigh` say which values became black and white.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#fits-stretch)]

## Headers and FitsFile

`FitsDetails`, in `RasterMetadata.FormatDetails`, keeps the HDU's whole header as a `FitsHeader`: a list of `FitsCard`s
(keyword, value and comment). Values are a `string`, `bool`, `long` or `double`, and commentary cards (`COMMENT`, `HISTORY`)
have no value. `GetString`, `GetInteger`, `GetReal` and `GetLogical` find a card's value by keyword. `FitsDetails` also keeps
which HDU and plane the raster is, its BITPIX, its tile compression, and the stretch with its limits. The world coordinate
cards (`CRVAL1`, `CD1_1` and the rest) are kept as cards; Egami does not compute sky positions from them.

`FitsFile` is for code that wants the file rather than a picture. `FitsFile.Load` reads every HDU's header (tables included),
`Hdus` lists them as `FitsHdu`s with their kind (a `FitsHduKind`), BITPIX, axes and compression, `ReadValues` gives an image's physical values as
`double`, and `ReadPlane` gives one plane as an unstretched `Raster<GrayF32>`.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#fits-file)]

## Tile compression

A tile-compressed image is a binary table in which each row codes one tile of the image, as the FITS tiled image convention
(Pence, Seaman and White) describes, and as fpack and funpack write and read it. Egami reads it as the image it holds: the
header's `ZBITPIX`, `ZNAXISn` and `ZTILEn` give the image, and `FitsHeader` shows the image's keywords, not the table's. The
codecs are Rice (`RICE_1`), gzip (`GZIP_1`, and `GZIP_2` with its byte shuffle), IRAF's PLIO for masks (`PLIO_1`) and
`NOCOMPRESS`. Floating-point images quantized to integers are restored from each tile's `ZSCALE` and `ZZERO`, with
`SUBTRACTIVE_DITHER_1` and `SUBTRACTIVE_DITHER_2` undone, and tiles stored losslessly in `GZIP_COMPRESSED_DATA` or
`UNCOMPRESSED_DATA` are read from there; gzip tiles of floats that were not quantized (no `ZSCALE`) hold the values
themselves. HCOMPRESS (`HCOMPRESS_1`) is not read: a load throws `UnsupportedFormatException` that
names it.

## Writing

The writer writes one primary HDU. `FitsWriterOptions.Bitpix` is 8, 16 or -32. By default it follows the pixel type: -32 for
floating-point pixels, 16 for 16-bit ones (stored with BZERO 32768, so 0 to 65535 survive exactly) and 8 for the rest. Gray
pixels write a 2D image; colour pixels write a cube of three planes, red, green and blue, which reads back as one image with
`FitsReaderOptions.Rgb`. Alpha is dropped, and a save says so in the Warnings.

A raster read from FITS keeps its header cards: everything but the cards the structure needs (such as `SIMPLE`, `BITPIX`,
`NAXISn`, `BSCALE`, `BZERO`, `BLANK`, `EXTEND` and the extension and compression keywords), which the writer makes again. A raster loaded
with a stretch no longer holds the physical values, so `DATAMIN`, `DATAMAX` and `BUNIT` are dropped from it.
`FitsWriterOptions.Keywords` adds more cards. Long strings are written as CONTINUE cards, keywords longer than eight characters
as HIERARCH, and characters outside printable ASCII are dropped, as the standard asks.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#fits-save)]

## Speed and memory

A FITS image is read whole: the stretch needs all the values before the first row. A 4000 × 3000 16-bit image loads in about
50 ms with `MinMax` and saves in about 25 ms; ImageMagick takes 180 ms to read it. A load needs about eight bytes a pixel
(24 with `Rgb`) while it runs, which is checked against `SafetyLimits.MaxTotalBytes` before it allocates.

## Damaged files

Warnings start `Fits.`. A lenient read of a file that ends early keeps the rows it has and leaves the rest black, with
`RowsMissing`. A compressed tile that does not decode is left black, with `TilesDamaged`. A strict read throws
`CorruptDataException` for each. A header without an `END` card, or with a BITPIX or NAXIS the standard does not allow, throws
`CorruptDataException` either way. Data after the last HDU that is not an extension is ignored.
