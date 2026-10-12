using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Text
{
    private static string CffFile => System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "SourceSans3-Regular.otf");

    private static string FontFile => System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.ttf");

    private static string VariableFile => System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "RobotoFlex-subset.ttf");

    private static string WebFile => System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.woff2");

    [Fact]
    public void LoadAFace()
    {
        #region load
        // One face from a file; a collection takes an index. The bytes are read once and every table is parsed when first needed.
        Typeface face = Typeface.FromFile(FontFile);
        Console.WriteLine($"{face.FamilyName} {face.SubfamilyName}, {face.GlyphCount} glyphs, {face.UnitsPerEm} units per em");

        // What a file holds, from its header alone.
        FontProbe probe = FontLoader.Probe(File.ReadAllBytes(FontFile));
        Console.WriteLine($"{probe.Kind}, {probe.FaceCount} face(s), {probe.Outlines} outlines");

        // Damaged fonts load leniently with diagnostics; Strict throws instead.
        var seen = new List<EgamiDiagnostic>();
        var options = new FontLoadOptions { Context = EgamiContext.Default with { Diagnostics = seen.Add } };
        Typeface lenient = Typeface.FromMemory(File.ReadAllBytes(FontFile), 0, options);
        #endregion

        Assert.Equal("Noto Sans", face.FamilyName);
        Assert.Equal(FontFileKind.Sfnt, probe.Kind);
        Assert.Empty(seen);
        Assert.Equal(face.GlyphCount, lenient.GlyphCount);
    }

    [Fact]
    public void WebFonts()
    {
        #region web
        // WOFF and WOFF2 files load like any font: they are unwrapped into an ordinary font in memory once.
        Typeface face = Typeface.FromFile(WebFile);
        FontFileKind container = face.Container;
        string? metadata = face.GetWoffMetadata();
        #endregion

        Assert.Equal(FontFileKind.Woff2, container);
        Assert.Equal("Noto Sans", face.FamilyName);
        Assert.Null(metadata);
    }

    [Fact]
    public void VariableFonts()
    {
        #region variable
        // A variable font's axes and named instances, and the face at any position on them.
        Typeface flex = Typeface.FromFile(VariableFile);
        foreach (VariationAxis axis in flex.VariationAxes)
        {
            Console.WriteLine($"{axis.Tag} {axis.Name}: {axis.Min} to {axis.Max}, default {axis.Default}");
        }

        Typeface semiBold = flex.WithVariations(FontVariation.Weight(600), FontVariation.Width(90));
        Typeface black = flex.WithNamedInstance(flex.NamedInstances.Count - 1);

        // The optical size follows the font size unless the face sets it (OpticalSizing.None turns that off).
        var caption = new Font(semiBold, 11);
        Typeface drawn = caption.EffectiveTypeface;

        // For a PDF, the subset is a static font at the face's position, which every viewer draws as it is.
        FontSubset subset = FontSubsetter.Subset(semiBold, caption.Shape("Static at 600").Glyphs.ToArray());
        #endregion

        Assert.True(flex.IsVariable);
        Assert.Contains(FontVariation.Weight(600), semiBold.Variations);
        Assert.Contains(FontVariation.OpticalSize(11), drawn.Variations);
        Assert.NotEqual(flex.PostScriptName, black.PostScriptName);
        Assert.False(subset.ToTypeface().IsVariable);
    }

    [Fact]
    public void RenderingQuality()
    {
        Typeface face = Typeface.FromFile(FontFile);
        using Raster<Rgba8> image = Raster.Create(300, 60, Rgba8.White);

        #region quality
        // Small text for a screen: light hinting (vertical only), quarter-pixel origins (the default) and, for an RGB display
        // the image will not leave, LCD edging.
        var screen = new Font(face, 13) { Hinting = FontHinting.Light, Edging = FontEdging.Lcd };
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(new CanvasOptions { PixelGeometry = PixelGeometry.RgbHorizontal }))
        {
            canvas.DrawText("Crisp caption", new Vector2(8, 24), screen, Rgba8.Black);

            // A face matched for bold italic it lacks is emboldened and slanted, unless AllowSynthesis is off.
            var synthetic = new Font(face, 13) { Synthesis = FontSynthesis.Bold | FontSynthesis.Italic };
            canvas.DrawText("Synthetic bold italic", new Vector2(8, 48), synthetic, Rgba8.Black);
        }
        #endregion

        int inked = 0;
        for (int x = 0; x < image.Width; x++)
        {
            inked += image.GetPixel(x, 20) != Rgba8.White ? 1 : 0;
        }

        Assert.True(inked > 10);
    }

    [Fact]
    public void TextAlongAPath()
    {
        Typeface face = Typeface.FromFile(FontFile);
        using Raster<Rgba8> image = Raster.Create(320, 180, Rgba8.White);

        #region path
        // A label along a curve: each glyph's midpoint on the path, turned to its direction, as SVG's textPath. The result is
        // still a glyph run, so a PDF backend writes it as searchable text.
        Path curve = new PathBuilder().MoveTo(20, 150).CubicTo(80, 20, 240, 20, 300, 150).ToPath();
        var font = new Font(face, 18);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas())
        {
            canvas.DrawTextOnPath("Text that follows the curve", curve, font, Rgba8.Black,
                new TextOnPathOptions { StartFraction = 0.5f, Anchor = TextAnchor.Middle, BaselineShift = 4 });
        }

        // Or place glyphs yourself, each with its own turn and scale.
        GlyphRun dial = GlyphRun.CreateTransformed(font, [face.GetGlyph('N'), face.GetGlyph('E')],
            [GlyphTransform.FromRotationScale(1, 0, new Vector2(150, 30)), GlyphTransform.FromRotationScale(1, 90, new Vector2(290, 90))]);
        #endregion

        Assert.Equal(2, dial.Count);
        int inked = 0;
        for (int x = 0; x < image.Width; x++)
        {
            inked += image.GetPixel(x, 60) != Rgba8.White ? 1 : 0;
        }

        Assert.True(inked > 5);
    }

    [Fact]
    public void GlyphsAndMetrics()
    {
        Typeface face = Typeface.FromFile(FontFile);

        #region glyphs
        // Character map, advance, ink box and kerning, all in font units with y up.
        ushort a = face.GetGlyph('A'), v = face.GetGlyph('V');
        float advance = face.GetAdvance(a);
        GlyphExtents ink = face.GetGlyphExtents(a);
        float kern = face.GetKerning(a, v);
        FontExtents extents = face.Extents;

        // An outline arrives through a sink: lines and quadratics for TrueType, lines and cubics for CFF.
        var counter = new SegmentCounter();
        face.GetGlyphOutline(a, counter);
        #endregion

        Assert.True(advance > 0 && !ink.IsEmpty && kern < 0 && extents.LineHeight > 0 && counter.Segments > 3);
    }

    [Fact]
    public void Caption()
    {
        #region caption
        // A caption on a photo: white text with a dark shadow, its baseline 24 pixels above the bottom.
        using Raster<Rgba8> photo = Raster.Create(640, 360, new Rgba8(90, 120, 160, 255));
        var font = new Font(Typeface.FromFile(FontFile), 28);
        using (RasterCanvas<Rgba8> canvas = photo.CreateCanvas())
        {
            var origin = new Vector2(24, photo.Height - 24);
            canvas.DrawText("Lofoten, March", origin + new Vector2(2, 2), font, new Rgba8(0, 0, 0, 160));
            canvas.DrawText("Lofoten, March", origin, font, Rgba8.White);
        }
        #endregion

        Assert.Equal(Rgba8.White, Enumerable.Range(24, 200).Select(x => photo.GetPixel(x, 320)).FirstOrDefault(p => p == Rgba8.White));
    }

    [Fact]
    public void LabelMeasuredToFit()
    {
        Typeface face = Typeface.FromFile(FontFile);

        #region measure
        // A label shrunk until it fits a 200-pixel button, then centred on it.
        const string label = "Download the full report";
        var box = new RectF(20, 20, 200, 40);
        var font = new Font(face, 24);
        while (font.MeasureText(label).Advance > box.Width - 16 && font.Size > 8)
        {
            font = new Font(face, font.Size - 1);
        }

        TextMeasure size = font.MeasureText(label);
        var origin = new Vector2(
            box.X + ((box.Width - size.Advance) / 2),
            box.Y + ((box.Height - size.Height) / 2) + size.Ascent);
        using Raster<Rgba8> image = Raster.Create(240, 80, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas())
        {
            canvas.FillRoundRect(new RoundRect(box, 8), new Rgba8(30, 90, 200, 255));
            canvas.DrawText(label, origin, font, Rgba8.White);
        }
        #endregion

        Assert.True(size.Advance <= box.Width - 16);
    }

    [Fact]
    public void SubsetForAPdf()
    {
        Typeface face = Typeface.FromFile(FontFile);

        #region subset
        // Keep only the glyphs a page uses, as a PDF writer embeds them: renumbered from 0, with the map to write the text with.
        GlyphRun run = new Font(face, 12).Shape("Invoice 2026-10");
        FontSubset subset = FontSubsetter.Subset(face, run.Glyphs.ToArray(), new SubsetOptions
        {
            FontNamePrefix = "ABCDEF+",
            KeepCmap = false,
        });
        byte[] fontFile2 = subset.Data.ToArray();
        ushort first = subset.GlyphMap[run.Glyphs[0]];

        // The face's embedding permissions are reported, never enforced: the writer decides.
        bool restricted = face.Embedding.HasFlag(FontEmbedding.Restricted);
        #endregion

        Assert.True(fontFile2.Length < 20_000);
        Assert.True(first > 0);
        Assert.False(restricted);
    }

    [Fact]
    public void BareCff()
    {
        Typeface otf = Typeface.FromMemory(FontSubsetter.Subset(Typeface.FromFile(FontFile), [36]).Data);
        _ = otf;
        byte[] fontFile3 = FontSubsetter.Subset(Typeface.FromFile(CffFile), [5, 6], new SubsetOptions { Format = SubsetFormat.BareCff }).Data.ToArray();

        #region cff
        // A CFF stream as a PDF embeds it (FontFile3): no character map, glyphs by id; widths from the PDF when it gives them.
        Typeface embedded = Typeface.FromCff(fontFile3, new CffOptions { UnitsPerEm = 1000 });
        float width = embedded.GetAdvance(1);
        #endregion

        Assert.Equal(3, embedded.GlyphCount);
        Assert.True(width > 0);
    }

    [Fact]
    public void InstalledFonts()
    {
        #region match
        // Installed fonts, found without native calls and indexed on first use; a style is matched by the CSS rules.
        FontMatch? match = FontCollection.System.Match(GenericFamily.SansSerif, FontStyle.Bold);

        // Your own fonts work the same way.
        var fonts = new FontCollection();
        fonts.AddFile(FontFile);
        FontMatch noto = fonts.Match("Noto Sans", FontStyle.BoldItalic)!.Value;
        Font font = noto.ToFont(16);
        #endregion

        Assert.Equal(FontSynthesis.Bold | FontSynthesis.Italic, font.Synthesis);
        _ = match;
    }

    [Fact]
    public void TextAsPaths()
    {
        var font = new Font(Typeface.FromFile(FontFile), 72);

        #region paths
        // Text as a shape: outline it, or clip a picture to it.
        Path title = font.GetTextPath("EGAMI", new Vector2(10, 80));
        using Raster<Rgba8> image = Raster.Create(300, 100, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas())
        {
            canvas.StrokePath(title, new Stroke(2), Rgba8.Black);
            canvas.ClipPath(title);
            canvas.FillRect(new RectF(0, 50, 300, 50), Rgba8.Red);
        }
        #endregion

        Assert.False(title.IsEmpty);
    }

    [Fact]
    public void TheGlyphCache()
    {
        #region cache
        // Small text draws from a process-wide cache of glyph masks; give it more room for many fonts and sizes.
        GlyphCache.ByteLimit = 16 * 1024 * 1024;
        GlyphCache.Clear();
        #endregion

        GlyphCache.ByteLimit = 4 * 1024 * 1024;
    }

    private sealed class SegmentCounter : IGlyphOutlineSink
    {
        public int Segments { get; private set; }

        public void MoveTo(float x, float y) => Segments++;

        public void LineTo(float x, float y) => Segments++;

        public void QuadTo(float cx, float cy, float x, float y) => Segments++;

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => Segments++;

        public void Close()
        {
        }
    }
}
