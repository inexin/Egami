using System.Diagnostics;
using System.Globalization;

using Egami.Formats.Avif;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>Times the Phase 2.9.6 AVIF encoder and reports size and PSNR, for comparing against avifenc in egami-compare.</summary>
internal static class AvifTiming
{
    /// <summary>Encodes each input at the quality and effort, once to warm up and then timed, and writes the file.</summary>
    internal static void Encode(int quality, int effort, string[] pairs)
    {
        int threads = pairs.Contains("--one-thread") ? 1 : Environment.ProcessorCount;
        pairs = [.. pairs.Where(p => p != "--one-thread")];
        Size? crop = null;
        if (pairs.Length > 1 && pairs[0] == "--crop")
        {
            string[] size = pairs[1].Split('x');
            crop = new Size(int.Parse(size[0], CultureInfo.InvariantCulture), int.Parse(size[1], CultureInfo.InvariantCulture));
            pairs = pairs[2..];
        }

        EgamiContext context = EgamiContext.Default with { MaxParallelism = threads };
        AvifWriterOptions options = new() { Quality = quality, Effort = effort, Lossless = quality > 100 ? true : null };
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            using Raster raster = Raster.Load(pairs[i], context);
            using Raster<Rgb8> whole = raster.As<Rgb8>();
            using Raster<Rgb8> source = Crop(whole, crop ?? new Size(whole.Width, whole.Height));
            byte[] data = Save(source, options);
            long start = Stopwatch.GetTimestamp();
            data = Save(source, options);
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            File.WriteAllBytes(pairs[i + 1], data);
            source.Save(pairs[i + 1] + ".png");
            using Raster<Rgb8> back = Raster.Load<Rgb8>(data);
            back.Save(pairs[i + 1] + ".dec.png");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{pairs[i]}: {data.Length} bytes, {8.0 * data.Length / (source.Width * source.Height):F3} bpp, PSNR {Psnr(source, back):F2} dB, {ms:F0} ms on {threads} threads"));
        }
    }

    /// <summary>Prints the PSNR of each image against the first.</summary>
    internal static void Psnr(string[] files)
    {
        using Raster<Rgb8> reference = Raster.Load<Rgb8>(files[0]);
        foreach (string file in files.Skip(1))
        {
            using Raster<Rgb8> other = Raster.Load<Rgb8>(file);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{file}: PSNR {Psnr(reference, other):F2} dB"));
        }
    }

    private static Raster<Rgb8> Crop(Raster<Rgb8> raster, Size size)
    {
        Raster<Rgb8> crop = Raster.Create<Rgb8>(size.Width, size.Height, raster.Context);
        int x0 = (raster.Width - size.Width) / 2, y0 = (raster.Height - size.Height) / 2;
        for (int y = 0; y < size.Height; y++)
        {
            raster.Row(y0 + y).Slice(x0, size.Width).CopyTo(crop.Row(y));
        }

        return crop;
    }

    private static byte[] Save(Raster raster, AvifWriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, AvifFormat.Info, options);
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

        double mse = sse / (3.0 * a.Width * a.Height);
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
