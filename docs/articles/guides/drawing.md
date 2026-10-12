# Drawing

Egami.Drawing is a separate package that draws antialiased paths, shapes, strokes, clips and images on a `Canvas`. It is a
preview (1.0.0-preview.2): its API may still change before 1.0. Text is drawn with Egami.Fonts; see
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

## Path geometry

Paths can be combined, outlined, dashed, measured, reshaped by effects, and read from and written as SVG path data. Every
result is a new `Path` that keeps lines, quadratics and cubics where the input had them, so a vector backend writes it exactly.

### Boolean operations

`Op` combines two areas with a `PathOp`: `Union`, `Intersect`, `Difference`, `Xor` or `ReverseDifference` (with shortcuts
for the first four). `PathOps.Combine` takes any number of paths in one pass, applied left to right. `Simplify` returns the
same area without overlaps or self-intersections.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#booleans)]

- Each input keeps its fill rule; open contours count as closed. Results use `NonZero`, with outer contours clockwise on screen
  and holes counterclockwise, and no contours cross.
- Crossings are found on the curves themselves and cut exactly. The `tolerance` (0.05 units by default) is how close points
  must be to count as the same: ends and crossings within a tenth of it merge, and edges that run within it of each other
  merge, so nearly coincident outlines leave no slivers. Pass a smaller tolerance for geometry that will be drawn larger.
- `Offset` grows an area by a distance, or shrinks it for a negative one, with a `LineJoin` at the corners.

### Outlines and dashes

`Stroke.ToPath` returns the outline a pen draws, as a path to fill: filled at the given scale it gives exactly the stroke's
pixels, and `simplify: true` removes its overlaps for export or hit testing. `Stroke.Dash` takes a `DashPattern` of absolute
on and off lengths and a phase, as PDF, SVG and Skia define them; `Scaled` turns lengths given in widths into units.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#dashes)]

A backend receives the pattern as data, so PDF can write `d` and SVG `stroke-dasharray`; the raster backend cuts the path
into dashes along its curves before outlining it.

### Measuring

`PathMeasure` lists each contour as a `ContourMeasure`: its `Length`, and the position, tangent and placing matrix at any
distance. `GetSegment` returns a piece of the contour that keeps its curves; `AddSegment` chains pieces into one contour.
Lengths are integrated, not taken from a flattened outline, and are exact to about 1e-4 units.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#measure)]

### Path effects

A `PathEffect` turns one path into another: `Dash`, `Corner` (rounded corners), `Trim`, `Jitter`, `Stamp` (a shape repeated
along the path, placed by a `StampStyle`), `Hatch` and `HatchPath` (lines or shapes on a lattice inside an area). `Then` and
`Plus` combine them. `path.Apply(effect)` applies one; `Stroke.Effect` has the canvas apply it before dashing and stroking.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#effects)]

`Jitter` hashes its seed with the contour and the point, so a path jitters the same way on every thread and machine.

### SVG path data

`Path.ParseSvg` reads the `d` attribute grammar of SVG 1.1 and 2; `TryParseSvg` returns false instead of throwing
`SvgPathException`, and `lenient: true` keeps every complete command before the first error. `ToSvg` writes path data, absolute
or relative, compact or spaced, to a precision (`SvgPathFormat`).

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#svg)]

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
the reference drawing library in every scenario timed, at one thread and all cores. Boolean operations, outlines, dashes, effects and SVG
parsing give paths within a mean level of SkiaSharp's, and measured lengths agree within 1e-3. The figures are in the 2.6.1
and 2.6.2 release notes.
