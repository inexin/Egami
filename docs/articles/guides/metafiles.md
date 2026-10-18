# Windows metafiles

Egami.Metafiles plays Windows metafiles, the vector pictures Office and Windows programs exchange: WMF (Windows Metafile),
EMF (Enhanced Metafile) and EMF+ (GDI+ records inside an EMF), and their gzip forms WMZ and EMZ. It draws them through
Egami.Drawing's canvas, so a chart pasted from Excel stays vector on a PDF page or in SVG output and rasterizes the same on
any OS. It only reads and plays: it does not write metafiles. It is fully managed.

## Loading

`Metafile.Load` reads a file, a stream or bytes in memory; gzip is detected by its first bytes. `Metafile.Kind` says what
it holds (a `MetafileKind`: `Wmf`, `Emf`, `EmfPlusDual` or `EmfPlusOnly`), and the header gives the picture's size:

- `Size` and `Frame` are in points: an EMF's frame rectangle (0.01 mm), a placeable WMF's box and units per inch, or, for a
  WMF without a placeable header, its window extent at 96 DPI (with a `Metafile.NoPlaceableHeader` warning).
- `Dpi` is the resolution the records were made at, and `Description` an EMF's application and picture names.

Loading is lenient: a record that runs past the end of the file ends the walk with a warning, and what came before it plays.
`Warnings` lists what the load found; `MetafileLoadOptions.Context` sets the limits (the file size and what EMZ and WMZ
inflate to) and the diagnostics handler.

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-limits)]

A picture taken out of a document is often already in memory; `Load` takes the bytes as they are.

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-bytes)]

## Drawing

`Metafile.Draw` plays the picture on any canvas: in points from the canvas's origin, or with its frame stretched to a box, as
Word stretches a picture to its frame. `Metafile.Rasterize` draws into a new raster at a DPI (96 by default) or a width
and height, over a background:

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-to-png)]

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-draw)]

`MetafileRenderOptions` carries the rest:

- `Antialias` is on by default; off, GDI records draw aliased as GDI itself draws them, which is what to compare with
  Windows. EMF+ records follow the file's own smoothing either way.
- `Playback` (a `MetafilePlayback`) picks which records of a dual file play: `Auto`, the EMF+ records as GDI+ plays them,
  or `Gdi`, the GDI records only, as programs without GDI+ draw the file.
- `Fonts` and `FontSubstitutions` choose the faces for text; `TextAsPaths` draws text as outlines.

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-gdi)]

## Text and fonts

Text records name a face, and each glyph's place comes from the record itself, so a line keeps its length in another face.
A face the fonts lack is replaced by a metric-compatible open font where one exists (Arial by Liberation Sans or Arimo, Times
New Roman by Liberation Serif or Tinos, Courier New by Liberation Mono or Cousine, Calibri by Carlito, Cambria by Caladea),
then by your `FontSubstitutions`, then by the generic family; each substitution is a `Metafile.FontSubstituted` warning.
Symbol and Wingdings text maps to Unicode in a substitute. Text given as glyph indices (Word writes these for complex
scripts) is drawn only in the face it was made for, since another face would show the wrong letters.

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-fonts)]

The open fonts are found only when they are installed, and many servers have none. The
[Egami.Fonts.Substitutes](text.md#substitute-fonts) package bundles Liberation, Carlito and Caladea, so put its faces in the
`Fonts` you pass:

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-substitutes)]

## Raster.Load

Registered with `EgamiContext.WithFormat`, `MetafileFormat` lets `Raster.Load` draw a metafile at its size at 96 DPI onto a
transparent `Rgba8` raster, and `Raster.Probe` give that size from the header without drawing. `MetafileDecodeOptions.Dpi`
sets another resolution.

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-format)]

## What plays, and how close it is

The GDI records of WMF and EMF play as GDI plays them: map modes, the world transform, pens and brushes (hatches and pattern
brushes sized from the reference device, so they scale with the picture), paths, clipping regions, bitmaps through Egami's BMP
reader, and text. Drawn aliased, the synthetic test files match Windows GDI within a mean of one level and 99% of pixels within
16 levels, family by family; text is measured with a looser bound, since GDI hints glyphs to whole pixels. Raster operations
Egami draws with the nearest blend: AND as multiply, OR as lighten, XOR as difference, and the common transparent-bitmap
idioms as one masked image; others draw as a copy with a `Metafile.RasterOperation` warning.

EMF+ records play as GDI+ plays them: the world and page transforms, containers, clips with all six combine modes,
smoothing, pixel offset and compositing; solid, hatch, texture, linear and path gradient brushes (path gradients as a
Gouraud mesh); pens with dashes, compound lines, inset alignment and anchor and arrow caps; cardinal curves; images, with
JPEG and PNG handed to the canvas unchanged so a PDF page embeds them as they are, and metafile images played as vectors up to
eight levels deep; GDI+ 1.1 image effects; `DrawString` laid out in its rectangle and `DrawDriverString`. In a dual file the
GDI records play only where GDI+ hands drawing to GDI (EmfPlusGetDC).

Flood fills, OpenGL records, escapes and PostScript carried in comments are skipped with a `Metafile.SkippedRecord`
warning. The [diagnostics](../diagnostics.md) page lists every `Metafile.*` id.
