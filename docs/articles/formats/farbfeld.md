# Farbfeld

Farbfeld is suckless.org's lossless format: the word `farbfeld`, the width and height as big-endian 32-bit numbers, then 16-bit
big-endian RGBA pixels, row by row. It reads and writes through the Egami.Formats.Extended package: register it with
`WithFarbfeld()` or `WithExtendedFormats()` ([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | The whole format |
| Writes | Every raster as 16-bit RGBA, so nothing but metadata is lost |
| Natural pixel type | `Rgba16` |
| `FormatInfo` | `FarbfeldFormat.Info`: name `Farbfeld`, extension `.ff`, MIME type `image/x-farbfeld` |

Rows stream from the input as they are asked for. Farbfeld files are often compressed with a general-purpose tool such as
bzip2; Egami reads and writes the uncompressed file only.
