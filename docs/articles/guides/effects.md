# Effects

The editor's effects menu: a vignette, glows and bloom, pixelate, oil paint, Kuwahara, spread, drop and inner shadows. Every effect
keeps the image's size, except `DropShadow`, which by default grows the canvas to hold the shadow.

| Operation | Does |
| --- | --- |
| `Vignette(radius, softness, color, center)` | Darkens or tints outside an ellipse fitted to the image, with a soft edge: ImageMagick's -vignette shape |
| `Glow(sigma, color, spread, inner)` | An outer glow behind the alpha shape, or an inner glow inside it, as an editor's layer style |
| `Bloom(threshold, sigma, intensity)` | Bright areas blurred and screened back in linear light: the photographic glow |
| `Pixelate(cellSize, origin)` | Each cell becomes its premultiplied average |
| `OilPaint(radius, levels)` | Each pixel becomes a pixel of the most common intensity in its square: ImageMagick's -paint |
| `Kuwahara(radius, sigma)` | Each pixel takes its value from the least varied of four squares: ImageMagick's -kuwahara |
| `Spread(radius, seed)` | Each pixel takes a random neighbour's value within the radius: ImageMagick's -spread |
| `DropShadow(dx, dy, sigma, color, options)` | Skia's DropShadow: the alpha moved, blurred and colored, with the image on top |
| `InnerShadow(dx, dy, sigma, color)` | A shadow inside the alpha shape, cast by everything outside it, as an editor's layer style |

## Vignette, glow and bloom

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#vignette)]

- `radius` is a fraction of the half-diagonal: 1 puts the edge of the ellipse through the corners. The ellipse has the image's
  proportions and its center is the image's middle unless `center` moves it.
- `softness` is the share of the radius the edge fades over. The fade is the profile of a blurred edge, computed per pixel from
  the distance to the ellipse, so it costs the same at any softness. While the ellipse stays inside the image the result is
  within 5 levels of ImageMagick's `-vignette` (which blurs a drawn ellipse, so it bends where the ellipse leaves the image).
- The color is mixed source-over on premultiplied values; its alpha is the vignette's strength.

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#glow)]

`Glow` is built from Egami's own steps and gives exactly what the same chain gives by hand: a layer that is the color with the
image's alpha (or 1 − alpha for an inner glow), blurred with `BlurMethod.Fast`, its alpha multiplied by 1 / (1 − spread) and by
the color's alpha; then the image is drawn over the layer, or the layer drawn over the image with `BlendMode.SourceAtop` so it
stays inside the shape. An opaque image has no edge to glow from: glow a logo or a cut-out.

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#bloom)]

`Bloom` is a chain too: each channel's part above `threshold` (stored values), stretched to 0..1 and scaled by `intensity`,
blurred in linear light with `BlurMethod.Fast`, and screened onto the image in linear light, so light adds as it does in a lens.

## Pixelate, oil paint, Kuwahara and spread

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#pixelate)]

Cells start at `origin` and repeat both ways; cells cut by the image's edges average what they hold. The average is taken on
premultiplied values, so transparent pixels do not darken a cell; for Rgba8 and Bgra8 it is computed in whole numbers and rounded
once, so it is exact.

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#oil-paint)]

`OilPaint` follows ImageMagick's `-paint`: a (2r + 1) square window, intensity in 256 bins (fewer with `levels`), and the output is
a whole pixel of the most common bin, picked by ImageMagick's scan order. With 256 levels the result equals ImageMagick's pixel for
pixel at 8 bits; the default 20 levels make broader strokes. The histogram slides along each row.

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#kuwahara)]

`Kuwahara` follows ImageMagick's `-kuwahara radius x sigma`: an optional Gaussian blur with ImageMagick's kernel (σ, out to the
radius), then of the four (r + 1) squares with the pixel at a corner, the one whose luma varies least gives the pixel its value,
sampled at its middle. Summed-area tables make the cost independent of the radius. Results are within a few levels of
ImageMagick's, where near-equal variances may pick another square.

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#spread)]

`Spread` moves no pixel further than the radius. The offsets come from a hash of the seed and the position (SplitMix64), so a seed
gives the same pixels on any machine, at any thread count and inside a scope.

## Drop shadows

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#drop-shadow)]

- The parameters mean what Skia's do: the offset moves the shadow, sigma is the Gaussian's standard deviation (σx, with
  `SigmaY` for a different σy), and the color's alpha is the shadow's opacity. Fractional offsets move the shadow by fractions.
- `Expand` (the default) grows the canvas by 3σ plus the offset on the sides the shadow reaches past the image, so the shadow is
  never cut off; the image moves right and down by what was added on the left and top. ImageMagick's `-shadow` adds round(2σ) on
  every side instead, which cuts about 2% of a shadow's edge. A growing step cannot run inside `Within`; with `Expand = false` the
  size stays and it can.
- `Method` is `BlurMethod.Fast` by default, whose cost does not grow with sigma; `BlurMethod.Exact` uses the Gaussian kernel and is
  within a level of the same chain built from Pad, Offset, ColorMatrix, Convolve and DrawImage.

## Inner shadows

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#inner-shadow)]

`InnerShadow` is the chain written by hand: the color with 1 − alpha, moved by the offset rounded to whole pixels with the
uncovered strips counted as outside, blurred fast, faded by the color's alpha and drawn source-atop. With glows and drop shadows it
covers an editor's layer styles, and on a canvas layer it is a `LayerOptions.Filter`.

## Cookbook

### A drop shadow for a logo

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#logo-shadow)]

The canvas grows to hold the shadow; pass `Expand = false` to keep the size, as inside `Within`.

### Blur a face

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#blur-face)]

Inside `Within`, cells stay on the grid that `origin` sets, and each cell averages all of its pixels, including those outside the
rectangle, so the result inside equals pixelating the whole image there. Start the grid at the rectangle's corner so whole cells
fill it.

### An oil-paint look

[!code-csharp[](../../../tests/Egami.DocSamples/FilterEffects.cs#oil-look)]
