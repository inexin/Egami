# PNG and APNG

Egami reads and writes PNG and its animated form, APNG: every color type and bit depth, interlaced files, transparency, text,
EXIF, XMP and ICC profiles.

## Reading

A PNG loads in its own layout: `Gray8`, `Gray16`, `GrayAlpha8`, `Rgb8`, `Rgba8` or `Rgba16`. An APNG loads as `Rgba8`, or
`Rgba16` for 16-bit files, with every frame composited to full size through every disposal and blend, as a browser shows it.
Damage is handled as the [Diagnostics](../diagnostics.md) page lists under `Png.`: ancillary chunks with a bad checksum are
ignored, and when the image data ends early the rows not decoded are transparent black.

## Writing

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#png)]

| Option | Default | Does |
| --- | --- | --- |
| `ColorType`, `BitDepth` | The pixel type's own | Force a layout, such as `PngColorType.Palette` |
| `Reduce` | `false` | Scan the image and write the smallest exact form |
| `Palette` | 256 colors, Floyd–Steinberg | How a palette is built when `ColorType` is `Palette` and the image has more colors |
| `CompressionLevel` | 5 | zlib level, 0 to 9 |
| `Filter` | `Adaptive` | Row filter, or one fixed filter |
| `FirstFrameOnly` | `false` | Write a still PNG from an animation |
| `Interlace` | `false` | Write Adam7, so a viewer can show a coarse image early; files are usually larger |
| `ParallelCompression` | `false` | Compress images with more than 4 MiB of image data on several cores |

`Interlace` is never taken from the source: an interlaced PNG is written plain unless you set it. With `ParallelCompression`,
the image data is cut into fixed bands of 1 MiB that compress on up to `EgamiContext.MaxParallelism` threads. The file is the
same on any machine and at any thread count, and in Egami's tests it was 0.1% to 0.3% larger; a 24-megapixel photo saved
8 times as fast on 12 cores. Smaller images are written exactly as with the option off.

[!code-csharp[](../../../tests/Egami.DocSamples/Formats.cs#png-large)]

Kept for round trips in `PngDetails`: gamma, chromaticities, sRGB intent, cICP, the ICC profile name, and every chunk Egami does
not read itself.

The writer also takes a palette you force, a fixed filter and a zlib strategy, and writes chunks you add to `PngDetails`:

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#png-writer)]

## APNG

Saving a raster with more than one frame as PNG writes an APNG. APNG keeps full color and partial alpha, so nothing is
quantized. Each frame stores only the rectangle that changed, and blends with `Over` when that stores less. Set
`PngWriterOptions.FirstFrameOnly` to write a still PNG from an animation. The [animation guide](../guides/animation.md) covers
frames, delays and loops.
