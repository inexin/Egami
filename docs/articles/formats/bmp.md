# BMP

Egami reads Windows bitmaps with core, info, V2 to V5 and OS/2 2.x headers at 1 to 32 bits, RLE4, RLE8 and RLE24, bit fields,
embedded PNG and JPEG and V5 ICC profiles. It writes 24-bit, 32-bit with alpha, and 1, 2, 4 or 8-bit palettes, with resolution
and ICC.

A BMP loads as `Bgr8`, or `Bgra8` with alpha.

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#bmp)]

A plain 32-bit BMP's fourth byte is used as alpha unless it is 0 in every pixel, in which case the image loads opaque; an alpha
mask in the header is always used. Pixels that an RLE delta skips are black. `BmpPixelFormat.Indexed2` writes Windows CE 2-bit
files, which many desktop readers cannot open, so `Auto` never picks it. Resolution is written in pixels per meter, and an ICC
profile switches the header to V5 with the profile embedded.
