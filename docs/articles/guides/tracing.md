# Bitmap tracing

Bitmap tracing turns a raster into vector shapes: a scanned signature, line art or a logo comes back as `Path` objects
with a color each, ready to draw at any size, record as a `Picture`, or save as SVG. It is part of Egami.Drawing, in the
namespace `Egami.Drawing.Tracing`; Egami.Svg adds the SVG output, and [`egami trace`](#from-the-command-line) does it all
from a shell.

Five modes trace filled shapes: black and white, grayscale, color, logo and pixel art. Centerline traces line art as stroked
lines. Traced output may improve between minor versions.

## Tracing a raster

`raster.Trace(options)` traces the raster's first frame and returns a `TraceResult`: its `Shapes`, back to front, and the
traced image's `Width` and `Height`.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#trace)]

- Each `TracedShape` has a `Path`, a `Color` (`Rgba8`), a `Kind` (`TracedShapeKind.Fill` or `Stroke`) and, for a stroke, a
  `StrokeWidth`. The black-and-white, grayscale, color, logo and pixel-art modes give filled shapes only; centerline gives strokes,
  drawn with round caps and joins.
- Coordinates are the raster's pixels, y down, with pixel (0, 0) covering the square from (0, 0) to (1, 1), as on a
  [canvas](drawing.md#a-canvas).
- In black and white, one shape is one ink region: its outer edge and its holes. A region inside another region's hole comes
  after it. `Palette` lists the shapes' colors, each once, back to front.
- `Trace` (in `RasterTraceExtensions`) works on any pixel type; `Trace<TPixel>` is the same for a typed raster. `TraceAsync`, for either, traces on a worker thread and
  takes a `CancellationToken`, checked between rows and between outlines.
- The raster's `EgamiContext` (or one you pass) limits the size: a raster larger than `SafetyLimits.MaxPixels` throws
  `LimitExceededException`.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#async)]

## Options

`TraceOptions` is a record: start from a preset and change what you need with `with`. Each setting is checked when it is set.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#options)]

| Setting | Default | What it does |
| --- | --- | --- |
| `Mode` | `BlackAndWhite` | How pixels become shapes: `BlackAndWhite`, `Grayscale`, `Color`, `Logo`, `Centerline` or `PixelArt`. |
| `Threshold` | `null` | Black and white and centerline: the level from 0 (black) to 1 (white) that splits ink from paper. A pixel is ink when its intensity, over white by its alpha, is at most this. `null` finds it with Otsu's method, as `Raster.FindThreshold` does. |
| `Colors` | 8 | Grayscale, color and logo: the most colors (gray levels) the image is reduced to, 2 to 256. An image with fewer flat colors gets fewer. |
| `Palette` | `null` | Grayscale, color and logo: fixed colors to reduce the image to instead; the shapes take them exactly. |
| `Layering` | `Stacked` | Grayscale, color, logo and pixel art: how the layers fit together (see below). |
| `Smoothing` | 0 | Grayscale and color: edge-keeping smoothing before the colors are found, from 0 to 1, for photos. Not used in logo. |
| `StrokeWidth` | `null` | Centerline: one pen width for every stroke, in pixels; `null` gives each stroke the width its ink covers. |
| `Exact` | `false` | Pixel art: the pixel squares, merged into one shape per color, instead of smooth shapes. |
| `PathFidelity` | 0.5 | How closely the curves follow the pixel edges: 0 gives the fewest curves, 1 the closest fit. 0.5 is a tolerance of half a pixel. |
| `Corners` | 0.25 | How readily corners stay sharp: 0 rounds every corner, 1 keeps every corner (the outline is a polygon). 0.25 keeps any turn of more than 60 degrees. |
| `SnapToLines` | `true` | Straight runs of an outline become lines. |
| `SnapToArcs` | `true` | Runs on a circle become circular arcs, and round outlines circles. |
| `Noise` | 2 | The smallest region kept, in pixels. Smaller regions merge into the regions around them, smallest first (in grayscale and color, into the neighbour they share the most edge with), so a speck inside a small hole goes before the hole. 0 or 1 keeps everything. |
| `IgnoreWhite` | `false` | Leaves out white: in black and white and centerline the white shape that otherwise comes first, under the ink, the size of the image; in grayscale, color and logo the layer of each color whose red, green and blue are all at least 240, whose areas stay empty (in logo, also the transparency the art encloses). |

## Presets

Each preset is a `TraceOptions` to start from, for one kind of image; change any setting with `with`.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#presets)]

| Preset | For | Settings |
| --- | --- | --- |
| `Default` | Scans to keep as a page | The defaults: black and white, with the paper as a white shape under the ink. |
| `BlackAndWhite` | Signatures, stamps, black-and-white logos | The defaults with `IgnoreWhite`: the ink alone. |
| `Logo` | Flat-color logos, icons and badges | `Logo` mode, up to 8 colors, stacked; white is traced. |
| `Poster` | Posters and illustrations, or photos as a few bold areas | `Color` mode, 6 colors, `Smoothing` 0.3, `Noise` 16. |
| `Photo` | Photos and painted art | `Color` mode, 16 colors, `Smoothing` 0.6, `Noise` 8, `PathFidelity` 0.3, no line or arc snapping. |
| `LineArt` | Clean line art, diagrams and handwriting | `Centerline` mode with `IgnoreWhite`. |
| `Sketch` | Pencil sketches and grainy scans | `Centerline` mode with `IgnoreWhite`, `Noise` 8 and `PathFidelity` 0.3. |
| `PixelArt` | Sprites, icons and other pixel art | `PixelArt` mode, stacked. |

The color presets were set on test photos: over five of them, a `Photo` trace drawn back differs from the photo by 2.9 %
of full scale on average and a `Poster` trace by 3.5 %, against 2.75 % for color mode's defaults, with a fifth and a twelfth
of its shapes (647 and 292 against 3444). A photo gives hundreds of shapes or more; fewer `Colors` or a larger `Noise` give fewer. `Sketch` drops the
grain of a scanned pencil drawing that `LineArt` would trace as dots and short strokes.

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

## Logo

`TraceMode.Logo` is for logos, icons and other flat-color art with antialiased edges, where a few pixels decide whether a
curve looks round. It finds the flat colors as color mode does, then splits every edge pixel into the two colors it blends
and places the edge from a smooth field of how much of each pixel each color covers. The curves come back rounder than in
color mode on small art: a disc of radius 6 keeps its radius to about 0.05 pixels.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#logo)]

- Transparency around the art is left out: no shape covers it. Transparency the art encloses, such as the inside of a
  letter O on a transparent background, is traced as white, which `IgnoreWhite` leaves out like any white.
- `Colors`, `Palette`, `Layering`, `Noise` and the fit settings work as in color mode. A palette maps each of the image's own
  colors to the nearest fixed color; with transparency it takes at most 254 colors.
- An image that is not flat color is refused with an `ArgumentException`: when more than a tenth of its pixels are neither
  one of its colors nor a blend of two, it is a photo or a gradient (trace it with `TraceMode.Color`), or it has more flat
  colors than `Colors` allows. JPEG noise on a flat logo is fine.
- Small images are sampled finer, up to 4 × 4 samples a pixel, so a 100-pixel icon has as smooth an outline as a large one.

## Centerline

`TraceMode.Centerline` is for line art, sketches and handwriting: instead of the outline of each ink region, it traces the
middle of each stroke as an open path, a `TracedShapeKind.Stroke` shape with the `StrokeWidth` its ink covers. Drawn with a
round pen, the strokes cover the ink again, and an editor can move, restyle or thin them like hand-drawn lines.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#centerline)]

- Ink is split from paper at `Threshold`, as in black and white, and `Noise` drops specks first.
- Lines that cross run straight through each other, as two strokes; where a line ends on another, as in a T, it ends on the
  other's centre line. A closed loop, such as the letter O, is one closed stroke.
- Each stroke has one width: the ink it covers over its length, allowing for its round ends. `StrokeWidth` sets one width for
  all of them instead, for a single pen.
- A dot too small to have a direction becomes a filled disc of its area.
- On strokes drawn from known paths, the traced centre lines lie within about 0.05 to 0.1 pixels of the drawn ones on
  average and the widths within 5 %. Filled areas wider than a pen stroke come back as very wide strokes: trace those in
  black and white.

## Pixel art

`TraceMode.PixelArt` is for sprites, icons and other art drawn pixel by pixel, after J. Kopf and D. Lischinski's
"Depixelizing Pixel Art" (2011). It reads each pixel as a cell of one flat color and decides which diagonal neighbours belong
together, so a line drawn in single diagonal steps comes back as one smooth band rather than a chain of squares, and an
upscaled sprite has no blocks and no blur.

[!code-csharp[](../../../tests/Egami.DocSamples/Tracing.cs#pixelart)]

- Each of the image's own colors becomes one shape, stacked or abutting as `Layering` says; every pixel's center stays in
  its own color. `Colors`, `Palette`, `Noise`, `Smoothing` and the fit settings are not used: pixel art keeps every pixel
  and its exact color.
- Edges between blocks at least a pixel long on both sides keep their corners: a 3 by 3 block stays square. Smaller details
  round off: a single pixel becomes a dot, a 2 by 2 block a disc, and the end of a line one pixel wide a round cap.
- Pixels under half alpha are left out, as `IgnoreWhite` leaves out white.
- An image of more than 256 colors (a photo, a gradient, a sprite scaled with smoothing) is refused with an
  `ArgumentException`: trace it with `TraceMode.Color` instead.
- `Exact` keeps the pixel squares, merged into one shape per color with straight edges on the pixel grid: drawn back at its
  size, the trace is the image again.
- The curves are then smoothed: the steps of long diagonal edges straighten out, while the curves stay within half a pixel
  of where they were and every pixel's center stays in its own color. The same image always gives the same shapes.

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

## From the command line

[`egami trace`](../cli/commands.md#trace) traces images and writes SVG, from a preset (`--preset logo`, `poster`,
`line-art`, `pixel-art`, ...) with any setting changed by its option (`--colors`, `--palette`, `--layering`, `--corners`,
`--path-fidelity`, `--noise`, `--ignore-white`, `--stroke-width`, `--exact`, ...). `--preview-png` also writes the trace drawn as a PNG,
to compare with the image.

```sh
egami trace logo.png --preset logo --colors 4 -o logo.svg --preview-png logo-trace.png
egami trace "scans/*.png" --preset line-art --stroke-width 2 -o "svg/{name}.svg"
egami trace sprite.png --mode pixel-art -o sprite.svg
```

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

Centerline mode replaces steps 2 to 4:

1. **Skeleton.** The ink is thinned to lines one pixel wide, after T. Y. Zhang and C. Y. Suen, "A fast parallel algorithm for
   thinning digital patterns" (1984), and read as a graph of lines between ends and junctions.
2. **Clean-up.** Each pixel's distance to the paper, from P. F. Felzenszwalb and D. P. Huttenlocher's exact distance
   transform, "Distance transforms of sampled functions" (2012), tells how thick the ink is there: branches shorter than
   that are thinning's spurs and go, and forks closer together than that are one crossing. Lines that meet nearly straight
   continue through a junction.
3. **Centres and widths.** Each point moves across the stroke to the middle of its ink, each free end out to half a pen width
   inside the ink's end, and each stroke takes the width that covers the ink nearest its skeleton.

Logo mode replaces steps 1 and 3:

1. **Coverage.** Each pixel is split into up to three shares: its flat color, or a blend of two colors (its own and a
   neighbour's), and, under alpha, transparency. Fringe colors that only blend others, along edges or from JPEG, leave the
   palette first.
2. **Fields.** Each color's shares become a signed distance to its edge, after S. Gustavson and R. Strand, "Anti-aliased
   Euclidean distance transform" (2011), which a cubic B-spline interpolates between pixel centres, prefiltered as in M.
   Unser, A. Aldroubi and M. Eden, "B-spline signal processing" (1993) and P. Thévenaz, T. Blu and M. Unser,
   "Interpolation revisited" (2000).
3. **Iso-lines.** The fields are sampled on a finer grid and each sample takes the color whose field leads there; the edge
   between two samples sits where the two fields cross, as in W. Lorensen and H. Cline's marching cubes (1987) in two
   dimensions. The rest is the color-mode walk and fit, scaled back to the image's pixels.

Pixel-art mode replaces steps 1 to 5:

1. **Similarity graph.** Each pixel is joined to each of its eight neighbours whose color is close in YUV (within 48/255 in
   Y, 7/255 in U and 6/255 in V, the limits of the hqx filters).
2. **Diagonals.** Where two diagonals of a 2 by 2 block cross, a block whose four pixels are all joined is shading and keeps
   neither. Otherwise each diagonal gets votes: the one on the longer curve (a run of pixels with two joins each) gets the
   difference in length, the one joining the smaller set of pixels in an 8 by 8 window gets the difference in size, and one
   with an end that has no other join gets 5. The diagonal with more votes stays; on a tie neither does.
3. **Cells.** Each pixel's square is reshaped so that two pixels joined along a diagonal share an edge, half a pixel's
   diagonal long, and the two pixels the diagonal passes cut a corner each: the simplified Voronoi diagram of the pixels and
   their joins, whose nodes lie on quarter pixels.
4. **Curves.** The cell edges between colors run from junction to junction; a right angle between straight legs of a pixel
   or more, cut or not, becomes a sharp corner, and the rest becomes a quadratic B-spline through the nodes. Where three
   colors meet, the two edges that are contours (or the two most nearly straight) join in one smooth curve and the third
   ends on it.
5. **Smoothing.** As in the paper's section 3.4, the B-spline nodes move to lower their energy: how far the curve turns near
   each (for a quadratic B-spline, the angle its control polygon turns through) plus the fourth power of its distance from
   where it started, in quarter pixels. Sweeps over the nodes try small moves from a fixed seed and keep the best. Junctions,
   sharp corners, the nodes next to junctions and curves along the image's edge stay put, and no node moves so far that the
   curve could come within 0.15 pixels of a pixel center (or half a pixel from where it was). `Exact` skips steps 1 to 5 and
   keeps the pixel edges.
6. **Shapes.** The curves around each color's cells become its shape, stacked or abutting as in color mode.
