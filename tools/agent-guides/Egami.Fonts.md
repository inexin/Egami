# Egami.Fonts: guide for coding agents

Egami.Fonts reads fonts and lays out text in managed code: TrueType, OpenType (CFF), WOFF, WOFF2, variable and color fonts,
OpenType shaping, bidi, font fallback and paragraph layout with wrapping, alignment and hit testing. Egami.Drawing's canvases draw
the result.

Namespaces: `Egami.Fonts` (typefaces, fonts, collections, shaping), `Egami.Fonts.Layout` (paragraphs), with `Egami.Drawing` to
draw.

{{shared}}

## Entry points

- `Typeface.FromFile`, `Typeface.FromMemory`: one face of a font file; `FontLoader.Probe` reads what a file holds.
- `new Font(typeface, size)`: a face at a size in pixels; `font.MeasureText(text)` gives its advance, ascent and height.
- `FontCollection`: faces by family; `AddSystemFonts()` adds the installed fonts, `Match(family, style)` picks a face.
- `canvas.DrawText(text, baseline, font, paint)`: one line, from Egami.Drawing.
- `Paragraph.Create(text, ParagraphStyle)` and `paragraph.Layout(width)`: wrapped lines; `canvas.DrawText(text, rect, style)` and
  `canvas.DrawTextLayout` draw them.
- `TextShaper.Shape(text, font, ShapingOptions)`: shaped runs with bidi and fallback, for your own layout.
- Egami.Fonts.Substitutes: bundled metric-compatible stand-ins for Arial, Times New Roman, Courier New, Calibri and Cambria.

## Samples

Load a font:

{{sample Text.cs#load}}

A caption on a photo:

{{sample Text.cs#caption}}

Measure text to fit a box:

{{sample Text.cs#measure}}

Paragraphs at any width:

{{sample ParagraphLayout.cs#layout}}

Drawing paragraphs:

{{sample ParagraphLayout.cs#draw}}

Mixed scripts with fallback fonts:

{{sample Shaping.cs#shape}}

## Common mistakes

- Passing the top of the text to `DrawText(text, point, font, paint)`. The point is the start of the baseline; add
  `MeasureText(...).Ascent` to place text by its top. Paragraph layouts are placed by their top-left corner instead.
- Expecting a font file name or family name to work where a `Font` is wanted: load a `Typeface` first, or match one in a
  `FontCollection`.
- Assuming the installed fonts exist on servers and in containers. Linux images often have none: ship the font files, or use
  Egami.Fonts.Substitutes.
- Drawing text in scripts the font lacks (Arabic, Hebrew, CJK, emoji) without fallback. Give `ShapingOptions.Fallback` or the
  paragraph style's `FontFamilies` more than one family.
- Reloading the same font file for every call. A `Typeface` is meant to be loaded once and shared.

## More

- Fonts and text: https://inexin.github.io/Egami/v1/articles/guides/text.html
- Unicode text and shaping: https://inexin.github.io/Egami/v1/articles/guides/unicode-text.html
- Paragraph layout: https://inexin.github.io/Egami/v1/articles/guides/paragraph-layout.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Fonts.html
