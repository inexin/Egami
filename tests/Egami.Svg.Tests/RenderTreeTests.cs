using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.Svg.Tests;

/// <summary>Task 5: the render tree, its cache and edits, use and references; task 6's basic drawing checks.</summary>
public class RenderTreeTests
{
    private const string Ns = "xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'";

    [Theory]
    [MemberData(nameof(TestSvg.CorpusData), MemberType = typeof(TestSvg))]
    public void SaveAndLoadGiveTheSameRenderTree(string name)
    {
        SvgDocument document = SvgDocument.Load(TestSvg.CorpusFile(name));
        SvgDocument again = SvgDocument.Parse(SaveTests.Save(document));
        AssertSameNode(document.RenderTree, again.RenderTree, name);
    }

    [Fact]
    public void SuiteFilesResolveWithoutThrowing()
    {
        List<string> failed = [];
        foreach (string test in TestSvg.Suite())
        {
            try
            {
                _ = SvgDocument.Load(TestSvg.SuiteFile(test)).RenderTree;
            }
            catch (SvgException)
            {
            }
            catch (Exception e)
            {
                failed.Add($"{test}: {e.GetType().Name}: {e.Message}");
            }
        }

        Assert.Empty(failed);
    }

    [Fact]
    public void ARectDrawsAsTheSameCanvasCalls()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='40' height='30'><rect x='5.5' y='4.25' width='20' height='13' fill='#3080c0' fill-opacity='0.5'/></svg>");
        using Raster<Rgba8> svg = document.Rasterize<Rgba8>();
        using Raster<Rgba8> direct = Raster.Create<Rgba8>(40, 30);
        using (RasterCanvas<Rgba8> canvas = direct.CreateCanvas())
        {
            canvas.FillPath(Path.Rect(new RectF(5.5f, 4.25f, 20, 13)), new SolidPaint(new RgbaF32(0x30 / 255f, 0x80 / 255f, 0xc0 / 255f, 0.5f)));
        }

