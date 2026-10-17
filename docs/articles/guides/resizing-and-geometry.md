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

Four more filters follow ImageMagick's definitions: `Lanczos2` (a two-lobe windowed sinc, nearly as sharp as `Lanczos3` with less
ringing), `Hermite` (a one-pixel cubic with no ringing), `Gaussian` (σ = ½, soft) and `Spline` (the cubic B-spline, the smoothest).
They work everywhere a filter does: resizes, rotations, transforms and warps.

### Many sizes from one image

`MipChain` halves an image again and again, averaging 2 × 2 blocks, and resizes each target from the smallest level at least twice
its size, so a set of thumbnails reads each source pixel about once. Results stay within 40 dB of a direct resize.

[!code-csharp[](../../../tests/Egami.DocSamples/Crops.cs#mip-chain)]

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

## Warps

A warp maps every output pixel back to a source point, and Egami samples those points as it samples transforms, so warps get
antialiasing where they shrink the image, and edge modes. `Warps` holds ImageMagick's distortions with ImageMagick's parameters
(`Barrel` and `BarrelInverse` lens correction, `Polar`, `DePolar`, `Arc`, `Swirl`, `Wave`, `Implode`), so their documentation applies
and their pixels land where ImageMagick's do, and Skia's `Magnifier`:

[!code-csharp[](../../../tests/Egami.DocSamples/Warps.cs#warps)]

Your own warp is an `IWarp`, which maps a whole output row per call, or `Warp.FromFunction` for a quick experiment:

[!code-csharp[](../../../tests/Egami.DocSamples/Warps.cs#custom-warp)]

`Displace` moves each pixel by a displacement map's channels, as Skia's `DisplacementMap` and ImageMagick's `-displace` do:

[!code-csharp[](../../../tests/Egami.DocSamples/Warps.cs#displace)]

`Offset` moves the image by whole pixels with no resampling, wrapping round by default as ImageMagick's `-roll` does, and `Tile` fills
a new canvas with copies of the image or a rectangle of it:

[!code-csharp[](../../../tests/Egami.DocSamples/Warps.cs#offset-tile)]

Warps that keep the size (`Barrel`, `Swirl`, `Implode`, `Magnifier`, `Displace`, `Offset`) run inside `Within`; `Polar`, `DePolar`,
`Arc`, `Wave` and `Tile` change it and throw there. A warp can move the subject anywhere, so it drops the EXIF subject area.

## Trimming and content crops

`Trim` cuts away the border: the edge rows and columns whose pixels all lie within a tolerance of the border color (the top-left
pixel's, or one you give), by ImageMagick's `-fuzz` distance, so its rectangle equals ImageMagick's `-trim`. `EntropyCrop` keeps the
busiest part of the image: the 16 × 16 tiles whose luminance entropy reaches a threshold.
`Raster.TrimBounds` and `Raster.EntropyBounds` return the rectangles without changing anything.

[!code-csharp[](../../../tests/Egami.DocSamples/Crops.cs#trim)]

[!code-csharp[](../../../tests/Egami.DocSamples/Crops.cs#entropy-crop)]

Their size depends on the pixels, so the chain runs up to them, measures, and plans the rest from the real size; it is still one
`Apply` or `Derive`. Every frame of an animation is measured and the union of their rectangles kept, so frames keep one size. An
image that is all border, or has no busy tile, is left as it is. They cannot run inside `Within`, and `Transcode` decodes the
whole image before running a chain that holds one.

### Straightening pages

`Deskew` finds the tilt of a scanned or photographed page's text lines and turns it back, keeping the page size with the corners in
the border color; `Raster.DetectSkew` returns the angle alone. A gray copy about 1,500 pixels on its long side is binarized with
Otsu's threshold and each angle up to `maxAngle` (15° by default) is scored by how sharply the ink's projection piles up in rows,
first in half-degree steps and then to 0.05°. A picture, noise or an already straight page returns 0 and is left alone.

[!code-csharp[](../../../tests/Egami.DocSamples/Crops.cs#deskew)]

## Combining images

`Raster.Append` lays images side by side or stacks them, as ImageMagick's `+append` and `-append` do; `Raster.Grid` makes a contact
sheet, as `montage` does; `SpriteSheet.Pack` packs images into one sheet with the MaxRects algorithm and returns every sprite's
rectangle. The result takes the first image's pixel type, metadata and color profile, and each input's first frame is used.

[!code-csharp[](../../../tests/Egami.DocSamples/Combining.cs#append)]

[!code-csharp[](../../../tests/Egami.DocSamples/Combining.cs#grid)]

[!code-csharp[](../../../tests/Egami.DocSamples/Combining.cs#sprite-sheet)]

Sprites are never rotated, and the same inputs always give the same sheet. `SpritePackOptions` sets the largest width, the gap
between sprites, edge extrusion for GPU sampling, power-of-two sizes and trimming of each sprite's transparent border, whose
offsets `SpriteSheet.Offsets` returns.

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
