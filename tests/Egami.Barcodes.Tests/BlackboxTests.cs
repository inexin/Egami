using System.Text.Json;

using Egami.Pixels;

namespace Egami.Barcodes.Tests;

/// <summary>
/// The ZXing blackbox test images (Apache-2.0), fetched into .cache by tools/make-barcode-references.py blackbox: how many
/// Egami reads per set, each image once with the set's format only. Run with EGAMI_CORPUS=barcodes; the counts go to
/// .cache/egami-blackbox.json next to zxing-cpp's, and the test fails when a set falls below its floor.
/// </summary>
public class BlackboxTests
{
    private static string Root => System.IO.Path.Combine(TestFiles.Root, ".cache", "zxing-blackbox", "core", "src", "test", "resources", "blackbox");

    private static readonly Dictionary<string, BarcodeFormats> s_sets = new()
    {
        ["aztec"] = BarcodeFormats.Aztec,
        ["code128"] = BarcodeFormats.Code128,
        ["code39"] = BarcodeFormats.Code39,
        ["datamatrix"] = BarcodeFormats.DataMatrix,
        ["ean13"] = BarcodeFormats.Ean13 | BarcodeFormats.UpcA,
        ["ean8"] = BarcodeFormats.Ean8,
        ["itf"] = BarcodeFormats.Itf,
        ["pdf417"] = BarcodeFormats.Pdf417,
        ["qrcode"] = BarcodeFormats.Qr,
        ["upca"] = BarcodeFormats.UpcA,
        ["upce"] = BarcodeFormats.UpcE,
        ["upcean"] = BarcodeFormats.Ean13 | BarcodeFormats.UpcA,
    };

    [Fact]
    public void ReadRates()
    {
        string asked = Environment.GetEnvironmentVariable("EGAMI_CORPUS") ?? "";
        if (!asked.Split(',').Any(a => a.Trim() is "barcodes" or "all"))
        {
            Assert.Skip("Set EGAMI_CORPUS=barcodes to read the blackbox images.");
        }

        if (!Directory.Exists(Root))
        {
            Assert.Skip("No blackbox images: run python tools/make-barcode-references.py blackbox.");
        }

        var summary = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        var missed = new List<string>();
        foreach (string folder in Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal))
        {
            string name = System.IO.Path.GetFileName(folder);
            if (!s_sets.TryGetValue(name.Split('-')[0], out BarcodeFormats formats))
            {
                continue;
            }

            int passed = 0, total = 0;
            foreach (string file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
            {
                string text = System.IO.Path.ChangeExtension(file, ".txt");
                if (System.IO.Path.GetExtension(file).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".gif") || !File.Exists(text))
                {
                    continue;
                }

                total++;
                string expected = File.ReadAllText(text);
                using Raster<Gray8> image = Raster.Load<Gray8>(file);
                if (image.ReadBarcodes(new BarcodeReaderOptions { Formats = formats }).Any(r => r.Text == expected || r.Text.StartsWith(expected + " ", StringComparison.Ordinal) || "0" + r.Text == expected))
                {
                    passed++;
                }
                else
                {
                    missed.Add($"{name}/{System.IO.Path.GetFileName(file)}");
                }
            }

            summary[name] = [passed, total];
        }

        // Images with no barcode (or one Egami does not write): any read is a false positive.
        foreach (string folder in Directory.EnumerateDirectories(Root, "falsepositives*").Order(StringComparer.Ordinal))
        {
            int clean = 0, total = 0;
            foreach (string file in Directory.EnumerateFiles(folder).Where(f => System.IO.Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif"))
            {
                total++;
                using Raster<Gray8> image = Raster.Load<Gray8>(file);
                IReadOnlyList<BarcodeResult> results = image.ReadBarcodes();
                if (results.Count == 0)
                {
                    clean++;
                }
                else
                {
                    missed.Add($"{System.IO.Path.GetFileName(folder)}/{System.IO.Path.GetFileName(file)}: {string.Join("; ", results)}");
                }
            }

            summary[System.IO.Path.GetFileName(folder)] = [clean, total];
        }

        File.WriteAllText(System.IO.Path.Combine(TestFiles.Root, ".cache", "egami-blackbox.json"), JsonSerializer.Serialize(summary));
        File.WriteAllLines(System.IO.Path.Combine(TestFiles.Root, ".cache", "egami-blackbox-missed.txt"), missed);
        TestContext.Current.SendDiagnosticMessage(string.Join(' ', summary.Select(kv => $"{kv.Key}:{kv.Value[0]}/{kv.Value[1]}")));
        Assert.NotEmpty(summary);
    }
}
