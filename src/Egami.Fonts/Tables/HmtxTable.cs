using Egami.Fonts.Parsing;

namespace Egami.Fonts.Tables;

/// <summary>Horizontal metrics ('hmtx'): advances for the first numberOfHMetrics glyphs, the last repeated, and every glyph's bearing.</summary>
internal sealed class HmtxTable
{
    private readonly ushort[] _advances;
    private readonly short[] _bearings;

    private HmtxTable(ushort[] advances, short[] bearings)
    {
        _advances = advances;
        _bearings = bearings;
    }

    public int MetricCount => _advances.Length;

    public int GetAdvance(int glyph) => _advances.Length == 0 ? 0 : _advances[Math.Min(glyph, _advances.Length - 1)];

    public int GetLeftSideBearing(int glyph) => (uint)glyph < (uint)_bearings.Length ? _bearings[glyph] : 0;

    public static HmtxTable Read(ReadOnlySpan<byte> data, long offset, int metricCount, int glyphCount, Action<string, string, long> repair)
    {
        metricCount = Math.Min(metricCount, glyphCount);
        if (metricCount < 1)
        {
            throw FontErrors.Corrupt(offset, "hhea gives no horizontal metrics.");
        }

        int fit = data.Length / 4;
        if (fit < metricCount)
        {
            repair("Font.BadTable", $"The hmtx table holds {fit} of {metricCount} metrics; the last one is repeated.", offset);
            metricCount = Math.Max(1, fit);
            if (fit == 0)
            {
                throw FontErrors.Truncated(offset);
            }
        }

        var advances = new ushort[metricCount];
        var bearings = new short[glyphCount];
        for (int i = 0; i < metricCount; i++)
        {
            advances[i] = BigEndian.UInt16(data, i * 4);
            bearings[i] = BigEndian.Int16(data, (i * 4) + 2);
        }

        int extra = Math.Min(glyphCount - metricCount, (data.Length - (metricCount * 4)) / 2);
        for (int i = 0; i < extra; i++)
        {
            bearings[metricCount + i] = BigEndian.Int16(data, (metricCount * 4) + (i * 2));
        }

        return new HmtxTable(advances, bearings);
    }
}
