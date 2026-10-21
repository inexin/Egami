# Filters

[!code-csharp[](../../../tests/Egami.DocSamples/Effects.cs#filters)]

| Operation | Does |
| --- | --- |
| `Blur(sigma, colorSpace)` | Gaussian blur, separable; exact up to sigma 100, three box passes above |
| `Blur(sigma, colorSpace, BlurMethod.Fast)` | Gaussian blur in constant time per pixel at any sigma, for shadows, glows and large blurs |
| `BoxBlur(radius)` | Mean of a square, in constant time per pixel whatever the radius |
| `Sharpen(amount, sigma)` | Unsharp mask: adds `amount` times the difference from a Gaussian blur |
| `Convolve(kernel, edge, preserveAlpha)` | Any `ConvolutionKernel`, separable ones in two passes |
| `Median(radius, includeAlpha)` | The median of each channel over a square: removes salt-and-pepper noise, keeps edges |
| `LensBlur(radius, options)` | An out-of-focus lens: points become discs or polygons |
| `BilateralBlur(spatialSigma, rangeSigma)` | Smooths flat areas, keeps edges |
| `MotionBlur(radius, sigma, angle)` | A streak in one direction, as ImageMagick's -motion-blur |
| `RotationalBlur(degrees, center)` | A spin around a point, as ImageMagick's -rotational-blur |
| `ZoomBlur(amount, center)` | Streaks along rays from a point |
| `AddNoise(kind, amount, monochrome, seed)` | ImageMagick's six noise kinds, the same for a seed at any thread count |
| `Despeckle()` | Crimmins' speckle removal, as ImageMagick's -despeckle |

## Large blurs

`Blur(sigma)` computes the exact Gaussian, so its cost grows with sigma. `BlurMethod.Fast` runs three box passes per axis whose
widths and fractional end weights give exactly the Gaussian's variance (Gwosdek et al., 2011): it costs the same at sigma 5 or 500,
about six times less than the exact blur at sigma 20, and stays within 50 dB PSNR of it from sigma 3 up.

[!code-csharp[](../../../tests/Egami.DocSamples/Filters.cs#fast-blur)]

## Median and despeckle

`Median` works on the stored values, so 8 and 16-bit results are exactly ImageMagick's `-statistic Median`. Its cost per pixel
does not grow with the radius. `Despeckle` is gentler: it nudges values a level at a time toward their neighbours and leaves edges
alone, exactly as ImageMagick's `-despeckle`.

[!code-csharp[](../../../tests/Egami.DocSamples/Filters.cs#median)]

[!code-csharp[](../../../tests/Egami.DocSamples/Filters.cs#despeckle)]

For heavy salt-and-pepper noise use `Median(1)` or `Median(2)` instead; both keep edges, and both give ImageMagick's pixels.

## Lens and bilateral blur

`LensBlur` convolves with an aperture: a disc, or a polygon of 5 to 12 blades turned by `Rotation`. It works in linear light,
since a lens adds light, and `HighlightGain` brightens light above `HighlightThreshold` before the blur, so small lights bloom into
bright discs. It runs by FFT on tiles, so its cost does not grow with the radius.

[!code-csharp[](../../../tests/Egami.DocSamples/Filters.cs#lens-blur)]

`LensBlur` works in linear light, so bright points stay bright discs instead of gray smudges; `Within` with a mask feathers the
edge between the sharp subject and the blurred background.

`BilateralBlur` weighs each neighbour by its distance and by how different its color is, so noise flattens and edges stay. The
color difference is the root-mean-square difference of red, green and blue on 0..1; on gray images the sigmas mean what
ImageMagick's `-bilateral-blur` means, with its intensity sigma 255 times `rangeSigma`.

[!code-csharp[](../../../tests/Egami.DocSamples/Filters.cs#bilateral)]

## Directional blurs

`MotionBlur` takes ImageMagick's parameters: 2⌈radius⌉ + 1 samples along `angle` (clockwise, y down), weighted by a Gaussian of
`sigma`. Egami reads between pixels bilinearly where ImageMagick rounds to the nearest pixel, so diagonal streaks are smoother.
`RotationalBlur` and `ZoomBlur` take about one sample per pixel of path; their center is in pixels, with pixel centers at +0.5.

[!code-csharp[](../../../tests/Egami.DocSamples/Filters.cs#directional)]

## Noise

`AddNoise` adds ImageMagick's `+noise` kinds to red, green and blue, never alpha, with `amount` as ImageMagick's `-attenuate`.
Each value's noise is a hash of the seed, frame and position, so the same seed gives the same pixels at any thread count, and
inside `Within` an area gets the noise the whole image would get there.

[!code-csharp[](../../../tests/Egami.DocSamples/Filters.cs#noise)]

The grain depends only on the seed and the pixel's position, so a batch of frames with the same seed grains alike.

## Edges

`EdgeMode` says what a kernel sees past the border: `Clamp` repeats the edge pixel (the default), `Mirror` reflects the image,
`Wrap` tiles it, and `Transparent` treats it as transparent black. The filters above repeat the edge pixel, as ImageMagick's
default virtual pixel does.

## Alpha

Blurs work on premultiplied values internally, so transparent pixels do not bleed their color into opaque ones.
`preserveAlpha: true` keeps the alpha channel as it was, for effects that should only change color. `Median` and `Despeckle`
keep alpha unless asked; `AddNoise` never touches it.

## Run it

[`samples/Filters`](https://github.com/inexin/Egami/tree/main/samples/Filters) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Filters` writes its results to `./output`.
