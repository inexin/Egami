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

/// <summary>One alpine:3.20 container with the xz CLI and a corpus folder mounted at /z, for timing liblzma next to Egami.</summary>
internal sealed class XzNative : IDisposable
{
    private readonly string _name = $"egami-xz-bench-{Environment.ProcessId}";

    public XzNative(string folder)
    {
        Docker("run", "-d", "--rm", "--name", _name, "-v", $"{Path.GetFullPath(folder)}:/z:ro", "alpine:3.20", "sleep", "7200");
        Docker("exec", _name, "apk", "add", "--no-cache", "xz", "coreutils");
    }

    /// <summary>The best wall time in seconds of five runs of a shell command in the container, output discarded.</summary>
    public double Seconds(string command)
    {
        string script = $"for i in 1 2 3 4 5; do s=$(date +%s%N); {command} > /dev/null; e=$(date +%s%N); echo $((e - s)); done";
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
