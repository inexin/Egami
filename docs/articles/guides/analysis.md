# Comparing and measuring images

Calls that look at images rather than change them: comparison metrics and a picture of the differences, per-channel statistics
and histograms, unique colors, opacity and gray checks, perceptual hashes for near-duplicates, and a pixel signature. They take
any pixel type and match ImageMagick's numbers (Python's imagehash for the hashes). Types live in `Egami.Analysis`.

## Compare two images

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#compare)]

`Raster.Compare` reads both images once and returns a `CompareResult`: for red, green, blue, alpha and overall, a
`ChannelDifference` with the mean absolute
error, mean squared error, RMSE, PSNR (+∞ for equal images) and the peak error, all on the 0 to 1 scale, so 8-bit and 16-bit
results line up; and `DifferentPixels`, the pixels with a channel off by more than `Fuzz` (ImageMagick's AE). `Overall` averages the
color channels, plus alpha when either image has it, as ImageMagick's "all" line.

The images must be the same size but may have different pixel types: an `Rgb8` and an `Rgba8` copy of one picture are identical.
Color profiles are not reconciled, so convert both with `ConvertColor` first when they differ.

**Alpha.** Colors are premultiplied by alpha first (`AlphaCompare.Premultiplied`, the default), so two fully transparent pixels are
equal whatever color is stored under them, which is what a viewer sees and what ImageMagick 7's metrics do. `AlphaCompare.Straight`
compares the stored channels.

**SSIM.** With `Ssim = true`, each channel also gets the structural similarity of Wang et al. (2004) with an 11 × 11 Gaussian window
(σ 1.5) and DSSIM = (1 − SSIM) / 2. It costs about as much as four blurs. ImageMagick 7.1 computes SSIM on straight channels, so
use `AlphaCompare.Straight` to reproduce its numbers on images with transparency.

`Region` compares one rectangle and `Frame` another frame. Results do not depend on the thread count.

## Visual regression tests

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#visual-regression)]

## A picture of the differences

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#difference)]

`DifferenceStyle.Highlight` is ImageMagick compare's picture: pixels that differ in `HighlightColor` (#F1001E) and the rest in
`LowlightColor` (white), with 20% of the first image, flattened onto white, showing through (`LowlightOpacity`). Set
`LowlightOpacity = 0` for a plain mask. `DifferenceStyle.Absolute` shows |a − b| per color channel, times `Amplify`.

## Statistics

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#statistics)]

`Statistics` returns a `RasterStatistics` with a `ChannelStatistics` for red, green, blue, alpha and the Rec. 709 luma: the minimum, maximum, mean, sample standard deviation,
skewness, excess kurtosis and entropy, as ImageMagick's identify -verbose reports them. Entropy is the Shannon entropy of the values
at 16-bit precision divided by its largest possible value for the number of distinct values present, so it is 1 when every
distinct value is equally common.

## Histograms

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#histogram)]

`Histogram` counts pixels in equal bins over 0 to 1 (256 by default, one per 8-bit value) for one channel or the luma.
`Percentile(p)` and `Median` return the level of the bin where the running count reaches that fraction.

## Colors, opacity and gray

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#colors)]

`CountColors` counts the distinct stored values exactly, as ImageMagick's %k. Pixel types of up to 3 bytes use a 2 MB table;
wider types keep a set of the colors seen, and its worst case (every pixel distinct, about 24 bytes a pixel) counts against
`MaxTotalBytes`. `GetColors(limit)` returns the colors as `Rgba16` when there are at most `limit`, and stops scanning once there are
more. `IsOpaque` and `IsGray` stop at the first pixel that fails and answer at once for types without alpha and gray types; both
check every frame unless given one.

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#grayscale-check)]

## Near-duplicates

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#hashes)]

Keep the 64-bit hashes in a database column; a lookup within a few bits finds recompressed and resized copies.

`PerceptualHash.Dct` (pHash style) and `PerceptualHash.Difference` (dHash) reduce the first frame's brightness, flattened onto
white, to 32 × 32 or 9 × 8 with a Lanczos filter and keep one bit per low frequency or neighbour pair. The same picture after JPEG
recompression, resizing or a small brightness change stays within about 6 bits; unrelated photos average about 32 bits apart.
The bits are in reading order, so `ToString("x16")` matches Python imagehash's hex string to within a few bits.

## Signatures

[!code-csharp[](../../../tests/Egami.DocSamples/Analysis.cs#signature)]

`Signature` is the SHA-256 of the size, the frame count and each frame's delay and pixels as 16-bit RGBA, as 64 hex digits: equal
exactly when the pixels are, whatever the file or pixel type. It is not ImageMagick's %#, whose bytes vary between versions.
