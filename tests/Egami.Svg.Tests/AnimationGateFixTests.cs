using Egami.Pixels;

namespace Egami.Svg.Tests;

/// <summary>Fixes from the stage E gate against Chromium (2.10.1 task 12), each pinned at the moment the gate saw it.</summary>
public sealed class AnimationGateFixTests
{
    private const string Ns = "http://www.w3.org/2000/svg";

    private static Rgba8 PixelAt(string svg, double seconds, int x, int y)
    {
        using Raster<Rgba8> raster = SvgDocument.Parse(svg).Rasterize<Rgba8>(new SvgRenderOptions { Time = TimeSpan.FromSeconds(seconds) });
        return raster.Row(y)[x];
    }

    [Fact]
    public void ASumOfPathsWithOtherCommandsLeavesTheBaseValue()
    {
        string svg = $"<svg xmlns='{Ns}' width='60' height='60'><path fill='green' d='M10,10 L30,10 L30,30 L10,30 Z'><animate attributeName='d' by='M20,20 Q40,20 40,40 Q20,40 20,20 Z' dur='2s' fill='freeze'/></path></svg>";
        foreach (double t in (double[])[0, 1, 3])
        {
            Assert.Equal(0, PixelAt(svg, t, 20, 20).R);
            Assert.Equal(255, PixelAt(svg, t, 20, 20).A);
        }
    }

    [Fact]
    public void AnEmptyBasePathCannotBeAddedTo()
    {
        string svg = $"<svg xmlns='{Ns}' width='120' height='120'><path fill='green' d=''><animate attributeName='d' by='M0 0 L100 0 L100 100 L0 100 Z' dur='4s' fill='freeze'/></path></svg>";
        Assert.Equal(0, PixelAt(svg, 3, 50, 50).A);
    }

    [Fact]
    public void EmptyPathsSummedOverABaseLeaveIt()
    {
        string svg = $"<svg xmlns='{Ns}' width='100' height='100'><path fill='green' d='M10 10 L90 10 L90 90 L10 90 Z'><animate attributeName='d' from='' to='' additive='sum' dur='4s' fill='freeze'/></path></svg>";
        Assert.Equal(255, PixelAt(svg, 2, 50, 50).A);
    }

    [Fact]
    public void AColorAnimatesTowardsCurrentColor()
    {
        string svg = $"<svg xmlns='{Ns}' width='20' height='20'><rect width='20' height='20' color='green' fill='red'><animate attributeName='fill' from='black' to='currentColor' dur='4s' fill='freeze'/></rect></svg>";
        Assert.Equal(new Rgba8(0, 64, 0, 255), PixelAt(svg, 2, 10, 10));
    }

    [Fact]
    public void CurrentColorFollowsTheAnimatedColor()
    {
        // As SVG 1.1's animate-elem-85: halfway, color is (0, 128, 255) and the fill halfway from green to it.
        string svg = $"<svg xmlns='{Ns}' width='20' height='20'><rect width='20' height='20' color='black' fill='blue'>" +
            "<animate attributeName='fill' from='#008000' to='currentColor' dur='4s' fill='freeze'/>" +
            "<animate attributeName='color' from='blue' to='cyan' dur='4s' fill='freeze'/></rect></svg>";
        Assert.Equal(new Rgba8(0, 128, 128, 255), PixelAt(svg, 2, 10, 10));
    }

    [Theory]
    [InlineData("dur='indefinite'", 4, true)]
    [InlineData("dur='indefinite'", 6, false)]
    [InlineData("dur='1s' repeatCount='5' fill='remove' min='8s'", 4, true)]
    [InlineData("dur='1s' repeatCount='5' fill='remove' min='8s'", 6, false)]
    public void AnAnimationCutByItsEndPlaysOnThroughMin(string timing, double seconds, bool applies)
    {
        // As SVG 1.1's animate-elem-65: end='2s' cuts the animation, min extends it, and it keeps applying until its own
        // repetitions (or min) end.
        string min = timing.Contains("min=", StringComparison.Ordinal) ? "" : "min='5s'";
        string svg = $"<svg xmlns='{Ns}' width='100' height='20'><rect x='0' width='20' height='20' fill='green'><set attributeName='x' to='74' end='2s' {min} {timing}/></rect></svg>";
        Assert.Equal(applies, PixelAt(svg, seconds, 80, 10).A == 255);
    }

