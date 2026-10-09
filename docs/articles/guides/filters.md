# Filters

[!code-csharp[](../../../tests/Egami.DocSamples/Effects.cs#filters)]

| Operation | Does |
| --- | --- |
| `Blur(sigma, colorSpace)` | Gaussian blur, separable; exact up to sigma 100, three box passes above |
| `BoxBlur(radius)` | Mean of a square, in constant time per pixel whatever the radius |
| `Sharpen(amount, sigma)` | Unsharp mask: adds `amount` times the difference from a Gaussian blur |
| `Convolve(kernel, edge, preserveAlpha)` | Any `ConvolutionKernel`, separable ones in two passes |

## Edges

`EdgeMode` says what a kernel sees past the border: `Clamp` repeats the edge pixel (the default), `Mirror` reflects the image,
`Wrap` tiles it, and `Transparent` treats it as transparent black.

## Alpha

Blurs work on premultiplied values internally, so transparent pixels do not bleed their color into opaque ones.
`preserveAlpha: true` keeps the alpha channel as it was, for effects that should only change color.

## Run it

[`samples/Filters`](https://github.com/inexin/Egami/tree/main/samples/Filters) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Filters` writes its results to `./output`.
