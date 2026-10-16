namespace Egami.Tests;

/// <summary>
/// The fetched corpora under .cache (conformance suites, test vectors and their references, never committed), and the committed
/// images the every-file streaming tests run over. They are slow and matter mostly while working on their codec, so a run reads
/// one only when EGAMI_CORPUS names it: "jxl", "avif,vp8" or "all".
/// </summary>
internal static class LocalCorpus
{
    /// <summary>The codecs with a local corpus, and the command that fetches it.</summary>
    private static readonly Dictionary<string, string> Fetch = new()
    {
        ["avif"] = "tools/make-avif-references.py",
        ["exr"] = "tools/make-hdr-references.py exr",
        ["jbig2"] = "tools/make-jbig2-references.py --local",
        ["jpeg2000"] = "tools/make-jpeg2000-references.py",
        ["jxl"] = "tools/make-jxl-references.py",
        ["tiff"] = "tools/make-tiff-references.py --pics",
        ["vp8"] = "tools/make-vp8-references.py",
    };

    private static readonly HashSet<string> Asked = [.. (Environment.GetEnvironmentVariable("EGAMI_CORPUS") ?? "")
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(c => c.ToLowerInvariant())];

    /// <summary>Gets whether this run asked for the codec's local corpus.</summary>
    internal static bool Enabled(string codec)
    {
        if (!Fetch.ContainsKey(codec))
        {
            throw new ArgumentException($"No local corpus is named {codec}.", nameof(codec));
        }

        return Asks(codec);
    }

    /// <summary>Gets whether EGAMI_CORPUS names this codec or folder of tests/images, or says "all".</summary>
    internal static bool Asks(string name) => Asked.Contains("all") || Asked.Contains(name);

    /// <summary>Gets the exception that skips a test whose local corpus this run does not read, saying how to get it.</summary>
    internal static Exception Skip(string codec) => Xunit.Sdk.SkipException.ForSkip(Enabled(codec)
        ? $"No local {codec} corpus: run {Fetch[codec]}."
        : $"Set EGAMI_CORPUS={codec} to check the local {codec} corpus.");
}
