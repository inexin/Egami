# Egami.Drawing: guide for coding agents

Egami.Drawing draws vector graphics on Egami rasters and on other canvases (PDF pages, SVG): antialiased shapes, paths, strokes,
dashes, clips, gradients and other paints, layers, blend modes, shadows, images and text (through Egami.Fonts).

Namespaces: `Egami.Drawing` (canvas, paths, paints), with `Egami` and `Egami.Pixels` from core Egami. `Egami.Drawing.Path`
clashes with `System.IO.Path`: add `using Path = Egami.Drawing.Path;` where you need both.

{{shared}}

## Entry points

- `raster.CreateCanvas()`: a `RasterCanvas<TPixel>` that draws onto the raster; dispose it when done. `CanvasOptions` sets, for
  example, blending in linear light.
- Canvas calls: `FillRect`, `FillRoundRect`, `FillCircle`, `FillOval`, `FillPath`, `StrokePath` and the other `Fill` and
  `Stroke` methods, `DrawImage`, `DrawText`; `Save` and `Restore` around `Translate`, `Rotate`, `Scale` and clips.
- `Path`: immutable; build one with a `PathBuilder` (`MoveTo`, `LineTo`, `CubicTo`, `Close`, then `ToPath()`), or take
  `Path.RegularPolygon`, `Path.Star` and similar. `Union`, `Intersect`, `Difference` and `Xor` combine paths.
- Paints: a color converts to a `SolidPaint`; gradients, image and noise paints carry a `BlendMode` and an `Opacity`.
- `ops.Draw(canvas => ...)`: drawing as one step of an `Apply` or `Derive` chain, also per frame of an animation.

## Samples

A canvas on a raster:

{{sample Drawing.cs#canvas}}

Shapes and paths:

{{sample Drawing.cs#shapes}}

Paths from a builder:

{{sample Drawing.cs#paths}}

Paints, blend modes and opacity:

{{sample Drawing.cs#paints}}

Drawing inside an operation chain:

{{sample Drawing.cs#pipeline}}

## Common mistakes

- Not disposing the canvas. Dispose it (or end its `using` block) before saving or reading the raster.
- Unbalanced `Save` and `Restore`. Every transform and clip change between them is undone by `Restore`; without it, later
  drawing stays rotated or clipped.
- Mixing up `System.IO.Path` and `Egami.Drawing.Path` (see the alias above).
- Drawing thin lines and translucent colors in the default encoded color space and finding them too dark: create the canvas
  with `new CanvasOptions { ColorSpace = ColorSpace.Linear }`.
- Expecting pixel-art edges from the default paint: set `Antialias = false` on the paint.
- Drawing text with a font name. Text needs an Egami.Fonts `Font` made from a `Typeface` (see the Egami.Fonts guide).

## More

- Drawing guide: https://inexin.github.io/Egami/v1/articles/guides/drawing.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
- API reference: https://inexin.github.io/Egami/v1/api/Egami.Drawing.html
