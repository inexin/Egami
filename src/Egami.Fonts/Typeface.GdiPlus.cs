using System.Buffers.Binary;

using Egami.Fonts.Tables;

namespace Egami.Fonts;

public sealed partial class Typeface
{
    private Dictionary<int, int>? _vdmx;

    /// <summary>
    /// The cell GDI+ reports and lays text out by, in font units: the Windows ascent and descent, the typographic ones when the
    /// font asks for them (USE_TYPO_METRICS), and the hhea ones when both are zero.
    /// </summary>
    internal (int Ascent, int Descent) GdiPlusCell
    {
        get
        {
            Os2Table os2 = Os2;
            if (!Os2Missing && os2.UseTypoMetrics && (os2.TypoAscender != 0 || os2.TypoDescender != 0))
            {
                return (os2.TypoAscender, -os2.TypoDescender);
            }

            VerticalMetrics win = WinMetrics;
            if (win.Ascent != 0 || win.Descent != 0)
            {
                return (win.Ascent, -win.Descent);
            }

            VerticalMetrics hhea = HheaMetrics;
            return (hhea.Ascent, -hhea.Descent);
        }
    }

    /// <summary>
    /// The VDMX table's largest yMax at a size in whole pixels per em, from its Windows ANSI record for a 1:1 aspect, as GDI uses
    /// it; null where the font has no such entry.
    /// </summary>
    internal int? VdmxYMax(int ppem)
    {
        if (_base is not null)
        {
            return _base.VdmxYMax(ppem);
        }

        Dictionary<int, int> sizes = Volatile.Read(ref _vdmx) ?? Publish(ref _vdmx, ReadVdmx(GetTableSpan(Tags.Vdmx, out int offset), offset >= 0));
        return sizes.TryGetValue(ppem, out int yMax) ? yMax : null;
    }

    // VDMX: a header (version, record count, ratio count), the ratio records (charset, x, y start, y end), an offset to a group per
    // ratio, and the groups (count, first and last size, then per size its pixel height, yMax and yMin). Damaged parts are skipped.
    private static Dictionary<int, int> ReadVdmx(ReadOnlySpan<byte> v, bool present)
    {
        Dictionary<int, int> sizes = [];
        if (!present || v.Length < 6)
        {
            return sizes;
        }

        int ratios = BinaryPrimitives.ReadUInt16BigEndian(v[4..]);
        for (int i = 0; i < ratios; i++)
        {
            int r = 6 + (4 * i), o = 6 + (4 * ratios) + (2 * i);
            if (o + 2 > v.Length)
            {
                break;
            }

            byte charSet = v[r], x = v[r + 1], yStart = v[r + 2], yEnd = v[r + 3];
            bool square = (x == 0 && yStart == 0 && yEnd == 0) || (yStart <= x && x <= yEnd);
            if (charSet != 1 || !square)
            {
                continue;
            }

            int group = BinaryPrimitives.ReadUInt16BigEndian(v[o..]);
            if (group + 4 > v.Length)
            {
                break;
            }

            int count = BinaryPrimitives.ReadUInt16BigEndian(v[group..]);
            for (int j = 0; j < count && group + 4 + (6 * j) + 6 <= v.Length; j++)
            {
                ReadOnlySpan<byte> entry = v[(group + 4 + (6 * j))..];
                sizes.TryAdd(BinaryPrimitives.ReadUInt16BigEndian(entry), BinaryPrimitives.ReadInt16BigEndian(entry[2..]));
            }

            break;
        }

        return sizes;
    }
}
