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
