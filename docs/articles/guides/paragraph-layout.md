# Paragraph layout

Egami.Fonts lays out paragraphs in the `Egami.Fonts.Layout` namespace: it finds where lines may break (UAX #14), turns styled
text into shaped runs with the shaper from [Unicode text and shaping](unicode-text.md), and builds lines at a width, with
alignment and justification, line height, spacing, tabs, max lines and ellipsis, inline placeholders, vertical lines,
decorations and shadows, and carets and selection for editors. The result is plain geometry (lines of `GlyphRun`s and
decoration shapes), so Egami.Drawing's canvas, a PDF canvas and SVG draw it the same way.

Phase 2.8.2 is landing: the Unicode Line_Break classes are in (`UnicodeData.GetLineBreakClass`); line breaking, rich text,
line layout, hit testing and drawing follow, and this guide grows with them.
