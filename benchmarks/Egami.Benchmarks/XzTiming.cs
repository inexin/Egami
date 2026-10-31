using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Egami.Compression.Lzma;


namespace Egami.Benchmarks;

/// <summary>
/// The xz decoder's throughput (TIFF LZMA, task 5): each file of the local corpus (tools/make-xz-references.py --local writes
/// .cache/xz) decoded on one thread, in MB of decoded data a second, next to liblzma's `xz -dc` in one alpine:3.20 container:
/// three rounds of five liblzma runs then five Egami runs of at least 0.2 s each, wall time, each side's best kept, so a burst
/// of other work on a busy machine hits both alike. The decided bar is 0.8 times native.
/// </summary>
internal static class XzTiming
{
    /// <summary>Decodes one file <paramref name="runs"/> times, for perf to count.</summary>
    internal static void Repeat(string file, int length, int runs)
    {
        byte[] data = File.ReadAllBytes(file);
        byte[] output = new byte[length];
        for (int i = 0; i < runs; i++)
        {
            XzDecoder.Decode(data, output);
        }
    }

    internal static void Run(string folder, string[] only)
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        using XzNative native = new(folder);
        List<double> ratios = [];
        foreach (JsonProperty file in manifest.RootElement.GetProperty("files").EnumerateObject().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (only.Length > 0 && !only.Any(o => file.Name.Contains(o, StringComparison.Ordinal)))
            {
                continue;
            }

            byte[] data = File.ReadAllBytes(Path.Combine(folder, file.Name + ".xz"));
            byte[] output = new byte[file.Value.GetProperty("length").GetInt32()];
            // Three rounds of liblzma then Egami, keeping each side's best, so a burst of other work hits both alike.
            XzDecoder.Decode(data, output);
            double nativeBest = double.MaxValue;
            double best = double.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                nativeBest = Math.Min(nativeBest, native.Seconds($"xz -dc /z/{file.Name}.xz"));
                for (int run = 0; run < 5; run++)
                {
                    int times = 0;
                    long start = Stopwatch.GetTimestamp();
                    do
                    {
                        XzDecoder.Decode(data, output);
                        times++;
                    }
                    while (Stopwatch.GetElapsedTime(start).TotalSeconds < 0.2);

                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds / times);
                }
            }

            double reference = output.Length / nativeBest / 1e6;
            double speed = output.Length / best / 1e6;
            ratios.Add(speed / reference);
            Console.WriteLine($"{file.Name,-20} {speed,7:F1} MB/s   xz {reference,7:F1} MB/s   {speed / reference:F2}x");
        }

        if (ratios.Count > 0)
        {
            Console.WriteLine($"{ratios.Count} files: geometric mean {Math.Exp(ratios.Average(Math.Log)):F2}x, worst {ratios.Min():F2}x of xz");
        }
    }
}

/// <summary>
/// The xz encoder against liblzma (TIFF LZMA, task 6): each local input at presets 0 to 9, compressed by Egami and by `xz -k -c
/// -N --check=none` in the container, timed in alternating rounds as <see cref="XzTiming"/> does, with the sizes; every output
/// goes to .cache/xz/encoded (with the committed inputs at every preset, with and without delta) and `xz -t` must accept each.
/// The decided bars are 0.5 times native speed and sizes within 5 % at presets 1, 6 and 9.
/// </summary>
internal static class XzEncodeTiming
{
    internal static void Run(string folder, string[] only)
    {
        string encoded = Path.Combine(folder, "encoded");
        Directory.CreateDirectory(encoded);
        WriteSmall(folder, encoded);
        using XzNative native = new(folder);
        foreach (string input in new[] { "text-big", "photo-big", "mixed-big" })
        {
            byte[] data = File.ReadAllBytes(Path.Combine(folder, "inputs", input + ".bin"));
            byte[] output = new byte[XzEncoder.MaxEncodedLength(data.Length)];
            for (int preset = XzEncoder.MinPreset; preset <= XzEncoder.MaxPreset; preset++)
            {
                string name = $"{input}-{preset}";
                if (only.Length > 0 && !only.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                int size = XzEncoder.Encode(data, output, preset);
                File.WriteAllBytes(Path.Combine(encoded, name + ".xz"), output.AsSpan(0, size).ToArray());
                int nativeSize = native.Size($"xz -k -c -{preset} --check=none /z/inputs/{input}.bin");
                double nativeBest = double.MaxValue;
                double best = double.MaxValue;
                int rounds = preset >= 6 ? 1 : 3;
                for (int round = 0; round < rounds; round++)
                {
                    nativeBest = Math.Min(nativeBest, native.Seconds($"xz -k -c -{preset} --check=none /z/inputs/{input}.bin", preset >= 6 ? 1 : 3));
                    long start = Stopwatch.GetTimestamp();
                    int times = 0;
                    do
                    {
                        XzEncoder.Encode(data, output, preset);
                        times++;
                    }
                    while (Stopwatch.GetElapsedTime(start).TotalSeconds < 0.5);

                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds / times);
                }

                double speed = data.Length / best / 1e6;
                double nativeSpeed = data.Length / nativeBest / 1e6;
                Console.WriteLine($"{name,-14} {size,9} bytes ({((double)size / nativeSize) - 1,6:+0.0%;-0.0%} vs {nativeSize,9})   "
                    + $"{speed,6:F2} MB/s ({speed / nativeSpeed:F2}x of {nativeSpeed:F2})");
            }
        }

        Console.WriteLine(native.Test("/z/encoded"));
    }

