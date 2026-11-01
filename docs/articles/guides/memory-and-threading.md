# Memory and threading

## The context

[!code-csharp[](../../../tests/Egami.DocSamples/MemoryAndSecurity.cs#context)]

A raster remembers the context it was loaded or created with, and its operations use it. `EgamiContext.Default` serves every call
that passes none; `EgamiContext.SetDefault` replaces it once, at startup, before anything reads it.

## Threads

Operations split the image into bands of rows and run them on up to `MaxParallelism` threads (the processor count by default).
Small images run on one thread, where splitting would cost more than it saves. Decoding is mostly sequential by nature; JPEG
splits files at their restart markers, pipelines progressive scans and converts in parallel bands, and animated formats map
frames in parallel. PNG decompresses a large image on a second thread while the first reverses the filters and converts the
rows, and compresses large images on several threads when `PngWriterOptions.ParallelCompression` is set, with the same bytes at
any thread count.

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
needed, or the frame or image is done, and `RenderPreview` draws an interlaced PNG, a progressive JPEG, a GIF frame, a layered AVIF
or a JPEG XL still as far as it has arrived. Files that are decoded into a buffer first (such as bottom-up BMP and TGA) wait for the end.

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#incremental)]

On a server, `RasterDecoder.OpenAsync` reads an asynchronous stream the same way, and `LoadAsync` from a stream that cannot seek,
such as a request body, no longer holds the whole file while it decodes:

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#async)]

`RasterEncoder` writes the other way: a band of rows or a frame at a time, into a file or stream, with the bytes `Save` writes.
Options that need every pixel before the first byte, such as an automatic palette, PNG's `Reduce` or choosing alpha by looking
at the pixels, throw `NotSupportedException` at `Create` and say what to set instead. BMP and TGA store rows bottom-up: on a
stream that cannot seek they are written top-down, as is run-length TGA, and an APNG there needs `PngWriterOptions.FrameCount`.
An interlaced PNG (`PngWriterOptions.Interlace`) holds the rows until the last one, since its first pass needs the whole height.

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#encode-rows)]

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#encode-frames)]

`WriteRowsAsync` and `CompleteAsync` write each band out asynchronously, and `SaveAsync` now does the same instead of
encoding the whole file into memory first:

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#encode-async)]

`Raster.Transcode` joins the two: rows go from the decoder through a short chain of operations into the encoder, so a thumbnail
of a 20,000 × 20,000 PNG holds well under a megabyte of pixels instead of 1.6 GB. When the chain starts with `Fit`, `Cover` or
`Resize`, the decoder already shrinks by a whole factor (`MinimumSize`), and a leading `Crop` becomes `Region`. The chain may
crop, resize, mirror left to right and adjust colors pixel by pixel; anything that needs rows out of order or the whole image,
such as `Blur` or `Rotate`, throws `NotSupportedException` before anything is written.

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#transcode)]

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#transcode-async)]

[!code-csharp[](../../../tests/Egami.DocSamples/Streaming.cs#transcode-progress)]

## Pixel memory

Pixel memory never lives on the garbage-collected heap as large arrays. `PooledPixelMemorySource.Shared` hands out pooled arrays
for small blocks and 64-byte-aligned unmanaged memory for large ones, keeps up to 128 MB of freed blocks for reuse, and splits very
large images into blocks of whole rows. `Trim()` frees what it keeps.

You can plug in your own source through `EgamiContext.Memory` by implementing `IPixelMemorySource`.

## Finding leaks

[!code-csharp[](../../../tests/Egami.DocSamples/MemoryAndSecurity.cs#usage)]

`LiveBlocks` and `LiveBytes` are always counted, at the cost of one atomic addition per allocation. `TrackLeaks` gives each new
block a small finalizable tracker, so turn it on only while you look; `CaptureStackTraces` makes allocations much slower still.
`LeakDetected` runs on the finalizer thread. Leaks come only through `LeakDetected`, never through the
[diagnostics](../diagnostics.md) handler, which reports what one call found.

## Your own memory source

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#memory-source)]

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#counting-source)]

[!code-csharp[](../../../tests/Egami.DocSamples/ApiExamples.cs#leak-detected)]

## Run it

[`samples/MemoryAndThreading`](https://github.com/inexin/Egami/tree/main/samples/MemoryAndThreading) is a console app that does what this guide shows on the bundled
images: `dotnet run --project samples/MemoryAndThreading` writes its results to `./output`.