    [Theory]
    [InlineData("attributeName='width' from='ERROR' to='ERROR'")]
    [InlineData("attributeName='width' to='200   px'")]
    [InlineData("attributeName='width' values='0px;100px;500    px'")]
    [InlineData("attributeName='fill' from='gr   een' to='red'")]
    public void AnIllegalValueLeavesTheAnimationWithoutEffect(string values)
    {
        string svg = $"<svg xmlns='{Ns}' width='300' height='100'><rect width='100' height='100' fill='green'><animate {values} dur='4s' fill='freeze'/></rect></svg>";
        foreach (double t in (double[])[0.5, 2, 5])
        {
            Rgba8 inside = PixelAt(svg, t, 50, 50), outside = PixelAt(svg, t, 150, 50);
            Assert.Equal(new Rgba8(0, 128, 0, 255), inside);
            Assert.Equal(0, outside.A);
        }
    }

    [Fact]
    public void AnEmptyByPathLeavesTheAnimationWithoutEffect()
    {
        string svg = $"<svg xmlns='{Ns}' width='100' height='100'><path fill='green' d='M40 40 L60 40 L60 60 L40 60 Z'><animate attributeName='d' from='' by='' dur='4s' fill='freeze'/></path></svg>";
        Assert.Equal(255, PixelAt(svg, 2, 50, 50).A);
    }

    [Fact]
    public void AMotionOfNoLengthHoldsTheElementAtItsPoint()
    {
        string svg = $"<svg xmlns='{Ns}' width='300' height='150'><rect x='-200' width='200' height='100' fill='blue'><animateMotion from='100,100' to='100,100' rotate='auto' dur='4s' fill='freeze'/></rect></svg>";
        Assert.Equal(255, PixelAt(svg, 2, 50, 120).A);
        Assert.Equal(0, PixelAt(svg, 2, 150, 120).A);
    }

    [Fact]
    public void PacedMotionFollowsItsKeyPoints()
    {
        // keyPoints 1;0 run the path backwards: at 1 s of 5, 80 % along M-200,50 h250, x = 0, then the translate.
        string svg = $"<svg xmlns='{Ns}' width='300' height='150'><rect transform='translate(100, -50)' width='100' height='100' fill='green'><animateMotion dur='5s' keyPoints='1;0' keyTimes='0;1' path='M-200,50h250'/></rect></svg>";
        Assert.Equal(255, PixelAt(svg, 1, 150, 50).A);
        Assert.Equal(0, PixelAt(svg, 1, 50, 50).A);
    }

    [Fact]
    public void AnimationsUnderAFailingConditionHaveNoEffect()
    {
        string svg = $"<svg xmlns='{Ns}' xmlns:xlink='http://www.w3.org/1999/xlink' width='100' height='100'>" +
            "<g systemLanguage='x-xl'><set xlink:href='#t' attributeName='width' to='0'/></g>" +
            "<switch><set systemLanguage='x-xl' xlink:href='#t' attributeName='fill' to='red'/><set xlink:href='#t' attributeName='height' to='100'/></switch>" +
            "<rect id='t' width='100' height='50' fill='green'/></svg>";
        Assert.Equal(new Rgba8(0, 128, 0, 255), PixelAt(svg, 1, 50, 75));
    }

    [Fact]
    public void AtACornerMotionTurnsWithTheSegmentThatEndsThere()
    {
        // Halfway along M20 20 H100 V60 H20 Z is the corner (100, 60): the arrow still points down, along V60.
        string svg = $"<svg xmlns='{Ns}' width='120' height='80'><path d='M0 -3 L12 0 L0 3 Z' fill='red'><animateMotion path='M20 20 H100 V60 H20 Z' rotate='auto' dur='3s' fill='freeze'/></path></svg>";
        Assert.True(PixelAt(svg, 1.5, 100, 68).A > 128);
        Assert.True(PixelAt(svg, 1.5, 92, 60).A < 64);
    }
}
