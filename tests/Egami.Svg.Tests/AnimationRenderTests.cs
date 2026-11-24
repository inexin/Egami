using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Svg.Tests;

/// <summary>SVG animation stage A, task 3: the time override in the styler and resolver, and the public members.</summary>
public class AnimationRenderTests
{
    private const string Ns = "xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'";

    private static SvgDocument Doc(string body) => SvgDocument.Parse($"<svg {Ns} width='100' height='100'>{body}</svg>");

    private static IEnumerable<SvgPath> Paths(SvgNode node) => node switch
    {
        SvgPath path => [path],
        SvgGroup group => group.Children.SelectMany(Paths),
        _ => [],
    };

    private static bool Same(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static SvgPath Shape(SvgGroup tree, string id) => Paths(tree).Single(p => p.Id == id);

    [Fact]
    public void GeometryAttributesAnimate()
    {
        SvgDocument document = Doc("<rect id='r' x='0' y='0' width='10' height='10'><animate attributeName='x' from='0' to='80' dur='4s'/></rect>");
        Assert.Equal(40, Shape(document.RenderTreeAt(TimeSpan.FromSeconds(2)), "r").Path.Bounds.X, 3);
        Assert.Equal(0, Shape(document.RenderTree, "r").Path.Bounds.X, 3);
        Assert.Equal("0", document.FindById("r")!.Attributes["x"]);
    }

    [Fact]
    public void PresentationAttributesAnimateAsHints()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10' fill='red'><animate attributeName='fill' from='red' to='blue' dur='2s'/><set attributeName='opacity' to='0.5' begin='0.5s'/></rect>");
        SvgPath shape = Shape(document.RenderTreeAt(TimeSpan.FromSeconds(1)), "r");
        RgbaF32 fill = ((SolidPaint)shape.Fill!).Color;
        Assert.Equal(128 / 255f, fill.R, 3);
        Assert.Equal(128 / 255f, fill.B, 3);
        Assert.Equal(0.5f, shape.Opacity, 3);
    }

