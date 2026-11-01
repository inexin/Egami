using System.Diagnostics;
using System.Globalization;

using Egami.Heic;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>Times Egami.Heic's HEIC writer and reports size and PSNR, for comparing against heif-enc in egami-compare.</summary>
internal static class HeicTiming
{
    /// <summary>Encodes each input at the quality and effort, three times after a warm-up, and writes the file of the fastest.</summary>
    internal static void Encode(int quality, int effort, string[] pairs)
    {
        int threads = pairs.Contains("--one-thread") ? 1 : Environment.ProcessorCount;
        pairs = [.. pairs.Where(p => p != "--one-thread")];
        EgamiContext context = (EgamiContext.Default with { MaxParallelism = threads }).WithHeic();
        HeicWriterOptions options = new() { Quality = Math.Min(quality, 100), Effort = effort, Lossless = quality > 100 ? true : null };
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            using Raster raster = Raster.Load(pairs[i], context);
            using Raster<Rgb8> source = raster.As<Rgb8>();
            byte[] data = Save(source, options);
            long allocated = GC.GetTotalAllocatedBytes(true);
            double best = double.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                long start = Stopwatch.GetTimestamp();
                data = Save(source, options);
                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"allocated {(GC.GetTotalAllocatedBytes(true) - allocated) / 3e6:F0} MB an encode"));
            File.WriteAllBytes(pairs[i + 1], data);
            using Raster<Rgb8> back = Raster.Load<Rgb8>(data, context);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{pairs[i]}: {data.Length} bytes, {8.0 * data.Length / (source.Width * source.Height):F3} bpp, PSNR {Psnr(source, back):F2} dB, {best:F0} ms on {threads} threads"));
        }
    }

    private static byte[] Save(Raster<Rgb8> raster, HeicWriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, HeicFormat.Info, options);
        return stream.ToArray();
    }

    private static double Psnr(Raster<Rgb8> a, Raster<Rgb8> b)
    {
        double sse = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<Rgb8> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                int dr = ra[x].R - rb[x].R, dg = ra[x].G - rb[x].G, db = ra[x].B - rb[x].B;
                sse += (dr * dr) + (dg * dg) + (db * db);
            }
        }

        return 10 * Math.Log10(255.0 * 255 * 3 * a.Width * a.Height / Math.Max(sse, 1e-9));
    }
}
