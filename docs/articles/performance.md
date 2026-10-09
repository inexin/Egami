# Performance

## Tips that matter

- **Load in the type you will work in.** `Raster.Load<Rgba8>` converts row by row while decoding; loading the natural type and
  converting afterwards touches every pixel twice.
- **Use `Derive` for thumbnails.** It reads the source once and writes only the small result; `Copy` then `Apply` copies the full
  image first.
- **Let JPEG decode small.** `ReaderOptions.MinimumSize` decodes at 1/2, 1/4 or 1/8 scale straight from the DCT.
- **Chain operations.** A chain is planned as a whole: a `Crop` before a resize reads only the crop, adjacent color steps run as
  one pass, and `AutoOrient` before a resize turns only the result.
- **Probe before you load** when you might reject the file.
- **Batch work on one thread per image** (`MaxParallelism = 1`) and run images side by side; for a single large image, leave
  `MaxParallelism` at the processor count.
- **Dispose rasters.** Pixel memory is pooled; disposing returns it at once for the next image.

## Running the benchmarks

```sh
dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*"
dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*SimpleDecode*"
```

The benchmarks use BenchmarkDotNet and generated test images (a 1920 × 1080 photo-like image and a screenshot-like one), so
anyone can reproduce them.

## Egami's numbers

Median times on one machine (24 cores, .NET 10, x64 with AVX2), decoding from memory into the file's natural pixel type:

| Scenario | 1 thread | All cores |
| --- | --- | --- |
| BMP 24-bit decode, 1920 × 1080 | 0.31 ms | 0.37 ms |
| BMP 8-bit palette decode | 1.6 ms | 1.7 ms |
| TGA 32-bit RLE decode | 3.1 ms | 2.9 ms |
| QOI decode, photo | 15.3 ms | 15.6 ms |
| PPM decode | 0.32 ms | 0.39 ms |
| QOI encode, photo | 14.3 ms | 14.4 ms |
| TGA 32-bit RLE encode | 3.6 ms | 3.6 ms |
| BMP 24-bit encode | 0.46 ms | 0.46 ms |

Steady-state decoding and encoding allocate only the result raster: under 2.5 KB of managed memory a call for these formats.
