# Sun raster

Sun raster is the rasterfile format of SunOS: a 32-byte big-endian header, an optional colormap, then rows of 1, 8, 24 or 32
bits, plain or byte-encoded. It reads and writes through the Egami.Formats.Extended package: register it with `WithSunRaster()`
or `WithExtendedFormats()` ([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | Types 0 (old), 1 (standard), 2 (byte-encoded) and 3 (RGB order); 1, 8, 24 and 32 bits; colormaps of equal red, green and blue planes, and raw colormaps skipped |
| Writes | Gray rasters as 8-bit gray levels without a colormap, everything else as 24-bit pixels, blue first (type 1), or byte-encoded (type 2) when `SunRasterWriterOptions.Rle` is set |
| Natural pixel type | `Gray8` for 1-bit and 8-bit images without a colormap or with a gray one, `Rgb8` otherwise |
| `FormatInfo` | `SunRasterFormat.Info`: name `Sun raster`, extensions `.ras` and `.sun`, MIME type `image/x-sun-raster` |

Without a colormap, 1-bit pixels are black where set and 8-bit pixels are gray levels. The pad byte of 32-bit pixels is not read
as alpha. `SunRasterDetails` keeps the depth, the colormap, the coding and the byte order.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#options)]

Rows stream from the input as they are asked for. Sun raster has no alpha and no metadata: a save that drops them says so in the
Warnings.
