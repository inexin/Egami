using Egami.Pixels;

namespace Egami.Svg.Tests;

/// <summary>
/// What animation frames share (the parsed sheets, the cascades of elements no animation touches, prepared keyframes) gives
/// each frame what it gave when every frame cascaded the document from the start.
/// </summary>
public sealed class AnimationCacheTests
{
    private const string Ns = "http://www.w3.org/2000/svg";

    private static Rgba8 PixelAt(SvgDocument document, double seconds, int x, int y)
    {
        using Raster<Rgba8> raster = document.Rasterize<Rgba8>(new SvgRenderOptions { Time = TimeSpan.FromSeconds(seconds) });
        return raster.Row(y)[x];
    }

    [Fact]
    public void AnImportantDeclarationWinsOverPreparedKeyframesOnItsElementOnly()
    {
        // The two rects share one animation; the second's !important opacity wins over it in every frame, in either order.
        foreach (string order in (string[])["ab", "ba"])
        {
            string a = "<rect id='a' class='f' width='50' height='50' fill='blue'/>";
            string b = "<rect id='b' class='f' x='50' width='50' height='50' fill='blue' style='opacity: 1 !important'/>";
            SvgDocument document = SvgDocument.Parse(
                $"<svg xmlns='{Ns}' width='100' height='50'><style>@keyframes k {{ from {{ opacity: 0 }} to {{ opacity: 0 }} }} .f {{ animation: k 4s infinite }}</style>" +
                (order == "ab" ? a + b : b + a) + "</svg>");
            foreach (double t in (double[])[1, 2, 3])
            {
                Assert.Equal(0, PixelAt(document, t, 25, 25).A);
                Assert.Equal(255, PixelAt(document, t, 75, 25).A);
            }
        }
    }

    [Fact]
    public void PreparedKeyframesResolveCurrentColorOnEachElement()
    {
        SvgDocument document = SvgDocument.Parse(
            $"<svg xmlns='{Ns}' width='100' height='50'><style>@keyframes k {{ from {{ fill: currentcolor }} to {{ fill: currentcolor }} }} rect {{ animation: k 4s infinite }}</style>" +
            "<rect width='50' height='50' fill='red' color='#00ff00'/><rect x='50' width='50' height='50' fill='red' color='#0000ff'/></svg>");
        foreach (double t in (double[])[1, 2])
        {
            Assert.Equal(new Rgba8(0, 255, 0, 255), PixelAt(document, t, 25, 25));
            Assert.Equal(new Rgba8(0, 0, 255, 255), PixelAt(document, t, 75, 25));
        }
    }

    [Fact]
    public void AnElementSmilAnimatesOnlySomeOfTheTimeCascadesAsItIsThen()
    {
        // The CSS animation makes every frame cascade; the rect's SMIL fill applies from 1 s to 2 s only.
        SvgDocument document = SvgDocument.Parse(
            $"<svg xmlns='{Ns}' width='100' height='50'><style>@keyframes k {{ to {{ opacity: 0.5 }} }} #o {{ animation: k 4s infinite }}</style>" +
            "<rect id='o' x='50' width='50' height='50' fill='black'/>" +
            "<rect width='50' height='50' fill='#ff0000'><set attributeName='fill' to='#00ff00' begin='1s' dur='1s'/></rect></svg>");
        Assert.Equal(new Rgba8(255, 0, 0, 255), PixelAt(document, 0.5, 25, 25));
        Assert.Equal(new Rgba8(0, 255, 0, 255), PixelAt(document, 1.5, 25, 25));
        Assert.Equal(new Rgba8(255, 0, 0, 255), PixelAt(document, 2.5, 25, 25));
        Assert.Equal(new Rgba8(255, 0, 0, 255), PixelAt(document, 0.5, 25, 25));
    }

    [Fact]
    public void AnEditedStyleSheetTakesEffectInTheNextFrame()
    {
        SvgDocument document = SvgDocument.Parse(
            $"<svg xmlns='{Ns}' width='50' height='50'><style>@keyframes k {{ from {{ fill: #ff0000 }} to {{ fill: #ff0000 }} }} rect {{ animation: k 4s infinite }}</style>" +
            "<rect width='50' height='50'/></svg>");
        Assert.Equal(new Rgba8(255, 0, 0, 255), PixelAt(document, 1, 25, 25));
        document.Root.Elements().First(e => e.Name == "style").Text = "@keyframes k { from { fill: #0000ff } to { fill: #0000ff } } rect { animation: k 4s infinite }";
        Assert.Equal(new Rgba8(0, 0, 255, 255), PixelAt(document, 1, 25, 25));
    }

    [Fact]
    public void FramesDrawnOnManyThreadsMatchFramesDrawnOneByOne()
    {
        SvgDocument document = SvgDocument.Parse(
            $"<svg xmlns='{Ns}' width='60' height='20'><style>@keyframes k {{ 0% {{ opacity: 0.2; fill: #ff0000 }} 50% {{ opacity: 1; fill: #00ff00 }} }}" +
            " rect { animation: k 1s infinite } rect:nth-child(3) { animation-delay: -0.3s } rect:nth-child(4) { animation-delay: -0.6s }</style>" +
            "<rect width='20' height='20'/><rect x='20' width='20' height='20'/><rect x='40' width='20' height='20'/></svg>");
        SvgAnimationOptions options = new() { FrameRate = 20, Length = TimeSpan.FromSeconds(1), MergeDuplicates = false };
        int frames = 0;
        foreach (SvgFrame<Rgba8> frame in document.RenderFrames<Rgba8>(options))
        {
            using Raster<Rgba8> drawn = frame.Raster;
            using Raster<Rgba8> alone = document.Rasterize<Rgba8>(new SvgRenderOptions { Time = frame.Time });
            for (int y = 0; y < drawn.Height; y++)
            {
                Assert.True(drawn.Row(y).SequenceEqual(alone.Row(y)), $"frame at {frame.Time}, row {y}");
            }

            frames++;
        }

        Assert.Equal(20, frames);
    }
}
