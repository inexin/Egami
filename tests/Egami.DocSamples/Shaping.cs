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

    [Fact]
    public void ShapeText()
    {
        Typeface face = Typeface.FromFile(FontFile);
        Typeface hebrew = Typeface.FromFile(System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSansHebrew-Regular.ttf"));
        var fonts = new FontCollection();
        fonts.Add(hebrew);

        #region shape
        // Whole text: lines at hard breaks, bidi levels, script runs, fallback fonts, runs in display order.
        var font = new Font(face, 24);
        var options = new ShapingOptions { Fallback = new FontFallback(fonts.Families) };
        ShapedText shaped = TextShaper.Shape("Shalom: \u05E9\u05DC\u05D5\u05DD!", font, options);

        float x = 0;
        foreach (ShapedRun run in shaped.Lines[0].Runs)
        {
            GlyphRun glyphs = run.ToGlyphRun(new System.Numerics.Vector2(x, 0), shaped.Text); // what a canvas draws
            x += run.Advance;
        }
        #endregion

        Assert.Contains(shaped.Lines[0].Runs, r => r.Font.Typeface == hebrew && r.BidiLevel == 1);
    }

    [Fact]
    public void Spans()
    {
        Typeface face = Typeface.FromFile(FontFile);
        var font = new Font(face, 16);

        #region spans
        // A styled range: its own font, language or features.
        var options = new ShapingOptions
        {
            Language = "en",
            Spans = [new TextSpan(6, 6) { Font = font with { Kerning = false }, Features = [FontFeature.SmallCaps] }],
        };
        ShapedText shaped = TextShaper.Shape("Hello, World", font, options);
        #endregion

        Assert.Equal(2, shaped.Lines[0].Runs.Count);
    }

    [Fact]
    public void Fallback()
    {
        #region fallback
        // Characters the font lacks: the caller's families first, then installed fonts only when asked for.
        FontFallback mine = new(FontCollection.System.Families.Where(f => f.Name is "Segoe UI Emoji" or "Noto Color Emoji"));
        FontFallback system = FontFallback.System;      // depends on the machine's fonts
        FontFallback none = FontFallback.None;          // the default: missing characters draw as .notdef
        #endregion

        Assert.NotNull(mine);
        Assert.NotNull(system.Resolver);
        Assert.Empty(none.Families);
    }
}
