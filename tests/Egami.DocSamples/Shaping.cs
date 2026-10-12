using Egami.Fonts;
using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Shaping
{
    private static string FontFile => System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.ttf");

    [Fact]
    public void ShapeARun()
    {
        Typeface face = Typeface.FromFile(FontFile);

        #region run
        // One run, one font, as HarfBuzz's hb_shape: ligatures, kerning and marks from the font's GSUB and GPOS.
        var font = new Font(face, 24);
        var glyphs = new GlyphBuffer();
        string text = "office";
        TextShaper.ShapeRun(text, 0, text.Length, font, new RunProperties { Script = Script.Latin }, glyphs);

        for (int i = 0; i < glyphs.Count; i++)
        {
            ushort id = glyphs.GlyphIds[i];
            int cluster = glyphs.Clusters[i];                 // index in text the glyph came from
            GlyphPosition position = glyphs.Positions[i];     // in the font's units (pixels here)
            bool breakable = (glyphs.Flags[i] & GlyphFlags.UnsafeToBreak) == 0;
        }

        float width = glyphs.Advance;
        #endregion

        Assert.True(glyphs.Count < text.Length); // "ffi" is one ligature in Noto Sans.
        Assert.True(width > 0);
    }

    [Fact]
    public void FeatureSettings()
    {
        Typeface face = Typeface.FromFile(FontFile);
        var font = new Font(face, 24);
        var glyphs = new GlyphBuffer();
        string text = "office 0123";

        #region features
        // HarfBuzz's feature syntax, or the presets.
        FontFeature noLigatures = FontFeature.Parse("liga=0");
        FontFeature figures = FontFeature.TabularFigures;
        FontFeature alternates = FontFeature.StylisticSet(1);
        FontFeature someKerning = FontFeature.Parse("kern[0:3]=0"); // off for the first three characters only

        var properties = new RunProperties { Features = [noLigatures, figures] };
        TextShaper.ShapeRun(text, 0, text.Length, font, properties, glyphs);
        #endregion

        Assert.Equal(text.Length, glyphs.Count);
        Assert.Equal("ss01", alternates.Tag.ToString());
        Assert.False(someKerning.IsGlobal);
    }

    [Fact]
    public void LayoutTables()
    {
        Typeface face = Typeface.FromFile(FontFile);

        #region layout
        // What the font's layout tables list.
        OpenTypeLayout layout = face.Layout;
        IReadOnlyList<FontTag> scripts = layout.Scripts;                                // DFLT, cyrl, grek, latn
        IReadOnlyList<FontTag> languages = layout.GetLanguageSystems(new FontTag("latn"));
        IReadOnlyList<FontTag> features = layout.GetFeatures(new FontTag("latn"));      // liga, kern, ...
        #endregion

        Assert.True(layout.HasGsub && layout.HasGpos);
        Assert.Contains(new FontTag("latn"), scripts);
        Assert.Contains(new FontTag("kern"), features);
        Assert.NotNull(languages);
    }
}
