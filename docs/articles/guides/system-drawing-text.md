# Matching System.Drawing text

Egami can draw small UI text the way System.Drawing does on Windows, for images that replace ones made with
`Graphics.DrawString` (GDI+) or `TextRenderer` (GDI), such as buttons in emails. The settings come from measurements of GDI and
GDI+ on Windows; [Fonts and text](text.md) explains each of them.

## GDI+ ClearTypeGridFit

`Font.WithGdiPlusLook()` and `CanvasOptions.GdiPlusClearType` together give GDI+'s ClearTypeGridFit look.

- The font's TrueType instructions run for GDI+, with Microsoft's ClearType compatibility rules (`FontHinting.Full`,
  `TrueTypeTarget.GdiPlus`).
- Advances are grid-fitted (`FontMetrics.GridFitted`) and glyph origins sit on whole pixels.
- LCD edging uses GDI+'s filter (`LcdFilter.Gdi`) and sampling (`LcdSampling.Gdi`, no smoothing along the stripes).
- The blend uses GDI+'s gamma: `TextGamma` 1.4, which is 1 + `Graphics.TextContrast` / 10 at the default contrast of 4.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawing.cs#gdiplus)]

For a white Trebuchet MS label at 16 pixels on a black or a #0078d4 button, the result is within 0.5% of GDI+'s brightness. The
mean per-pixel difference is under 2 levels out of 255, against 9.5 for Egami's default text.

## GDI ClearType

For `TextRenderer` or GDI's `ExtTextOut`:

- Set `Font.HintingTarget = TrueTypeTarget.Gdi` and `Font.Metrics = FontMetrics.Compatible`. GDI's compatible widths equal
  `GetCharABCWidths` for 9,688 of 9,690 glyph sizes measured.
- Set `TextGamma` to the system's ClearType contrast divided by 1000. That is 1.2 when it is 1200, and Windows' default is 1.4.

## DrawString's layout

`Graphics.DrawString` at a point, with the default `StringFormat`, starts the text 1/6 em right of the point, rounded to a whole
pixel. The first baseline sits the font's ascent below the point. `MeasureString` adds about 0.275 em on each side and 1/8 em to
the height. `StringFormat.GenericTypographic` drops all of this padding. These rules belong to System.Drawing's layout, not to
the text, so Egami keeps them in a sample rather than in the library:

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawing.cs#drawstring)]
