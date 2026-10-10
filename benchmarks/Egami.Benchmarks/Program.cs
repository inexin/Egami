using BenchmarkDotNet.Running;

using Egami;
using Egami.Benchmarks;
using Egami.Pixels;
using Egami.Tests.Memory;

// Run all:      dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*PngDecode*"
// Write the benchmark photo as PNG, for tools/make-jpeg-bench-files.py: -- --write-photo <path>
if (args is ["--write-photo", string path])
{
    using Raster<Rgba8> photo = BenchImages.Photo(4000, 3000);
    photo.Save(path);
    return;
}

// Write the camera stand-ins and the thumbnail file, for tools/make-jpeg-bench-files.py: -- --write-camera-files <dir>
if (args is ["--write-camera-files", string directory])
{
    CameraFiles.Write(directory, BenchFiles.Jpeg("photo-420.jpg"));
    return;
}

// Report what 1.0 Load holds at its peak for each file, for the Phase 2.2 memory gates: -- --peak <file>...
// Limits are raised so the large generated images (tools/make-large-images.py) load.
if (args is ["--peak", .. string[] files])
{
    SafetyLimits limits = SafetyLimits.Default with { MaxPixels = 1L << 32, MaxTotalBytes = 1L << 36 };
    foreach (string file in files)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        MemoryPeak peak = PeakMemory.Measure(context => Raster.Load(file, context).Dispose(), limits);
        Console.WriteLine($"{Path.GetFileName(file)}: Load {peak}, {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:N0} ms");
    }

    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
