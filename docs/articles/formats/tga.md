# TGA

Egami reads Truevision TGA files: color-mapped, true-color and gray, raw and RLE, 8 to 32 bits, every origin, and the TGA 2.0
footer and extension area. It writes 8-bit gray, 24 and 32-bit, and 8-bit palettes, raw or RLE, with a TGA 2.0 footer.

A TGA loads as `Bgr8`, `Bgra8`, `Gray8` or `GrayAlpha8`. TGA has no signature, so detection takes a file as TGA only when its whole
18-byte header is consistent; see [Detection](index.md#detection).

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#tga)]

The descriptor's attribute bits say whether the extra bits are alpha, and a TGA 2.0 alpha type of "none" overrides them;
premultiplied alpha is divided out on load. The extension area's author and comments become `Author` and `Comment` text entries,
and the writer stores them back. `TgaPixelFormat.Bgr24` drops alpha rather than blending it onto a background.

The writer's pixel format and run-length coding, and the origin and alpha type a file states, read back through `TgaDetails`:

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#tga-writer)]
