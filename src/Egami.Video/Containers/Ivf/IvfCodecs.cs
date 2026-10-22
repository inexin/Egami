namespace Egami.Video.Containers.Ivf;

/// <summary>The FourCCs IVF files name their codec with, mapped to and from Matroska codec ids.</summary>
internal static class IvfCodecs
{
    private static readonly (string FourCC, string CodecId)[] s_map =
    [
        ("VP80", CodecIds.Vp8),
        ("VP90", CodecIds.Vp9),
        ("AV01", CodecIds.Av1),
    ];

    public static string? CodecId(string fourCC)
    {
        foreach ((string f, string id) in s_map)
        {
            if (string.Equals(f, fourCC, StringComparison.Ordinal))
            {
                return id;
            }
        }

        return null;
    }

    public static string? FourCC(string codecId)
    {
        foreach ((string f, string id) in s_map)
        {
            if (string.Equals(id, codecId, StringComparison.Ordinal))
            {
                return f;
            }
        }

        return null;
    }

    public static string Printable(string fourCC) =>
        string.Concat(fourCC.Select(c => c is >= ' ' and <= '~' ? c : '?'));
}
