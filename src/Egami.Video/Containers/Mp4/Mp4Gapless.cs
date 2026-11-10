using System.Globalization;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// iTunes's gapless playback tag, iTunSMPB: a freeform ilst item (mean com.apple.iTunes) whose text is twelve hex fields after a
/// space. The second is the encoder delay (priming) in samples, the third the padding added after the last real sample and the
/// fourth the number of real samples; the rest are zero. Apple's AAC encoders write it, often instead of an edit list, and
/// players use it to trim the priming and the padding.
/// </summary>
internal static class Mp4Gapless
{
    /// <summary>The freeform item's name, which is also the tag's key in <see cref="MediaInfo.Tags"/>.</summary>
    public const string TagName = "iTunSMPB";

    /// <summary>Reads the priming, padding and length from the tag's text; returns <see langword="null"/> when it is not one.</summary>
    public static (long Priming, long Padding, long Samples)? Parse(string value)
    {
        string[] fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 4
            || !long.TryParse(fields[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long priming)
            || !long.TryParse(fields[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long padding)
            || !long.TryParse(fields[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long samples)
            || priming < 0 || padding < 0 || samples < 0)
        {
            return null;
        }

        return (priming, padding, samples);
    }

    /// <summary>Writes the tag's text in Apple's layout.</summary>
    public static string Format(long priming, long padding, long samples) => string.Create(
        CultureInfo.InvariantCulture,
        $" 00000000 {priming:X8} {padding:X8} {samples:X16} 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000");
}
