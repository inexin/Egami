# Edges and morphology

Edge operators measure how fast the image changes at each pixel; Canny's detector turns that into thin edge lines; and
morphology grows, shrinks and cleans up shapes by taking the maximum or minimum over a neighbourhood. All of them keep the
image's size, so they work inside `Within` scopes.

| Operation | Does |
| --- | --- |
| `Edges(op, grayscale)` | The gradient magnitude of a fixed kernel, 0 to 1; gray by default, or per channel |
| `CannyEdges(sigma, low, high)` | Thin white edges on black, as ImageMagick's `-canny` |
| `Morphology(op, shape, iterations)` | Dilate, erode, open, close, gradient, top hat or bottom hat over a `StructuringElement` |
| `Dilate(radius)`, `Erode(radius)` | The maximum or minimum over a square; `Dilate(rx, ry)` and `Erode(rx, ry)` over a rectangle |

## Find edges

[!code-csharp[](../../../tests/Egami.DocSamples/EdgesAndMorphology.cs#edges)]

`EdgeOperator` picks the kernels. Every operator is scaled so a full black-to-white step gives 1, so their results can be
compared and thresholded alike.

| Operator | Kernels | Notes |
| --- | --- | --- |
| `Sobel` (default) | 3 × 3 pair, weights 1, 2, 1 | The usual choice |
| `Scharr` | 3 × 3 pair, weights 3, 10, 3 | Its strength depends least on the edge's direction |
| `Prewitt` | 3 × 3 pair, weights 1, 1, 1 | |
| `RobertsCross` | 2 × 2 diagonal differences | Sharpest and noisiest; the pixel and the one below right |
| `Kirsch`, `Robinson` | Eight 3 × 3 compass kernels | The strongest of the eight directions |
| `Laplacian3x3`, `Laplacian5x5` | One kernel, absolute value | Second derivative: edges come out as double lines |
| `LaplacianOfGaussian` | 5 × 5, absolute value | A Laplacian smoothed against noise |

Gradient pairs write `sqrt(gx² + gy²)`; the compass operators keep the largest of their eight responses. The edges are
measured on premultiplied values, so transparent areas count as black, and alpha is kept. With `grayscale: true` (the
default) the pixels are first reduced to Rec. 709 luma and the magnitude is written as gray; with `false` red, green and blue
each get their own. Past the border the edge pixel repeats.

## A selection from Canny

[!code-csharp[](../../../tests/Egami.DocSamples/EdgesAndMorphology.cs#canny)]

`CannyEdges` runs the four steps of Canny's detector: a Gaussian blur of the luma with standard deviation `sigma`, the
gradient from Canny's own 2 × 2 first differences, non-maximum suppression (a pixel is kept only if neither neighbour across
its edge is stronger), and hysteresis: pixels at least `high` times the image's strongest gradient start edges, and edges
follow 8-connected pixels down to `low` times it. The output is white edges, one pixel wide, on black, with alpha kept.

The parameters mean what ImageMagick's `-canny 0x{sigma}+{low × 100}%+{high × 100}%` means, and the edge maps match it pixel
for pixel except where ImageMagick stops a trace early: it keeps its trace stack in one image row, so a trace deeper than the
image is wide ends part way. Egami follows every connected pixel, so it finds the same edges plus the rest of those traces.

A gray edge map is a `Mask` as it is. A color raster used as a `Mask` is read by its alpha, so for an RGBA edge map pass
`MaskChannel.Luminance`: `new Mask(edges, Channel: MaskChannel.Luminance)`.

## Clean a scanned form with Close

[!code-csharp[](../../../tests/Egami.DocSamples/EdgesAndMorphology.cs#morphology)]

Morphology is flat: dilation takes each channel's maximum over the shape, erosion the minimum, on the stored values, so 8 and
16-bit results are exact. The rest combine them:

| `MorphologyOp` | Is | Use |
| --- | --- | --- |
| `Dilate` | Maximum | Grow light areas, close dark gaps |
| `Erode` | Minimum | Shrink light areas, remove light specks |
| `Open` | Erode, then dilate | Remove light details smaller than the shape; never brightens |
| `Close` | Dilate, then erode | Remove dark details smaller than the shape; never darkens |
| `Gradient` | Dilate minus erode | Outlines as wide as the shape |
| `TopHat` | Image minus its open | The light details the open removed |
| `BottomHat` | Its close minus the image | The dark details the close removed |

`iterations` repeats each dilation and erosion: `Open` with 2 erodes twice, then dilates twice, as ImageMagick's `Open:2`.
Alpha is dilated and eroded with the color, so a logo's shape grows and shrinks with it; `Gradient`, `TopHat` and
`BottomHat` keep alpha, since a difference of alpha would make an opaque image transparent. Past the border the edge pixel
repeats, as ImageMagick does.

## Shapes

[!code-csharp[](../../../tests/Egami.DocSamples/EdgesAndMorphology.cs#shapes)]

The factories make ImageMagick's kernels of the same name, so `-morphology Dilate Disk:4` and
`Morphology(MorphologyOp.Dilate, StructuringElement.Disk(4))` give identical pixels. `Cross(r)` is ImageMagick's `Plus:r`.

| Shape | Cells |
| --- | --- |
| `Square(r)` | The (2r + 1) square |
| `Rectangle(rx, ry)` | The (2rx + 1) × (2ry + 1) rectangle, Skia's dilate and erode radii |
| `Disk(r)` | x² + y² ≤ r² |
| `Diamond(r)` | \|x\| + \|y\| ≤ r |
| `Cross(r)` | The row and column through the origin, 2r + 1 long |
| `FromMask(cells, origin)` | Any set of cells, `[y, x]`, with the origin on the pixel being computed |

Erosion reads the cells as offsets from the pixel and dilation reads them mirrored, as ImageMagick does, so `Open` and
`Close` work as expected for lopsided shapes too. Squares and rectangles cost the same per pixel whatever their size (van Herk and
Gil-Werman's running maximum down the columns); other shapes cost in proportion to their height.

[!code-csharp[](../../../tests/Egami.DocSamples/EdgesAndMorphology.cs#dilate-erode)]
