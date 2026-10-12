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
- A run is one script in one direction; splitting text into runs (bidi, script, font fallback) comes with `TextShaper.Shape`.
- The engine gives exactly hb-shape's glyphs, clusters, positions and flags on HarfBuzz's own tests and on Egami's corpus.

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
