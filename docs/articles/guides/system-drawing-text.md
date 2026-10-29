# Matching System.Drawing text

Egami can draw small UI text the way System.Drawing does on Windows, for images that replace ones made with
`Graphics.DrawString` (GDI+) or `TextRenderer` (GDI), such as buttons in emails. The settings come from measurements of GDI and
GDI+ on Windows; [Fonts and text](text.md) explains each of them.

## GDI+ ClearTypeGridFit

`Font.WithGdiPlusLook()` and `CanvasOptions.GdiPlusClearType` together give GDI+'s ClearTypeGridFit look.

- The font's TrueType instructions run for GDI+, with Microsoft's ClearType compatibility rules (`FontHinting.Full`,
  `TrueTypeTarget.GdiPlus`).
- Advances are grid-fitted (`FontMetrics.GridFitted`), as `DrawString` lays out text with the default `StringFormat`, and
  glyph origins sit on whole pixels. GDI+ rounds an advance up from about 0.458 of a pixel rather than 0.5, and the GDI+
  target does the same.
- With `StringFormat.GenericTypographic` GDI+ pulls lines back toward their design width. Use
  `FontMetrics.GridFittedToDesign` for that: a run that hinted advances would make more than 2.1 pixels wider than designed
  keeps its design advances, rounded up to sixteenths of a pixel.
- LCD edging uses GDI+'s filter (`LcdFilter.Gdi`) and sampling (`LcdSampling.Gdi`): six on-or-off samples per pixel across
  the stripes, on each pixel's centre line, so every channel's coverage is a whole number of sixths.
- The blend uses GDI+'s gamma: `TextGamma` 1.4, which is 1 + `Graphics.TextContrast` / 10 at the default contrast of 4. With
  `LcdSampling.Gdi` it goes through GDI+'s 8-bit gamma tables, as measured: the drawn values equal GDI+'s for every coverage
  on every gray background tested.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawing.cs#gdiplus)]

For a white Trebuchet MS label at 16 pixels on a black or a #0078d4 button, the result is within 0.5% of GDI+'s brightness. The
mean per-pixel difference is 0.67 levels out of 255 on black and 0.38 on blue, against 9.5 and 5.4 for Egami's default text.
Fonts that GDI+ draws from embedded bitmaps at small sizes, such as Calibri, still differ: Egami draws their outlines.

## Shapes

Two canvas settings match what `Graphics` does with shapes at its defaults, `SmoothingMode.AntiAlias` and
`PixelOffsetMode.None`. `CanvasOptions.GdiPlusClearType` sets both.

- `PixelOffset.None` puts whole coordinates on pixel centres, as GDI+'s `None`, `Default` and `HighSpeed` offset modes do. Paths,
  their paints and clips move half a pixel right and down. Text moves only when it is unhinted, just as GDI+ moves its
  `AntiAlias` text but not its grid-fitted text, and images do not move. `PixelOffset.Half`, Egami's default, is GDI+'s `Half`.
- `Antialiasing.GdiPlus` samples like `AntiAlias` and `HighQuality`, which are the same mode. Each pixel has 8 samples across
  and 4 down, and a sample on a left or top edge is inside. Pens of 1.5 device pixels or less draw as GDI+'s thin lines: one
  pixel thick across the line's major axis, at full strength, with pointed ends and no caps.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawing.cs#gdiplus-shapes)]

On rectangles, polygons, ovals and lines at fractional positions, the mean difference from GDI+ is 0.12 levels, down from 1.94
with Egami's closest settings before. Rectangles and polygons match to the level. Ovals and round caps still differ a little,
because GDI+ flattens curves its own way.

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
