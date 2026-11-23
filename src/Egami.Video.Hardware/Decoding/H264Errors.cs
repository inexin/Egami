namespace Egami.Video.Codecs.H264;

/// <summary>
/// Builds the exceptions that Egami's H.264 readers throw, compiled into Egami.Video.Hardware with them: the hardware
/// decoder's wording of what it does not decode.
/// </summary>
internal static class H264Errors
{
    /// <summary>The format name errors carry.</summary>
    internal const string FormatName = "H.264";

    /// <summary>What every message starts with.</summary>
    internal const string Prefix = "H.264: ";

    internal static CorruptDataException Corrupt(string problem) => new(FormatName, -1, Prefix + problem);

    internal static UnsupportedFormatException Unsupported(string what) =>
        new($"H.264: {what} The hardware decoder does not decode it (it decodes progressive 8-bit 4:2:0 streams); Egami.Video.H264 may.");
}
