# Pixel types

A raster stores one pixel type, chosen when you load or create it. Pick the type your data is in, or the one you will work in:
loading straight into it converts row by row while decoding, with no full-size copy in between.

[!code-csharp[](../../../tests/Egami.DocSamples/PixelTypes.cs#choose-type)]

## The types

| Type | Bytes | Holds | Typical use |
| --- | --- | --- | --- |
| `Rgba8`, `Bgra8` | 4 | 8-bit color, straight alpha | The default working type; Windows and GPU buffers |
| `Argb8`, `Abgr8` | 4 | 8-bit color, alpha first | Java and other alpha-first buffers |
| `Rgb8`, `Bgr8` | 3 | 8-bit color | Photos without alpha |
| `Gray8`, `GrayAlpha8` | 1, 2 | 8-bit gray | Grayscale images |
| `Alpha8` | 1 | Alpha only | Masks, glyph coverage |
| `Rgba16`, `Rgb16` | 8, 6 | 16-bit color, with or without alpha | 16-bit PNG, PPM and TIFF |
| `Gray16`, `GrayAlpha16` | 2, 4 | 16-bit gray | 16-bit gray PNG and PAM |
| `RgbaF32`, `RgbF32`, `GrayF32` | 16, 12, 4 | 32-bit float | Precise processing, PFM, depth and height maps |
| `RgbaF16` | 8 | 16-bit half float | HDR, GPU textures |
| `Bgr565`, `Bgra4444`, `Rgb10A2` | 2, 2, 4 | Packed color | Embedded displays, legacy textures, 10-bit displays |
| `Cmyk8`, `Cmyk16` | 4, 8 | Ink amounts | CMYK JPEG, TIFF and PSD |
| `PremulRgba8`, `PremulBgra8`, `PremulRgbaF32` | 4, 4, 16 | Color premultiplied by alpha | Compositing, Windows and GPU surfaces |

Packed types are named after their channels from the lowest bit up, as DXGI and Vulkan name formats, so a Direct3D or Vulkan
texture wraps without a swizzle: `Bgr565` has blue in bits 0 to 4 and red in 11 to 15, the same memory as Skia's and Android's
RGB 565.

## Conversions

Every type converts to every other with `raster.As<TPixel>()` or, for a span, `PixelConvert.Convert`. Conversions between
integer types are exact wherever the target can hold the value: 8 to 16 bits multiplies by 257, packed levels round to the
nearest value both ways, and 16 to 8 bits rounds to nearest, so every value of a narrower type survives a round trip through a
wider one. Half floats round as `(Half)f` does.

Values are sRGB-encoded with straight alpha. Gray types take the Rec. 709 luma in linear light; `Alpha8` reads as black with its
alpha, and converting a color to it keeps only alpha.

Half and float types keep values outside 0..1 through conversions, geometry and saving to PFM; filters and color adjustments
work in 0..1 and clip, as they do for `RgbaF32`.

[!code-csharp[](../../../tests/Egami.DocSamples/PixelTypes.cs#hdr)]

## CMYK

`Cmyk8` and `Cmyk16` hold ink amounts, 0 meaning no ink. Until Egami applies ICC profiles, they convert to and from RGB with the
plain formula, red = (1 − C)(1 − K) — what the JPEG reader has always used — so print-accurate color needs the file's profile.

[!code-csharp[](../../../tests/Egami.DocSamples/PixelTypes.cs#cmyk-inks)]

Moving pixels (crop, pad, flip, turning by 90°) copies inks. Resize, blur, sharpen, convolution and free rotation filter the four
inks as numbers, so black stays in the black channel. Color adjustments are defined on RGB, so they convert to RGB and back and
rebuild black from the formula. `DrawImage` onto a CMYK raster turns the source's color into inks and blends inks.

## Premultiplied alpha

The `Premul` types store each color channel multiplied by alpha, as Skia and GPU surfaces do. Their conversions still take and
return straight colors, so loading, saving and converting work as for any type. Resize, blur, convolution, rotation and
`DrawImage` run on the stored values directly, without multiplying and dividing per pixel: on translucent images resize is about
30% and `DrawImage` about 35% faster than on `Rgba8`.

[!code-csharp[](../../../tests/Egami.DocSamples/PixelTypes.cs#premultiplied-surface)]

Unpremultiplying loses precision where alpha is low (a color at alpha 10 has 11 levels), so keep straight types for images you
will edit color by color.

## Packed types

[!code-csharp[](../../../tests/Egami.DocSamples/PixelTypes.cs#packed-framebuffer)]

`Bgr565`, `Bgra4444` and `Rgb10A2` keep their bits in `Value`, with level constructors and properties for each channel. BMP
files with 5-6-5 or 4-4-4-4 bit fields read straight into them.

## Your own pixel type

Implement `IPixelType<TSelf>` on an unmanaged struct. `Layout` says how it is stored: a packed layout gives its pixel size and
channel widths with `new PixelLayoutInfo(order, PixelChannelType.Packed, alpha, bitsPerPixel, widths...)`, and a layout Egami
has no name for uses `PixelChannelOrder.Other`. The 16-bit members `ToRgba16` and `FromRgba16` have default bodies; override them
with exact integer code if your type is wider than 8 bits. See [custom operations and formats](extending.md).

## Run it

[`samples/PixelTypes`](https://github.com/inexin/Egami/tree/main/samples/PixelTypes) is a console app that does what this guide
shows on the bundled images: `dotnet run --project samples/PixelTypes` writes its results to `./output`.
