# XPM

XPM, the X PixMap format, is C source holding an array of strings: the size and color count, a color table keyed by one or more
characters, then one string of characters per row. It reads and writes version 3 through the Egami.Formats.Extended package:
register it with `WithXpm()` or `WithExtendedFormats()` ([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | Up to 8 characters a pixel; of each color's keys the color (`c`), else gray (`g`, `g4`) or mono (`m`); `None`, `#RGB` to `#RRRRGGGGBBBB` and X11 color names, such as `light blue` and `gray50` |
| Writes | Every color of the raster, as many characters a pixel as the count needs; a pixel below half alpha is `None`; the name and hotspot from `XpmDetails` |
| Natural pixel type | `Rgba8` |
| `FormatInfo` | `XpmFormat.Info`: name `XPM`, extension `.xpm`, MIME type `image/x-xpixmap` |

A color name Egami does not know reads as black with the warning `Xpm.ColorUnknown`, and a pixel that names no color in the table
reads as transparent with `Xpm.PixelUnknown`; a strict read throws for either. The writer needs every pixel before its color
table, so the rows wait in memory until the last one. `XpmDetails` keeps the array's name, the hotspot and the characters a
pixel.
