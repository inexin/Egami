# Drawing

Egami.Drawing is a separate package that draws antialiased paths, shapes, strokes, clips and images on a `Canvas`. It is a
preview (1.0.0-preview.1): its API may still change before 1.0. Text is drawn with Egami.Fonts; see
[Fonts and text](text.md).

## A canvas

`raster.CreateCanvas()` returns a `RasterCanvas<TPixel>` that draws on the raster's first frame, in the pixel type's own
precision. Calls draw when they are made; `Save` and `Restore` bracket changes to the transform and clip.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#canvas)]

- Coordinates are float pixels, y down. Pixel (0, 0) covers the square from (0, 0) to (1, 1), so a `RectF` with whole-number
  edges covers whole pixels and a rectangle from 2.5 covers pixel 2 by half.
- Every pixel gets its exact covered area, rounded to 1/255: abutting shapes leave no seams.
- `CanvasOptions` sets where edges blend (`ColorSpace.Encoded`, the default, or `Linear`) and the curve `Tolerance`
  (0.1 pixel by default).
- A canvas is for one thread at a time. A large call spreads its rows over `EgamiContext.MaxParallelism` cores, with the same
  pixels at any core count.

## Shapes

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#shapes)]

Each shape has a `Fill` and a `Stroke` call. `RoundRect` takes `CornerRadii`, one x and y radius per corner; its `Kind`
(`RoundRectKind`) says when it is really a rectangle or an ellipse. `FillRing` fills between two round rectangles.

## Paths

A `Path` holds contours of lines, quadratic and cubic curves, built with a `PathBuilder`, and never changes once built.
Arcs, ovals and round corners become cubic curves as they are added. `PathVerb` names each command and `Points` holds their
points, for code that reads paths back.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#paths)]

- `FillRule` decides what is inside: `NonZero` (the default) or `EvenOdd`. Shapes are added clockwise; a
  `PathDirection.CounterClockwise` contour inside a clockwise one makes a hole under `NonZero` too.
- `Contains` uses the winding number on the curves themselves, and counts a point on the outline as inside.
- `Transform` takes a `Matrix3x2`, or a `Homography` for perspective.

## Strokes

`Stroke` is the pen: `Width`, `Cap` (`LineCap.Butt`, `Round`, `Square`), `Join` (`LineJoin.Miter`, `Round`, `Bevel`) and
`MiterLimit` (4). The pen is applied in local coordinates, so a non-uniform scale gives an elliptical pen.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#strokes)]

- Width 0 is a hairline: one device pixel across its major axis at any scale, as in Skia. A stroke thinner than a pixel draws
  as a hairline, fainter by its width.
- Overlaps within one stroke never double the color.
- `DrawPoints` takes a `PointMode`: dots, pairs of points as lines, or one polyline.

## Paints

A color converts to a `SolidPaint` wherever a `Paint` is expected. The paint also carries a `BlendMode`, an `Opacity` and
`Antialias`.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#paints)]

## Clips

`ClipRect`, `ClipRoundRect` and `ClipPath` take a `ClipOp`: `Intersect` keeps the inside, `Difference` the outside. Clips are
antialiased unless asked not to be. A clip that is a pixel-aligned rectangle costs nothing; any other clip is a coverage mask
of one byte per pixel of its bounds, and drawing through it costs about 1.2 times drawing without.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#clips)]

## Images

`DrawImage` draws all or part of an image into a rectangle, under the transform and clip, sampled with Egami's
`SamplingOptions`. At its own size on whole pixels it blends exactly as `DrawImage` in an operation chain does.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#images)]

## In an operation chain

`Draw` adds a step that draws on each frame with a canvas over the step's pixels (`DrawingExtensions`).

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#pipeline)]

## Other backends

`Canvas` is abstract. Each public call is checked, tracked (transform, clip bounds, save stack) and then ends in one protected
hook, so a backend sees shapes and pens as data: a PDF page can write a rectangle as `re` and a pen as `w`, `J`, `j` and `M`.
Hooks a backend leaves alone have defaults: rectangles, round rectangles and ovals become paths. A backend that cannot express a
call (a blend mode its format lacks) can draw it into pixels with `RasterizeFallback`, which returns a `FallbackImage`.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#backend)]

## Accuracy and speed

Fills are within a mean level of SkiaSharp's and closer than SkiaSharp's to the exact covered area; drawing is faster than
the reference drawing library in every scenario timed, at one thread and all cores. The figures are in the 2.6.1 release notes.
