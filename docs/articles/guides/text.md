# Fonts and text

Egami.Fonts is a separate package that reads TrueType, OpenType, WOFF, WOFF2 and variable fonts in managed code: faces, glyph
outlines, metrics, kerning, installed fonts and subsets. Egami.Drawing uses it to draw and measure a line of text on a canvas. Egami.Drawing and Egami.Fonts are at
1.0 and their APIs are frozen. Egami.Fonts ships no font: text needs a face from a file, a stream,
memory or the installed fonts, or from the opt-in [substitute fonts](#substitute-fonts) package.

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
`GetKerning` give the numbers. Glyph ids are `ushort` throughout, as OpenType caps a face at 65,535 glyphs.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#glyphs)]

- TrueType outlines arrive as lines and quadratic curves and CFF outlines as lines and cubic curves, exactly as stored: every outline
  of the test fonts equals fontTools' point for point.
- `Extents` is a `FontExtents` with ascent, descent, line gap, cap and x heights, decoration lines and the italic angle, by the
  browsers' rule: OS/2 typo metrics when the face sets USE_TYPO_METRICS, else hhea. `HheaMetrics`, `TypoMetrics` and
  `WinMetrics` give each stored `VerticalMetrics` set.
- `Typeface.GetKerning` is GPOS pair positioning under the kern feature (the DFLT and latn scripts), else the kern table. Text
  drawn on a canvas is shaped with all of the font's OpenType features (see [Unicode text and shaping](unicode-text.md)).

## Text on a canvas

A `Font` is a typeface at a size, the em in canvas units: pixels on a raster, points on a PDF page. `Font.FromPoints` sizes from
points at a resolution. `Canvas.DrawText` puts the left end of the first line's alphabetic baseline at the origin. `Font` is a
value type; `default(Font)` has no typeface, and its metrics, shaping and measuring throw `InvalidOperationException`.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#caption)]

- `Font.Shape` turns text into a `GlyphRun` with the full shaper: ligatures, kerning, marks, the script rules, bidi order, and a new
  line at each hard break (`\n`, `\r\n`, `\r`, U+0085, U+2028, U+2029). `ShapingOptions` set the direction, language, features
  and spans. A run has one font, so text that falls back to other fonts is shaped by `TextShaper.Shape` and drawn with
  `Canvas.DrawShapedText`; `Canvas.DrawText` does that by itself when the options ask for fallback. Nothing wraps; without
  fallback a missing character draws .notdef.
- A `ShapedText` drawn with `Canvas.DrawShapedText` is shaped once, so it can be measured, kept and drawn again.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#shaped)]

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

### Text gamma and contrast

Two canvas settings change how heavy small text looks without touching its shapes. `CanvasOptions.TextGamma` (0.5 to 3,
default 1) blends glyph coverage per channel as (a·src^γ + (1 − a)·dst^γ)^(1/γ) on the stored values, as GDI ClearType does:
above 1, light text on a dark ground gets heavier and dark text on a light ground lighter. `CanvasOptions.TextContrast` (0 to 1,
default 0) first boosts coverage a to a + (1 − a)·c·a, the contrast boost of Skia and DirectWrite, which thickens thin edges
whatever the colours.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#text-gamma)]

- Only glyphs drawn from the glyph cache (up to 256 pixels per em, under a scale-and-translate transform) take them; shapes,
  images, blurred text and text drawn as paths never do. The defaults draw exactly what Egami 1.2.0 drew.
- The gamma needs to read the canvas, so it applies to solid source-over text on an 8-bit RGB canvas outside layers, the text
  LCD edging can draw too; other text takes the contrast only.
- With `ColorSpace.Linear` the gamma is ignored, since that blend is already gamma-correct; the contrast still applies.
- To look like GDI+ `ClearTypeGridFit` (its default TextContrast of 4) with LCD edging, light hinting and whole-pixel origins,
  use a gamma of 1.15 and no contrast: the brightness a white Trebuchet MS label adds to a black or a #0078d4 button is then
  within 1% of GDI+'s. With the GDI filter and sampling below, use 1.25. Letter shapes and spacing still differ, since
  TrueType instructions are not run.

### LCD filter, sampling and ClearType level

Three more canvas settings shape LCD text. `CanvasOptions.LcdFilter` spreads each subpixel's coverage over five taps that add
up to 256: `LcdFilter.Default` (16, 64, 96, 64, 16, Egami's filter since 2.7.2), `Balanced` (8, 77, 86, 77, 8), `Light` (0, 85,
86, 85, 0), `Gdi` (the even three-subpixel spread measured from GDI and GDI+, the weights of `Light`) or `LcdFilter.Custom`.
`CanvasOptions.LcdSampling` = `LcdSampling.Gdi` takes, across the stripes, only each pixel's centre line, so edges along the
stripes stay sharp as in GDI+ ClearType; `Smooth`, the default, takes exact areas. `CanvasOptions.ClearTypeLevel` (0 to 1,
default 1) mixes the filtered coverage with the pixel's grayscale coverage, as Windows' ClearType level does.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#lcd-look)]

With all three at their defaults LCD text is drawn as in Egami 1.2.0.

### TrueType hinting

`FontHinting.Full` runs the font's own TrueType instructions, with Egami's interpreter, at a whole number of pixels per em.
`Font.HintingTarget` picks what the interpreter tells the font it is: `TrueTypeTarget.GdiPlus` (the default) or `Gdi`, both with
the answers measured from Windows and Microsoft's ClearType compatibility rules (x hinted on a 1/16-pixel grid), or `FreeType`
(FreeType's minimal rules: x left alone). CFF faces, faces without instructions, variable faces and sizes the gasp table leaves unhinted fall back to
`FontHinting.Light`. So does text under a transform that scales x and y differently or turns it.

`Font.Metrics = FontMetrics.GridFitted` takes each glyph's advance from its hinted phantom points at that size, and
`FontMetrics.Compatible` takes GDI's compatible widths: the hdmx table's widths, the rounded design advance where the LTSH table
says the glyph scales linearly, or the advance of a black-and-white run of the glyph's instructions. On the six Windows UI fonts
from 8 to 24 pixels per em, these equal GDI's `GetCharABCWidths` for 9,688 of 9,690 glyph sizes. Shaping,
measuring and layout use these whole-pixel advances, and drawing puts every glyph origin on a whole pixel, so measured text
always matches drawn text.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#full-hinting)]

With the GDI+ settings in that sample, a white Trebuchet MS label on a button has the brightness GDI+'s `ClearTypeGridFit`
gives it to within 0.5%, and the mean per-pixel difference from GDI+ falls from 9.5 levels (light hinting) to 1.9 on a black
button: for that label the two are nearly pixel for pixel the same.

### Embolden and stem darkening

`Font.Embolden` grows outlines on every side by an amount in the font's units (pixels on a raster), with round joins, on top
of synthetic bold; advances never change, and glyph paths and ink bounds include it. `Font.StemDarkening` grows thin stems by
the default darkening curve FreeType documents for its CFF engine (its darkening-parameters property): 0.4 pixel for stems up
to 0.5 pixel wide, 0.275 from 1 to 1.667 pixels, nothing from 2.333, half on each side. The stem width is the face's own (a CFF
font's StdVW, else measured across 'l'), so darkening fades as text grows. It follows the device size, so only drawing on a
raster applies it.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#embolden)]

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

`MeasureText` also takes a `ReadOnlySpan<char>`, so a slice of a longer text or a reused buffer is measured without making a
string of it.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#measure-span)]

## Text as paths

`FontExtensions` holds `GetGlyphPath`, `GetTextPath` and `GlyphRun.ToPath`, which give `Path`s in canvas units, y down, to stroke, clip to or combine.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#paths)]

## Installed fonts and matching

`FontCollection.System` finds the installed fonts without native calls, by scanning the platform's font folders (and fontconfig's
dir entries on Linux), and indexes them on first use from the name and OS/2 tables only. It is read-only. A `FontCollection` of
your own takes faces, files and folders, and `AddSystemFonts` copies the installed fonts into it, so your fonts and the machine's
are matched together. Families are found by typographic, legacy, full or PostScript name in any language, and
`FontFamily.Match` picks a face by the CSS Fonts 4 rules for a `FontStyle` (weight, width and `FontSlant`). The `FontMatch`
records the `FontSynthesis` a lighter or upright face would need; `GenericFamily` names fixed per-platform lists.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#match)]

## Substitute fonts

Documents often name Arial, Helvetica, Times, Courier, Calibri or Cambria without carrying the font, and many machines (Linux
servers and containers above all) have none of them. Egami.Fonts.Substitutes is a separate, opt-in package that bundles open
fonts with the same glyph widths, so text set in a substitute keeps the original's line breaks: Liberation Sans for Helvetica
and Arial, Liberation Serif for Times and Times New Roman, Liberation Mono for Courier and Courier New, Carlito for Calibri and
Caladea for Cambria, each in four styles. It adds about 7 MB, which is why Egami.Fonts does not carry the fonts itself. The
fonts are under the SIL Open Font License 1.1, and their licences ship in the package.

`SubstituteFonts.Find` takes a family name or a common alias (`ArialMT`, `Times-Roman`, `Courier New`; spaces, hyphens and case
are ignored) and returns the face in the nearest style, or null for any other name. `SubstituteFonts.Collection` holds all
twenty faces as a `FontCollection`, to match by family name or to pass to anything that takes fonts, such as
`MetafileRenderOptions.Fonts` or `SvgFontOptions.Collection`.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#substitutes)]

## Subsets

`FontSubsetter.Subset` writes a font with only some glyphs, as PDF embedding and SVG export want: TrueType, OpenType CFF or a bare
CID-keyed CFF stream (`SubsetFormat`), closed over composites and accents, with unused CFF subroutines dropped. `SubsetOptions`
sets the format, dense or kept glyph ids, the character map, extra tables and the subset tag; the `FontSubset` holds the bytes and
the glyph map.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#subset)]

A PDF needs only the glyphs it draws. A subset that is to be shaped again, such as a web font for a known set of pages, sets
`SubsetOptions.LayoutClosure`: the kept glyphs gain every glyph GSUB can substitute for them (`OpenTypeLayout.GetClosure` with the
default features), and GSUB, GPOS and GDEF are copied unchanged. The copied tables use the old glyph ids, so it needs
`RetainGlyphIds`.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#subset-layout)]

## Font data for PDF

A PDF font reaches its glyphs in ways a text layout never does: by glyph name, by CID, through one particular cmap subtable or
through the font program's own encoding. `Typeface` gives each as FreeType does, so a PDF reader built on Egami finds the glyphs
PDFium finds.

- `GetGlyphName` and `TryGetGlyphByName` read a name-keyed CFF font's charset, else the post table (versions 1, 2 and 2.5). A
  simple font's `/Differences` array names glyphs this way. When two glyphs share a name the lower id wins.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#names)]

A simple font's `/Differences` names glyphs; `TryGetGlyphByName` finds them in a CFF charset or a TrueType post table.

- `CidSystemInfo`, `GetCid` and `TryGetGlyphByCid` read a CID-keyed CFF font's ROS and charset, for CIDFontType0 fonts.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#cids)]

- `CharacterMaps` lists every cmap subtable as stored and `FindCharacterMap` picks one by platform and encoding; a
  `CharacterMap` maps the subtable's own codes (bytes for (1,0) and (3,0)), never translated, and `GetMappings` lists them all as
  `CharacterMapping`s. `BuiltInEncoding` is a name-keyed
  CFF font's own encoding as a platform 7 map (0 Standard, 1 Expert, 2 custom). `GetGlyph(codePoint)` keeps choosing its one
  Unicode subtable as before.

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#cmaps)]

A symbolic TrueType font looks codes up in (3,0), then (1,0); a non-symbolic one in (3,1). A Type1C font without `/Encoding`
uses the CFF program's own encoding, `BuiltInEncoding`.

- `CffStandardStrings` has the 391 standard strings and the Standard and Expert encodings, for writing CFF (a Type 1 font
  converted for embedding, say).

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#cffstrings)]

Everything is read on first use, once per face, and is safe from several threads. Damaged data never throws: a name the post
table cannot give is null, a subtable that cannot be read maps nothing, and each reports a warning (`Font.BadTable`) through the
context's diagnostics, unless `FontLoadOptions.Strict` asks for an exception. On the 1,723 font programs embedded in Egap's
corpus, names, CIDs, subtables and built-in encodings equal FreeType's and fontTools' (FreeType's where the two differ).

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### A label along a curve

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#label-curve)]

### A variable weight slider

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#weight-slider)]

### Searchable text in a PDF

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#pdf-text)]

A `GlyphRun` from the shaper carries the text and a cluster per glyph, in visual order; that is all a PDF writer needs for
ToUnicode and ActualText.

### A variable font in a PDF

[!code-csharp[](../../../tests/Egami.DocSamples/Text.cs#variable-pdf)]

The subset of a variable face is always a static instance; `SubsetOptions.Variations` gives the position without making a face.
