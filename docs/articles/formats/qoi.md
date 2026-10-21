# QOI

Egami reads and writes the whole Quite OK Image format, with 3 or 4 channels and both colorspaces. Files it writes are byte for
byte what the reference encoder writes.

A QOI loads as `Rgb8` or `Rgba8`.

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#qoi)]

QOI's colorspace byte maps to `RasterMetadata.ColorSpace` (sRGB or linear) and back.
