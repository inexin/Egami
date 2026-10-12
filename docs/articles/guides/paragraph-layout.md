# Paragraph layout

Egami.Fonts lays out paragraphs in the `Egami.Fonts.Layout` namespace: it finds where lines may break (UAX #14), turns styled
text into shaped runs with the shaper from [Unicode text and shaping](unicode-text.md), and builds lines at a width, with
alignment and justification, line height, spacing, tabs, max lines and ellipsis, inline placeholders, vertical lines,
decorations and shadows, and carets and selection for editors. The result is plain geometry (lines of `GlyphRun`s and
decoration shapes), so Egami.Drawing's canvas, a PDF canvas and SVG draw it the same way.

Phase 2.8.2 is landing: line breaking and rich text are in; line layout, hit testing and drawing follow, and this guide
grows with them.

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

## Rich text and paragraphs

A `RichText` is a string with `StyleSpan`s over it (start and length in UTF-16 units, never overlapping) and a `Placeholder`
for each object replacement character (U+FFFC). `RichTextBuilder` builds one: `Push` a `TextStyle`, `Append` text,
`AddPlaceholder` for an inline box, `Pop` back. Pushed styles nest, and the result has one flat span per stretch of equal style.

[!code-csharp[](../../../tests/Egami.DocSamples/ParagraphLayout.cs#rich-text)]

Every `TextStyle` field is optional. An unset field takes the enclosing builder style's, then the `ParagraphStyle`'s
`DefaultStyle`, then Egami's default (16 units, black, normal line height):

- Fonts: `FontFamilies` in order of preference (later families and the paragraph's `Fallback` cover what the first lacks,
  per grapheme cluster), `Size`, `FontStyle`, `Variations`, `Language` and OpenType `Features`.
- Paint: `Color` (an `Rgba8`), or a `Paint`, which is any `ITextPaint`; Egami.Drawing's `Paint` is one, so gradients and image
  paints reach text. `Background` or `BackgroundPaint` fill behind the text, `Decoration` adds a `TextDecoration` (its
  `TextDecorationLines`, `TextDecorationStyle` and `TextDecorationSkipInk`), and `Shadows` a list of `TextShadow`s.
- Spacing: `LetterSpacing` after each grapheme cluster (which turns optional ligatures off), `WordSpacing`, a `LineHeight`
  (`Normal`, a `Multiple` of the font size or an `Absolute` height, by its `LineHeightKind`) and a `BaselineShift`.

A `Placeholder` has a width, a height and a `PlaceholderAlignment`: on the baseline at a given offset, above or below it, or at
the line box's top, bottom or middle. Bidi, line breaking and indexes treat it as one character.

`ParagraphStyle` holds the rest: the `DefaultStyle`, the paragraph `Direction`, `TextAlign`, `TextAlignLast` and `TextJustify`,
the `LineBreakOptions` in `LineBreaking`, `LineFill` (greedy or balanced), the `Hyphen` and `Ellipsis` strings, `MaxLines` and
`MaxHeight`, a `Strut`, `TrimEdges`, `TabSize` or `TabStop`s with their `TabAlignment`, the `WritingMode` and
`TextOrientation`, and the font `Fallback`.

`Paragraph.Create` prepares the text once: it resolves the styles, finds the bidi levels and break opportunities, splits the text
into runs where the bidi level, script, font or a shaping field changes, and shapes each run. Paint fields never split a run, so
a color change inside a ligature keeps the ligature. A paragraph is immutable and safe to share between threads.