    /// <summary>Encodes the committed corpus's inputs (tests/reference/xz) at every preset, with and without delta, for xz -t.</summary>
    private static void WriteSmall(string folder, string encoded)
    {
        string references = Path.Combine(Path.GetFullPath(Path.Combine(folder, "..", "..")), "tests", "reference", "xz");
        foreach (string file in Directory.Exists(references) ? Directory.GetFiles(references, "*.bin.gz") : [])
        {
            using System.IO.Compression.GZipStream gzip = new(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
            using MemoryStream raw = new();
            gzip.CopyTo(raw);
            byte[] data = raw.ToArray();
            byte[] output = new byte[XzEncoder.MaxEncodedLength(data.Length)];
            string name = Path.GetFileName(file)[..^".bin.gz".Length];
            for (int preset = XzEncoder.MinPreset; preset <= XzEncoder.MaxPreset; preset++)
            {
                foreach (int delta in new[] { 0, 1, 3 })
                {
                    int size = XzEncoder.Encode(data, output, preset, delta);
                    File.WriteAllBytes(Path.Combine(encoded, $"small-{name}-{preset}-d{delta}.xz"), output.AsSpan(0, size).ToArray());
                }
            }
        }
    }
}

/// <summary>One alpine:3.20 container with the xz CLI and a corpus folder mounted at /z, for timing liblzma next to Egami.</summary>
internal sealed class XzNative : IDisposable
{
    private readonly string _name = $"egami-xz-bench-{Environment.ProcessId}";

    public XzNative(string folder)
    {
        Docker("run", "-d", "--rm", "--name", _name, "-v", $"{Path.GetFullPath(folder)}:/z:ro", "alpine:3.20", "sleep", "7200");
        Docker("exec", _name, "apk", "add", "--no-cache", "xz", "coreutils");
    }

    /// <summary>The size of a shell command's output in the container.</summary>
    public int Size(string command) => int.Parse(Docker("exec", _name, "sh", "-c", $"{command} | wc -c").Trim(), CultureInfo.InvariantCulture);

    /// <summary>Runs xz -t on every .xz file of a folder in the container; returns a line saying how many pass.</summary>
    public string Test(string folder) =>
        Docker("exec", _name, "sh", "-c", $"n=0; bad=0; for f in {folder}/*.xz; do n=$((n+1)); xz -t \"$f\" || {{ bad=$((bad+1)); echo \"xz -t fails: $f\"; }}; done; echo \"xz -t: $((n-bad)) of $n files decode\"");

    /// <summary>The best wall time in seconds of <paramref name="runs"/> runs of a shell command in the container, output discarded.</summary>
    public double Seconds(string command, int runs = 5)
    {
        string script = $"for i in $(seq {runs}); do s=$(date +%s%N); {command} > /dev/null; e=$(date +%s%N); echo $((e - s)); done";
        return Docker("exec", _name, "sh", "-c", script).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => long.Parse(l.Trim(), CultureInfo.InvariantCulture)).Min() / 1e9;
    }

    public void Dispose() => Docker("rm", "-f", _name);

    private static string Docker(params string[] args)
    {
        ProcessStartInfo start = new("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        _ = error.Result;
        return output;
    }
}
