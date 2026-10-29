# Drawing

Egami.Drawing is a separate package that draws antialiased paths, shapes, strokes, clips and images on a `Canvas`. It is at
1.0 (1.0.0) and its API is stable: 1.x versions add to it without breaking code that uses it. Text is drawn with
Egami.Fonts; see [Fonts and text](text.md).

## A canvas

`raster.CreateCanvas()` returns a `RasterCanvas<TPixel>` that draws on the raster's first frame, in the pixel type's own
precision. Calls draw when they are made; `Save` and `Restore` bracket changes to the transform and clip.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#canvas)]

- Coordinates are float pixels, y down. Pixel (0, 0) covers the square from (0, 0) to (1, 1), so a `RectF` with whole-number
  edges covers whole pixels and a rectangle from 2.5 covers pixel 2 by half.
- Every pixel gets its exact covered area, rounded to 1/255: abutting shapes leave no seams.
- `CanvasOptions` sets where edges blend (`ColorSpace.Encoded`, the default, or `Linear`) and the curve `Tolerance`
  (0.1 pixel by default).
- `CanvasOptions.PixelOffset` and `CanvasOptions.Antialiasing` switch to GDI+'s pixel centres and antialiasing, for images
  that must look like System.Drawing's (see [Matching System.Drawing](system-drawing-text.md#shapes)).
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

### Gradients

`LinearGradient`, `RadialGradient`, `ConicalGradient` (between two circles, as the HTML canvas, Skia and PDF define it) and
`SweepGradient` take `GradientStop`s from 0 to 1. `Tile` says what lies past the ends (`Clamp`, `Repeat`, `Mirror`, `Decal`), and
with `Clamp`, `ExtendStart` and `ExtendEnd` can leave one side empty, as a PDF shading's Extend does. Stops mix in encoded sRGB by
default, or in linear light, Oklab or Oklch with a `HueMethod` (`GradientInterpolation`, whose `InterpolationSpace` names the
space), as CSS Color 4 defines them, and premultiplied so a fade to a transparent color never darkens. Every paint has a
`LocalMatrix`, which turns a radial gradient into an ellipse. On 8-bit targets a gradient reads 1024 colors made once per paint;
`Dither` adds a 4×4 ordered pattern that hides banding on large soft ramps.

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#gradients)]

### Images, noise and your own paints

`ImagePaint` fills with an image under its local matrix, tiled per axis, sampled by Egami's filters and from a `MipChain` level
when shrunk. `NoisePaint.Fractal` and `NoisePaint.Turbulence` (its `NoisePaintKind`) are SVG's feTurbulence (matching SkiaSharp's noise
shaders), and `NoisePaint.Simplex` is smoother and tiles exactly. `BlendPaint` combines two paints with a blend mode,
`WithColorMatrix` runs a color matrix on a paint (a `ColorMatrixPaint`), and a `CustomPaint` record fills rows of premultiplied
colors from C#.

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#image-paint)]

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#noise)]

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#composed-paints)]

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#custom-paint)]

A colour can also be given as printing inks: `new SolidPaint(InkColor)`, a `GradientStop` with an `InkColor`, and
`Mesh.WithInks` and `Patch.WithInks` for vertex and corner colours. An ink canvas prints the inks; every other canvas,
SVG included, draws the colour's appearance in sRGB. `Paint.Overprint` (an `Overprint`) says which plates a paint prints on an
ink canvas, as PDF's overprint does; RGB canvases ignore it. The [colour management guide](color-management.md) covers inks.

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#ink-paints)]

## Layers

`SaveLayer` sends later drawing into a layer that the matching `Restore` merges with an opacity, a blend mode, a filter (any chain
of Egami's operations that keeps the size) and a soft mask: what PDF calls a transparency group. Two overlapping shapes in a
half-opacity layer fade as one. The layer covers only its bounds inside the clip, grown by the filter's reach (`RasterOps.Reach`,
an `OperationReach`), so a blur near the clip's edge still sees what was drawn just outside it. `Backdrop` runs a chain on what
lies behind the layer first, for frosted glass, and `Isolated = false` starts the layer from the backdrop.

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#layers)]

A `SoftMask` is drawn on its own canvas; its luminosity over a backdrop color, or its alpha, through an optional transfer curve,
multiplies the layer, as a PDF soft mask does.

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#soft-mask)]

