# BMP

Egami reads Windows bitmaps with core, info, V2 to V5 and OS/2 2.x headers at 1 to 32 bits, RLE4, RLE8 and RLE24, OS/2
Huffman 1D, bit fields, embedded PNG and JPEG and V5 ICC profiles. It writes 24-bit, 32-bit with alpha, 16-bit 5-5-5, 5-6-5 and
1-5-5-5, and 1, 2, 4 or 8-bit palettes, the 4 and 8-bit ones optionally run-length encoded, with resolution and ICC.

A BMP loads as `Bgr8`, or `Bgra8` with alpha.

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#bmp)]

A plain 32-bit BMP's fourth byte is used as alpha unless it is 0 in every pixel, in which case the image loads opaque; an alpha
mask in the header is always used. Pixels that an RLE delta skips are black. `BmpPixelFormat.Indexed2` writes Windows CE 2-bit
files, which many desktop readers cannot open, so `Auto` never picks it. `Bgr555` writes the default 16-bit layout with no
bit fields; `Bgr565` and `Bgra5551` write a V4 header with bit-field masks. Loading a 16-bit file as `Bgr555`, `Bgr565` or
`Bgra5551` copies its rows when the masks match the type. Resolution is written in pixels per meter, and an ICC
profile switches the header to V5 with the profile embedded.

`BmpWriterOptions.RunLength` writes `Indexed4` and `Indexed8` as RLE4 and RLE8: runs of one index (and for RLE4 two that take
turns) as a count and the index, the rest as absolute runs, an end of line after each row and an end of bitmap after the last.
Images with flat areas shrink a great deal; noisy ones can grow a little. Absolute RLE4 runs are kept to an even length, since
Pillow drops the last index of an odd one; ImageMagick and Pillow read the files with the same pixels. Other layouts cannot be
run-length encoded, and asking for it throws `ArgumentException`.

OS/2 Huffman 1D files (compression 3 under an OS/2 2.x header, 1 bit a pixel) hold CCITT T.4 one-dimensional rows, which
Egami's CCITT decoder reads with or without EOL codes before the rows, or byte-aligned as TIFF's modified Huffman. White runs
are palette entry 0 and black runs entry 1; the `BmpDetails.Compression` of such a file is `Huffman1D`. Rows the data does not
reach are left black with the `Bmp.RowsMissing` warning, or throw in strict mode.
