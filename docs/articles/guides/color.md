# Color

Color operations follow the W3C Filter Effects definitions, so they match what a browser's CSS `filter` does.

[!code-csharp[](../../../tests/Egami.DocSamples/Effects.cs#color)]

| Operation | 1 (or 0°) | Range |
| --- | --- | --- |
| `Brightness(amount)` | No change | 0 gives black; above 1 brightens |
| `Contrast(amount)` | No change | 0 gives flat gray |
| `Saturation(amount)` | No change | 0 gives gray; above 1 oversaturates |
| `HueRotate(degrees)` | No change at 0° and 360° | Any angle |
| `Sepia(amount)` | Full sepia | 0 to 1 |
| `Grayscale(weights, amount, colorSpace)` | Full gray | 0 to 1; `LumaWeights.Rec709` (default) or `Rec601` |
| `Invert(amount)` | Full inversion | 0 to 1 |
| `Opacity(amount)` | No change | 0 to 1, multiplies alpha |

Adjacent color steps are multiplied together and run as one pass over the pixels.

## Color matrices

[!code-csharp[](../../../tests/Egami.DocSamples/Effects.cs#color-matrix)]

`default(ColorMatrix)` is the identity, so an uninitialized matrix changes nothing.

## Encoded or linear

`Grayscale` takes a `ColorSpace`: `Encoded` (the default) weighs the stored sRGB values as most software does; `Linear` weighs
light, which keeps the perceived brightness of saturated colors. Converting a color raster to `Gray8` with `As<Gray8>()` uses
Rec. 709 luma in linear light.

## Run it

[`samples/Color`](https://github.com/inexin/Egami/tree/main/samples/Color) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Color` writes its results to `./output`.
