using Egami.Fonts;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>Light hinting's upgrade (2.7.5 tasks 24 and 25): the x-height scale tweak, zones active under 3/4 pixel, stems kept whole.</summary>
public class LightHintingTests
{
    private static Typeface Noto => TextTests.Noto.Value;

    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(19)]
    public void XHeightLandsOnAWholePixel(int ppem)
    {
        float sy = ppem / (float)Noto.UnitsPerEm;
        HintMap map = HintMap.For(Noto, sy)!;
        float x = map.Map(Noto.GetGlyphExtents(Noto.GetGlyph('x')).YMax);
        Assert.Equal(MathF.Round(x), x, 3);
    }

    [Fact]
    public void TallZonesAreNotSnapped()
    {
        // At 100 pixels per em Noto's round overshoots are 3/4 pixel or more: 'o' keeps its overshoot unrounded, as a scale would put
        // it, while at 12 pixels per em it snaps to whole pixels.
        float top = Noto.GetGlyphExtents(Noto.GetGlyph('o')).YMax;
        float big = 100f / Noto.UnitsPerEm, small = 12f / Noto.UnitsPerEm;
        HintMap atBig = HintMap.For(Noto, big)!, atSmall = HintMap.For(Noto, small)!;
        Assert.Equal(MathF.Round(atSmall.Map(top)), atSmall.Map(top), 3);
        Assert.NotEqual(MathF.Round(atBig.Map(top)), atBig.Map(top));
    }

    [Theory]
    [InlineData('H')]
    [InlineData('e')]
    public void CrossbarsSitOnWholePixelsWithTheirHeightKept(char c)
    {
        ushort glyph = Noto.GetGlyph(c);
        float sy = 13f / Noto.UnitsPerEm;
        var sink = new SegmentSink(Noto.UnitsPerEm);
        Noto.GetGlyphOutline(glyph, sink);
        HintMap map = HintMap.For(Noto, sy, glyph)!;

        // The crossbar: the lowest pair of horizontal edges between the baseline and the x-height (or, for H, the cap height).
        float top = c == 'H' ? Noto.GetGlyphExtents(glyph).YMax : Noto.GetGlyphExtents(Noto.GetGlyph('x')).YMax;
        var edges = sink.Horizontals.Select(h => h.Y).Where(y => y > Noto.UnitsPerEm / 20f && y < top * 0.9f).Distinct().Order().ToList();
        Assert.True(edges.Count >= 2, $"{c} has {edges.Count} crossbar edges.");
        float low = edges[0], high = edges[1];
        Assert.Equal(MathF.Round(map.Map(low)), map.Map(low), 3);
        float height = map.Map(high) - map.Map(low);
        Assert.InRange(height, (high - low) * sy * 0.9f, (high - low) * sy * 1.1f);
    }
}
