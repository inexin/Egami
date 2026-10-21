# Tone and color

Egami has the tone and color tools photo editors have: levels, gamma, curves and sigmoidal contrast; posterize, solarize,
colorize, tint, duotone, thresholds and an accessibility filter; 3D lookup tables from .cube files and Hald images; and the
adjustments that read the image first, from auto levels to white balance, equalization, CLAHE and local contrast.

The tone steps follow ImageMagick's formulas, with values from 0 to 1 where ImageMagick takes percentages: `Level(0.1f, 0.9f)` is
`-level 10%,90%`. They work on the stored, gamma-encoded values, as photo editors and LUT files do, change red, green and blue,
keep alpha, and clamp to 0..1 after each step.

## Tone steps

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#tone)]

Levels, an S-curve and a tint run as one pass over the pixels.

| Step | Does | ImageMagick |
| --- | --- | --- |
| `Level(black, white, gamma, outputBlack, outputWhite)` | Stretches black..white to outputBlack..outputWhite with a midtone gamma | `-level`, `+level` |
| `Gamma(gamma)` | v^(1/gamma): 2.2 brightens | `-gamma` |
| `SigmoidalContrast(contrast, midpoint, increase)` | An S-curve that adds contrast without clipping, or its inverse | `±sigmoidal-contrast` |
| `Posterize(levels, dither)` | Rounds each channel to evenly spaced levels, optionally with ordered or Floyd–Steinberg dithering | `-posterize` |
| `Solarize(threshold)` | Inverts values above the threshold | `-solarize` |
| `Threshold(level, mode, below, above)` | Two colors, by luminance, saturation, chroma or each channel (`ThresholdMode`) | `-threshold` |
| `Colorize(color, amount)` | Blends toward a color | `-colorize` |
| `Tint(color, amount)` | Pushes the midtones toward a color, keeping black and white | `-tint` |
| `Duotone(shadows, highlights)` | Maps luminance onto a line between two colors | `-grayscale` then `+level-colors` |
| `HighContrast(grayscale, invert, contrast)` | Skia's high-contrast accessibility filter (`ContrastInvert`) | — |
| `SimulateColorBlindness(kind, severity)` | Machado, Oliveira and Fernandes (2009), from mild anomaly to full -opia (`ColorVisionDeficiency`) | — |

Every one of these matches ImageMagick 7 to within one level at 8 bits, and thresholds are identical, including where a pixel lies
exactly on the level. Adjacent tone steps, color matrices and filters fuse into one pass over the pixels; on 8- and 16-bit
images a fused chain is a lookup table per channel, rounded between steps, so it gives exactly the pixels the steps give one by one.

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#effects)]

## Color filters

A `ColorFilter` is a color adjustment as a value, as Skia's color filters are: a function of one pixel's color, with no
neighbours, position or statistics. Every tone step and color matrix has a factory; `Curves` takes `ToneCurve`s, `FromLut` a 3D
table; `Then` chains filters and `Lerp` mixes two, so a look can be applied at any strength. Run one with `ApplyFilter`, on your
own pixels with `Apply`, or save it as a .cube file with `ToLut3D`.

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#color-filter)]

`ToneCurve.FromPoints` draws a curve as photo editors do, a monotone cubic through the points (Fritsch and Carlson) that never
overshoots; `FromTable` and `FromFunction` take a table or a function.

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#curves)]

## 3D lookup tables

A `Lut3D` is an N × N × N grid of output colors, with an optional 1D shaper before it. `CubeFile` reads and writes .cube files
(Adobe's 1.0 specification and DaVinci Resolve's 1D-plus-3D form); the reader is strict and checks the limits before it allocates.
`Lut3D.FromHald` and `ToHald` read and write Hald CLUT images, and `Raster.HaldIdentity` makes the identity to grade in an editor.
`LutInterpolation.Tetrahedral` (the default, as Resolve and OpenColorIO read tables) keeps grays neutral; `Trilinear` matches
ImageMagick's `-hald-clut` to within a level.

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#cube)]

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#hald)]

## Adjustments that read the image

`AutoLevel`, `ContrastStretch`, `Normalize`, `AutoGamma`, `AutoWhiteBalance`, `Equalize` and `AutoThreshold` measure the image
before they change it: the chain runs up to the step, the step measures the result, and it becomes a plain filter for the rest of
the chain. Inside `Within` or `OnChannels` they measure only the scope; transparent pixels do not count. An animation is measured
as a whole, so its frames get one correction and do not flicker; `perFrame: true` measures each frame alone, as ImageMagick does.
Black and white points, gammas and threshold levels equal ImageMagick's.

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#auto)]

`linked: true` (the default) gives red, green and blue one pair of points, which keeps colors; `false` gives each channel its own,
which also removes a cast. Each step has a `Raster.Measure…` helper that returns the filter without changing anything, to apply
one correction to many images.

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#measure)]

## White balance

`WhiteBalance(temperature, tint)` corrects for a light of a known color temperature with the Bradford transform, as a raw
editor's sliders do; `WhiteBalance(neutral)` makes a picked color gray; `AutoWhiteBalance` finds the cast itself
(`WhiteBalanceMethod.GrayWorld`, ImageMagick's `-white-balance`, or `WhitePatch`). Both fixed corrections are also filters,
`ColorFilter.WhiteBalance(neutral)` and `ColorFilter.WhiteBalance(temperature, tint)`, so they can join a chain or a paint.

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#white-balance)]

## Equalization, CLAHE and local contrast

`Equalize` spreads the values evenly: of L* by default, so colors hold (`EqualizeMode.Luminance`), or of each channel as
ImageMagick's `-equalize` (`EqualizeMode.Channels`). `Clahe` equalizes each tile of the image with a clipped histogram and blends
the tiles smoothly (Zuiderveld's CLAHE, on L* as ImageMagick runs it); `LocalContrast` is ImageMagick's `-local-contrast`. Both
look at neighbours, and each frame is its own.

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#equalize)]

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#clahe)]

## Thresholds

`AutoThreshold` finds the level with Otsu's, the triangle or Kapur's method (`ThresholdMethod`), as ImageMagick's
`-auto-threshold`; `Raster.FindThreshold` returns the level alone. `AdaptiveThreshold` compares each pixel with the mean of the
window around it (Bradley and Roth), for pages under uneven light.

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#threshold)]

[!code-csharp[](../../../tests/Egami.DocSamples/Tone.cs#auto-threshold)]

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### Tint a photo

[!code-csharp[](../../../tests/Egami.DocSamples/Blending.cs#tint)]
