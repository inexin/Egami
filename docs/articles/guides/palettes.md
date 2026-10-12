# Palettes and dithering

The palette machinery behind the GIF, palette PNG, BMP and TGA writers, opened up: build a palette or bring your own, reduce an
image to it in its own pixel type, get the indices, and pick from the published dithers. Types live in `Egami.Quantization`.

## Reduce colors

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#quantize)]

`RasterOps.Quantize` gives each frame a palette (its own colors when they fit, Wu's quantizer otherwise) and replaces each pixel by
its palette color, keeping the pixel type: an `Rgba16` image stays `Rgba16` with at most `MaxColors` distinct values. The
`PaletteOptions` that the writers take drive it:

- `MaxColors`, `Dither` and `AlphaThreshold`, as in 1.0.
- `Palette`: a fixed palette instead of building one. Every writer that takes `PaletteOptions` honours it, and throws, naming both
  numbers, when it has more colors than the format allows.
- `DitherStrength`, 0 to 1: how much of the error is diffused, or how tall the ordered pattern is.
- `Serpentine`: error diffusion alternates direction from row to row.
- `RefineIterations`: k-means passes (Lloyd's algorithm on every fourth pixel) that polish Wu's palette; they never make it fit worse.
- `ExactMatching`: each color goes to its exact nearest palette color rather than through the 64-level lookup table.
- `Kernel`: a custom `ErrorDiffusionKernel` instead of `Dither`.

The defaults are 1.0's: Wu's palette, Floyd–Steinberg at full strength, left to right, table matching. Every GIF, PNG, BMP and TGA
file Egami wrote before stays the same byte for byte.

## Dithers

`DitherMode` has 1.0's `FloydSteinberg`, `Ordered` (8 × 8 Bayer) and `None`, plus Atkinson, Burkes, Jarvis–Judice–Ninke, Sierra
(three rows, two rows and Lite), Stevenson–Arce and Stucki error diffusion with their published kernels; `Riemersma`, error
diffusion along a Hilbert curve; and `Bayer2x2`, `Bayer4x4`, `Bayer16x16` and `Ordered3x3` patterns. Error diffusion runs row
after row; ordered patterns treat every pixel alike, so animation frames dither the same way. `Posterize` takes the same modes.

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#kernel)]

## Fixed palettes

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#remap)]

`Palette.FromColors` takes up to 256 colors and an optional transparent entry. `RasterOps.Remap` maps onto a palette as
ImageMagick's -remap does, keeping each pixel's alpha when the palette is opaque. `Palette.Build` and `Palette.FromRaster` build
one from every frame of an image; `Nearest` and `NearestIndex` look colors up.

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#build)]

`Palettes` has ready-made ones:

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#ready-made)]

## Indices

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#indexed)]

`Raster.ToIndexed` returns an `IndexedImage`: a byte per pixel and the palette, as a palette PNG would store them. The indices are
pooled, so dispose it.

## Dominant colors

[!code-csharp[](../../../tests/Egami.DocSamples/Palettes.cs#dominant)]

`Raster.DominantColors` returns `WeightedColor`s, largest share first. The image is averaged down to at most
`DominantColorOptions.MaxSamples` pixels, transparent ones left out, and clustered by k-means in Oklab, seeded by Wu's palette so
the result is the same on every run; clusters closer than `MergeDistance` merge. The weights of the colors returned sum to 1.
