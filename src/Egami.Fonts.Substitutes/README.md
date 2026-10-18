# Egami.Fonts.Substitutes

Egami.Fonts.Substitutes bundles open fonts that stand in for the fonts documents name most, for
[Egami.Fonts](https://www.nuget.org/packages/Egami.Fonts).

- Liberation Sans, Serif and Mono for Helvetica, Arial, Times, Times New Roman, Courier and Courier New, Carlito for Calibri and
  Caladea for Cambria, each in regular, bold, italic and bold italic.
- Every glyph has the width of the font it replaces, so text set in a substitute keeps the original's line breaks.
- Works on any machine, whatever fonts are installed. The fonts are under the SIL Open Font License 1.1; their licences ship in
  the package.

```csharp
using Egami.Fonts;
using Egami.Fonts.Substitutes;

Typeface? arial = SubstituteFonts.Find("Arial", FontStyle.Normal);   // Liberation Sans
FontCollection all = SubstituteFonts.Collection;                    // every bundled face
```

See [Fonts and text](https://inexin.github.io/Egami/v1/articles/guides/text.html#substitute-fonts).

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Documentation: [inexin.github.io/Egami](https://inexin.github.io/Egami/). Source and changelog:
[github.com/inexin/Egami](https://github.com/inexin/Egami).
