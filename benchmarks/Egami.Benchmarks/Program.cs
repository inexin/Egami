using BenchmarkDotNet.Running;

using Egami;
using Egami.Benchmarks;
using Egami.Formats;
using Egami.Pixels;
using Egami.Tests.Memory;

// Run all:      dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*PngDecode*"
// Time the Phase 2.5.4 filter scenarios at one thread and all cores: -- --time-filters [name...]
if (args is ["--time-filters", .. string[] names])
{
    FilterTiming.Run(names);
    return;
}

// Time VP8 decoding (Phase 2.3.5 speed gate) at one thread and all cores: -- --time-vp8 <file.ivf>...
if (args is ["--time-vp8", .. string[] ivfs])
{
    Vp8Timing.Run(ivfs);
    return;
}

// Time JPEG XL decoding (Phase 2.9.3 speed gate) at one thread and all cores: -- --time-jxl <file.jxl>...
if (args is ["--time-jxl", .. string[] jxls])
{
    JxlTiming.Run(jxls);
    return;
}

// Time AVIF loading (Phase 2.9.2 speed gate) at one thread and all cores: -- --time-avif <file.avif>...
if (args is ["--time-avif", .. string[] avifs])
{
    JxlTiming.Run(avifs);
    return;
}

// Encode lossless JPEG XL (Phase 2.9.3 encoder gate), timed: -- --encode-jxl <effort> <in> <out.jxl> [<in> <out.jxl>]...
if (args is ["--encode-jxl", string effort, .. string[] pairs])
{
    JxlTiming.Encode(int.Parse(effort, System.Globalization.CultureInfo.InvariantCulture), pairs);
    return;
}

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

// Report what 1.0 Load, RasterDecoder.ReadRows and a thumbnail Transcode hold at their peak for each file, for the Phase 2.2 memory gates: -- --peak <file>...
// Limits are raised so the large generated images (tools/make-large-images.py) load.
if (args is ["--peak", .. string[] files])
{
    SafetyLimits limits = SafetyLimits.Default with { MaxPixels = 1L << 32, MaxTotalBytes = 1L << 36 };
    foreach (string file in files)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        MemoryPeak peak = PeakMemory.Measure(context => Raster.Load(file, context).Dispose(), limits);
        Console.WriteLine($"{Path.GetFileName(file)}: Load {peak}, {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:N0} ms");
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        RasterAccess access = RasterAccess.Buffered;
        peak = PeakMemory.Measure(
            context =>
            {
                using RasterDecoder decoder = RasterDecoder.Open(file, context);
                access = decoder.Info.Access;
                using Raster<Rgba8> band = Raster.Create<Rgba8>(decoder.Info.Width, 256, context);
                do
                {
                    while (decoder.ReadRows(band) > 0)
                    {
                    }
                }
                while (decoder.NextFrame());
            },
            limits);
        Console.WriteLine($"{Path.GetFileName(file)}: ReadRows ({access}, 256-row Rgba8 band) {peak}, {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:N0} ms");
        start = System.Diagnostics.Stopwatch.GetTimestamp();
        peak = PeakMemory.Measure(context => Raster.Transcode(File.OpenRead(file), Stream.Null, Egami.Formats.Jpeg.JpegFormat.Info, x => x.Fit(1024, 1024), context: context), limits);
        Console.WriteLine($"{Path.GetFileName(file)}: Transcode to a 1,024-pixel JPEG {peak}, {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:N0} ms");
    }

    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
