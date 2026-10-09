# Compositing

`DrawImage` draws one raster onto another with source-over blending: the normal way a layer covers what is under it.

[!code-csharp[](../../../tests/Egami.DocSamples/Effects.cs#draw-image)]

- The image may sit partly or wholly outside; only the overlap changes.
- It may be any pixel type; its first frame is drawn onto every frame of the target.
- `opacity` multiplies its alpha: 1 draws it as it is, 0 draws nothing.
- For `Rgba8` and `Bgra8` targets the blend is exact integer math: every result is the true value rounded to nearest.

## Part of an image

[!code-csharp[](../../../tests/Egami.DocSamples/Effects.cs#draw-image-area)]

The area is clipped to the image; an area that does not overlap it throws `ArgumentOutOfRangeException`. Drawing an area costs
the same as drawing an image of that size.
