# EPS

Egami reads Encapsulated PostScript by the images the file carries. It never runs PostScript, so what Load returns is either
Photoshop's own copy of the image or the preview the writing program saved, not the drawing the PostScript makes. Egami does
not write EPS.

`EpsFormat.Info` is registered in `EgamiContext.Default`. It is detected by the DOS binary header (bytes C5 D0 D3 C6) or a
`%!PS` first line; plain PostScript is detected too, so it fails with a message saying PostScript is not run rather than as an
unknown format. The extensions are `.eps`, `.epsf`, `.epsi` and `.ept`, and the MIME types `application/postscript`,
`application/eps` and `image/x-eps`.

[!code-csharp[](../../../tests/Egami.DocSamples/Eps.cs#eps-load)]

## What Load returns

Load takes the first of these the file has:

| Image | Written by | What Load returns |
| --- | --- | --- |
| Photoshop's image, described by a `%ImageData` comment | Photoshop, when it saves a raster as EPS | The full image at full resolution: `Gray8` for gray and bitmap, `Rgb8` or `Cmyk8`, from binary, hex or JPEG data |
| TIFF, behind the binary header | Illustrator, Photoshop, InDesign, CorelDRAW on Windows | The TIFF, decoded by Egami's TIFF reader, in its own pixel type |
| Windows metafile (WMF), behind the binary header | CorelDRAW, FreeHand, older Windows programs | The metafile drawn at the bounding box size at 96 DPI (or `MetafileDecodeOptions.Dpi`) by a format the context has registered for metafiles, such as [Egami.Metafiles](../guides/metafiles.md)' `MetafileFormat.Info` |
| EPSI, a `%%BeginPreview` hex bitmap | Unix tools, Inkscape | The 1, 2, 4 or 8-bit gray bitmap as `Gray8`, 0 white and the largest value black as EPSF 3.0 says |
| None, only PostScript | TeX, plotting tools, old Mac files | `Raster.Probe` gives the bounding box size, one pixel a point; `Raster.Load` throws `UnsupportedFormatException` |

A preview is a picture of the drawing, often small and sometimes only black and white, so every load of a preview carries an
`Eps.PreviewLoaded` warning. Photoshop's image is the picture itself and carries none.

## Photoshop's image

Photoshop writes the whole raster into the PostScript and describes it in a `%ImageData` comment: the size, 1 or 8 bits a
channel, the mode (bitmap or gray, Lab, RGB or CMYK), padding channels, whether the channels are interleaved by pixel or by row,
and whether the data is binary, hex or JPEG. Egami reads gray, bitmap, RGB and CMYK in all of these layouts. CMYK stays
`Cmyk8`, with 0 meaning no ink, and the file's ICC profile, XMP and IPTC come with it.

[!code-csharp[](../../../tests/Egami.DocSamples/Eps.cs#eps-photoshop)]

An EPS file placed inside another (between `%%BeginDocument` and `%%EndDocument`, as Illustrator embeds a placed Photoshop
file) belongs to the drawing: its image and comments are passed over, not loaded as the outer file's. Lab images are left to
the file's preview, with an `Eps.ImageDataSkipped` warning. Data that cannot be decoded falls back to
the preview too, with `Eps.ImageDataDamaged`; data cut short gives 0 for the missing samples, with `Eps.ImageDataTruncated`.

## Metafile previews

Core Egami does not depend on Egami.Metafiles. It draws a metafile preview through any format the context registers that reads
metafiles and is not one of core's own: with Egami.Metafiles, `EgamiContext.Default.WithFormat(MetafileFormat.Info)`. The
metafile is fitted to the bounding box, at 96 DPI or the `MetafileDecodeOptions.Dpi` passed in the reader options. Without a
metafile format a metafile preview is skipped for the file's EPSI preview, with an `Eps.MetafileSkipped` warning, or Load fails
with a message naming Egami.Metafiles.

## Metadata

The metadata holds an `EpsDetails` (which image was loaded, the bounding box in points as an `EpsBoundingBox`, preferring
`%%HiResBoundingBox`, the creator, title and creation date), the same three comments as text entries, and Photoshop's XMP
packet, ICC profile and IPTC when the file has them.

## The container

`EpsFile` gives the parts without decoding anything: the PostScript, the TIFF and metafile previews as bytes, which image Load
takes (`EpsPreviewKind`), the header comments, Photoshop's sections and what was damaged. A program that draws vectors can hand
`WmfPreview` to Egami.Metafiles itself.

[!code-csharp[](../../../tests/Egami.DocSamples/Eps.cs#eps-file)]

## Damaged files

Damage never crashes the reader: sections the binary header puts past the end of the file are cut short or left out, a
checksum that does not match is a warning, and a bounding box, preview header or `%ImageData` comment that cannot be read is
left unset. A strict load (`RecoveryMode.Strict`) throws `CorruptDataException` instead. The `Eps.*` entries in
[Diagnostics](../diagnostics.md) list every warning.

## References

Adobe's *Encapsulated PostScript File Format Specification* 3.0 (Technical Note 5002) and *PostScript Language Document
Structuring Conventions Specification* 3.0 (Technical Note 5001) for the container and comments, Adobe's Photoshop File Format
Specification for `%ImageData`, and Microsoft's MS-WMF for the placeable header.
