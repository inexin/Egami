# Custom operations and formats

Everything here uses only Egami's public API; Egami's own test suite has an outside project that does each of these things.

## Operations

An operation implements `IRasterOperation`: it says what size it makes and fills the destination rows.

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#custom-operation)]

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#use-custom-operation)]

- `OutputSize` is asked while the chain is planned, before any pixel changes. Throw there for arguments that cannot work.
- When the size stays the same, `Source` and `Destination` may be the same plane (`IsInPlace`); read a row before you write it.
- `ForEachBand` runs bands of rows on up to `EgamiContext.MaxParallelism` threads and checks the cancellation token between
  bands.
- A step that changes the size updates the EXIF dimensions and drops the thumbnail, as the built-in resizes do.
- `TPixel.Layout` tells you the pixel type, so you can take a fast path for the types you know and fall back to `ToScaled` and
  `FromScaled` for the rest. Check `Layout.Alpha` before you assume straight alpha.

To run a chain on part of a larger picture, such as a tile or a canvas layer, pad the part by how far the chain reads:
`RasterOps.Reach` returns that as an `OperationReach`, or `null` when a step reads the whole image (a resize, a turn, a custom
operation).

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#reach)]

## Formats

A new file format plugs in through a `FormatInfo` with your own reader and writer; [Your own format](../formats/custom.md)
shows how.

## Pixel types

A pixel type is an unmanaged struct implementing `IPixelType<TSelf>`. Its `Layout` says how it is stored: a layout Egami knows
(`Rgba` with `Unorm8`, say) lets Egami take its fast paths; anything else uses `PixelChannelOrder.Other`, and Egami converts it
only through its own `ToScaled` and `FromScaled`.

## Wrapping memory

`Raster.Wrap` puts a raster over memory you own, without copying: a `Memory<TPixel>` with a stride in pixels, or a `Memory<byte>`
with a stride in bytes, such as a video frame whose rows are padded. Pass an `IDisposable` owner to have the raster release the
memory when it is disposed.

## Rows and planes

For a one-off pass over pixels, `ForEachRow` runs a lambda on each row in parallel; no operation class needed.

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#row-action)]

Each frame's pixels are a `PixelPlane<TPixel>`: rows of `Span<TPixel>`, and the whole frame as one span when it is stored in one
block.

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#pixel-plane)]

`RasterOps` records each step of a chain; built-in steps and your own mix freely.

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#raster-ops)]

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#row-band-operation)]

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### Interop with raw buffers

[!code-csharp[](../../../tests/Egami.DocSamples/Cookbook.cs#raw-buffers)]

## Run it

[`samples/Extending`](https://github.com/inexin/Egami/tree/main/samples/Extending) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Extending` writes its results to `./output`.
