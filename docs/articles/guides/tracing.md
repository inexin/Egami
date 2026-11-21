# Bitmap tracing

Bitmap tracing turns a raster into filled vector shapes: a scanned signature, line art or a logo comes back as `Path` objects
with a color each, ready to draw at any size, record as a `Picture`, or save as SVG. It is part of Egami.Drawing, in the
namespace `Egami.Drawing.Tracing`; Egami.Svg adds the SVG output.

Three modes trace filled shapes: black and white, grayscale and color. Logo, centre-line and pixel-art modes come in later 1.x
versions, as new `TraceMode` values; traced output may also improve between minor versions.

## Tracing a raster

`raster.Trace(options)` traces the raster's first frame and returns a `TraceResult`: its `Shapes`, back to front, and the
traced image's `Width` and `Height`.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#trace)]

- Each `TracedShape` has a `Path`, a `Color` (`Rgba8`), a `Kind` (`TracedShapeKind.Fill` or `Stroke`) and, for a stroke, a
  `StrokeWidth`. The black-and-white, grayscale and color modes give filled shapes only.
- Coordinates are the raster's pixels, y down, with pixel (0, 0) covering the square from (0, 0) to (1, 1), as on a
  [canvas](drawing.md#a-canvas).
- In black and white, one shape is one ink region: its outer edge and its holes. A region inside another region's hole comes
  after it. `Palette` lists the shapes' colors, each once, back to front.
- `Trace` (in `RasterTraceExtensions`) works on any pixel type; `Trace<TPixel>` is the same for a typed raster. `TraceAsync` traces on a worker thread and
  takes a `CancellationToken`, checked between rows and between outlines.
- The raster's `EgamiContext` (or one you pass) limits the size: a raster larger than `SafetyLimits.MaxPixels` throws
  `LimitExceededException`.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#async)]

## Options

`TraceOptions` is a record: start from a preset and change what you need with `with`. Each setting is checked when it is set.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#options)]

| Setting | Default | What it does |
| --- | --- | --- |
| `Mode` | `BlackAndWhite` | How pixels become shapes: `BlackAndWhite`, `Grayscale` or `Color`. |
| `Threshold` | `null` | Black and white: the level from 0 (black) to 1 (white) that splits ink from paper. A pixel is ink when its intensity, over white by its alpha, is at most this. `null` finds it with Otsu's method, as `Raster.FindThreshold` does. |
| `Colors` | 8 | Grayscale and color: the most colors (gray levels) the image is reduced to, 2 to 256. An image with fewer flat colors gets fewer. |
| `Palette` | `null` | Grayscale and color: fixed colors to reduce the image to instead; the shapes take them exactly. |
| `Layering` | `Stacked` | Grayscale and color: how the layers fit together (see below). |
| `Smoothing` | 0 | Grayscale and color: edge-keeping smoothing before the colors are found, from 0 to 1, for photos. |
| `PathFidelity` | 0.5 | How closely the curves follow the pixel edges: 0 gives the fewest curves, 1 the closest fit. 0.5 is a tolerance of half a pixel. |
| `Corners` | 0.25 | How readily corners stay sharp: 0 rounds every corner, 1 keeps every corner (the outline is a polygon). 0.25 keeps any turn of more than 60 degrees. |
| `SnapToLines` | `true` | Straight runs of an outline become lines. |
| `SnapToArcs` | `true` | Runs on a circle become circular arcs, and round outlines circles. |
| `Noise` | 2 | The smallest region kept, in pixels. Smaller regions merge into the regions around them, smallest first (in grayscale and color, into the neighbour they share the most edge with), so a speck inside a small hole goes before the hole. 0 or 1 keeps everything. |
| `IgnoreWhite` | `false` | Leaves out white: in black and white the white shape that otherwise comes first, under the ink, the size of the image; in grayscale and color the layer of each color whose red, green and blue are all at least 240, whose areas stay empty. |

`TraceOptions.Default` is the defaults. `TraceOptions.BlackAndWhite` is the defaults with `IgnoreWhite`: the ink alone.

## Grayscale and color

`TraceMode.Color` finds the image's own flat colors, at most `Colors` of them, and traces each as a layer of shapes;
`TraceMode.Grayscale` does the same with gray levels. Pixels along antialiased or blurred edges are blends of the colors on
either side, so they are left out when the colors are found, and each takes the color of the side it is nearer.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#color)]

- `TraceLayering.Stacked` (the default) puts the color with the largest area first. Each layer covers its own pixels and
  those of the layers above it that it touches, so the layers above lie on it and no gap shows between colors when they are
  drawn antialiased. One shape is one connected part of a layer.
- `TraceLayering.Abutting` gives each color only its own pixels, one shape per region. Where regions meet, the boundary is
  cut at the points where three meet, each piece is fitted once, and both neighbours use it, so they share their edge
  exactly and never overlap. This suits editing the shapes one by one.
- `Smoothing` runs a bilateral filter, one to three passes, before the colors are found: texture and noise flatten while
  edges between clearly different colors stay put.

## Shapes and paths

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#shapes)]

- `ToPath(color)` joins the filled shapes of one color into one path, with the non-zero fill rule, or returns `Path.Empty`.
- Outer edges run clockwise on screen and holes counter-clockwise, so `NonZero` and `EvenOdd` fill a traced path the same.
- The paths are ordinary `Path` objects: transform them, combine them with `PathOps`, measure them, or write them as SVG path
  data with `ToSvg`.

## Drawing and recording

`Draw(canvas)` draws the shapes in the canvas's current transform and clip, so a scale draws them larger with no stair steps.
`ToPicture()` records them as a `Picture` the size of the traced image, to replay on any canvas, render with `ToRaster` or save
as a [drawing file](egdr-format.md).

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#draw)]

## SVG

Egami.Svg writes a trace through `SvgCanvas` (`SvgTraceExtensions`): `ToSvg()` returns the markup and `SaveSvg` writes a file or a stream, one `path`
element per shape, back to front, in an SVG the size of the traced image. `SvgWriteOptions` sets the number precision.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#svg)]

## How tracing works

Each mode follows the same steps, written from the papers in the [references](../../references.md):

1. **Labels.** Black and white splits ink from paper at the threshold; ink pixels that touch only at a corner belong together
   (8-connected) and paper is 4-connected. Grayscale and color give each pixel its nearest palette color; regions are
   4-connected. Regions under `Noise` pixels merge into their neighbours.
2. **Boundaries.** The pixel edges between regions form closed loops, walked with the region on the right, after P.
   Selinger's "Potrace: a polygon-based tracing algorithm" (2003).
3. **Edge points.** Each pixel edge gives a point, moved across the edge by how much the pixels on either side blend the two
   colors, so an antialiased edge lands where it was drawn. A hard staircase gives one point per step, on the line it draws,
   and a pixel corner between two sides stays.
4. **Corners.** A point where the outline turns by more than the `Corners` angle, over a few points and over twice as many,
   is a corner; it moves to where lines fitted to the points on either side meet.
5. **Curves.** Between corners, straight runs become lines and runs on a circle arcs; the rest is fitted with cubic curves
   by least squares, after P. J. Schneider, "An algorithm for automatically fitting digitized curves" (1990), split at the
   worst point until every point is within the `PathFidelity` tolerance.

A traced circle keeps its size to a few hundredths of a pixel when its edge is antialiased: a disc of radius 30 comes back
with a radius of 29.95.
