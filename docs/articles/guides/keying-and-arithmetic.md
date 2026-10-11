# Keying and arithmetic

Operations that ImageMagick users expect: making a color transparent, replacing a color, flood fill, arithmetic with a constant,
and stacking several images. Each matches its ImageMagick option exactly at 8 and 16 bits.

## Color tolerance

Keying compares colors with ImageMagick's -fuzz distance on the stored values: a tolerance from 0 (the exact color) to 1 (every
color). Alpha counts three times as much as each color channel, and colors count in proportion to how visible both pixels are, so
two fully transparent pixels always match.

## Make a color transparent

[!code-csharp[](../../../tests/Egami.DocSamples/Keying.cs#green-screen)]

`MakeTransparent` clears alpha where a pixel lies within the tolerance and keeps its color, as ImageMagick's -transparent does.
`softness` ramps alpha back up over the next band of distance, for antialiased edges; with no softness the result is ImageMagick's.

## Replace a color

[!code-csharp[](../../../tests/Egami.DocSamples/Keying.cs#replace)]

## Flood fill

[!code-csharp[](../../../tests/Egami.DocSamples/Keying.cs#flood-fill)]

`FloodFill` fills the region connected to the seed whose colors lie within the tolerance of the seed's own. It spreads to the four
side neighbours by default, as ImageMagick does; `Connectivity.Eight` adds the diagonals. Its visited set, one bit a pixel, counts
against `MaxTotalBytes`. Inside a scope, the region is found within the scope's area.

## Select by color

[!code-csharp[](../../../tests/Egami.DocSamples/Keying.cs#select-by-color)]

`ColorMask` and `FloodMask` return a `Gray8` mask, 255 inside the selection, for `Within(Mask, …)`.

## Arithmetic with a constant

[!code-csharp[](../../../tests/Egami.DocSamples/Arithmetic.cs#arithmetic)]

`Arithmetic` applies a `PixelMath` operator (add, subtract, multiply, divide, pow, log, min, max, set) to the chosen channels, on
values scaled 0 to 1 and clamped. 8-bit and 16-bit pixels are evaluated as 16-bit samples and rounded as ImageMagick's Q16 build
rounds them, so the result equals -evaluate exactly.

## Stack images

[!code-csharp[](../../../tests/Egami.DocSamples/Arithmetic.cs#stack)]

`Raster.Stack` combines the same channel of the same pixel across two or more rasters (`StackMode`: mean, median, min, max), as
ImageMagick's -evaluate-sequence does for mean, min and max. The median is taken per channel, as Photoshop's median stack does;
ImageMagick's picks the whole pixel whose intensity is the median. Stack works a few rows at a time, holding one row of each input.
