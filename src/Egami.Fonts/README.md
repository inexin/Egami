# Egami.Fonts

Egami.Fonts reads fonts and lays out text in managed code.

- TrueType, OpenType (CFF), WOFF, WOFF2, EOT (MicroType Express too), variable and color fonts: faces, glyph outlines,
  metrics, kerning, installed fonts and subsetting.
- WOFF, WOFF2 and EOT writing, with the WOFF2 glyf, loca and hmtx transforms and collections, and back to TTF, OTF and TTC.
- OpenType text shaping, bidi, and paragraph layout with wrapping, alignment and hit testing.
- Thai, Lao, Khmer and Myanmar line breaking and word selection with ICU's word dictionaries.
- Automatic hyphenation with built-in patterns for 35 languages, and optimal (Knuth-Plass) line breaking.
- Font data for PDF writers.

See [Fonts and text](https://inexin.github.io/Egami/v1/articles/guides/text.html), [Unicode text and shaping](https://inexin.github.io/Egami/v1/articles/guides/unicode-text.html) and
[Paragraph layout](https://inexin.github.io/Egami/v1/articles/guides/paragraph-layout.html).

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Documentation: [inexin.github.io/Egami](https://inexin.github.io/Egami/). Source and changelog:
[github.com/inexin/Egami](https://github.com/inexin/Egami).
