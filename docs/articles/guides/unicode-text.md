# Unicode text

Egami.Fonts carries its own Unicode 17.0 data and the Unicode algorithms text shaping builds on: character properties, the
bidirectional algorithm and text boundaries. They live in the `Egami.Fonts.Unicode` namespace. OpenType shaping itself (GSUB and
GPOS, the script shapers, feature switches and font fallback) arrives later in phase 2.8.1 and will be described here as it lands.

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
