# Bitmap tracing

Bitmap tracing turns a raster into filled vector shapes: a scanned signature, line art or a logo comes back as `Path` objects
with a color each, ready to draw at any size, record as a `Picture`, or save as SVG. It is part of Egami.Drawing, in the
namespace `Egami.Drawing.Tracing`; Egami.Svg adds the SVG output.

This first release traces black and white. Grayscale, color, logo, centre-line and pixel-art modes come in later 1.x
versions, as new `TraceMode` values; traced output may also improve between minor versions.

## Tracing a raster

`raster.Trace(options)` traces the raster's first frame and returns a `TraceResult`: its `Shapes`, back to front, and the
traced image's `Width` and `Height`.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#trace)]

- Each `TracedShape` has a `Path`, a `Color` (`Rgba8`), a `Kind` (`TracedShapeKind.Fill` or `Stroke`) and, for a stroke, a
  `StrokeWidth`. Black and white gives filled shapes only.
- Coordinates are the raster's pixels, y down, with pixel (0, 0) covering the square from (0, 0) to (1, 1), as on a
  [canvas](drawing.md#a-canvas).
- One shape is one ink region: its outer edge and its holes. A region inside another region's hole comes after it.
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
| `Mode` | `BlackAndWhite` | How pixels become shapes. |
| `Threshold` | `null` | The level from 0 (black) to 1 (white) that splits ink from paper: a pixel is ink when its intensity, over white by its alpha, is at most this. `null` finds it with Otsu's method, as `Raster.FindThreshold` does. |
| `PathFidelity` | 0.5 | How closely joined curves follow the curves they replace: 0 gives the fewest curves, 1 keeps every curve. 0.5 is a tolerance of 0.2 pixels. |
| `Corners` | 0.25 | How readily corners stay sharp: 0 rounds every corner, 1 keeps every corner (the outline is a polygon). |
| `Noise` | 2 | The smallest region kept, in pixels. Smaller regions, ink or paper, merge into the regions around them, smallest first, so a speck inside a small hole goes before the hole. 0 or 1 keeps everything. |
| `IgnoreWhite` | `false` | Leaves out the white shape that otherwise comes first, under the ink, the size of the image. |

`TraceOptions.Default` is the defaults; they match potrace's published ones (corner threshold 1, curve tolerance 0.2,
single-pixel specks dropped). `TraceOptions.BlackAndWhite` is the defaults with `IgnoreWhite`: the ink alone.

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

## How black and white is traced

The tracer follows P. Selinger, "Potrace: a polygon-based tracing algorithm" (2003), written from the paper:

1. **Ink map.** Each pixel is ink or paper at the threshold. Ink pixels that touch only at a corner belong together
   (8-connected); paper is 4-connected. Regions under `Noise` pixels merge into their neighbours.
2. **Boundaries.** The edges between ink and paper pixels form closed loops, walked with ink on the right. Where two ink
   pixels meet at a corner, the walk turns so they stay in one loop.
3. **Polygon.** Each loop gets the polygon with the fewest sides that stays within half a pixel of it, and among those the
   one whose sides lie closest to the loop. Each corner then moves, by at most half a pixel, to where its sides' best-fit
   lines meet.
4. **Curves and corners.** At each polygon corner a curve runs between the two sides' midpoints; a corner that is sharp enough
   for its side lengths, by the `Corners` setting, stays a corner instead. Runs of curves that bend the same way, less than
   180 degrees in all, join into one curve where it stays within the `PathFidelity` tolerance.

Because each curve only reaches the near edge of its corner's half-pixel square, a traced round shape comes out a fraction of a
pixel smaller than its pixels: about 0.2 pixels in radius for a circle 60 pixels across.
