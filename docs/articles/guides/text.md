# Fonts and text

Egami.Fonts is a separate package that reads TrueType, OpenType, WOFF, WOFF2 and variable fonts in managed code: faces, glyph
outlines, metrics, kerning, installed fonts and subsets. Egami.Drawing uses it to draw and measure a line of text on a canvas. Both are previews
(Egami.Fonts 1.0.0-preview.2): the API may still change before 1.0. Egami ships no font: text needs a face from a file, a stream,
memory or the installed fonts.

## Faces

A `Typeface` is one face, immutable and shared by every thread. `Typeface.FromFile`, `FromStream` and `FromMemory` read TrueType
(.ttf), OpenType CFF and CFF2 (.otf), collections (.ttc, .otc, with a face index), WOFF and WOFF2 (.woff, .woff2, collections
too) and bare CFF; `FromCff` reads a bare CFF stream as PDF
embeds it, with `CffOptions` for the units per em and widths it lacks. `FontLoader.Probe` tells a `FontProbe` (its `FontFileKind`,
face count and `FontOutlines`) from the header, and `FontLoader.LoadAll` loads every face of a collection.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#load)]

- The file is read into memory once and every table is a slice of it; tables are parsed when first used.
- Loading is lenient: a missing or damaged name, OS/2, hhea, hmtx, post or cmap table gets a documented default and a
  `Font.MissingTable` or `Font.BadTable` diagnostic, and a broken glyph draws empty with `Font.BadGlyph`. Only a font with no
  readable glyph data throws `CorruptDataException`. `FontLoadOptions.Strict` throws on the first problem instead.
