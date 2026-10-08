using BenchmarkDotNet.Running;

using Egami;
using Egami.Benchmarks;
using Egami.Pixels;

// Run all:      dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*PngDecode*"
// Write the benchmark photo as PNG, for tools/make-jpeg-bench-files.py: -- --write-photo <path>
if (args is ["--write-photo", string path])
{
    using Raster<Rgba8> photo = BenchImages.Photo(4000, 3000);
    photo.Save(path);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
