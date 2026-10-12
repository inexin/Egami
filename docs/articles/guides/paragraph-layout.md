# Paragraph layout

Egami.Fonts lays out paragraphs in the `Egami.Fonts.Layout` namespace: it finds where lines may break (UAX #14), turns styled
text into shaped runs with the shaper from [Unicode text and shaping](unicode-text.md), and builds lines at a width, with
alignment and justification, line height, spacing, tabs, max lines and ellipsis, inline placeholders, vertical lines,
decorations and shadows, and carets and selection for editors. The result is plain geometry (lines of `GlyphRun`s and
decoration shapes), so Egami.Drawing's canvas, a PDF canvas and SVG draw it the same way.

Phase 2.8.2 is landing: line breaking is in; rich text, line layout, hit testing and drawing follow, and this guide grows
with them.

## Line breaking

`LineBreaks.Enumerate` walks a text's break opportunities through a `LineBreakEnumerator` that allocates nothing. Each
`LineBreak` gives the index the next line would start at, whether the line must end there (after a newline, CR LF, NEL, VT,
FF, U+2028 or U+2029) and whether the break follows a soft hyphen (U+00AD), which shows a hyphen when a line ends there. The
last break is always at the end of the text.

[!code-csharp[](../../../tests/Egami.DocSamples/ParagraphLayout.cs#line-breaks)]

The rules are Unicode's line breaking algorithm (UAX #14) from Egami's own Unicode 17.0 tables, with each character's
`LineBreakClass` from `UnicodeData.GetLineBreakClass`. `LineBreakOptions` adds CSS Text 3's switches:

- `WordBreak.BreakAll` lets letters and digits of any script break like ideographs; `WordBreak.KeepAll` keeps ideographs,
  kana and Hangul together like the letters of a word, for Korean text with spaces.
- `LineBreakStrictness` sets how Chinese and Japanese text breaks. `Auto` is CSS's default and the same as `Normal`: a line may
  start with a small kana, and in Chinese and Japanese (`Language` "zh" or "ja") with the hyphens U+2010, U+2013, U+301C and
  U+30A0. `Loose` also lets iteration marks, centred punctuation, wide prefixes and suffixes and inseparable characters
  break in Chinese and Japanese. `Strict` is UAX #14's own rules, which pass Unicode's LineBreakTest.txt in full. `Anywhere`
  breaks between any two grapheme clusters.
- `OverflowWrap` says whether a piece too wide for its line may break between grapheme clusters; line filling uses it.

Thai, Lao, Khmer and Myanmar text breaks only at spaces and zero width spaces: Egami has no dictionaries. An object
replacement character (U+FFFC, as placeholders use) breaks before and after.