- A WOFF or WOFF2 file is unwrapped once into an ordinary font in new memory (zlib, and Brotli through .NET's own decoder, with
  WOFF2's glyf, loca and hmtx transforms); the compressed bytes are not kept. `Container` says where a face came from, and
  `GetWoffMetadata` and `GetWoffPrivateData` return the file's own blocks. Every decoded table equals the reference decoders'.
- `GetNames` returns every `FontNameRecord`; `TryGetTable` returns any table's bytes by `FontTag`. `Embedding` reports the
  `FontEmbedding` permissions, which Egami never enforces.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#cff)]

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#web)]

## Variable fonts

A variable font is one file that holds a range of designs along its axes, such as weight from 100 to 900. `IsVariable`,
`VariationAxes` (each a `VariationAxis` with its range) and `NamedInstances` (each a `NamedInstance`, such as "Bold") describe it.
`WithVariations` returns the face at a position, given as `FontVariation` values, and `WithNamedInstance` at a named one: an
ordinary immutable `Typeface` sharing the file's memory, whose outlines, advances, extents, kerning, names and subsets all answer
for that position. `Variations` gives a face's position.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#variable)]

- Positions clamp to each axis; axes the font lacks are ignored, and the default position is the face itself. Faces at a position
  are cached, so drawing at one position costs what a static font costs.
- Outlines vary through gvar (TrueType) or CFF2's blends, advances through HVAR (or phantom points), the extents through MVAR and
  kerning through GDEF's deltas, with avar versions 1 and 2. At 25 positions in each test font every outline and advance equals
  fontTools' within 1/64 unit.
- A named instance takes the instance's names; another position keeps the default's names and gets a PostScript name of its own
  (Adobe Technical Note 5902), such as `NotoSans_650wght`.
- `FontCollection` lists a variable face's named instances as faces, and `FontFamily.Match` places the face anywhere its weight,
  width and slant or italic axes reach, as CSS does: weight 450 is drawn at 450, not snapped to 400.
- With `Font.OpticalSizing` at `OpticalSizing.Auto` (the default), a face with an opsz axis it leaves at the default is drawn at
  opsz equal to the size in canvas units; `Font.EffectiveTypeface` is the face drawn.
- `SubsetOptions.Variations` (by default the face's own position) makes the subset a static font at that position, written as an
  instancer would: the varied outlines rounded to whole units, metrics, OS/2 and post at the position, CFF2 written as CFF and the
  variation tables left out. Its outlines, advances and bearings equal fontTools' instancer's, so a PDF shows the position.
- A variable font's GSUB feature variations (glyph swaps by position) arrive with shaping in 2.8.

## Glyphs and metrics

Everything a face returns is in font units with y up. `GetGlyph` maps a code point (with a variation selector, through cmap format
14), `GetGlyphOutline` sends an outline to an `IGlyphOutlineSink`, and `GetAdvance`, `GetGlyphExtents` (a `GlyphExtents` box) and
`GetKerning` give the numbers.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#glyphs)]

- TrueType outlines arrive as lines and quadratic curves and CFF outlines as lines and cubic curves, exactly as stored: every outline
  of the test fonts equals fontTools' point for point.
- `Extents` is a `FontExtents` with ascent, descent, line gap, cap and x heights, decoration lines and the italic angle, by the
  browsers' rule: OS/2 typo metrics when the face sets USE_TYPO_METRICS, else hhea. `HheaMetrics`, `TypoMetrics` and
  `WinMetrics` give each stored `VerticalMetrics` set.
- Kerning is GPOS pair positioning under the kern feature (the DFLT and latn scripts), else the kern table. Ligatures, marks and
  other OpenType features arrive with shaping in 2.8.

## Text on a canvas

A `Font` is a typeface at a size, the em in canvas units: pixels on a raster, points on a PDF page. `Font.FromPoints` sizes from
points at a resolution. `Canvas.DrawText` puts the left end of the first line's alphabetic baseline at the origin.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#caption)]

- `Font.Shape` turns text into a `GlyphRun`: one glyph per code point, pair kerning, left to right, with `\n`, `\r\n` and `\r`
  starting a new line. There is no wrapping, alignment, bidi or font fallback until 2.8; a missing character draws .notdef.
- A run carries the source text and a cluster per glyph, so a PDF backend keeps text searchable. `Canvas.DrawGlyphRun` draws a
  run built elsewhere with `GlyphRun.Create`.
- Small glyphs are drawn from the `GlyphCache` of coverage masks, at whole device pixels, by the same rasterizer as paths, so caching
  changes speed, not pixels. Glyphs over 256 pixels per em, or under a rotation or skew, fill as paths.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#cache)]

## Rendering quality

Four switches on a `Font` change how a raster draws it, never how it measures: `MeasureText` gives the same numbers whatever they
are.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#quality)]

- `Font.Subpixel` (on by default) keeps glyph origins at quarter pixels horizontally, each quarter its own cached mask, so small
  text is evenly spaced; off, origins snap to whole pixels as in 2.7.1. Vertical positions always snap to whole pixels.
- `Font.Hinting` = `FontHinting.Light` snaps the baseline, x-height and cap-height zones (a CFF font's blue zones, else zones
  measured from the face's own glyphs) to whole pixels, up to 48 pixels per em; x values and advances never move. There is no
  TrueType bytecode hinting.
- `Font.Edging` picks `FontEdging.Antialias` (the default), `Alias` (one bit, pixel centres inside the outline) or `Lcd`: three
  samples per pixel along the canvas's `CanvasOptions.PixelGeometry`, filtered and blended per channel. LCD text draws only onto
  an 8-bit RGB canvas outside layers with a solid source-over paint; anything else falls back to grayscale with one
  `Text.LcdFallback` diagnostic per canvas. Images that leave the screen they were made for carry the colour fringes with them,
  so LCD is opt-in.
- `Font.Synthesis` (set by matching) is drawn: bold outsets the outline by em/24 at 9 pixels down to em/32 at 36 and above,
  advances unchanged, and italic slants 14 degrees, CSS's default oblique. Glyph paths and ink bounds include it;
  `Font.AllowSynthesis = false` turns it off.

## Color fonts

Emoji and other color glyphs draw in color by themselves. Per glyph, the first format the font has wins: COLR version 1 (a
graph of layers, gradients, transforms and compositing), COLR version 0 (layers of solid outlines), sbix, then CBDT and CBLC
(images); anything else draws its outline in the paint. `Typeface.ColorFormats` says what a face holds.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#color)]

- `Font.Palette` picks a CPAL palette by index or by `FontPaletteBackground` (`FontPalette.Light`, `FontPalette.Dark`), with
  `FontPaletteEntry` overrides; `Typeface.Palettes` lists each `FontPaletteInfo`. `Font.ColorGlyphs = false` draws outlines.
- The paint's color fills the glyph's foreground parts and its alpha and blend mode apply to the whole glyph, drawn as one group.
- `Typeface.TryGetColorGlyph` returns a `ColorGlyph`: a `PaintColorGlyph` whose `ColorPaint` graph an `IColorPaintVisitor`
  walks, with variable values taken at the face's position: `ColorLayersPaint`, `ColorSolidPaint` (a `GlyphColor`),
  `ColorLinearGradientPaint`, `ColorRadialGradientPaint` and `ColorSweepGradientPaint` (over a `ColorLine` of `ColorStop`s with a
  `ColorExtend`), `ColorGlyphPaint` (a glyph-outline clip), `ColorGlyphReferencePaint`, `ColorTransformPaint` and
  `ColorCompositePaint`; or a `BitmapColorGlyph` with its image (`BitmapGlyphEncoding`) and placement, from the strike nearest
  above the size.
- Every canvas draws color glyphs: the default glyph hook turns them into clips, fills, gradients, layers and images, so a PDF page
  gets vector color. A raster caches them, up to 256 pixels per em, as color images keyed by palette.
- OpenType SVG glyphs are not drawn: `Typeface.TryGetSvgDocument` returns the document for callers with an SVG renderer, and such
  glyphs fall back to their COLR, bitmap or outline data. Drawing them arrives with Egami.Svg.

## Text on a path

`Canvas.DrawTextOnPath` lays a line of text along a `Path` by SVG's textPath rules, through `Font.ShapeOnPath`: each glyph's
advance midpoint sits on the path, turned to its direction, and glyphs past either end are left out. `TextOnPathOptions` sets
the start (`StartOffset` in canvas units plus `StartFraction` of the length), the `TextAnchor`, the `BaselineShift` along the
normal, the `TextPathSide` and whether a closed contour wraps.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#path)]

- The result is a `GlyphRun` with a `GlyphTransform` per glyph (a rotation and uniform scale, then an origin, as Skia's RSXform),
  so a PDF backend writes it as real, searchable text; `GlyphRun.CreateTransformed` builds such runs by hand.
- Glyphs that are only moved draw from the glyph cache like any text; turned or scaled ones fill as paths. Text is not warped:
  each glyph keeps its shape, as SVG and browsers draw it.

## Measuring

`Font.MeasureText` lays text out exactly as `DrawText` does and returns a `TextMeasure`: the widest line's advance, the exact ink
box as `TextBounds`, the line count and the vertical metrics.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#measure)]

## Text as paths

`FontExtensions` holds `GetGlyphPath`, `GetTextPath` and `GlyphRun.ToPath`, which give `Path`s in canvas units, y down, to stroke, clip to or combine.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#paths)]

## Installed fonts and matching

`FontCollection.System` finds the installed fonts without native calls, by scanning the platform's font folders (and fontconfig's
dir entries on Linux), and indexes them on first use from the name and OS/2 tables only. A `FontCollection` of your own takes faces,
files and folders. Families are found by typographic, legacy, full or PostScript name in any language, and
`FontFamily.Match` picks a face by the CSS Fonts 4 rules for a `FontStyle` (weight, width and `FontSlant`). The `FontMatch`
records the `FontSynthesis` a lighter or upright face would need; `GenericFamily` names fixed per-platform lists.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#match)]

## Subsets

`FontSubsetter.Subset` writes a font with only some glyphs, as PDF embedding and SVG export want: TrueType, OpenType CFF or a bare
CID-keyed CFF stream (`SubsetFormat`), closed over composites and accents, with unused CFF subroutines dropped. `SubsetOptions`
sets the format, dense or kept glyph ids, the character map, extra tables and the subset tag; the `FontSubset` holds the bytes and
the glyph map.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#subset)]
