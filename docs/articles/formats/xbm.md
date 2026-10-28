# XBM

XBM is the X Window System's one-bit bitmap, written as C source: the width and height as `#define` lines, an optional hotspot,
and the bits as an array, black where a bit is set. It reads and writes through the Egami.Formats.Extended package: register it
with `WithXbm()` or `WithExtendedFormats()` ([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | X11 files of bytes and X10 files of 16-bit words, the leftmost pixel in the lowest bit; comments anywhere |
| Writes | X11, a pixel black when its gray value is below half, with the name and hotspot from `XbmDetails` |
| Natural pixel type | `Gray8`, black 0 and white 255 |
| `FormatInfo` | `XbmFormat.Info`: name `XBM`, extension `.xbm`, MIME type `image/x-xbitmap` |

`XbmDetails` keeps the name the C identifiers start with (`cursor` for `cursor_width`) and the hotspot.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#details)]

The file is read whole; a probe reads only the `#define` lines. A file that ends early keeps the rows it has, with the warning
`Xbm.RowsMissing`.
