using System.Diagnostics;
using System.Text.Json;

using Egami.Compression.Zstd;

namespace Egami.Benchmarks;

/// <summary>
/// The Zstandard decoder's throughput (TIFF ZSTD, task 1): each file of the local corpus (tools/make-zstd-references.py --local
/// writes .cache/zstd) decoded on one thread, the best of five runs of at least half a second each, in MB of decoded data a
/// second, next to zstd -b -d's speed for the same file from native.json. The decided bar is 0.6 times native.
/// </summary>
internal static class ZstdTiming
{
    internal static void Run(string folder, string[] only)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        using JsonDocument nativeJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "native.json")));
        JsonElement speeds = nativeJson.RootElement.GetProperty("speeds");
        List<double> ratios = [];
        foreach (JsonProperty file in manifest.RootElement.GetProperty("files").EnumerateObject().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if ((only.Length > 0 && !only.Any(o => file.Name.Contains(o, StringComparison.Ordinal))) || !speeds.TryGetProperty(file.Name, out JsonElement native))
            {
                continue;
            }

            byte[] data = File.ReadAllBytes(Path.Combine(folder, file.Name + ".zst"));
            byte[] output = new byte[file.Value.GetProperty("length").GetInt32()];
            ZstdDecoder.Decode(data, output);
            double best = double.MaxValue;
            for (int run = 0; run < 5; run++)
            {
                int times = 0;
                long start = Stopwatch.GetTimestamp();
                do
                {
                    ZstdDecoder.Decode(data, output);
                    times++;
                }
                while (Stopwatch.GetElapsedTime(start).TotalSeconds < 0.5);

                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds / times);
            }

            // zstd reports MB as 1,000,000 bytes.
            double speed = output.Length / best / 1e6;
            double reference = native.GetDouble();
            ratios.Add(speed / reference);
            Console.WriteLine($"{file.Name,-24} {speed,8:F0} MB/s   zstd {reference,6:F0} MB/s   {speed / reference:F2}x");
        }

        if (ratios.Count > 0)
        {
            Console.WriteLine($"{ratios.Count} files: geometric mean {Math.Exp(ratios.Average(Math.Log)):F2}x, worst {ratios.Min():F2}x of zstd");
        }
    }
}
