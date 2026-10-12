# Fonts and text

Egami.Fonts is a separate package that reads TrueType and OpenType fonts in managed code: faces, glyph outlines, metrics, kerning,
installed fonts and subsets. Egami.Drawing uses it to draw and measure a line of text on a canvas. Both are previews
(Egami.Fonts 1.0.0-preview.1): the API may still change before 1.0. Egami ships no font: text needs a face from a file, a stream,
memory or the installed fonts.

## Faces

A `Typeface` is one face, immutable and shared by every thread. `Typeface.FromFile`, `FromStream` and `FromMemory` read TrueType
(.ttf), OpenType CFF (.otf), collections (.ttc, .otc, with a face index) and bare CFF; `FromCff` reads a bare CFF stream as PDF
embeds it, with `CffOptions` for the units per em and widths it lacks. `FontLoader.Probe` tells a `FontProbe` (its `FontFileKind`,
face count and `FontOutlines`) from the header, and `FontLoader.LoadAll` loads every face of a collection.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#load)]

- The file is read into memory once and every table is a slice of it; tables are parsed when first used.
- Loading is lenient: a missing or damaged name, OS/2, hhea, hmtx, post or cmap table gets a documented default and a
  `Font.MissingTable` or `Font.BadTable` diagnostic, and a broken glyph draws empty with `Font.BadGlyph`. Only a font with no
  readable glyph data throws `CorruptDataException`. `FontLoadOptions.Strict` throws on the first problem instead.
- WOFF, WOFF2, CFF2 and variable fonts arrive in 2.7.2; a variable TrueType font draws its default instance.
- `GetNames` returns every `FontNameRecord`; `TryGetTable` returns any table's bytes by `FontTag`. `Embedding` reports the
  `FontEmbedding` permissions, which Egami never enforces.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#cff)]

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
