# Memory and threading

## The context

[!code-csharp[](../../../tests/Egami.DocSamples/MemoryAndSecurity.cs#context)]

A raster remembers the context it was loaded or created with, and its operations use it. `EgamiContext.Default` serves every call
that passes none; `EgamiContext.SetDefault` replaces it once, at startup, before anything reads it.

## Threads

Operations split the image into bands of rows and run them on up to `MaxParallelism` threads (the processor count by default).
Small images run on one thread, where splitting would cost more than it saves. Decoding is mostly sequential by nature; JPEG
decodes in parallel bands, and animated formats map frames in parallel.

Rasters are not thread-safe: do not run two chains on the same raster at once. Separate rasters, contexts and loads are
independent, and readers, writers and contexts can be shared freely.

## Streaming

`RasterDecoder` decodes a band of rows or a frame at a time, so an image never has to exist whole:

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#decode-rows)]

`ReaderOptions.Region` decodes only a rectangle and `MinimumSize` a reduced size, in `Load`, `Probe` and the decoder alike.
`RasterInfo.Access` says whether a format streams its rows (`RasterAccess.Streamed`) or is decoded into a buffer first
(`RasterAccess.Buffered`), and
`WorkingBytes` how much the decode holds besides your band:

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#region)]

`Raster.LoadInto` decodes into a raster you already have, and `ReadFrame` gives an animation one composed frame at a time:

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#load-into)]

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#frames)]

`RasterDecoder.OpenIncremental` decodes while the bytes are still arriving: `Append` each chunk as it comes, read the rows
finished so far, and call `EndOfInput` after the last. `Status` (a `DecodeStatus`) says whether rows are ready, more bytes are
needed, or the frame or image is done, and `RenderPreview` draws an interlaced PNG, a progressive JPEG or a GIF frame as far
as it has arrived. Files that are decoded into a buffer first (such as bottom-up BMP and TGA) wait for the end.

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#incremental)]

## Pixel memory

Pixel memory never lives on the garbage-collected heap as large arrays. `PooledPixelMemorySource.Shared` hands out pooled arrays
for small blocks and 64-byte-aligned unmanaged memory for large ones, keeps up to 128 MB of freed blocks for reuse, and splits very
large images into blocks of whole rows. `Trim()` frees what it keeps.

You can plug in your own source through `EgamiContext.Memory` by implementing `IPixelMemorySource`.

## Finding leaks

[!code-csharp[](../../../tests/Egami.DocSamples/MemoryAndSecurity.cs#usage)]

`LiveBlocks` and `LiveBytes` are always counted, at the cost of one atomic addition per allocation. `TrackLeaks` gives each new
block a small finalizable tracker, so turn it on only while you look; `CaptureStackTraces` makes allocations much slower still.
`LeakDetected` runs on the finalizer thread.

## Your own memory source

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#memory-source)]

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#counting-source)]

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#leak-detected)]

## Run it

[`samples/MemoryAndThreading`](https://github.com/inexin/Egami/tree/main/samples/MemoryAndThreading) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/MemoryAndThreading` writes its results to `./output`.
