# GIF

Egami reads GIF 87a and 89a files, with every disposal method, interlaced images, transparency, the loop count, comments, XMP and
ICC profiles, and writes GIF 89a.

## Reading

A GIF loads as `Rgba8`, every frame composited to full size as a browser shows it. Damaged files load as far as
they can, with the warnings the [Diagnostics](../diagnostics.md) page lists under `Gif.`.

GIF files keep their palettes, version and application extensions in `GifDetails`, and each frame's in `GifFrameDetails`:

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#gif-details)]

## Writing

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#gif)]

GIF holds at most 256 colors per frame. Frames with few enough colors keep them exactly. Others get a palette from Wu's
quantizer, dithered with Floyd–Steinberg by default. GIF transparency is on or off, so pixels below
`PaletteOptions.AlphaThreshold` (128 by default) become transparent.

| Option | Default | Does |
| --- | --- | --- |
| `Palette` | 256 colors, Floyd–Steinberg | How a palette is built when a frame has more colors than fit; see [Palettes and dithering](../guides/palettes.md) |
| `PaletteScope` | `Auto` | One global table when all frames fit in it, else one per frame; `Global` and `PerFrame` choose |
| `OptimizeFrames` | `true` | Each frame after the first stores only the rectangle that changed; `false` writes every frame whole |

Saving a raster with more than one frame writes an animation; see the [animation guide](../guides/animation.md).
