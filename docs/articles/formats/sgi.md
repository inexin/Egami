# SGI

SGI is Silicon Graphics' image format (.rgb, .rgba, .bw, .sgi): a 512-byte header, then one to four channels of 8 or 16-bit
samples, each channel a plane of bottom-up rows, plain or run-length coded. It reads and writes through the
Egami.Formats.Extended package: register it with `WithSgi()` or `WithExtendedFormats()` ([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | 1 to 4 channels (gray, gray and alpha, RGB, RGBA) of 8 or 16 bits, plain or coded, dimension 1, 2 or 3; coded rows in any order in the file; further channels are skipped with a `Sgi.ChannelsSkipped` warning |
| Writes | The raster's own channels (gray, gray and alpha, RGB or RGBA) and depth (16-bit above 8 bits a channel), coded unless `SgiWriterOptions.Rle` is off, with the image name from `SgiDetails` |
| Natural pixel type | `Gray8`, `GrayAlpha8`, `Rgb8` or `Rgba8`, or their 16-bit forms |
| `FormatInfo` | `SgiFormat.Info`: name `SGI`, extensions `.sgi`, `.rgb`, `.rgba`, `.bw`, `.int`, `.inta`, MIME types `image/sgi` and `image/x-rgb` |

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#options)]

The planes are stored bottom-up and the coded rows may lie anywhere, so a decode reads the file whole and then hands out rows,
and the writer keeps the coded rows in memory until the last one. A file that ends early leaves the rows it lacks transparent
black with the warning `Sgi.RowsMissing`, and a damaged coded row is cut short with `Sgi.RowDamaged`. SGI keeps alpha and 16-bit
samples but no metadata. Images are at most 65,535 pixels on a side.
