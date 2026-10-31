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

Saving a raster with more than one frame as GIF, PNG, WebP or JPEG XL writes an animation. The writers work out each frame's stored rectangle,
disposal and blend from the pixels, so an edited animation stays small: only what changed between frames is stored.

GIF holds at most 256 colors per frame and has on-or-off transparency; APNG keeps full color and partial alpha. The [GIF](../formats/gif.md)
and [PNG and APNG](../formats/png.md) pages have each writer's options.

## Frame by frame

`Raster<TPixel>.Frames` is a `FrameList<TPixel>`: index, add, insert, move and remove frames. Each frame's `FrameMetadata` holds
its delay, how it is disposed (`FrameDisposal`) and blended (`FrameBlend`) when written as APNG, and the area that changed.

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#frame-list)]

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#frame-metadata)]

## Animation tools

Six steps work on the whole animation rather than on each frame. They run in `Apply`, `Derive` and `Transcode` (which then decodes
the whole animation first) and mix with per-frame steps, but not inside `Within`, since a scope is an area of every frame. Frames in
memory are already full pictures of what is on screen, so Egami needs no Coalesce, and the writers find each frame's changed area
themselves, so it needs no Optimize.

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationTools.cs#shrink)]

- `MergeDuplicates(tolerance)` removes each frame equal to the one before it and adds its delay to the frame that stays, as
  ImageMagick's -layers RemoveDups; what is on screen at every moment is unchanged.
- `DropZeroDelay()` removes frames with no delay, which viewers never show, as -layers RemoveZero (nothing, when every delay is zero).
- `Retime(fps)` samples fixed ticks, each showing the frame visible when it starts, as ffmpeg's fps filter, then merges neighbours
  showing the same frame. It never adds frames and keeps the total duration; a rate at or above the animation's own changes
  nothing. Browsers show GIF delays under 20 ms as 100 ms, so 50 fps is the useful ceiling for GIF.

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationTools.cs#speed)]

`ChangeSpeed(factor)` divides every delay, rounded to whole milliseconds; `Reverse()` plays the frames backwards with their delays.

### One animation on another

[!code-csharp[](../../../tests/Egami.DocSamples/AnimationTools.cs#sticker)]

`DrawAnimation` cuts both timelines at every frame boundary of either (boundaries closer than 10 ms snap together, since GIF counts
in hundredths), and each piece shows the base frame and the overlay frame of that moment, drawn as `DrawImage` draws with
`AnimationOverlayOptions.Blend` and `Opacity`, so an overlay at another speed keeps its speed. `Repeat` plays the overlay that many
times (0 loops it for the base's whole length) from `Start`. A still base lasts as long as the overlay plays and takes its loop
count. `AnimationTiming.ByIndex` pairs base frame i with overlay frame i instead, as ImageMagick's -layers Composite does. The
number of frames the result will have is checked against `MaxFrames` and `MaxTotalBytes` before anything is drawn.

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### Make an animated GIF from frames

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#gif-from-frames)]

## Run it

[`samples/Animation`](https://github.com/inexin/Egami/tree/main/samples/Animation) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Animation` writes its results to `./output`.
