using System.Diagnostics;
using System.Text.Json;

using Egami.Barcodes;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// Times the barcode reader on folders of images. Given the ZXing blackbox folder that tools/make-barcode-references.py
/// blackbox fetches, it reads each set with that set's formats only, on one thread, the best of three passes after half a
/// second of warm-up, as egami-compare's speed gate does; with zxing-cpp's times (egami-compare's
/// .cache/zxing-cpp-bench.json) it also prints the ratios, per set, per symbology and in total. Any other folder is read at
/// the default options, at one thread and at all cores.
/// </summary>
internal static class BarcodeTiming
{
    private static readonly Dictionary<string, (BarcodeFormat Formats, string Symbology)> s_sets = new()
    {
        ["aztec"] = (BarcodeFormat.Aztec, "Aztec"),
        ["code128"] = (BarcodeFormat.Code128, "Code 128"),
        ["code39"] = (BarcodeFormat.Code39, "Code 39"),
        ["datamatrix"] = (BarcodeFormat.DataMatrix, "Data Matrix"),
        ["ean13"] = (BarcodeFormat.Ean13 | BarcodeFormat.UpcA, "EAN and UPC"),
        ["ean8"] = (BarcodeFormat.Ean8, "EAN and UPC"),
        ["itf"] = (BarcodeFormat.Itf, "ITF"),
        ["pdf417"] = (BarcodeFormat.Pdf417, "PDF417"),
        ["qrcode"] = (BarcodeFormat.Qr, "QR"),
        ["upca"] = (BarcodeFormat.UpcA, "EAN and UPC"),
        ["upce"] = (BarcodeFormat.UpcE, "EAN and UPC"),
        ["upcean"] = (BarcodeFormat.Ean13 | BarcodeFormat.UpcA, "EAN and UPC"),
    };

    /// <summary>Runs the timing: -- --time-barcodes [--zxing-cpp &lt;bench.json&gt;] [--only &lt;prefix&gt;] &lt;folder&gt;...</summary>
    internal static void Run(string[] args)
    {
        string? zxingCpp = null, only = null;
        var folders = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--zxing-cpp":
                    zxingCpp = args[++i];
                    break;
                case "--only":
                    only = args[++i];
                    break;
                default:
                    folders.Add(args[i]);
                    break;
            }
        }

        foreach (string folder in folders)
        {
            if (Directory.Exists(Path.Combine(folder, "qrcode-1")))
            {
                RunSets(folder, zxingCpp, only);
            }
            else
            {
                RunFolder(folder);
            }
        }
    }

    private static void RunSets(string root, string? zxingCpp, string? only)
    {
        Dictionary<string, double> cpp = [];
        if (zxingCpp is not null)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(zxingCpp));
            cpp = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value[2].GetDouble());
        }

        var one = EgamiContext.Default with { MaxParallelism = 1 };
        var totals = new SortedDictionary<string, (double Egami, double Cpp, int Images)>(StringComparer.Ordinal);

        // One untimed pass over every set first, so that no set pays for compiling code the JIT has not seen yet.
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            if (s_sets.TryGetValue(Path.GetFileName(folder).Split('-')[0], out var warm))
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.png"))
                {
                    using Raster<Gray8> image = Raster.Load<Gray8>(file);
                    image.ReadBarcodes(new BarcodeReaderOptions { Formats = warm.Formats, Context = one });
                }
            }
        }

        Console.WriteLine("| Set | Read | Egami ms | zxing-cpp ms | Ratio |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (string folder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(folder);
            if (!s_sets.TryGetValue(name.Split('-')[0], out var set) || (only is not null && !name.StartsWith(only, StringComparison.Ordinal)))
            {
                continue;
            }

            var images = Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" && File.Exists(Path.ChangeExtension(f, ".txt")))
                .Select(f => (Image: Raster.Load<Gray8>(f), Expected: File.ReadAllText(Path.ChangeExtension(f, ".txt")))).ToList();
            try
            {
                if (images.Count == 0)
                {
                    continue;
                }

                var options = new BarcodeReaderOptions { Formats = set.Formats, Context = one };
                int read = images.Count(i => i.Image.ReadBarcodes(options).Any(r => r.Text == i.Expected || "0" + r.Text == i.Expected));
                // Half a second of passes first, so that the JIT has compiled the hot code fully, as the speed gate does.
                long warm = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(warm).TotalMilliseconds < 500)
                {
                    foreach (var (image, _) in images)
                    {
                        image.ReadBarcodes(options);
                    }
                }

                double best = double.MaxValue;
                for (int pass = 0; pass < 3; pass++)
                {
                    long start = Stopwatch.GetTimestamp();
                    foreach (var (image, _) in images)
                    {
                        image.ReadBarcodes(options);
                    }

                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }

                double egami = best / images.Count;
                double reference = cpp.TryGetValue(name, out double c) ? c : double.NaN;
                Console.WriteLine($"| {name} | {read}/{images.Count} | {egami:F2} | {reference:F2} | {egami / reference:F2} |");
                if (!double.IsNaN(reference))
                {
                    var (e, r, n) = totals.GetValueOrDefault(set.Symbology);
                    totals[set.Symbology] = (e + best, r + (reference * images.Count), n + images.Count);
                }
            }
            finally
            {
                images.ForEach(i => i.Image.Dispose());
            }
        }

        if (totals.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("| Symbology | Images | Egami ms | zxing-cpp ms | Ratio |");
            Console.WriteLine("|---|---|---|---|---|");
            foreach (var (symbology, (e, r, n)) in totals)
            {
                Console.WriteLine($"| {symbology} | {n} | {e / n:F2} | {r / n:F2} | {e / r:F2} |");
            }

            double egamiAll = totals.Values.Sum(t => t.Egami), cppAll = totals.Values.Sum(t => t.Cpp);
            Console.WriteLine($"| all | {totals.Values.Sum(t => t.Images)} | {egamiAll:F0} | {cppAll:F0} | {egamiAll / cppAll:F2} |");
        }
    }

    private static void RunFolder(string folder)
    {
        int[] counts = [1, Environment.ProcessorCount];
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
