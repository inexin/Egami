using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Egami.Benchmarks;

/// <summary>
/// libzstd timed live next to Egami (the --live option of --time-zstd and --time-zstd-encode): one alpine:3.20 container with the
/// zstd CLI (1.5.6) and the local corpus mounted, and `zstd -b` run in it just before Egami times the same file, so both sides see
/// the same load on a busy machine.
/// </summary>
internal sealed partial class ZstdNative : IDisposable
{
    private readonly string _name = $"egami-zstd-bench-{Environment.ProcessId}";

    public ZstdNative(string folder)
    {
        Docker("run", "-d", "--rm", "--name", _name, "-v", $"{Path.GetFullPath(folder)}:/z:ro", "alpine:3.20", "sleep", "7200");
        Docker("exec", _name, "apk", "add", "--no-cache", "zstd");
    }

    /// <summary>zstd -b -d's speed for a file of the corpus, in MB of decoded data a second.</summary>
    public double Decode(string file) =>
        double.Parse(Speeds().Matches(Docker("exec", _name, "zstd", "-b", "-d", "-i1", $"/z/{file}"))[^1].Groups[1].Value, CultureInfo.InvariantCulture);

    /// <summary>zstd -b's compression speed and size for an input of the corpus at a level.</summary>
    public (double Speed, int Size) Encode(string input, int level)
    {
        string text = Docker("exec", _name, "zstd", $"-b{level}", level > 15 ? "-i1" : "-i2", $"/z/inputs/{input}");
        Match match = Result().Matches(text)[^1];
        return (double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
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
        return output + error.Result;
    }

    [GeneratedRegex(@"([\d.]+) MB/s")]
    private static partial Regex Speeds();

    [GeneratedRegex(@"(\d+) -> +(\d+) \([^)]*\), +([\d.]+) MB/s")]
    private static partial Regex Result();
}
