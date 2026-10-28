# WBMP

WBMP is the Wireless Application Protocol's one-bit bitmap: a type and a header byte, the width and height as multi-byte
integers, then rows of bits, white where set. It reads and writes type 0 through the Egami.Formats.Extended package: register it
with `WithWbmp()` or `WithExtendedFormats()` ([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | Type 0, with extension headers skipped |
| Writes | Type 0, a pixel white when its gray value is at least half |
| Natural pixel type | `Gray8`, black 0 and white 255 |
| `FormatInfo` | `WbmpFormat.Info`: name `WBMP`, extension `.wbmp`, MIME type `image/vnd.wap.wbmp` |

WBMP has no real signature: a file is taken for WBMP only when it starts with type 0, no extension headers and a size above
zero, and only after every other registered format has declined it. Rows stream from the input.
