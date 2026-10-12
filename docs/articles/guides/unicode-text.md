# Unicode text and shaping

Egami.Fonts carries its own Unicode 17.0 data and the Unicode algorithms text shaping builds on: character properties, the
bidirectional algorithm and text boundaries, in the `Egami.Fonts.Unicode` namespace. On top of them, `Egami.Fonts.Shaping`
shapes text with a font's OpenType tables, as HarfBuzz does. Phase 2.8.1 is still landing: the layout engine and HarfBuzz's
script shapers are here (Arabic, Syriac, N'Ko and Mongolian joining, Hebrew, the nine Indic scripts, Khmer, Myanmar, Thai, Lao,
Hangul, and the Universal Shaping Engine for Tibetan, Sinhala, Javanese and the other complex scripts); itemization, font
fallback and canvas drawing follow.

The tables are generated from the Unicode Character Database by `tools/make-unicode-tables.py` and never come from the .NET runtime,
so the same text gives the same answer on every runtime and machine. `UnicodeData.UnicodeVersion` says which version they follow.

## Character properties

`UnicodeData` answers per code point: the general category (as the runtime's `UnicodeCategory`), the `Script` and its script
extensions, the `BidiClass`, the mirroring character and `BidiPairedBracketType`, the `JoiningType` and `JoiningGroup` of Arabic
and Syriac letters, the canonical combining class, the `IndicSyllabicCategory` and `IndicPositionalCategory`, the
`VerticalOrientation`, the `EastAsianWidth`, the emoji properties and whether a character is default ignorable.

[!code-csharp[](../../../tests/Egami.DocSamples/Unicode.cs#properties)]

- A `Script` has its ISO 15924 `Code`, its Unicode `Name` and the OpenType tags fonts use for it, newest first (`dev2` then `deva`).
  Common, Inherited and Unknown are the shared scripts, and fonts know them as `DFLT`.
- Every lookup is a few array reads and allocates nothing. Code points run from 0 to 0x10FFFF; lone surrogates are code points too.

## Bidirectional text

`Bidi.Resolve` runs the Unicode Bidirectional Algorithm (UAX #9) in full: embeddings, overrides and isolates up to depth 125,
paired brackets and the weak and neutral rules. It returns a `BidiResult` with a level per UTF-16 unit and the `BidiRun`s of equal
level; `Reorder` gives one line's runs from left to right. `TextDirection.Auto`, the default, takes each paragraph's direction from
its first strong character; `LeftToRight` and `RightToLeft` force it.

[!code-csharp[](../../../tests/Egami.DocSamples/Unicode.cs#bidi)]

- Each paragraph separator ends a paragraph, and each paragraph is resolved on its own.
- `Levels` lays each paragraph out as one line. When text is wrapped, call `Reorder` per line: trailing spaces then take the
  paragraph's level, as rule L1 asks.
- The result rents its arrays; dispose it when done.
- Unicode's BidiTest.txt and BidiCharacterTest.txt pass in full.

## Graphemes and words

`TextSegmenter.Graphemes` walks extended grapheme clusters (UAX #29): what a reader sees as one character, such as a letter with
its accents, a Hangul syllable, a Devanagari conjunct or an emoji sequence. `TextSegmenter.Words` walks the segments between word
boundaries with Unicode's default rules. Both return a `TextSegment` per segment through a `GraphemeEnumerator` or
`WordEnumerator` that allocates nothing; `IsGraphemeBoundary` checks one index.

[!code-csharp[](../../../tests/Egami.DocSamples/Unicode.cs#segments)]

- Words use no dictionary, so Thai, Lao, Khmer and CJK text without spaces is not split into words.
- Line breaking (UAX #14) arrives with paragraph layout in 2.8.2.
- Unicode's GraphemeBreakTest.txt and WordBreakTest.txt pass in full.

## Shaping a run

`TextShaper.ShapeRun` shapes one run of text in one font, as HarfBuzz's hb_shape: the font's GSUB substitutions (ligatures,
contextual and alternate forms) and GPOS positioning (kerning, marks, cursive attachment) with the default features,
normalization against the font's character map, and the fallbacks HarfBuzz uses for fonts that lack them. `RunProperties`
gives the script (taken from the text when null), a BCP 47 language (none by default: the font's default forms, never the
machine's culture), the direction, feature settings and the `ClusterLevel`. The result lands in a reusable `GlyphBuffer`:
glyph ids, the UTF-16 index each glyph's cluster starts at, `GlyphFlags` and a `GlyphPosition` per glyph, in visual order and in
the font's units.

[!code-csharp[](../../../tests/Egami.DocSamples/Shaping.cs#run)]

- `GlyphFlags.UnsafeToBreak` marks where breaking the text needs shaping again; line layout uses it to re-shape only there.
- A run is one script in one direction; `TextShaper.Shape` splits real text into runs.
- The engine gives exactly hb-shape's glyphs, clusters, positions and flags on HarfBuzz's own tests and on Egami's corpus.

## Shaping text

`TextShaper.Shape` shapes a whole string: it splits it into lines at hard breaks (`
`, `
`, `
`, U+0085, U+2028,
U+2029), resolves each line's bidi levels, splits the line into runs of one level, one script, one span and one font, shapes each
run as `ShapeRun` does and returns the runs in display order. Nothing wraps; that is line layout's job.

[!code-csharp[](../../../tests/Egami.DocSamples/Shaping.cs#shape)]

- `ShapedText.Lines` holds a `ShapedLine` per line, with its `ParagraphLevel`, its `Runs` from left to right, its `Advance` and the
  highest ascent and lowest descent of its fonts. Each `ShapedRun` has its `Font`, `BidiLevel`, `Script`, `Language`, text range
  and `GlyphBuffer`; `ToGlyphRun` turns it into the `GlyphRun` a canvas draws.
- `ShapingOptions.Direction` is `Auto` by default: each paragraph takes the direction of its first strong character.
- `ShapingOptions.Spans` give ranges their own font, language or features; spans must not overlap.

[!code-csharp[](../../../tests/Egami.DocSamples/Shaping.cs#spans)]

## Font fallback

A font that does not map a character draws .notdef, unless `ShapingOptions.Fallback` names fonts to try. Fallback works on whole
grapheme clusters, so a letter and its accents, or an emoji sequence, stay in one font: the first font that maps every character
of the cluster takes it. The span's font comes first, then the `FontFallback.Families` (matched to the font's style), then the
`FontFallback.Resolver`. Spaces and punctuation stay in the font before them when it has them, so fallback does not split words.
Emoji presentation (a default-emoji character or VS16) prefers a color face (COLR, sbix or CBDT); VS15 prefers an outline face.

[!code-csharp[](../../../tests/Egami.DocSamples/Shaping.cs#fallback)]

- `FontFallback.None`, the default, keeps output independent of the machine: only the fonts passed are used.
- `FontFallback.System` resolves from the installed fonts through `SystemFontFallback`: fixed families per script on Windows, macOS and Linux (Segoe UI Emoji,
  Apple Color Emoji or Noto Color Emoji for emoji; the language picks Japanese, Chinese or Korean faces for Han), then any
  installed face that maps the cluster. Output then depends on the fonts installed.
- An `IFontFallbackResolver` of your own receives the cluster's text, script, language and the font's style.

## Features

A `FontFeature` sets one OpenType feature for the whole run or a range of it: 0 turns a default feature off, 1 turns a feature
on, higher values pick an alternate. `FontFeature.Parse` reads HarfBuzz's syntax (`liga=0`, `+smcp`, `ss01`, `aalt=2`,
`kern[3:5]=0`); the presets cover common cases.

[!code-csharp[](../../../tests/Egami.DocSamples/Shaping.cs#features)]

## Layout tables

`Typeface.Layout` returns an `OpenTypeLayout`, which lists the scripts, language systems and features the font's GSUB and GPOS
offer. A table whose structure is damaged is ignored whole, with a `Font.BadTable` diagnostic, as HarfBuzz ignores it; a font
that drives shaping past its limits stops with `Font.ShapingLimit`.

[!code-csharp[](../../../tests/Egami.DocSamples/Shaping.cs#layout)]
