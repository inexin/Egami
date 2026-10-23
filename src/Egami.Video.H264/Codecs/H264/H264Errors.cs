namespace Egami.Video.Codecs.H264;

/// <summary>Builds the exceptions the H.264 decoder throws.</summary>
internal static class H264Errors
{
    /// <summary>The format name errors carry.</summary>
    internal const string FormatName = "H.264";

    /// <summary>What every message starts with.</summary>
    internal const string Prefix = "H.264: ";

    internal static CorruptDataException Corrupt(string problem) => new(FormatName, -1, Prefix + problem);

    internal static UnsupportedFormatException Unsupported(string what) => new($"H.264: {what} Egami.Video.H264 does not decode it (V7 decodes progressive Constrained Baseline, Main, High, High 10 and High 4:2:2 streams).");
}
