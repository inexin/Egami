# Matching System.Drawing text

Egami can draw small UI text the way System.Drawing does on Windows, for images that replace ones made with
`Graphics.DrawString` (GDI+) or `TextRenderer` (GDI), such as buttons in emails. The settings come from measurements of GDI and
GDI+ on Windows; [Fonts and text](text.md) explains each of them.

## GDI ClearType

For `TextRenderer` or GDI's `ExtTextOut`:

- Set `Font.Hinting = FontHinting.Full`, `Font.HintingTarget = TrueTypeTarget.Gdi` and `Font.Metrics = FontMetrics.Compatible`.
  GDI's compatible widths equal `GetCharABCWidths` for 9,688 of 9,690 glyph sizes measured.
- Draw with LCD edging, `LcdFilter.Gdi` and `LcdSampling.Gdi`.
- Set `TextGamma` to the system's ClearType contrast divided by 1000. That is 1.2 when it is 1200, and Windows' default is 1.4.

## DrawString's layout

`Graphics.DrawString` at a point, with the default `StringFormat`, starts the text 1/6 em right of the point, rounded to a whole
pixel. The first baseline sits the font's ascent below the point. `MeasureString` adds about 0.275 em on each side and 1/8 em to
the height. `StringFormat.GenericTypographic` drops all of this padding. These rules belong to System.Drawing's layout, not to
the text, so Egami keeps them in a sample rather than in the library:

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawing.cs#drawstring)]
