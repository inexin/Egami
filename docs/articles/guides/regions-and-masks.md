# Regions, masks and channels

Any chain that keeps the image's size can run on part of the image: inside a rectangle with `Within(Rect, …)`, or through a gray
mask with `Within(Mask, …)`. The inner chain is the same lambda `Apply` and `Derive` take.

## A rectangle

[!code-csharp[](../../../tests/Egami.DocSamples/Scopes.cs#within-rect)]

The blur reads the real pixels around the rectangle, so its edge has no seam.

- Only pixels inside the rectangle change. It is clipped to the image; one that misses it changes nothing.
- Filters read the real pixels around the rectangle, so a blurred rectangle has no seam: inside, it equals the whole-image blur.
- Positions inside the scope (DrawImage, FillColor) are the image's coordinates, as outside it.
- Flips and a 180° turn mirror the rectangle itself.
- A step that changes the size (resize, crop, pad, a 90° or free turn, AutoOrient) or the color encoding (ConvertColor, ToneMap)
  throws `ArgumentException` when the chain is recorded, naming the step, before any pixel changes.
- Custom operations work inside a scope without changes: they run on a copy of the area and see it as their whole image.

## A mask

[!code-csharp[](../../../tests/Egami.DocSamples/Scopes.cs#within-mask)]

A soft mask gives a soft transition; blur the mask raster first to feather a hard selection.

A `Mask` reads coverage from any raster: a one-channel raster's value (gray, or `Alpha8`'s alpha) by default, otherwise its alpha,
otherwise its luminance; `MaskChannel` picks a channel explicitly. `Offset` places it, `Inverted` flips it, and outside the raster
coverage is 0 (full when inverted).

- The result is mixed with the original by coverage on premultiplied pixels: 0 keeps the original exactly, full keeps the result
  exactly, and a transparent result leaves no color fringe.
- Only the box around the mask's non-zero coverage is touched, found once when the chain is planned.
- Coverage is 8-bit for 8-bit images and float for 16-bit and float ones, so a 16-bit mask keeps its gradient on a 16-bit photo.
- Masking an image with itself reads the mask from a copy.
- A soft edge is the mask's own business: blur the mask raster first for a feathered selection.

## Channels

`ExtractChannel` pulls one channel (`ColorChannel`: red, green, blue, alpha or luminance) out as a gray raster, `Gray8` from 8-bit
images, `Gray16` from 16-bit ones and `GrayF32` from float ones; `CombineChannels` builds an image from three or four gray rasters.
Both equal ImageMagick's -separate and -combine at 8 and 16 bits.

[!code-csharp[](../../../tests/Egami.DocSamples/Channels.cs#extract-combine)]

`ShuffleChannels` rearranges channels in place, each output channel taking an input channel or a constant (`ChannelSource`). It is a
color matrix, so it fuses with Brightness, Contrast and the other color steps.

[!code-csharp[](../../../tests/Egami.DocSamples/Channels.cs#shuffle)]

`OnChannels` is a scope like `Within`: the chain runs, and its result is kept only in the chosen channels (`ColorChannels`). Color
steps alone become one matrix with the other channels left alone, so they cost nothing extra.

[!code-csharp[](../../../tests/Egami.DocSamples/Channels.cs#on-channels)]

## Alpha

`AlphaToGray` shows alpha as gray (ImageMagick's -alpha extract), `LuminanceToAlpha` turns brightness into alpha (-alpha copy),
`CopyAlpha` takes alpha from a mask, and `Flatten` composites onto a background color and makes the image opaque (-alpha remove). The
JPEG writer's Background option flattens with the same arithmetic.

[!code-csharp[](../../../tests/Egami.DocSamples/Channels.cs#alpha)]

## Nesting

Scopes nest: a rectangle inside a rectangle is their intersection, a mask inside a mask multiplies the coverage, and rectangles and
masks combine in either order.

## Cost

A rectangle costs nothing beyond the work inside it; a filter reads only the band of rows and columns around the area its kernel
reaches. A mask adds one mixing pass; a chain of one per-pixel step mixes as it runs, and other chains keep the original area in one
scratch plane, counted against `MaxTotalBytes`.
