# Getting started

Egami loads, edits and saves images in .NET 10, in fully managed code. This page takes you from install to a saved file.

## Install

```sh
dotnet add package Egami
```

One package holds everything: every format, operation and pixel type. It has no dependencies and works with trimming and
Native AOT.

## Load, edit, save

[!code-csharp[](../../tests/Egami.DocSamples/GettingStarted.cs#load-edit-save)]

`Raster.Load` detects the format from the file's first bytes and returns a `Raster` in the pixel type that holds the file without
loss. `Apply` runs a chain of operations in place. `Save` picks the writer from the file extension.

## Pixel types

A raster is a grid of one pixel type, such as `Rgba8` (8-bit red, green, blue and alpha) or `Gray16`. Ask for the type you want
to work in, and rows come back as spans of it.

[!code-csharp[](../../tests/Egami.DocSamples/GettingStarted.cs#pixel-types)]

| Type | Channels | Typical use |
| --- | --- | --- |
| `Rgba8`, `Bgra8` | 8-bit color with alpha | The default working type; interop with Windows and GPU buffers |
| `Rgb8`, `Bgr8` | 8-bit color | Photos without alpha |
| `Argb8`, `Abgr8` | 8-bit color with alpha first | Interop with alpha-first buffers |
| `Alpha8` | Alpha only | Masks and glyph coverage |
| `Gray8`, `Gray16`, `GrayAlpha8`, `GrayAlpha16` | Gray | Grayscale images and masks |
| `Rgba16`, `Rgb16` | 16-bit color, with or without alpha | 16-bit PNG and PPM |
| `RgbaF32` | 32-bit float color with alpha | High-precision processing |

Values are sRGB-encoded with straight (not premultiplied) alpha. `raster.As<TPixel>()` converts a whole raster;
`PixelConvert.Convert` converts a span.

## Disposal

[!code-csharp[](../../tests/Egami.DocSamples/GettingStarted.cs#disposal)]

Pixel memory comes from a pool, not the garbage-collected heap, so a raster you forget to dispose holds its memory until the next
collection. Methods that return a new raster say so in their name: `Copy`, `As` and `Derive`.

## Sync and async

[!code-csharp[](../../tests/Egami.DocSamples/GettingStarted.cs#async)]

## Choosing a format

[!code-csharp[](../../tests/Egami.DocSamples/GettingStarted.cs#choosing-a-format)]

| Use | Format |
| --- | --- |
| Photos for the web | JPEG (or PNG when you need alpha) |
| Screenshots, diagrams, anything with sharp edges or alpha | PNG |
| Short animations | GIF for reach, APNG for full color and alpha |
| Fast lossless storage | QOI |
| Interchange with older tools | BMP, TGA, PNM |

Next: the [formats guide](guides/formats.md) lists what each format reads and writes, and the [cookbook](cookbook/index.md) has
task-sized recipes.