        AssertSamePixels(direct, svg);
    }

    [Fact]
    public void ACircleAndAPathDrawAsTheSameCanvasCalls()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='50' height='50'><circle cx='20' cy='22' r='13' fill='none' stroke='red' stroke-width='3'/><path d='M5 45 C 15 30 35 60 45 40' stroke='#0a0' stroke-linecap='round' fill='none'/></svg>");
        using Raster<Rgba8> svg = document.Rasterize<Rgba8>(new SvgRenderOptions { CanvasOptions = CanvasOptions.Default });
        using Raster<Rgba8> direct = Raster.Create<Rgba8>(50, 50);
        using (RasterCanvas<Rgba8> canvas = direct.CreateCanvas())
        {
            Path circle = new PathBuilder().MoveTo(33, 22)
                .ArcTo(new Vector2(13, 13), 0, false, true, new Vector2(20, 35))
                .ArcTo(new Vector2(13, 13), 0, false, true, new Vector2(7, 22))
                .ArcTo(new Vector2(13, 13), 0, false, true, new Vector2(20, 9))
                .ArcTo(new Vector2(13, 13), 0, false, true, new Vector2(33, 22))
                .Close().ToPath();
            canvas.StrokePath(circle, new Stroke(3), new SolidPaint(new RgbaF32(1, 0, 0, 1)));
            canvas.StrokePath(Path.ParseSvg("M5 45 C 15 30 35 60 45 40"), new Stroke(1) { Cap = LineCap.Round }, new SolidPaint(new RgbaF32(0, 0xaa / 255f, 0, 1)));
        }

        AssertSamePixels(direct, svg);
    }

    [Fact]
    public void AnEditRedraws()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='10' height='10'><rect id='r' width='10' height='10' fill='red'/></svg>");
        using Raster<Rgba8> before = document.Rasterize<Rgba8>();
        Assert.Equal(new Rgba8(255, 0, 0, 255), Pixel(before, 5, 5));
        SvgGroup tree = document.RenderTree;
        document.FindById("r")!.Attributes["fill"] = "blue";
        Assert.NotSame(tree, document.RenderTree);
        using Raster<Rgba8> after = document.Rasterize<Rgba8>();
        Assert.Equal(new Rgba8(0, 0, 255, 255), Pixel(after, 5, 5));
        document.Root.Children.Add(new SvgElement("rect") { Attributes = { ["width"] = "5", ["height"] = "10", ["fill"] = "lime" } });
        using Raster<Rgba8> added = document.Rasterize<Rgba8>();
        Assert.Equal(new Rgba8(0, 255, 0, 255), Pixel(added, 2, 5));
        Assert.Equal(new Rgba8(0, 0, 255, 255), Pixel(added, 7, 5));
    }

    [Fact]
    public void StylesCascade()
    {
        SvgDocument document = SvgDocument.Parse($$"""
            <svg {{Ns}} width='30' height='10'>
              <style>rect { fill: blue } .b { fill: lime } #c { fill: yellow !important }</style>
              <rect width='10' height='10' fill='red'/>
              <rect class='b' x='10' width='10' height='10' fill='red'/>
              <rect id='c' x='20' width='10' height='10' style='fill: red'/>
            </svg>
            """);
        using Raster<Rgba8> raster = document.Rasterize<Rgba8>();
        Assert.Equal(new Rgba8(0, 0, 255, 255), Pixel(raster, 5, 5));
        Assert.Equal(new Rgba8(0, 255, 0, 255), Pixel(raster, 15, 5));
        Assert.Equal(new Rgba8(255, 255, 0, 255), Pixel(raster, 25, 5));
    }

    [Fact]
    public void UseCopiesInheritFromTheUse()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='20' height='10'><defs><rect id='r' width='10' height='10'/></defs><use xlink:href='#r' fill='lime'/><use href='#r' x='10' fill='blue'/></svg>");
        using Raster<Rgba8> raster = document.Rasterize<Rgba8>();
        Assert.Equal(new Rgba8(0, 255, 0, 255), Pixel(raster, 5, 5));
        Assert.Equal(new Rgba8(0, 0, 255, 255), Pixel(raster, 15, 5));
    }

    [Fact]
    public void ReferenceCyclesAreReportedNotFollowed()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='10' height='10'><g id='a'><use href='#b'/></g><g id='b'><use href='#a'/></g><use id='self' href='#self'/></svg>");
        Assert.Contains(document.Warnings, w => w.Id == "Svg.BadReference");
        Assert.Throws<SvgException>(() => SvgDocument.Parse($"<svg {Ns} width='10' height='10'><g id='a'><use href='#a'/></g></svg>", new SvgLoadOptions { Strict = true }));
    }

    [Fact]
    public void UseChainsStopAtTheElementLimit()
    {
        System.Text.StringBuilder text = new($"<svg {Ns} width='10' height='10'><defs><rect id='a0' width='1' height='1'/>");
        for (int i = 1; i < 12; i++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"<g id='a{i}'>");
            for (int j = 0; j < 10; j++)
            {
                text.Append(System.Globalization.CultureInfo.InvariantCulture, $"<use href='#a{i - 1}'/>");
            }

            text.Append("</g>");
        }

        text.Append("</defs><use href='#a11'/></svg>");
        SvgException e = Assert.Throws<SvgException>(() => SvgDocument.Parse(text.ToString()));
        Assert.Contains("elements", e.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void DrawByIdFitsASymbol()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='100' height='100'><symbol id='s' viewBox='0 0 10 10'><rect width='10' height='10' fill='lime'/></symbol></svg>");
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(40, 40);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            document.Draw(canvas, "s", new RectF(10, 10, 20, 20));
        }

        Assert.Equal(0, Pixel(raster, 5, 5).A);
        Assert.Equal(new Rgba8(0, 255, 0, 255), Pixel(raster, 20, 20));
        Assert.Equal(0, Pixel(raster, 35, 35).A);
        Assert.Throws<ArgumentException>(() => document.Draw(raster.CreateCanvas(), "missing"));
    }

    [Fact]
    public void RasterizeSizes()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} viewBox='0 0 20 10'/>");
        using Raster<Rgba8> a = document.Rasterize<Rgba8>(new SvgRenderOptions { Width = 300 });
        Assert.Equal((300, 150), (a.Width, a.Height));
        using Raster<Rgba8> b = document.Rasterize<Rgba8>(new SvgRenderOptions { Height = 15 });
        Assert.Equal((30, 15), (b.Width, b.Height));
        using Raster<Rgba8> c = document.Rasterize<Rgba8>(new SvgRenderOptions { Scale = 2.5f, Background = new RgbaF32(1, 1, 1, 1) });
        Assert.Equal((50, 25), (c.Width, c.Height));
        Assert.Equal(new Rgba8(255, 255, 255, 255), Pixel(c, 3, 3));
        EgamiContext small = new() { Limits = new SafetyLimits { MaxPixels = 1000 } };
        Assert.Throws<LimitExceededException>(() => document.Rasterize<Rgba8>(new SvgRenderOptions { Width = 1000, Context = small }));
    }

    internal static Rgba8 Pixel(Raster<Rgba8> raster, int x, int y)
    {
        Rgba8[] pixels = new Rgba8[raster.Width * raster.Height];
        raster.CopyPixelsTo(pixels);
        return pixels[(y * raster.Width) + x];
    }

    private static void AssertSamePixels(Raster<Rgba8> expected, Raster<Rgba8> actual)
    {
        Rgba8[] a = new Rgba8[expected.Width * expected.Height], b = new Rgba8[a.Length];
        expected.CopyPixelsTo(a);
        actual.CopyPixelsTo(b);
        Assert.Equal(a, b);
    }

    private static void AssertSameNode(SvgNode a, SvgNode b, string name)
    {
        Assert.True(a.GetType() == b.GetType(), $"{name}: {a.GetType().Name} became {b.GetType().Name}");
        Assert.True(Near(a.Transform, b.Transform) && a.Opacity == b.Opacity && a.BlendMode == b.BlendMode && a.Id == b.Id, $"{name}: node {a.Id} changed");
        Assert.True((a.Clip is null) == (b.Clip is null) && (a.Mask is null) == (b.Mask is null), $"{name}: clip or mask of {a.Id} changed");
        switch (a, b)
        {
            case (SvgGroup x, SvgGroup y):
                Assert.True(x.Children.Count == y.Children.Count, $"{name}: group {a.Id} has {x.Children.Count} children, then {y.Children.Count}");
                for (int i = 0; i < x.Children.Count; i++)
                {
                    AssertSameNode(x.Children[i], y.Children[i], name);
                }

                break;
            case (SvgPath x, SvgPath y):
                Assert.True(x.Path.ToSvg() == y.Path.ToSvg(), $"{name}: path {a.Id} changed");
                Assert.True(x.Fill == y.Fill || (x.Fill is PicturePaint && y.Fill is PicturePaint), $"{name}: fill of {a.Id} changed");
                Assert.True(x.Stroke == y.Stroke, $"{name}: stroke of {a.Id} changed");
                break;
        }
    }

    private static bool Near(Matrix3x2 a, Matrix3x2 b) =>
        MathF.Abs(a.M11 - b.M11) < 1e-4f && MathF.Abs(a.M12 - b.M12) < 1e-4f && MathF.Abs(a.M21 - b.M21) < 1e-4f &&
        MathF.Abs(a.M22 - b.M22) < 1e-4f && MathF.Abs(a.M31 - b.M31) < 1e-3f && MathF.Abs(a.M32 - b.M32) < 1e-3f;
}
