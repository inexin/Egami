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

## Blend modes

Pass a `BlendMode` to choose how each pixel combines with the one under it: the twelve Porter–Duff operators, Plus and Modulate,
and the W3C blend modes from Multiply to Luminosity, plus Add and Subtract.

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#draw-image-mode)]

- Only the overlap changes, as when drawing a layer: `DestinationIn` cuts the image's shape out of the overlap and leaves the rest.
- `SourceOver` on stored values is exactly the plain `DrawImage`.
- Blends use the stored values, as browsers and editors do; pass `ColorSpace.Linear` to blend light instead, with the raster's own curve.
- On `PremulRgba8`, `PremulBgra8`, `Rgba8` and `Bgra8` the Porter–Duff operators, Plus, Modulate and the product-and-sum modes are exact
  integer math; ColorDodge, ColorBurn, SoftLight and the four non-separable modes are within a level of exact, with alpha exact.

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#blend-mode)]

## Blend a color

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#blend-color)]

`BlendColor` is `DrawImage` of a solid image the size of the raster, without making one.

## Arithmetic composite

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#draw-arithmetic)]

Each premultiplied channel becomes k1·S·D + k2·S + k3·D + k4, as SVG's feComposite arithmetic operator and Skia's arithmetic
blender define it, kept a valid premultiplied pixel unless `clamp` is false.

## Blend rows in your own code

`Compositor` runs the same kernels on spans of any pixel type, with optional per-pixel coverage for antialiased edges. It allocates
nothing once warm and is safe to call from many threads.

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#compositor)]

## Run it

[`samples/Compositing`](https://github.com/inexin/Egami/tree/main/samples/Compositing) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Compositing` writes its results to `./output`.
