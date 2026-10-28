# PCX

PCX is ZSoft's PC Paintbrush format: a 128-byte header, then scanlines of one to four color planes, nearly always run-length
coded. It reads and writes through the Egami.Formats.Extended package: register it with `WithPcx()` or
`WithExtendedFormats()` ([Extended formats](extended.md)).

| | |
| --- | --- |
| Reads | 1, 2, 4 and 8-bit images of one plane; 1-bit images of 2 to 4 planes (EGA, 16 colors); 24-bit (three planes) and 32-bit (four planes, the fourth as alpha); coded or not; runs that carry on into the next scanline |
| Writes | Version 5, run-length coded: gray rasters as one 8-bit plane with a gray palette, everything else as 24-bit RGB; the resolution in dots per inch (72 when the raster has none) |
| Natural pixel type | `Gray8` for 1-bit and for 8-bit images the header calls gray, `Rgba8` for four planes, `Rgb8` otherwise |
| `FormatInfo` | `PcxFormat.Info`: name `PCX`, extension `.pcx`, MIME types `image/vnd.zbrush.pcx` and `image/x-pcx` |

Palettes come from the header (16 colors) or, for 8-bit images, from the 769 bytes after the pixels, so an 8-bit file is read
to its end before its first row. A 1-bit image of one plane is black where a bit is clear and white where it is set, as its
palette says; ImageMagick writes and reads such files the other way round, so its 1-bit PCX files load inverted. A file whose
256-color palette is missing reads as gray levels with the warning `Pcx.PaletteMissing`.

`PcxDetails` keeps the version, the bits per pixel, the plane count and the palette.

[!code-csharp[](../../../tests/Egami.DocSamples/FormatsExtended.cs#details)]

The files Egami writes have no alpha and no metadata but the resolution; a save that drops alpha or deeper samples says so in
the Warnings. Images are at most 65,534 pixels wide and 65,536 high.