### Common layer effects

In Egami each layer effect is a `LayerOptions.Filter` or `Backdrop` chain, so any Egami operation works on a layer.

| Effect | Egami |
| --- | --- |
| Gaussian or box blur effect | `Filter = o => o.Blur(sigma)` or `o.BoxBlur(radius)` |
| Drop shadow | `Filter = o => o.DropShadow(dx, dy, sigma, color, new DropShadowOptions { Expand = false })` |
| Outer glow | `Filter = o => o.Glow(sigma, color, spread)` |
| Inner glow | `Filter = o => o.Glow(sigma, color, spread, inner: true)` |
| Inner shadow | `Filter = o => o.InnerShadow(dx, dy, sigma, color)` |
| Color matrix, brightness, contrast, saturation, grayscale, sepia, hue | `Filter = o => o.ColorMatrix(...)` and the tone steps |
| Opacity | `LayerOptions.Opacity` |
| Blend mode | `LayerOptions.BlendMode` |
| Backdrop blur and other backdrop effects | `Backdrop = o => o.Blur(sigma)` and any other chain |

## Blurs and shadows

`Paint.Blur` takes a `MaskBlur`: the shape's coverage blurred by a sigma in device pixels before the paint is applied, keeping
the inside and outside as its `BlurStyle` says, as Skia's blur mask filter. Axis-aligned rectangles with the normal style are
blurred exactly, from the error function. `DrawShadow` casts a Material-style shadow from a shape's elevation and
a light, after Skia's shadow utilities; a group's drop shadow is a layer filter.

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#blur)]

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#shadows)]

## Clips

`ClipRect`, `ClipRoundRect` and `ClipPath` take a `ClipOp`: `Intersect` keeps the inside, `Difference` the outside. Clips are
antialiased unless asked not to be. A clip that is a pixel-aligned rectangle costs nothing; any other clip is a coverage mask
of one byte per pixel of its bounds, and drawing through it costs about 1.2 times drawing without.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#clips)]

## Images

`DrawImage` draws all or part of an image into a rectangle, under the transform and clip, sampled with Egami's
`SamplingOptions`. At its own size on whole pixels it blends exactly as `DrawImage` in an operation chain does.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#images)]

## Nine-patches and lattices

`DrawImageNinePatch` stretches an image to a rectangle while its corners keep their size: the edges stretch one way and the
center both ways, as Skia's drawImageNine. `DrawImageLattice` generalizes it: a `Lattice`'s x and y divisions cut the image into
columns and rows that alternate fixed and stretchable, and each cell can draw its part of the image, nothing, or a fixed color
(`LatticeCellKind`). Fixed parts shrink in proportion when the destination is too small for them. `NinePatch.FromAndroid` reads
an Android .9.png: the one-pixel border's black markers become the lattice, and the right and bottom markers the content
padding.

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#lattice)]

The raster backend draws the cells as one textured mesh, so neighboring cells meet without seams at any scale or position.

## Sprite atlases

