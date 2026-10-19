using System.Diagnostics;

using Egami.Barcodes;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// Times the barcode reader on folders of images (the ZXing blackbox sets that tools/make-barcode-references.py blackbox
/// fetches): the mean time per image at the default options, the best of three runs, at one thread and at all cores.
/// </summary>
internal static class BarcodeTiming
{
    internal static void Run(string[] folders)
    {
        int[] counts = [1, Environment.ProcessorCount];
        foreach (string folder in folders)
        {
            var images = Directory.EnumerateFiles(folder).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal).Select(f => Raster.Load<Gray8>(f)).ToList();
            try
            {
                foreach (int threads in counts)
                {
                    var options = new BarcodeReaderOptions { Context = EgamiContext.Default with { MaxParallelism = threads } };
                    double best = double.MaxValue;
                    for (int run = 0; run < 3; run++)
                    {
                        long start = Stopwatch.GetTimestamp();
                        foreach (Raster<Gray8> image in images)
                        {
                            image.ReadBarcodes(options);
                        }

                        best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / images.Count);
                    }

                    double pixels = images.Average(i => (double)i.Width * i.Height) / 1e6;
                    Console.WriteLine($"{Path.GetFileName(folder)}: {best:F1} ms an image ({pixels:F2} MP mean) on {threads} thread{(threads == 1 ? "" : "s")}");
                }
            }
            finally
            {
                images.ForEach(i => i.Dispose());
            }
        }
    }
}
