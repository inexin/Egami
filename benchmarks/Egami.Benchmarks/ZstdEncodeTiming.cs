using System.Diagnostics;
using System.Text.Json;

using Egami.Compression.Zstd;

namespace Egami.Benchmarks;

/// <summary>
/// The Zstandard encoder against libzstd (TIFF ZSTD, task 2): each local input (tools/make-zstd-references.py --local) at the
/// levels in native-encode.json (--native-encode), encoded on one thread, the best of 10 runs of at least 0.2 s (wall time, as zstd -b), with
/// the size; outputs go to .cache/zstd/encoded for `make-zstd-references.py --check`. The decided bars are 0.5 times libzstd's
/// speed and sizes within 5 % of its at levels 1 to 15.
/// </summary>
internal static class ZstdEncodeTiming
{
    internal static void Run(string folder, string[] only)
    {
        using JsonDocument native = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "native-encode.json")));
        string encoded = Path.Combine(folder, "encoded");
        Directory.CreateDirectory(encoded);
        WriteSmall(folder, encoded);
        using ZstdNative? live = only.Contains("--live") ? new ZstdNative(folder) : null;
        only = [.. only.Where(o => o != "--live")];
        Dictionary<string, byte[]> inputs = [];
        foreach (JsonProperty entry in native.RootElement.GetProperty("results").EnumerateObject().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (only.Length > 0 && !only.Contains(entry.Name, StringComparer.Ordinal))
            {
                continue;
            }

            int cut = entry.Name.LastIndexOf('-');
            string source = entry.Name[..cut];
            int level = int.Parse(entry.Name[(cut + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            if (!inputs.TryGetValue(source, out byte[]? data))
            {
                inputs[source] = data = File.ReadAllBytes(Path.Combine(folder, "inputs", source + ".bin"));
            }

            byte[] output = new byte[ZstdEncoder.MaxEncodedLength(data.Length)];
            int size = ZstdEncoder.Encode(data, output, level);
            double best = double.MaxValue;
            for (int run = 0; run < 10; run++)
            {
                // Wall time, as zstd -b measures, over short runs, so the best of them catches a quiet moment on a busy machine.
                int times = 0;
                long start = Stopwatch.GetTimestamp();
                do
                {
                    ZstdEncoder.Encode(data, output, level);
                    times++;
                }
                while (Stopwatch.GetElapsedTime(start).TotalSeconds < 0.2);

                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds / times);
            }

            byte[] back = new byte[data.Length];
            bool same = ZstdDecoder.Decode(output.AsSpan(0, size), back) == data.Length && back.AsSpan().SequenceEqual(data);
            File.WriteAllBytes(Path.Combine(encoded, entry.Name + ".zst"), output.AsSpan(0, size).ToArray());
            if (!same)
            {
                Console.WriteLine($"{entry.Name}: the decoder does not give the input back");
            }

            double speed = data.Length / best / 1e6;
            double nativeSpeed = live?.Encode(source + ".bin", level).Speed ?? entry.Value.GetProperty("speed").GetDouble();
            int nativeSize = entry.Value.GetProperty("size").GetInt32();
            Console.WriteLine($"{entry.Name,-16} {size,10} bytes ({(double)size / nativeSize - 1,7:+0.0%;-0.0%} vs {nativeSize,10})   "
                + $"{speed,7:F1} MB/s ({speed / nativeSpeed:F2}x of {nativeSpeed,6:F1}){(same ? "" : "   ROUND TRIP FAILS")}");
        }
    }

    /// <summary>Encodes the committed decoder corpus's inputs (tests/reference/zstd) at every level, for the CLI check.</summary>
    private static void WriteSmall(string folder, string encoded)
    {
        // The folder is .cache/zstd under the repository root.
        string root = Path.GetFullPath(Path.Combine(folder, "..", ".."));
        string references = Path.Combine(root, "tests", "reference", "zstd");
        foreach (string file in Directory.Exists(references) ? Directory.GetFiles(references, "*.bin.gz") : [])
        {
            using System.IO.Compression.GZipStream gzip = new(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
            using MemoryStream raw = new();
            gzip.CopyTo(raw);
            byte[] data = raw.ToArray();
            byte[] output = new byte[ZstdEncoder.MaxEncodedLength(data.Length)];
            string name = Path.GetFileName(file)[..^".bin.gz".Length];
            for (int level = ZstdEncoder.MinLevel; level <= ZstdEncoder.MaxLevel; level++)
            {
                int size = ZstdEncoder.Encode(data, output, level);
                File.WriteAllBytes(Path.Combine(encoded, $"small-{name}-{level}.zst"), output.AsSpan(0, size).ToArray());
            }
        }
    }
}
