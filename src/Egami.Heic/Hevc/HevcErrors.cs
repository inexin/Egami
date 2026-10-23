namespace Egami.Heic.Hevc;

/// <summary>Builds the exceptions the HEVC decoder throws.</summary>
internal static class HevcErrors
{
    /// <summary>The format name errors carry.</summary>
    internal const string FormatName = "HEIC";

    /// <summary>What every message starts with.</summary>
    internal const string Prefix = "HEVC: ";

    internal static CorruptDataException Corrupt(string problem) => new(FormatName, -1, Prefix + problem);

    internal static UnsupportedFormatException Unsupported(string what) => new($"HEVC: {what} Egami.Heic does not decode it (2.9.4 decodes intra pictures of the Main, Main 10, Main Still Picture and format range extensions profiles up to 12 bits).");
}
