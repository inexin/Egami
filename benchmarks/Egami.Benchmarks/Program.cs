using BenchmarkDotNet.Running;

using Egami;
using Egami.Benchmarks;
using Egami.Formats;
using Egami.Pixels;
using Egami.Tests.Memory;

// Run all:      dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*PngDecode*"
// Time the Phase 2.5.7 byte blur's stages at one thread: -- --blur-stages; run one stage n times, for a profiler: -- --blur-one <stage> <n>
if (args is ["--blur-one", string only, string times])
{
    BlurStages.Run(only, int.Parse(times, System.Globalization.CultureInfo.InvariantCulture));
    return;
}

if (args is ["--blur-stages"])
{
    BlurStages.Run();
    return;
}

// Time the Phase 2.5.4 filter scenarios at one thread and all cores: -- --time-filters [name...]
if (args is ["--time-filters", .. string[] names])
{
    FilterTiming.Run(names);
    return;
}

// Time the 2.4.8 image scenarios (JPEG, PNG, resize, compose, YUV, blur): -- --time-image [--one-thread|--threads=N] [--runs=N] [--list] [name...]
if (args is ["--time-image", .. string[] imageArgs])
{
    ImageTiming.Run(imageArgs);
    return;
}

// Time AV1 decoding (Phase 2.9.5 speed gate) at one thread and all cores: -- --time-av1 [--one-thread] <file.ivf>...
if (args is ["--time-av1", .. string[] av1s])
{
    Av1Timing.Run(av1s);
    return;
}

// Time AV1 inter encoding (2.9.9): -- --encode-av1 <frames> <quality> <efforts, comma-separated> <file.ivf> [--one-thread]
if (args is ["--encode-av1", string av1Frames, string av1Quality, string av1Efforts, string av1Clip, .. string[] av1Rest])
{
    Av1Timing.Encode(int.Parse(av1Frames, System.Globalization.CultureInfo.InvariantCulture), int.Parse(av1Quality, System.Globalization.CultureInfo.InvariantCulture),
        [.. av1Efforts.Split(',').Select(e => int.Parse(e, System.Globalization.CultureInfo.InvariantCulture))], av1Clip,
        av1Rest.Contains("--one-thread") ? 1 : Environment.ProcessorCount);
    return;
}

// Time Zstandard decoding against zstd -b -d (TIFF ZSTD task 1): -- --time-zstd .cache/zstd [name part...]
if (args is ["--time-zstd", string zstdFolder, .. string[] zstdOnly])
{
    ZstdTiming.Run(zstdFolder, zstdOnly);
    return;
}

// Time Zstandard encoding against zstd -b (TIFF ZSTD task 2): -- --time-zstd-encode .cache/zstd [input-level...]
if (args is ["--time-zstd-encode", string zstdEncodeFolder, .. string[] zstdEncodeOnly])
{
    ZstdEncodeTiming.Run(zstdEncodeFolder, zstdEncodeOnly);
    return;
}

// Time VP8 decoding (Phase 2.3.5 speed gate) at one thread and all cores: -- --time-vp8 <file.ivf>...
if (args is ["--time-vp8", .. string[] ivfs])
{
    Vp8Timing.Run(ivfs);
    return;
}

// Encode a raw 4:2:0 clip as VP8 (5.2 encoder speed gate), timed, with size and PSNR:
// -- --encode-vp8 <in.yuv> <width> <height> <frames> <quantizer> <effort> <threads> <out.ivf> [runs]
if (args is ["--encode-vp8", string vp8In, string vp8W, string vp8H, string vp8Frames, string vp8Q, string vp8Effort, string vp8Threads, string vp8Out, .. string[] vp8Runs])
{
    static int I(string s) => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    Vp8EncodeTiming.Run(vp8In, I(vp8W), I(vp8H), I(vp8Frames), I(vp8Q), I(vp8Effort), I(vp8Threads), vp8Out, vp8Runs.Length > 0 ? I(vp8Runs[0]) : 1);
    return;
}

// Time the barcode reader: -- --time-barcodes [--zxing-cpp <bench.json>] [--only <set prefix>] <folder>... (a blackbox folder is read
// set by set with its formats, as the 2.11.1 speed gate does; any other folder at one thread and all cores)
if (args is ["--time-barcodes", .. string[] barcodeArgs])
{
    BarcodeTiming.Run(barcodeArgs);
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

// Encode AVIF (Phase 2.9.6), timed, with size and PSNR: -- --encode-avif <quality, or 101 for lossless> <effort> <in> <out.avif>... [--one-thread]
if (args is ["--encode-avif", string avifQuality, string avifEffort, .. string[] avifPairs])
{
    AvifTiming.Encode(int.Parse(avifQuality, System.Globalization.CultureInfo.InvariantCulture), int.Parse(avifEffort, System.Globalization.CultureInfo.InvariantCulture), avifPairs);
    return;
}

// PSNR of images against the first: -- --psnr <reference> <image>...
if (args is ["--psnr", .. string[] psnrFiles])
{
    AvifTiming.Psnr(psnrFiles);
    return;
}

// Encode lossless JPEG XL (Phase 2.9.3 encoder gate), timed: -- --encode-jxl <effort> <in> <out.jxl> [<in> <out.jxl>]...
if (args is ["--encode-jxl", string effort, .. string[] pairs])
{
    JxlTiming.Encode(int.Parse(effort, System.Globalization.CultureInfo.InvariantCulture), pairs);
    return;
}

// Profile one thread's VP8 decode stage by stage (Phase 2.4.5), IVF clips and lossy WebP stills; the stages need a build with
// -p:Vp8StageTimers=true: -- --vp8-stages <file.ivf|file.webp>...
if (args is ["--vp8-stages", .. string[] vp8Files])
{
    Vp8Timing.RunStages(vp8Files);
    return;
}

// Write the 2.3.1 WebP benchmark pictures (photo 2048 × 1536, screenshot 1920 × 1080) as PNG: -- --write-webp-pictures <dir>
if (args is ["--write-webp-pictures", string pictureDirectory])
{
    Vp8Timing.WritePictures(pictureDirectory);
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
