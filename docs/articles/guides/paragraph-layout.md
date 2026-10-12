# Paragraph layout

Egami.Fonts lays out paragraphs in the `Egami.Fonts.Layout` namespace: it finds where lines may break (UAX #14), turns styled
text into shaped runs with the shaper from [Unicode text and shaping](unicode-text.md), and builds lines at a width, with
alignment and justification, line height, spacing, tabs, max lines and ellipsis, inline placeholders, vertical lines,
decorations and shadows, and carets and selection for editors. The result is plain geometry (lines of `GlyphRun`s and
decoration shapes), so Egami.Drawing's canvas, a PDF canvas and SVG draw it the same way.

Phase 2.8.2 is landing: line breaking, rich text, line filling, line boxes and alignment are in; line limits, hit testing
and drawing follow, and this guide grows with them.

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

## Laying out lines

`Paragraph.Layout` lays a paragraph out at a width and returns a `TextLayout`: its `Width` and `Height`, the `LongestLine`, and
its `Lines`. Each `TextLine` has its text range (`Start`, `Length`), its line box (`Top`, `Ascent`, `Descent`, `Baseline`), its
content's `Left` and `Width` (trailing spaces hang outside it), whether it ends at a `HardBreak`, and its `LayoutRun`s in visual
order. A run is glyphs (`Glyphs`, a `GlyphRun` already at its layout position), a placeholder's box, a tab or a break, by its
`LayoutRunKind`, with its `BidiLevel` and `StyleIndex`.

[!code-csharp[](../../../tests/Egami.DocSamples/ParagraphLayout.cs#layout)]

- Lines fill greedily, as browsers do: each takes the text up to the last break opportunity that fits. A taken soft hyphen
  shows the `Hyphen` string, counted in the line's width. A piece too wide for any line overflows, or with `OverflowWrap`
  breaks between grapheme clusters.
- Where a line breaks inside a shaped run at a point the shaper marks unsafe (an Arabic join, a contextual substitution),
  the pieces on each side are shaped again, so a line has the glyphs it would have on its own.
- Each line reorders its own bidi runs (UAX #9 rules L1 and L2): trailing spaces take the paragraph's direction.
- `LineFill.Balanced` evens the lines of a short paragraph (up to 6 lines): of the breaks that fit, the ones that leave the
  least squared space on every line, the last included, as Chromium's `text-wrap: balance` does.
- `float.PositiveInfinity` lays the paragraph out without wrapping; the box is then as wide as the longest line.

### Line boxes and spacing

Line boxes follow CSS Inline Layout 3, with the numbers browsers use:

- Each run's inline box is its font's ascent and descent, rounded to whole units as browsers round them, plus the leading
  its `LineHeight` adds: `Normal` adds the font's line gap, `Multiple` and `Absolute` set the height (a computed height is
  floored to 1/64 unit, browsers' layout unit, so stacked lines do not drift). The leading is split half above and half below,
  the top half rounded down. A `BaselineShift` moves the box down (or up, when negative).
- Every line also holds the root inline box, from the paragraph's `DefaultStyle`, as CSS's strut does; a `Strut` adds a
  minimum box of its own, and with `Force` it alone sets every line box, for line grids.
- Placeholders sit by their alignment: on the baseline at an offset, above or below it, or at the middle (half the default
  font's x-height above the baseline, as CSS's `middle`); boxes aligned to the line's top or bottom stretch it last.
- `TrimEdges` drops the space above the first line's text and below the last one's (CSS `text-box-trim`).
- Letter spacing follows each grapheme cluster and word spacing each word separator; tabs advance to the next of the
  `TabStops`, which align the text after them at their start, end, centre or first decimal separator, or else to the next
  multiple of `TabSize` spaces, at least half a space on.
- Characters take the first family that has them, as CSS matches fonts, so spaces and punctuation between words of another
  script stay in the first family.

Line boxes equal Chromium's within 0.01 unit on every line-height, strut, spacing, tab, placeholder and mixed-font case, and
glyph positions equal its within 0.01 once its 1/64 layout unit is allowed for.

### Alignment and justification

`TextAlign` places each line: `Start` and `End` follow the paragraph's direction, `Left`, `Right` and `Center` do not, and
`Justify` stretches lines to the full width. The last line, and lines before a hard break, use `TextAlignLast`, whose `Auto`
is the start for justified paragraphs. Trailing spaces hang past the line's end and never count for alignment.

`TextJustify` says where justified lines stretch: `InterWord` at word separators, `InterCharacter` between any two grapheme
clusters except inside cursive scripts such as Arabic, and `Auto` (the default) at word separators and around Chinese,
Japanese and Korean characters, as browsers justify. `None` leaves justified lines at the start. Arabic is justified at its
word spaces; kashida stretching waits for real demand.

### Line limits and the ellipsis

`MaxLines` and `MaxHeight` stop the lines: at the limit, or before the line whose bottom would pass the height. When text is
cut, `TextLayout.DidExceed` is true and `HiddenRange` says what was hidden; the last kept line drops whole grapheme clusters
from its end until its text and the `Ellipsis` (U+2026 by default, shaped in the style of the last kept character) fit, and
the ellipsis ends the line in the paragraph's direction, flagged `Ellipsized`. A null `Ellipsis` cuts at the line's end.

### Metrics

A `TextLayout` also answers how big its text is: `MinIntrinsicWidth` (CSS min-content, the widest piece that cannot break;
`OverflowWrap.Anywhere` lowers it to the widest grapheme cluster) and `MaxIntrinsicWidth` (max-content, the widest line
without wrapping), equal to Chromium's; `FirstBaseline` and `LastBaseline` for aligning boxes on text; `InkBounds`, the union
of the glyphs' ink and placeholder boxes, which can reach past the layout box; `PlaceholderRects`, one `TextBounds` per
placeholder in order, where the caller draws them; and `GetLineAt` and `GetLineAtY` to find the line holding a character or
a height.

`LineFlow` lays a paragraph out one line at a time, each at its own width, to flow text across columns and pages or around
images: `Paragraph.CreateFlow`, then `NextLine` until it returns null. Each line's top is 0; place it where it goes.

[!code-csharp[](../../../tests/Egami.DocSamples/ParagraphLayout.cs#flow)]

Line breaks equal headless Chromium's on a corpus of eleven scripts at five widths each, and on the feature cases; the few
differences are Chromium's own (its older quotation mark rule, no break at U+2028, no break after a slash in URLs, letter
spacing in Arabic).