    [Fact]
    public void AnAnimatedPropertyTheElementDoesNotSetStillApplies()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10'><set attributeName='fill' to='#00ff00'/></rect>");
        Assert.Equal(1f, ((SolidPaint)Shape(document.RenderTreeAt(TimeSpan.FromSeconds(1)), "r").Fill!).Color.G, 3);
    }

    [Fact]
    public void CssRulesStillWinOverAnAnimatedPresentationAttribute()
    {
        SvgDocument document = Doc("<style>rect { fill: #00ff00 }</style><rect id='r' width='10' height='10' fill='red'><set attributeName='fill' to='blue'/></rect>");
        RgbaF32 fill = ((SolidPaint)Shape(document.RenderTreeAt(TimeSpan.FromSeconds(1)), "r").Fill!).Color;
        Assert.Equal((0f, 1f, 0f), (fill.R, fill.G, fill.B));
    }

    [Fact]
    public void ToAnimationsStartFromTheComputedValue()
    {
        SvgDocument document = Doc("<g fill='#000000'><rect id='r' width='10' height='10'><animate attributeName='fill' to='#ffffff' dur='2s'/></rect></g>");
        RgbaF32 fill = ((SolidPaint)Shape(document.RenderTreeAt(TimeSpan.FromSeconds(1)), "r").Fill!).Color;
        Assert.Equal(128 / 255f, fill.R, 3);
    }

    [Fact]
    public void VisibilityAndDisplayStep()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10'><set attributeName='display' to='none' begin='1s'/></rect>");
        Assert.Single(Paths(document.RenderTreeAt(TimeSpan.FromSeconds(0.5))));
        Assert.Empty(Paths(document.RenderTreeAt(TimeSpan.FromSeconds(1.5))));
    }

    [Fact]
    public void AnimationsNamedByHrefAnimateTheirTarget()
    {
        SvgDocument document = Doc("<circle id='c' cx='10' cy='10' r='5'/><animate href='#c' attributeName='r' from='5' to='15' dur='2s'/>");
        Assert.Equal(20, Shape(document.RenderTreeAt(TimeSpan.FromSeconds(1)), "c").Path.Bounds.Width, 2);
    }

    [Fact]
    public void SetCanSwitchAReference()
    {
        SvgDocument document = Doc("<defs><rect id='a' width='10' height='10'/><rect id='b' width='30' height='30'/></defs><use id='u' href='#a'><set attributeName='href' to='#b' begin='1s'/></use><use xlink:href='#a'><set attributeName='xlink:href' to='#b' begin='1s'/></use>");
        Assert.All(Paths(document.RenderTreeAt(TimeSpan.FromSeconds(0.5))), p => Assert.Equal(10, p.Path.Bounds.Width, 3));
        Assert.All(Paths(document.RenderTreeAt(TimeSpan.FromSeconds(2))), p => Assert.Equal(30, p.Path.Bounds.Width, 3));
    }

    [Fact]
    public void TheRootViewBoxAnimates()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='100' height='100' viewBox='0 0 100 100'><rect id='r' width='50' height='50'/><animate attributeName='viewBox' from='0 0 100 100' to='0 0 50 50' dur='1s' fill='freeze'/></svg>");
        SvgGroup tree = document.RenderTreeAt(TimeSpan.FromSeconds(2));
        SvgGroup mapped = (SvgGroup)tree.Children[0];
        Assert.Equal(2, mapped.Transform.M11, 3);
    }

    [Fact]
    public void PathDataMorphs()
    {
        SvgDocument document = Doc("<path id='p' d='M0 0 L10 0 L10 10 Z'><animate attributeName='d' to='M0 0 L50 0 L50 50 Z' dur='2s'/></path>");
        Assert.Equal(30, Shape(document.RenderTreeAt(TimeSpan.FromSeconds(1)), "p").Path.Bounds.Width, 3);
    }

    [Fact]
    public void NoTimeDrawsTheStaticDocument()
    {
        const string Body = "<rect width='40' height='40' fill='red'/><circle cx='50' cy='50' r='20' fill='blue'/>";
        SvgDocument still = Doc(Body);
        SvgDocument animated = Doc(Body.Replace("fill='red'/>", "fill='red'><animate attributeName='width' to='100' dur='1s'/><set attributeName='fill' to='green'/></rect>", StringComparison.Ordinal));
        using Raster<Rgba8> a = still.Rasterize<Rgba8>();
        using Raster<Rgba8> b = animated.Rasterize<Rgba8>();
        using Raster<Rgba8> c = animated.Rasterize<Rgba8>(new SvgRenderOptions { Time = null });
        Assert.True(Same(a, b));
        Assert.True(Same(a, c));

        using Raster<Rgba8> moved = animated.Rasterize<Rgba8>(new SvgRenderOptions { Time = TimeSpan.FromSeconds(0.5) });
        Assert.False(Same(a, moved));
    }

    [Fact]
    public void ADocumentWithoutAnimationsGivesItsRenderTree()
    {
        SvgDocument document = Doc("<rect width='10' height='10'/>");
        Assert.Same(document.RenderTree, document.RenderTreeAt(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ANegativeTimeIsRefused()
    {
        SvgDocument document = Doc("<rect width='10' height='10'/>");
        Assert.Throws<ArgumentOutOfRangeException>(() => document.RenderTreeAt(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgRenderOptions { Time = TimeSpan.FromSeconds(-1) });
    }

    [Fact]
    public void DrawAndDrawByIdTakeTheTime()
    {
        SvgDocument document = Doc("<g id='g'><rect width='10' height='10' fill='#000'><set attributeName='fill' to='#fff' begin='1s'/></rect></g>");
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(20, 20);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            document.Draw(canvas, (RectF?)null, new SvgRenderOptions { Time = TimeSpan.FromSeconds(2) });
        }

        Assert.Equal(255, raster.GetPixel(5, 5).R);
        using Raster<Rgba8> byId = Raster.Create<Rgba8>(20, 20);
        using (RasterCanvas<Rgba8> canvas = byId.CreateCanvas())
        {
            document.Draw(canvas, "g", null, new SvgRenderOptions { Time = TimeSpan.FromSeconds(2) });
        }

        Assert.Equal(255, byId.GetPixel(5, 5).R);
    }

    [Fact]
    public void TimesPastTheHorizonExtendTheTimeline()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10'><animate id='a' attributeName='x' from='0' to='10' begin='0;b.end' dur='1s'/><animate id='b' attributeName='x' from='10' to='0' begin='a.end' dur='1s'/></rect>");
        Assert.Equal(5, Shape(document.RenderTreeAt(TimeSpan.FromSeconds(100.5)), "r").Path.Bounds.X, 3);
        Assert.Equal(5, Shape(document.RenderTreeAt(TimeSpan.FromSeconds(1.5)), "r").Path.Bounds.X, 3);
        Assert.Equal(5, Shape(document.RenderTreeAt(TimeSpan.FromSeconds(0.5)), "r").Path.Bounds.X, 3);
    }

    [Fact]
    public void EditsRebuildTheTimeline()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10'><animate id='a' attributeName='x' from='0' to='10' dur='1s'/></rect>");
        Assert.Equal(TimeSpan.FromSeconds(1), document.Animation.Duration);
        document.FindById("a")!.Attributes["dur"] = "4s";
        Assert.Equal(TimeSpan.FromSeconds(4), document.Animation.Duration);
    }

    [Fact]
    public void FrameDiagnosticsAreDeliveredOnce()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = EgamiContext.Default with { Diagnostics = seen.Add };
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='100' height='100'><rect width='10' height='10'><set attributeName='width' to='wide'/></rect></svg>", new SvgLoadOptions { Context = context });
        seen.Clear();
        _ = document.RenderTreeAt(TimeSpan.FromSeconds(1));
        _ = document.RenderTreeAt(TimeSpan.FromSeconds(2));
        Assert.Single(seen, d => d.Id == "Svg.BadAttribute");
    }
}
