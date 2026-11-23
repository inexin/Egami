namespace Egami.Heic.Hevc;

/// <summary>The errors of the HEVC parser the hardware decoder compiles in, in the hardware decoder's words.</summary>
internal static class HevcErrors
{
    internal const string FormatName = "HEVC";

    internal const string Prefix = "HEVC: ";

    internal static CorruptDataException Corrupt(string problem) => new(FormatName, -1, Prefix + problem);

    internal static UnsupportedFormatException Unsupported(string what) =>
        new($"HEVC: {what} The hardware decoder does not decode it (it decodes 4:2:0 streams in the Main and Main 10 profiles); Egami.Video.Hevc may.");

    /// <summary>A stream larger than the GPU decodes: its size, level or number of reference pictures.</summary>
    internal static UnsupportedFormatException Beyond(string what) => new($"HEVC: {what} Egami.Video.Hevc may decode it.");
}
