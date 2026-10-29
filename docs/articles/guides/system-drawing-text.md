# Matching System.Drawing

Egami can draw the way System.Drawing does on Windows: UI text as `Graphics.DrawString` (GDI+) and `TextRenderer` (GDI) draw
it, and shapes, gradients and scaled images as `Graphics` does, for images that replace ones made with them, such as buttons in
emails. Every setting is opt-in, and each comes from measurements of GDI and GDI+ on Windows; [Fonts and text](text.md) explains
the text settings. Egami.SystemDrawing applies all of them for you.

| GDI+ | Egami |
| --- | --- |
| `TextRenderingHint.ClearTypeGridFit` | `Font.WithGdiPlusLook()` with `CanvasOptions.GdiPlus` |
| `PixelOffsetMode.None`, `Default`, `HighSpeed` / `Half`, `HighQuality` | `CanvasOptions.PixelOffset.None` / `Half` (the default) |
| `SmoothingMode.AntiAlias`, `HighQuality` | `CanvasOptions.Antialiasing.GdiPlus`; `SmoothingMode.None` is `Paint.Antialias = false` |
| `LinearGradientBrush.GammaCorrection` | `GradientInterpolation.GdiPlusGamma` |
| `PathGradientBrush` | a `Mesh` fanned from the brush's `CenterPoint` |
| `InterpolationMode` | the `ResampleFilter.GdiPlus*` members, per draw call (see [Image scaling](#image-scaling)) |
| `CompositingQuality.HighQuality`, `GammaCorrected` | `CanvasOptions.ColorSpace = ColorSpace.Linear` |

`CanvasOptions.GdiPlus` sets GDI+'s defaults for text, pixel offset and antialiasing. It does not choose a resample
filter: that belongs to each `DrawImage` or `ImagePaint` call's `SamplingOptions`, so pass `ResampleFilter.GdiPlusBilinear` there for
GDI+'s default `InterpolationMode`.

## GDI+ ClearTypeGridFit

`Font.WithGdiPlusLook()` and `CanvasOptions.GdiPlus` together give GDI+'s ClearTypeGridFit look.

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
Some fonts carry black-and-white bitmaps for small sizes, such as Calibri at 12, 13, 15, 16, 17 and 19 pixels, and GDI+ draws
those instead of the outlines. `Font.WithGdiPlusLook()` turns on `Font.EmbeddedBitmaps`, so Egami does too: Calibri's glyphs at
those sizes equal GDI+'s pixel for pixel.

### Placing text in a box

`DrawString` with a layout rectangle and `StringFormat.LineAlignment` places a line by GDI+'s own rule, which can differ from
centring on the font's ascent and descent by more than a pixel. `GdiPlusText.Baseline` gives the baseline it uses, for one line
in a box:

- The line is a cell tall: the ascent and descent `FontFamily.GetCellAscent` and `GetCellDescent` report (the OS/2 Windows
  metrics, or the typographic ones when the font sets USE_TYPO_METRICS), plus 1/8 em with the default `StringFormat`. GDI+'s
  line spacing does not count for one line.
- `LineAlignment` puts that cell at the box's top, middle or bottom.
- The baseline is `Font.GdiPlusAscent` below the cell's top. With `ClearTypeGridFit` the top is first rounded to a whole pixel,
  and the ascent is whole pixels too: the cell ascent rounded, or the font's VDMX maximum at the size when that is larger. For
  Trebuchet MS at 16 pixels that is 17 pixels, against a cell ascent of 15.02.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawing.cs#gdiplus-baseline)]

Against GDI+, 9,720 of 9,720 baselines are equal (10 fonts, 11 to 24 pixels, nine box heights at three fractional positions,
both formats and all three alignments), and the button above needs no vertical shift to match. Egami's own
`Canvas.DrawText` with a rectangle and `VerticalAlign` keeps its own centring.

## Shapes

Two canvas settings match what `Graphics` does with shapes at its defaults, `SmoothingMode.AntiAlias` and
`PixelOffsetMode.None`. `CanvasOptions.GdiPlus` sets both.

- `PixelOffset.None` puts whole coordinates on pixel centres, as GDI+'s `None`, `Default` and `HighSpeed` offset modes do. Paths,
  their paints and clips move half a pixel right and down. Text moves only when it is unhinted, just as GDI+ moves its
  `AntiAlias` text but not its grid-fitted text. Images go onto GDI+'s grid, where source and destination pixel centres line up: an
  image scaled by s moves (1 − s) / 2 pixels, and one drawn 1:1 does not move. `PixelOffset.Half`, Egami's default, is GDI+'s `Half`.
- `Antialiasing.GdiPlus` samples like `AntiAlias` and `HighQuality`, which are the same mode. Each pixel has 8 samples across
  and 4 down, and a sample on a left or top edge is inside. Pens of 1.5 device pixels or less draw as GDI+'s thin lines: one
  pixel thick across the line's major axis, at full strength, with pointed ends and no caps.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawing.cs#gdiplus-shapes)]

On rectangles, polygons, ovals and lines at fractional positions, the mean difference from GDI+ is 0.12 levels, down from 1.94
with Egami's closest settings before. Rectangles and polygons match to the level. Ovals and round caps still differ a little,
because GDI+ flattens curves its own way.

## Gradients

Egami's gradients already match GDI+'s within a fraction of a level when they mix in the stored values, as `LinearGradientBrush`
does by default. Its `Blend`, `InterpolationColors` and `WrapMode` (Tile is `TileMode.Repeat`, TileFlipX is `Mirror`) map to
stops and tile modes.

- `GammaCorrection = true` is `GradientInterpolation.GdiPlusGamma`: 16 steps between each pair of colors, each mixed in
  gamma-2.2 light and rounded to 8 bits.
- A `PathGradientBrush` is a fan of triangles from its `CenterPoint` to the flattened path, with the surround colors at the
  path's points and the center color at the center: draw it as a `Mesh`. GDI+'s default `CenterPoint` is the mean of the path's
  points, which for an ellipse is not its middle.

[!code-csharp[](../../../tests/Egami.DocSamples/SystemDrawing.cs#gdiplus-gradients)]

## Image scaling

`Graphics.DrawImage` scales with its `InterpolationMode`. Each mode has a `ResampleFilter` of its own, recovered from
DrawImage's impulse responses:

| InterpolationMode | ResampleFilter |
| --- | --- |
| Default, Low, Bilinear | `GdiPlusBilinear`: two pixels at every scale, so shrinking skips pixels |
| Bicubic | `GdiPlusBicubic`: Keys cubic (a = −0.5), four pixels at every scale |
| HighQualityBilinear | `GdiPlusHighQualityBilinear`: the tent, widened when shrinking, convolved with a one-pixel box |
| High, HighQualityBicubic | `GdiPlusHighQualityBicubic`: Keys cubic (a = −1), widened when shrinking, convolved with a one-pixel box |
| NearestNeighbor | `Nearest` |

With `PixelOffsetMode.Half`, GDI+ uses Egami's grid. With its default offset, source and destination pixel centres sit on whole
coordinates, so a scale s moves the image by (1 − s) / 2 destination pixels against Egami's grid; `CanvasOptions.PixelOffset.None`
does the same. Over both offsets, DrawImage and Egami's canvas are 0.38 levels apart on average, down from 2.53 with Egami's
closest filters before.

`CompositingQuality.HighQuality` and `GammaCorrected` blend in linear light, which is `CanvasOptions.ColorSpace = ColorSpace.Linear`
(within 0.1 levels of GDI+). The other qualities blend the stored values, Egami's default.

## Metafiles

Egami.Metafiles' EMF+ player follows the records' interpolation modes with the `GdiPlus*` filters and their gamma-corrected
gradients with `GdiPlusGamma`. Smoothing and compositing apply to the whole canvas, so set them for the playback through
`MetafileRenderOptions.CanvasOptions`, for example `CanvasOptions.GdiPlus`. With `PixelOffset.None` there, the player
leaves the pixel offset to the canvas.

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
