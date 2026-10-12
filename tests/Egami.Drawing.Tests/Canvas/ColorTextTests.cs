using System.Numerics;

using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// Drawing color glyphs (2.7.2 tasks 12 to 14): COLR, sbix and CBDT glyphs draw in color through canvas calls on every backend,
/// cached as RGBA on a raster with the same pixels as uncached, as one group under the paint's alpha.
/// </summary>
public class ColorTextTests
{
    private static readonly Lazy<Typeface> ColrV1 = new(() => Typeface.FromFile(TextTests.FontPath("derived/NotoColorEmoji-COLRv1-subset.ttf")));
    private static readonly Lazy<Typeface> ColrV0 = new(() => Typeface.FromFile(TextTests.FontPath("derived/Twemoji-COLRv0-subset.ttf")));
    private static readonly Lazy<Typeface> Sbix = new(() => Typeface.FromFile(TextTests.FontPath("derived/Twemoji-sbix.ttf")));
    private static readonly Lazy<Typeface> Cbdt = new(() => Typeface.FromFile(TextTests.FontPath("derived/NotoColorEmoji-CBDT-subset.ttf")));

    public static TheoryData<string> Faces() => ["colr1", "colr0", "sbix", "cbdt"];

    [Theory]
    [MemberData(nameof(Faces))]
    public void EmojiDrawInColor(string kind)
    {
        Typeface face = Face(kind);
        using Raster<Rgba8> image = Draw(new Font(face, 40), Rgba8.Black);
        Assert.Contains(Pixels(image), p => Chroma(p) > 60);

        // Off, the outlines draw in the paint: gray at most, nothing for a bitmap-only face.
        using Raster<Rgba8> plain = Draw(new Font(face, 40) { ColorGlyphs = false }, Rgba8.Black);
        Assert.All(Pixels(plain), p => Assert.True(Chroma(p) <= 1));
    }

    [Theory]
    [MemberData(nameof(Faces))]
    public void CachedAndUncachedColorPixelsAreIdentical(string kind)
    {
        Typeface face = Face(kind);
        long limit = GlyphCache.ByteLimit;
        try
        {
            GlyphCache.Clear();
            using Raster<Rgba8> cold = Draw(new Font(face, 33), Rgba8.Black);
            using Raster<Rgba8> warm = Draw(new Font(face, 33), Rgba8.Black);
            GlyphCache.ByteLimit = 0;
            using Raster<Rgba8> uncached = Draw(new Font(face, 33), Rgba8.Black);
            Assert.Equal(Pixels(cold), Pixels(warm));
            Assert.Equal(Pixels(cold), Pixels(uncached));
        }
        finally
        {
            GlyphCache.ByteLimit = limit;
        }
    }

    [Fact]
    public void CachedColorGlyphsMatchCanvasCalls()
    {
        // The same glyph through the cache (at a whole-pixel origin) and straight through canvas calls.
        Typeface face = ColrV1.Value;
        var font = new Font(face, 48);
        ushort glyph = face.GetGlyph(0x1F600);
        Assert.True(face.TryGetColorGlyph(glyph, null, out ColorGlyph? color));
        using Raster<Rgba8> cached = Raster.Create(80, 80, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = cached.CreateCanvas())
        {
            canvas.DrawGlyphRun(GlyphRun.Create(font, [glyph], [new Vector2(10, 60)]), Vector2.Zero, Rgba8.Black);
        }

        using Raster<Rgba8> direct = Raster.Create(80, 80, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = direct.CreateCanvas())
        {
            ColorGlyphDrawing.Draw(canvas, font, face, color, new Vector2(10, 60), new SolidPaint(Rgba8.Black));
        }

        int worst = Pixels(cached).Zip(Pixels(direct)).Max(p => Math.Max(Math.Abs(p.First.R - p.Second.R), Math.Max(Math.Abs(p.First.G - p.Second.G), Math.Abs(p.First.B - p.Second.B))));
        Assert.True(worst <= 2, $"The largest difference is {worst} levels.");
    }

    [Fact]
    public void ColrGlyphsReachOtherBackendsAsCanvasCalls()
    {
        var counter = new CallCounter();
        counter.DrawText("\U0001F600\U0001F680", new Vector2(10, 60), new Font(ColrV1.Value, 48), Rgba8.Black);
        Assert.True(counter.Clips > 0);
        Assert.True(counter.Gradients > 0);
        Assert.Equal(0, counter.Paths);
    }

    [Fact]
    public void TheGlyphIsOneGroupUnderThePaintsAlpha()
    {
        var font = new Font(ColrV1.Value, 40);
        using Raster<Rgba8> opaque = Draw(font, Rgba8.Black);
        using Raster<Rgba8> half = Draw(font, new Rgba8(0, 0, 0, 128));
        Rgba8[] a = Pixels(opaque), b = Pixels(half);
        float alpha = 128 / 255f;
        for (int i = 0; i < a.Length; i++)
        {
            int expected = (int)MathF.Round(255 + ((a[i].R - 255) * alpha));
            Assert.InRange(b[i].R, expected - 3, expected + 3);
        }
    }

    [Fact]
    public void PalettesChangeTheColors()
    {
        Typeface face = Typeface.FromFile(TextTests.FontPath("test_glyphs-glyf_colr_1.ttf"));
        Assert.True(face.Palettes.Count >= 2);
        ushort glyph = Enumerable.Range(0, face.GlyphCount).Select(g => (ushort)g).First(g => face.HasColorGlyph(g));
        var font = new Font(face, 40);
        using Raster<Rgba8> first = DrawGlyph(font, glyph);
        using Raster<Rgba8> second = DrawGlyph(font with { Palette = new FontPalette(1) }, glyph);
        Assert.NotEqual(Pixels(first), Pixels(second));
    }

    private static Typeface Face(string kind) => kind switch
    {
        "colr1" => ColrV1.Value,
        "colr0" => ColrV0.Value,
        "sbix" => Sbix.Value,
        _ => Cbdt.Value,
    };

    private static Raster<Rgba8> Draw(Font font, Rgba8 color)
    {
        Raster<Rgba8> image = Raster.Create(260, 70, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.DrawText("\U0001F600\U0001F680⭐\U0001F44D", new Vector2(6.3f, 52), font, color);
        return image;
    }

    private static Raster<Rgba8> DrawGlyph(Font font, ushort glyph)
    {
        Raster<Rgba8> image = Raster.Create(60, 60, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.DrawGlyphRun(GlyphRun.Create(font, [glyph], [new Vector2(5, 48)]), Vector2.Zero, Rgba8.Black);
        return image;
    }

    private static int Chroma(Rgba8 p) => Math.Max(p.R, Math.Max(p.G, p.B)) - Math.Min(p.R, Math.Min(p.G, p.B));

    private static Rgba8[] Pixels(Raster<Rgba8> image)
    {
        var all = new Rgba8[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                all[(y * image.Width) + x] = image.GetPixel(x, y);
            }
        }

        return all;
    }

    private sealed class CallCounter() : Egami.Drawing.Canvas(300, 100)
    {
        public int Clips { get; private set; }

        public int Gradients { get; private set; }

        public int Paths { get; private set; }

        protected override void OnClipPath(Path path, ClipOp op, bool antialias) => Clips++;

        protected override void OnDrawPaint(Paint paint)
        {
            if (paint is Gradient)
            {
                Gradients++;
            }
        }

        protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint) => Paths++;

        protected override void OnDrawImage(Raster image, RectF source, RectF destination, Operations.SamplingOptions sampling, Paint? paint)
        {
        }
    }
}
