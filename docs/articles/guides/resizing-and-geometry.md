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

## Orientation

[!code-csharp[](../../../tests/Egami.DocSamples/Geometry.cs#auto-orient)]

Rotating and flipping keep the EXIF orientation honest, and resizing and cropping update the EXIF pixel dimensions and drop the
embedded thumbnail, which no longer matches.

## Planning

[!code-csharp[](../../../tests/Egami.DocSamples/Geometry.cs#planning)]

Animations: every step runs on every frame. See the [animation guide](animation.md).