`DrawAtlas` draws many sprites from one image in one call, as Skia's drawAtlas: each sprite is a source rectangle placed by a
`SpriteTransform` (a rotation and uniform scale, then a move, as Skia's RSXform), and may be tinted by a color combined with it
by a blend mode. Each sprite blends with what lies below on its own, so additive particles add up. It pairs with
`SpriteSheet.Pack`; the sheet's texels are prepared once for the whole call.

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#atlas)]

## Meshes and patches

A `Mesh` is triangles (`MeshMode`: a list, a strip or a fan, from the vertices or from indices) with an optional color,
texture coordinate or value at each vertex. `DrawMesh` fills the triangles' union once, antialiased on its outer edges only, so
a mesh has no cracks between its triangles, which PDF requires of its mesh shadings; where triangles overlap, the later one
shows. Vertex colors interpolate in the canvas's color space, premultiplied (Gouraud shading); with texture coordinates, the
paint is mapped onto each triangle; and the two combine by a blend mode, the vertex color as the destination. As in Skia, a
solid paint's color gives way to the vertex colors and only its alpha applies.

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#mesh)]

A `Patch` is a curved four-sided surface: a Coons patch from four cubic edges (`Patch.Coons`, 12 points, PDF shading type 6
and Skia's drawPatch) or a tensor-product patch from a 4×4 net (`Patch.Tensor`, PDF type 7), with colors, values or texture
coordinates at its corners. `DrawPatch` cuts it into triangles under the current transform until they stray less than a
quarter of a device pixel from the surface, and where it folds over itself the larger parameters cover the smaller, as PDF
specifies.

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#patch)]

Values are PDF's way of shading through a function: each vertex or corner carries one number t, interpolated, then mapped to a
color through gradient stops, so a renderer of PDF shadings types 4 to 7 hands Egami its meshes and patches directly and the
colors stay smooth inside each triangle.

## Recorded drawings

A `PictureRecorder` is a canvas that records instead of drawing; `Finish` returns an immutable `Picture`. `DrawPicture` replays
it on any canvas under a matrix, through the public calls, so it stays sharp at any scale and a vector backend receives it as
vectors. A paint with an opacity or a blend mode merges the whole drawing as one layer; `cacheAsImage` draws an image of it
rendered at the current scale and kept with the drawing, for a drawing repeated many times at one scale. `ToRaster` renders it,
and `Playback` replays it without the state `DrawPicture` adds. A drawing replays on many threads and canvases at once.

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#recording)]

The drawing is recorded once and replayed as vectors, so it is sharp at every size; `cacheAsImage` trades that for speed when
it is drawn many times at one size.

### Drawing patterns

A `PicturePaint` fills shapes with a drawing repeated by tile modes: the tiling patterns of PDF and SVG, and Skia's picture
shader. The raster backend renders one tile at the scale it is drawn at and keeps it with the drawing, so a zoomed pattern stays
sharp.

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#pattern)]

### Documents

A canvas also carries what a document backend needs and pixels never show. `DrawImage` takes an `EncodedImage`, a file's bytes
probed but not decoded, so a PDF page can embed a JPEG as it is while a raster canvas draws the decoded pixels.
`DrawImageMask` paints through an image used as a mask, as PDF's stencil masks: its alpha, or for an image without alpha its
gray level, is the coverage. `LayerOptions.Knockout` makes a layer a knockout group: each call inside it composites with what
the layer started as and replaces earlier calls where it covers. A layer inside it counts as one call, which replaces what is
under it wherever it drew. `DrawAnnotation` marks a link to a URL, a named destination or a link to one (`Annotation`, as Skia's drawAnnotation), and
`BeginMarkedContent` and `EndMarkedContent` bracket drawing with a `MarkedContent` tag for tagged PDF, with a structure node id,
replacement text and alternate text. Backends get `OnDrawEncodedImage`, `OnDrawAnnotation`, `OnBeginMarkedContent` and
`OnEndMarkedContent`; by default the image is decoded and the others do nothing.

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#encoded-image)]

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#image-mask)]

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#annotations)]

### Saving drawings

`Picture.Save` writes Egami's own documented binary format, [.egdr](egdr-format.md), and `Picture.Load` reads it back as
untrusted input, under the context's safety limits. Images are kept as PNG, text as its outlines. A drawing that holds C# code (a
`CustomPaint`, or a layer's filter or soft mask) cannot be saved: `Save` throws and names the call.

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#egdr)]

## Path geometry

Paths can be combined, outlined, dashed, measured, reshaped by effects, and read from and written as SVG path data. Every
result is a new `Path` that keeps lines, quadratics and cubics where the input had them, so a vector backend writes it exactly.

### Boolean operations

`Op` combines two areas with a `PathOp`: `Union`, `Intersect`, `Difference`, `Xor` or `ReverseDifference` (with shortcuts
for the first four). `Path.Combine` takes any number of paths in one pass, applied left to right. `Simplify` returns the
same area without overlaps or self-intersections.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#booleans)]

Boolean operations keep curves, so the frame exports to PDF or SVG exactly. See [Path geometry](#path-geometry).

- Each input keeps its fill rule; open contours count as closed. Results use `NonZero`, with outer contours clockwise on screen
  and holes counterclockwise, and no contours cross.
- Crossings are found on the curves themselves and cut exactly. The `tolerance` (0.05 units by default) is how close points
  must be to count as the same: ends and crossings within a tenth of it merge, and edges that run within it of each other
  merge, so nearly coincident outlines leave no slivers. Pass a smaller tolerance for geometry that will be drawn larger.
- `Inflate` grows an area by a distance, or shrinks it for a negative one, with a `LineJoin` at the corners.

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

## Printing inks

`InkCanvas` draws into an `InkRaster`'s plates instead of RGB pixels. It shares the raster canvas's rasterizer, clips and layers,
so shapes, strokes and text cover the same pixels; where the RGB canvas blends colours, it sets each plate. Ink paints print
their inks by name, and RGB colours, gradients and images convert into the process inks as `InkCanvasOptions` says (the RGB
encoding, intent and black point compensation). `InkCanvas.Clear(InkColor.None)` leaves blank paper.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#ink-canvas)]

## Other backends

`Canvas` is abstract. Each public call is checked, tracked (transform, clip bounds, save stack) and then ends in one protected
hook, so a backend sees shapes and pens as data: a PDF page can write a rectangle as `re` and a pen as `w`, `J`, `j` and `M`.
Hooks a backend leaves alone have defaults: rectangles, round rectangles and ovals become paths. A backend that cannot express a
call (a blend mode its format lacks, a `CustomPaint`, a filtered layer) can draw it into pixels with `RasterizeFallback`, which
returns a `FallbackImage`. Layers reach a backend through `OnSaveLayer` and `OnRestoreLayer`; a PDF page would write a
transparency group with its opacity, blend mode and soft mask. `OnDrawMesh` and `OnDrawPatch` let a PDF page write shading
types 4 to 7, `OnDrawPicture` a form XObject drawn once and referenced each time, and `OnDrawImageLattice` and `OnDrawAtlas` images
per cell or sprite; by default meshes go through the raster fallback, patches become meshes, and the others become images and
meshes.

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#backend)]

## Accuracy and speed

Fills are within a mean level of SkiaSharp's and closer than SkiaSharp's to the exact covered area.
Boolean operations, outlines, dashes, effects and SVG parsing give paths within a mean level of SkiaSharp's, and measured lengths agree within 1e-3. The figures are in the 2.6.1
and 2.6.2 release notes.

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### Draw a rounded badge

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#badge)]

Drawing at twice the size through `Scale(2)` keeps every shape's coordinates as designed.

### Draw chart axes

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#chart-axes)]

Hairlines on half-pixel coordinates fill exactly one column or row of pixels.

### Clip a photo to a circle

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#clipped-photo)]

### Animate a line drawing itself

[!code-csharp[](../../../tests/Egami.DocSamples/Drawing.cs#draw-itself)]

`PathEffect.Trim` keeps a fraction of the path's length; stepping it from 0 to 1 gives the frames.

### Paint a dusk sky

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#sky-gradient)]

Mixing in Oklch keeps the hues from turning gray between stops, and `Dither` hides the bands of a wide soft ramp. See
[Gradients](#gradients).

### Draw a card with a shadow

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#card-shadow)]

### Fade a photo's edges

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#faded-edge)]

### Fade a group as one

[!code-csharp[](../../../tests/Egami.DocSamples/Paints.cs#group-opacity)]

See [Layers](#layers).

### Stretch a button image to any size

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#stretchable-button)]

The corners keep their size and the edges and center stretch. An Android .9.png carries its own lattice: see
[Nine-patches and lattices](#nine-patches-and-lattices).

### Shade a gradient mesh

[!code-csharp[](../../../tests/Egami.DocSamples/Advanced.cs#gradient-mesh)]

See [Meshes and patches](#meshes-and-patches).

### Separations of a CMYK page with spot colours

[!code-csharp[](../../../tests/Egami.DocSamples/InkRecipes.cs#ink-separations)]

`InkCanvas` draws on plates with any `Canvas` call; RGB images and colours convert through the ink set's profile. See
[Printing inks](#printing-inks).

### A spot-colour label with overprint

[!code-csharp[](../../../tests/Egami.DocSamples/InkRecipes.cs#ink-spot-label)]

`Overprint.On` paints only the inks a colour names; `NonZero` also leaves the plates of a CMYK colour's zero components alone,
as PDF's overprint mode 1.

### Check total ink coverage before printing

[!code-csharp[](../../../tests/Egami.DocSamples/InkRecipes.cs#ink-coverage)]
