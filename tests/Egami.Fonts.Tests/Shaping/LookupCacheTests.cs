using Egami.Fonts.Shaping;

namespace Egami.Fonts.Tests.Shaping;

/// <summary>The per-face lookups shaping keeps (GDEF glyph properties, the Latin-1 glyphs) give what the tables give.</summary>
public sealed class LookupCacheTests
{
    [Theory]
    [InlineData("DejaVuSans.ttf")]
    [InlineData("NotoSans-Regular.ttf")]
    [InlineData("SourceSans3-Regular.otf")]
    [InlineData("Roboto-Regular.ttf")]
    public void KeptGlyphPropertiesEqualTheClassTables(string file)
    {
        GdefTable kept = OpenTypeLayoutTables.For(TestFonts.Load(file)).Gdef;
        Assert.True(kept.HasGlyphClasses);
        GdefTable fresh = new(kept.Data);
        for (uint glyph = 0; glyph < 70000; glyph++)
        {
            int expected = fresh.LookUpGlyphProps(glyph);
            Assert.Equal(expected, kept.GetGlyphProps(glyph));
            Assert.Equal(expected, kept.GetGlyphProps(glyph));
        }
    }

    [Theory]
    [InlineData("DejaVuSans.ttf")]
    [InlineData("NotoSerif-Regular.ttf")]
    [InlineData("SourceSerif4-Regular.otf")]
    public void LatinGlyphsEqualTheCharacterMap(string file)
    {
        Typeface face = TestFonts.Load(file);
        CharacterMap map = face.CharacterMaps.First(m => m.PlatformId == face.Cmap.PlatformId && m.EncodingId == face.Cmap.EncodingId);
        for (int c = 0; c < 0x300; c++)
        {
            Assert.Equal(map.GetGlyph(c), face.GetGlyph(c));
        }
    }
}
