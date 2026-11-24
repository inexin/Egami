# EPS (previews)

Egami reads Encapsulated PostScript by the preview image the file carries. It never runs PostScript, so the image is the
preview the writing program saved, not the drawing the PostScript makes; a `Eps.PreviewLoaded` warning says so on every load.
`EpsFormat.Info` is registered in `EgamiContext.Default` and detected by the DOS binary header (bytes C5 D0 D3 C6) or a
`%!PS` first line; plain PostScript is detected too, so it fails with a message saying PostScript is not run rather than as an
unknown format. Egami does not write EPS.

[!code-csharp[](../../../tests/Egami.DocSamples/Eps.cs#eps-load)]

Load takes the first of these the file has:

| Preview | Written by | What Load returns |
| --- | --- | --- |
| TIFF, behind the binary header | Illustrator, Photoshop, InDesign, CorelDRAW on Windows | The TIFF, decoded by Egami's TIFF reader, in its own pixel type |
| Windows metafile (WMF), behind the binary header | CorelDRAW, FreeHand, older Windows programs | The metafile drawn at the bounding box size at 96 DPI (or `MetafileDecodeOptions.Dpi`) by a format the context has registered for metafiles, such as [Egami.Metafiles](../guides/metafiles.md)' `MetafileFormat.Info` |
| EPSI, a `%%BeginPreview` hex bitmap | Unix tools, Inkscape | The 1, 2, 4 or 8-bit gray bitmap as `Gray8`, 0 white and the largest value black as EPSF 3.0 says |
| None, only PostScript | TeX, plotting tools, old Mac files | `Raster.Probe` gives the bounding box size, one pixel a point; `Raster.Load` throws `UnsupportedFormatException` |

Core Egami does not depend on Egami.Metafiles: without a metafile format in the context a metafile preview is skipped for the
file's EPSI preview, with an `Eps.MetafileSkipped` warning, or Load fails with a message naming Egami.Metafiles.

The metadata holds an `EpsDetails` (which preview was loaded, the bounding box in points as an `EpsBoundingBox`, preferring
`%%HiResBoundingBox`, the creator, title and creation date), the same three comments as text entries, and Photoshop's XMP
packet, ICC profile and IPTC when the file has them.

## The container

`EpsFile` gives the parts without decoding anything: the PostScript, the TIFF and metafile previews as bytes, which preview
Load takes (`EpsPreviewKind`), the header comments, Photoshop's sections and what was damaged. A program that draws vectors can
hand `WmfPreview` to Egami.Metafiles itself.

[!code-csharp[](../../../tests/Egami.DocSamples/Eps.cs#eps-file)]

Damage never crashes the reader: sections the binary header puts past the end of the file are cut short or left out, a
checksum that does not match is a warning, and a bounding box or preview header that cannot be read is left unset. A strict
load (`RecoveryMode.Strict`) throws `CorruptDataException` instead.
