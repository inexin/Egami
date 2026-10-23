namespace Egami.Nal;

/// <summary>
/// Builds the exceptions the shared NAL code throws. Each package that compiles this folder supplies, in a partial part of its
/// own, the format name the errors carry (<c>FormatName</c>) and the text every message starts with (<c>Prefix</c>), so an error
/// from shared code reads the same as one from the package's own decoder.
/// </summary>
internal static partial class NalErrors
{
    internal static CorruptDataException Corrupt(string problem) => new(FormatName, -1, Prefix + problem);
}
