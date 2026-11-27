using System.Diagnostics;
using System.Runtime.Intrinsics;

using Egami.Formats;
using Egami.Formats.Avif;
using Egami.Formats.Jpeg;
using Egami.Formats.Jxl;
using Egami.Formats.Png;
using Egami.Formats.Webp;
using Egami.Heic;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// Writes the small decode corpus of roadmap 2.4.13's SIMD timing (tools/time-simd.py): the benchmark photo at 1920 × 1080 written
/// by Egami's own encoders, so every machine makes the same files without other tools. Run: <c>-- --write-simd-corpus &lt;dir&gt;
/// [--skip=avif,heic]</c>; it prints each file's name, size and encode time. <c>-- --simd-info</c> prints which instruction sets the
/// runtime uses, so the script can show what each DOTNET_Enable* setting turned off.
/// </summary>
internal static class SimdCorpus
{
    internal static void Info()
    {
        Console.WriteLine($"architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Vector128={Vector128.IsHardwareAccelerated}");
        Console.WriteLine($"Vector256={Vector256.IsHardwareAccelerated}");
        Console.WriteLine($"Vector512={Vector512.IsHardwareAccelerated}");
        Console.WriteLine($"Sse41={System.Runtime.Intrinsics.X86.Sse41.IsSupported}");
        Console.WriteLine($"Avx2={System.Runtime.Intrinsics.X86.Avx2.IsSupported}");
        Console.WriteLine($"Avx512F={System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");
        Console.WriteLine($"AdvSimd={System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported}");
        Console.WriteLine($"AdvSimdArm64={System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported}");
    }

    internal static void Write(string directory, string[] args)
    {
        HashSet<string> skip = [.. args.FirstOrDefault(a => a.StartsWith("--skip=", StringComparison.Ordinal))?["--skip=".Length..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries) ?? []];
        Directory.CreateDirectory(directory);
        using Raster<Rgba8> photo = BenchImages.Photo(1920, 1080);
        using Raster<Rgb8> rgb = photo.As<Rgb8>();
        (string Name, string Kind, Raster Image, FormatInfo Format, WriterOptions Options)[] files =
        [
            ("photo-420.jpg", "jpeg", rgb, JpegFormat.Info, new JpegWriterOptions { Quality = 90 }),
            ("photo-444.jpg", "jpeg", rgb, JpegFormat.Info, new JpegWriterOptions { Quality = 90, Subsampling = ChromaSubsampling.Subsample444 }),
            ("photo-progressive.jpg", "jpeg", rgb, JpegFormat.Info, new JpegWriterOptions { Quality = 90, Progressive = true }),
            ("photo-lossy.webp", "webp", rgb, WebpFormat.Info, new WebpWriterOptions { Lossless = false, Quality = 90 }),
            ("photo-lossless.webp", "webp", rgb, WebpFormat.Info, new WebpWriterOptions { Lossless = true }),
            ("photo-lossy.jxl", "jxl", rgb, JxlFormat.Info, new JxlWriterOptions { Lossless = false, Distance = 1 }),
            ("photo-lossless.jxl", "jxl", rgb, JxlFormat.Info, new JxlWriterOptions { Lossless = true }),
            ("photo.png", "png", photo, PngFormat.Info, new PngWriterOptions()),
            ("photo.avif", "avif", rgb, AvifFormat.Info, new AvifWriterOptions { Quality = 80 }),
            ("photo.heic", "heic", rgb, HeicFormat.Info, new HeicWriterOptions { Quality = 80 }),
        ];
        foreach ((string name, string kind, Raster image, FormatInfo format, WriterOptions options) in files)
        {
            if (skip.Contains(kind))
            {
                continue;
            }

            long start = Stopwatch.GetTimestamp();
            using MemoryStream stream = new();
            image.Save(stream, format, options);
            File.WriteAllBytes(Path.Combine(directory, name), stream.ToArray());
            Console.WriteLine($"{name}: {stream.Length} bytes, encoded in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms");
        }
    }
}
