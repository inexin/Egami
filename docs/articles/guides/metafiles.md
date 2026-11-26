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

Egami.Metafiles plays the records as Windows does (GDI for WMF and EMF, GDI+ for EMF+), in managed code on any OS.

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

Each glyph still goes where the file put it, so lines keep their length in a substitute face.

The open fonts are found only when they are installed, and many servers have none. The
[Egami.Fonts.Substitutes](text.md#substitute-fonts) package bundles Liberation, Carlito and Caladea, so put its faces in the
`Fonts` you pass:

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-substitutes)]

## Raster.Load

Registered with `EgamiContext.WithFormat`, `MetafileFormat` lets `Raster.Load` draw a metafile at its size at 96 DPI onto a
transparent `Rgba8` raster, and `Raster.Probe` give that size from the header without drawing. `MetafileDecodeOptions.Dpi`
sets another resolution.

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#metafile-format)]

## Writing EMF

`EmfCanvas` is a `Canvas` that writes an EMF: anything drawn on it, by your code or by a `Picture`, becomes a metafile that
Word, PowerPoint, Windows and Egami.Metafiles play. Disposing it writes the file to the stream, which stays open. Device
units are pixels at `EmfWriteOptions.Dpi` (96 by default).

[!code-csharp[](../../../tests/Egami.DocSamples/Metafiles.cs#emf-canvas)]

`EmfWriteOptions.Kind` picks what goes in the file: `EmfPlusDual` (the default) writes EMF+ records and a GDI copy of each
drawing for programs without GDI+, `EmfPlusOnly` the EMF+ records alone and `Emf` the GDI records alone. `Compress` writes
EMZ, `Description` the header's description.

Paths, rectangles, rounded rectangles, ovals and points, every `Stroke` setting, transforms, save and restore, clips and
aliased drawing are written as vectors; equal pens, brushes and paths are written once and shared. The GDI copy draws
colors opaque, since GDI has no alpha.

Paints are written as EMF+ brushes where one draws the same thing:

- A `SolidPaint` is a color with its alpha. A `ColorMatrixPaint` or `BlendPaint` made only of solid paints is the color
  Egami works out for it.
- A `LinearGradient` is a linear gradient brush with its stops as preset colors (clamped, repeated or mirrored). When its
  colors are opaque, the GDI copy draws it exactly too, with gradient fills clipped to the shape.
- A `RadialGradient` is a path gradient brush on a circle around its center (clamped, repeated or mirrored).
- An `ImagePaint` or `PicturePaint` is a texture brush that repeats, mirrors or draws one copy; a picture's tile is drawn by
  Egami at the resolution, as Egami draws picture paints on a raster.
- Gradients mixed in linear light, or between stops of different alpha, get extra stops mixed as Egami mixes them.
- A stroke with any of these paints is written as its outline, filled.

The GDI copy has no form for radial gradients, translucent gradients or textures, so it gets an image of those calls; the
EMF+ part stays vector and there is no warning, except in a GDI-only file (`Kind = Emf`).

A layer with only an opacity whose shapes cover no pixel in common is written as those shapes with the opacity in each paint.
Sweep and two-point conical gradients, gradients mixed in other spaces or transparent past their ends, clamped image
paints, other blend modes, blurs, meshes, and any other layer with opacity, blending, filters or masks are drawn by Egami
as an image at `FallbackScale` times the resolution and placed in the file, with one `Metafile.FallbackImage` warning in
`Warnings` for each kind. A call that blends with what is beneath it (a blend mode, a backdrop filter) is drawn over
everything drawn before it, and its image replaces those pixels.

Images are written as EMF+ images drawn with their source rectangle and sampling, and in the GDI copy as `AlphaBlend`
bitmaps of the pixels they use. A PNG, or a baseline or progressive JPEG, drawn as an `EncodedImage` keeps its bytes as they
are; other images are written as PNG. A paint's opacity is baked into the image. GDI cannot turn or mirror a bitmap, so a
turned or mirrored image is an image of the call in the GDI copy. Lattices and atlases are drawn as their images, and
pictures are played inline, so they stay vectors.

Text is written as outlines by default, so it looks the same where the font is missing. With `EmfText.Glyphs`
(`EmfWriteOptions.Text`) a glyph run is written as text: an EMF+ font with `DrawDriverString` (glyph indices and positions)
and a GDI font with `ExtTextOutW` (glyph indices and advances), smaller, but drawn with wrong glyphs by a reader without
the font. A run stays outlines when its face has no family name GDI can hold (31 characters), is synthesized, emboldened
or a variable instance, when its glyphs are turned or scaled one by one, when the transform mirrors it, or when its paint
is not one color. Color glyphs are drawn as their layers (COLR) or images (CBDT, sbix) in both modes.

`Metafile.Load(...).Draw(emfCanvas)` writes any WMF, EMF or EMF+ file again as an EMF, which plays as the original does.

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
