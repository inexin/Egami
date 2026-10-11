# Resizing and geometry

Every operation is a step in a chain you record in a lambda. `Apply` runs the chain in place; `Derive` returns a new raster and
leaves the source alone. Both take a `CancellationToken`, checked between steps and between bands of rows.

## Resize, fit and cover

[!code-csharp[](../../../tests/Egami.DocSamples/Geometry.cs#resize)]

| Operation | Output size | Aspect ratio |
| --- | --- | --- |
| `Resize(w, h)` | Exactly w × h; 0 for one side derives it | Kept only when a side is 0 |
| `Fit(w, h)` | At most w × h | Kept; the image fits inside |
| `Cover(w, h, anchor)` | Exactly w × h | Kept; the image covers the box, and `anchor` picks the part kept |

`Cover` reads only the part it keeps: nothing is cropped first.

## Filters and linear light

[!code-csharp[](../../../tests/Egami.DocSamples/Geometry.cs#filters)]

| Filter | Use |
| --- | --- |
| `Bicubic` (default) | Catmull-Rom: sharp and fast, the usual choice |
| `Lanczos3` | Sharpest, for downscaling photos |
| `Mitchell` | Softer, fewer halos, good for upscaling |
| `Bilinear`, `Box` | Fast, soft |
| `Nearest` | No blending: pixel art, masks, label images |

`ColorSpace.Linear` converts to linear light, resamples and converts back. It costs a little time and matters most for small
bright detail on dark backgrounds, such as text or stars.

## Crop, pad, rotate, flip

[!code-csharp[](../../../tests/Egami.DocSamples/Geometry.cs#crop-pad-rotate)]

`Crop`, `Pad`, `FlipHorizontal`, `FlipVertical` and rotations by multiples of 90° move pixels exactly. Other angles resample onto
a canvas the size of the rotated bounding box, with the corners filled with the background color. `FillColor` paints a rectangle,
or the whole image.

## Transforms and perspective

`Transform` takes any affine matrix (`Matrix3x2`), perspective matrix (`Homography`) or 3D matrix (`Matrix4x4`, projected as CSS
`matrix3d` draws a flat element). Matrices map source positions to destination positions, with the origin at the image's top-left
corner and pixel centers at +0.5, as Skia and ImageMagick's `-distort` do; they compose with `*`, left first.

[!code-csharp[](../../../tests/Egami.DocSamples/Transforms.cs#transform)]

The result is the transformed image's bounding box, rounded to whole pixels, unless `TransformOptions.Viewport` picks a rectangle
of the destination plane. Where the image keeps or gains size, each output pixel blends the filter's kernel around its source point,
exactly as `Rotate` does; where it shrinks or tilts away, the filter weighs an ellipse covering the pixel's footprint in the source
(elliptical weighted averaging, as ImageMagick's `-distort` does), so fine detail fades to its average instead of aliasing into
moiré. Footprints are capped at 32 source pixels, so a plane stretching to the horizon aliases slightly in the far distance. A
transform that only scales equals `Resize`, and a pure rotation equals `Rotate`, pixel for pixel.

`Homography.FromQuad` builds the perspective that moves four points onto four others, such as a photographed page onto an upright
canvas:

[!code-csharp[](../../../tests/Egami.DocSamples/Transforms.cs#homography)]

`TransformOptions` (a `SamplingOptions` with a viewport) chooses the filter, the color space it blends in, what samples outside
the source read (`Edges`: transparent background, or the image clamped, mirrored or wrapped), and the background color:

[!code-csharp[](../../../tests/Egami.DocSamples/Transforms.cs#transform-options)]

`Skew` shears about the image center and `QuadDistort` moves the image's four corners to four points:

[!code-csharp[](../../../tests/Egami.DocSamples/Transforms.cs#skew)]

[!code-csharp[](../../../tests/Egami.DocSamples/Transforms.cs#quad-distort)]

A transform whose viewport has the input's size keeps the size, so it runs inside `Within`. A perspective that sends a corner of
the image to infinity or behind the viewer has no finite bounds, so it needs a viewport. Like `Rotate`, a transform sets the EXIF
orientation to Normal, updates the dimensions, drops the thumbnail and moves the subject area through the matrix.

## Orientation

[!code-csharp[](../../../tests/Egami.DocSamples/Geometry.cs#auto-orient)]

Rotating and flipping keep the EXIF orientation honest, and resizing and cropping update the EXIF pixel dimensions and drop the
embedded thumbnail, which no longer matches.

## Planning

[!code-csharp[](../../../tests/Egami.DocSamples/Geometry.cs#planning)]

Animations: every step runs on every frame. See the [animation guide](animation.md).

## Run it

[`samples/Geometry`](https://github.com/inexin/Egami/tree/main/samples/Geometry) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Geometry` writes its results to `./output`.
