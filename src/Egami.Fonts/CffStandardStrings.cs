using System.Collections.ObjectModel;

using Egami.Fonts.Cff;

namespace Egami.Fonts;

/// <summary>
/// The Compact Font Format's predefined data (Adobe Technical Note #5176, appendices A and B): the 391 standard strings that
/// string ids (SIDs) below 391 name, and the Standard and Expert encodings as glyph names by code.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="cffstrings"/>
/// </example>
public static class CffStandardStrings
{
    /// <summary>The number of standard strings; SIDs from this one on index a font's own String INDEX.</summary>
    public const int Count = 391;

    private static readonly ReadOnlyCollection<string> s_standard = Array.AsReadOnly(CffStandard.StandardEncodingNames);
    private static readonly ReadOnlyCollection<string> s_expert = Array.AsReadOnly(CffStandard.ExpertEncodingNames);

    /// <summary>Gets the 256 glyph names of the Standard Encoding by code, ".notdef" for codes it leaves unused.</summary>
    public static IReadOnlyList<string> StandardEncoding => s_standard;

    /// <summary>Gets the 256 glyph names of the Expert Encoding by code, ".notdef" for codes it leaves unused.</summary>
    public static IReadOnlyList<string> ExpertEncoding => s_expert;

    /// <summary>Gets a standard string by its SID.</summary>
    /// <param name="sid">The SID, 0 to 390.</param>
    /// <returns>The string, such as "space" for 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sid"/> is negative or 391 or more.</exception>
    public static string Get(int sid)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sid);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sid, Count);
        return CffStandard.GetStandardString(sid);
    }

    /// <summary>Finds the SID of a standard string.</summary>
    /// <param name="name">The string, such as "Aacute"; compared ordinally.</param>
    /// <param name="sid">The SID, or -1 when the string is not a standard one.</param>
    /// <returns><see langword="true"/> if <paramref name="name"/> is a standard string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public static bool TryGetSid(string name, out int sid)
    {
        ArgumentNullException.ThrowIfNull(name);
        sid = CffStandard.SidOf(name);
        return sid >= 0;
    }
}
