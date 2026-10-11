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

## Formats

A format is a `FormatInfo`: a name, extensions, a signature check, a reader and a writer.

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#custom-format)]

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#use-custom-format)]

Readers read only through `EgamiInput`, which keeps them inside the safety limits:

| Member | Use |
| --- | --- |
| `Peek`, `Read`, `ReadExactly`, `Skip`, `ReadByte`, `ReadUInt16/32…` | Read the data; a short read throws `CorruptDataException` |
| `CreateRaster<TPixel>(width, height)` | The raster you decode into, size-checked before any allocation |
| `AddFrame(raster)` | Another frame for animated formats, counted against `MaxFrames` and the memory budget |
| `RentScratch(bytes)` | Working memory counted against the same budget |
| `CheckMemory`, `CheckFrameCount`, `CheckMetadataBytes` | Check a number from the header before acting on it |
| `Warn(raster, id, text)` | Note something a lenient read recovered from, with a stable id such as `Pcx.PaletteMissing` |
| `Corrupt(offset, problem)` | The exception to throw for data that breaks the format's rules |

Respect `ReaderOptions.Recovery`: in `Lenient` mode keep what decoded and warn; in `Strict` mode throw. The writer gets the
raster and a stream; options come as your own `WriterOptions` subclass.

### Streaming

Two optional interfaces let a format stream. A reader that also implements `IRowFormatReader` returns a `RowReader<TNatural>`
from `Open`: the open image hands out rows as `RasterDecoder` asks for them (top-down, never going back within a frame) and
moves through frames with `NextFrame`. The decoder applies `Region`, `MinimumSize` and the pixel conversion after it. A writer
that also implements `IRowFormatWriter` returns a `RowEncoder<TPixel>` from `Start`, which takes rows (or frames, for an
animation) one at a time; `Save`, `SaveAsync`, `RasterEncoder` and `Transcode` then all write through it. When streaming,
`EncodeStart.Whole` is `null`: an option that needs every pixel before the first byte should call `EncodeStart.Refuse`.

[!code-csharp[](../../../tests/Egami.DocSamples/Extending.cs#row-format)]

A format without them still works everywhere: the decoder reads the whole image with `Read` and serves rows from it, and the
encoder gathers the image and calls `Write` at the end.

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

## Run it

[`samples/Extending`](https://github.com/inexin/Egami/tree/main/samples/Extending) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/Extending` writes its results to `./output`.
