# Animation

A raster holds one or more frames, all the size of the raster and the same pixel type. GIF and APNG files load with every frame
composited to full size, as a browser shows them, so each frame is a complete picture you can edit on its own.

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#frames)]

- `Frames.Add` and `Frames.Insert` copy the frame you pass, from this raster or another of the same size.
- `FrameMetadata.Delay` is how long a frame shows; `RasterMetadata.LoopCount` is how many times the animation plays, 0 for
  forever.
- Every operation in a chain runs on every frame. `DrawImage` draws its image's first frame onto each one.

## Reading part of an animation

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#first-frame)]

`ReaderOptions.Frames` takes a range: `..1` for the first frame, `^1..` for the last (which has to read every frame to find it),
`2..5` for three in the middle. `Raster.Probe` counts the frames without decoding them.

## Writing animations

Saving a raster with more than one frame as GIF or PNG writes an animation. The writers work out each frame's stored rectangle,
disposal and blend from the pixels, so an edited animation stays small: only what changed between frames is stored.

### GIF

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationAndMetadata.cs#gif)]

GIF holds at most 256 colors per frame. Frames with few enough colors keep them exactly. Others get a palette from Wu's
quantizer, dithered with Floyd–Steinberg by default. GIF transparency is on or off, so pixels below
`PaletteOptions.AlphaThreshold` (128 by default) become transparent.

### APNG

APNG keeps full color and partial alpha, so nothing is quantized; frames blend with `Over` when that stores less. Set
`PngWriterOptions.FirstFrameOnly` to write a still PNG from an animation.
