# Egami.Fonts.Substitutes: guide for coding agents

Egami.Fonts.Substitutes bundles open fonts that stand in for the fonts documents name most, for Egami.Fonts: Liberation Sans,
Serif and Mono for Helvetica, Arial, Times, Times New Roman, Courier and Courier New, Carlito for Calibri and Caladea for Cambria,
each in regular, bold, italic and bold italic. Every glyph has the width of the font it replaces, so text keeps its line breaks.
Use it where documents, metafiles or System.Drawing code name fonts the machine does not have, such as on Linux servers.

Namespaces: `Egami.Fonts.Substitutes`, with `Egami.Fonts`.

{{shared}}

## Entry points

- `SubstituteFonts.Find(family, style)`: the stand-in for a family name, or null when there is none.
- `SubstituteFonts.Collection`: all twenty faces as a `FontCollection`.
- Egami.Metafiles takes them through `MetafileRenderOptions.Fonts`; Egami.SystemDrawing through
  `SystemDrawingOptions.FontSubstitutes = SubstituteFonts.Find`.

## Samples

{{sample Text.cs#substitutes}}

Metafiles drawn with the substitutes first, then the installed fonts:

{{sample Metafiles.cs#metafile-substitutes}}

## Common mistakes

- Using `Find` for any family. It knows only the families above; check for null and fall back to installed or shipped fonts.
- Expecting the same glyph shapes as the original. Widths match, the design does not.

## More

- Substitute fonts: https://inexin.github.io/Egami/v1/articles/guides/text.html#substitute-fonts
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
