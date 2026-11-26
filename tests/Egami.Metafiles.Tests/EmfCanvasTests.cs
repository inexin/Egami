using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.Metafiles.Tests;

/// <summary>
/// EMF writing, stage A: every canvas call drawn straight onto a raster canvas and again through <see cref="EmfCanvas"/> then
/// Egami's own player, EMF+ and GDI parts, compared pixel by pixel.
/// </summary>
public class EmfCanvasTests(ITestOutputHelper output)
{
    internal const int W = 160, H = 120;

    internal static Dictionary<string, Action<Canvas>> Scenes => EmfScenes.Vectors;

    public static TheoryData<string> SceneNames => [.. Scenes.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The scenes without alpha, which GDI draws as Egami does but for antialiasing.</summary>
    public static TheoryData<string> OpaqueSceneNames => [.. Scenes.Keys.Where(k => k is not ("fill-alpha" or "clear-and-paint")).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void EmfPlusPlaysLikeTheRasterCanvas(string scene)
    {
        // Exact but for GDI+'s one-pixel hairline and its curve flattening at a few edge pixels.
        (double mean, int max, int over) = Compare(scene, EmfWriteOptions.Default, MetafilePlayback.Auto);
        Assert.True(mean < 0.3 && over < 50, $"{scene}: mean {mean:F3}, max {max}, {over} pixels off by more than 32");
    }

    [Theory]
    [MemberData(nameof(OpaqueSceneNames))]
    public void GdiPartPlaysCloseToTheRasterCanvas(string scene)
    {
        // GDI has whole-pixel coordinates and GDI+ (whose frame rule the file follows) one pixel more than GDI across the frame,
        // so the GDI part is compared with coverage cut at half and a looser tolerance.
        (double mean, int max, int over) = Compare(scene, EmfWriteOptions.Default, MetafilePlayback.Gdi, opaque: true);
        Assert.True(mean < 6, $"{scene}: mean {mean:F3}, max {max}, {over} pixels off by more than 32");
    }

    private (double Mean, int Max, int Over) Compare(string scene, EmfWriteOptions options, MetafilePlayback playback, bool opaque = false)
    {
        Action<Canvas> draw = Scenes[scene];
        using Raster<Rgba8> direct = Direct(draw);
        byte[] file = Write(draw, options);
        using Raster<Rgba8> played = Play(file, playback);
        if (opaque)
        {
            Flatten(direct);
            Flatten(played);
        }

        (double mean, int max, int over) = Difference(direct, played);
        output.WriteLine($"{scene} {playback}: mean {mean:F3}, max {max}, over {over}");
        return (mean, max, over);
    }

    internal static Raster<Rgba8> Direct(Action<Canvas> draw, int w = W, int h = H)
    {
        Raster<Rgba8> r = Raster.Create<Rgba8>(w, h);
        using (RasterCanvas<Rgba8> c = r.CreateCanvas())
        {
            draw(c);
        }

        return r;
    }

    internal static byte[] Write(Action<Canvas> draw, EmfWriteOptions? options = null, int w = W, int h = H)
    {
        using MemoryStream ms = new();
        using (EmfCanvas c = new(ms, w, h, options))
        {
            draw(c);
        }

        return ms.ToArray();
    }

    internal static Raster<Rgba8> Play(byte[] file, MetafilePlayback playback = MetafilePlayback.Auto, int w = W, int h = H)
        => Metafile.Load(file).Rasterize<Rgba8>(new MetafileRenderOptions { Width = w, Height = h, Playback = playback });

    /// <summary>The mean channel difference, the largest, and how many pixels differ by more than 32 in a channel.</summary>
    internal static (double Mean, int Max, int Over) Difference(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        long sum = 0;
        int max = 0, over = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<Rgba8> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                Rgba8 p = ra[x], q = rb[x];
                int d = Math.Max(Math.Max(Math.Abs(p.R - q.R), Math.Abs(p.G - q.G)), Math.Max(Math.Abs(p.B - q.B), Math.Abs(p.A - q.A)));
                sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) + Math.Abs(p.A - q.A);
                max = Math.Max(max, d);
                over += d > 32 ? 1 : 0;
            }
        }

        return (sum / (4.0 * a.Width * a.Height), max, over);
    }

    /// <summary>Every pixel over white, then any partly covered one as either its color or white: what GDI draws, roughly.</summary>
    private static void Flatten(Raster<Rgba8> r)
    {
        for (int y = 0; y < r.Height; y++)
        {
            Span<Rgba8> row = r.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                Rgba8 p = row[x];
                row[x] = p.A < 128 ? new Rgba8(255, 255, 255, 255) : new Rgba8(p.R, p.G, p.B, 255);
            }
        }
    }

    internal static Path Star(Vector2 center, float radius, FillRule rule) => EmfScenes.Star(center, radius, rule);
}
