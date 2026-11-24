using System.Numerics;

using Egami.Drawing;
using Egami.Svg.Animation;
using Egami.Svg.Reading;

namespace Egami.Svg.Tests;

/// <summary>SVG animation stage B: animateTransform (task 4), animateMotion and discard (task 5), and base values that inherit.</summary>
public class AnimationElementTests
{
    private const string Ns = "xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'";

    private static SvgDocument Doc(string body) => SvgDocument.Parse($"<svg {Ns} width='100' height='100'>{body}</svg>");

    private static IEnumerable<SvgPath> Paths(SvgNode node) => node switch
    {
        SvgPath path => [path],
        SvgGroup group => group.Children.SelectMany(Paths),
        _ => [],
    };

    private static SvgPath Shape(SvgDocument document, double seconds, string id = "r") =>
        Paths(document.RenderTreeAt(TimeSpan.FromSeconds(seconds))).Single(p => p.Id == id);

    private static SvgPath? Find(SvgDocument document, double seconds, string id = "r") =>
        Paths(document.RenderTreeAt(TimeSpan.FromSeconds(seconds))).SingleOrDefault(p => p.Id == id);

    /// <summary>The animated transform attribute of the rect with id r at a time.</summary>
    private static string? TransformAt(string rect, string animations, double time, string attribute = "transform")
    {
        SvgDocument document = Doc($"{rect}{animations}</rect>");
        SvgTimeline timeline = SvgTimeline.Build(document, new SvgReport(strict: false));
        SvgAnimationFrame frame = SvgAnimationFrame.Compute(timeline, time, new SvgBaseValues(document).Get);
        return frame.TryGet(document.FindById("r")!, attribute, out string? value) ? value : null;
    }

    private static void Near(Vector2 expected, Matrix3x2 m)
    {
        Assert.Equal(expected.X, m.M31, 2);
        Assert.Equal(expected.Y, m.M32, 2);
    }

    [Theory]
    [InlineData("type='rotate' from='0 10 10' to='90 10 10'", 0.5, "rotate(45 10 10)")]
    [InlineData("type='translate' from='0' to='10 20'", 0.5, "translate(5 10)")]
    [InlineData("type='scale' from='1' to='3 5'", 0.5, "scale(2 3)")]
    [InlineData("type='skewX' values='0;30;60'", 0.75, "skewX(45)")]
    [InlineData("type='skewY' from='0' to='20'", 0.25, "skewY(5)")]
    [InlineData("type='translate' values='0;10;100' calcMode='paced'", 0.5, "translate(50 0)")]
    [InlineData("type='rotate' values='0;10;100' calcMode='paced'", 0.5, "rotate(50 0 0)")]
    [InlineData("type='rotate' values='0;90' accumulate='sum' repeatCount='3'", 1.5, "rotate(135 0 0)")]
    [InlineData("type='scale' by='2'", 0.5, "scale(1 1)")]
    [InlineData("type='translate' from='5' by='10'", 0.5, "translate(10 0)")]
    [InlineData("type='scale' to='3'", 0.5, "scale(2 2)")]
    [InlineData("type='rotate' to='90 5 5'", 0.5, "rotate(45 5 5)")]
    public void AnimateTransformGivesItsValue(string attributes, double time, string expected) =>
        Assert.Equal(expected, TransformAt("<rect id='r' width='10' height='10'>", $"<animateTransform attributeName='transform' dur='1s' {attributes}/>", time));

    [Fact]
    public void AdditiveTransformsAppendToTheListBelow()
    {
        const string Rect = "<rect id='r' width='10' height='10' transform='translate(10 0)'>";
        Assert.Equal("translate(10 0) scale(1.5 1.5)", TransformAt(Rect, "<animateTransform attributeName='transform' type='scale' from='1' to='2' additive='sum' dur='1s'/>", 0.5));
        Assert.Equal("scale(1.5 1.5)", TransformAt(Rect, "<animateTransform attributeName='transform' type='scale' from='1' to='2' dur='1s'/>", 0.5));
        Assert.Equal("translate(10 0) rotate(45 0 0) translate(5 0)", TransformAt(Rect,
            "<animateTransform attributeName='transform' type='rotate' from='0' to='90' additive='sum' dur='1s'/>" +
            "<animateTransform attributeName='transform' type='translate' from='0' to='10' additive='sum' dur='1s'/>", 0.5));
    }

    [Fact]
    public void AToTransformStartsFromAnUnderlyingTransformOfItsType() =>
        Assert.Equal("rotate(20 0 0)", TransformAt("<rect id='r' width='10' height='10' transform='rotate(10)'>", "<animateTransform attributeName='transform' type='rotate' to='30' dur='1s'/>", 0.5));

    [Fact]
    public void AnimateTransformMovesTheShapeAndPaintTransforms()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10'><animateTransform attributeName='transform' type='translate' from='0 0' to='40 20' dur='2s'/></rect>");
        Near(new Vector2(20, 10), Shape(document, 1).Transform);
        Assert.Equal("rotate(45 0 0)", TransformAt("<rect id='r' width='10' height='10'>", "<animateTransform attributeName='gradientTransform' type='rotate' from='0' to='90' dur='1s'/>", 0.5, "gradientTransform"));
    }

    [Fact]
    public void BadTransformValuesAreReported()
    {
        SvgDocument document = Doc("<rect width='10' height='10'><animateTransform attributeName='transform' type='rotate' by='1 2'/></rect>");
        SvgReport report = new(strict: false);
        SvgTimeline timeline = SvgTimeline.Build(document, report);
        Assert.Null(timeline.Animations[0].Function);
        Assert.Contains(report.Warnings, w => w.Id == "Svg.BadAnimation");
    }

    [Theory]
    [InlineData("path='M0 0 L100 0' dur='2s'", 1, 50, 0)]
    [InlineData("path='M0 0 L100 0 L100 10' calcMode='linear' dur='2s'", 1, 100, 0)]
    [InlineData("path='M0 0 L100 0 L100 10' dur='2s'", 1, 55, 0)]
    [InlineData("values='0,0;100,0;100,10' calcMode='linear' dur='1s'", 0.75, 100, 5)]
    [InlineData("values='0,0;100,0;100,10' dur='1s'", 0.5, 55, 0)]
    [InlineData("values='0,0;10,0;20,0' calcMode='discrete' dur='3s'", 1.5, 10, 0)]
    [InlineData("from='10,10' to='30,10' dur='1s'", 0.5, 20, 10)]
    [InlineData("from='10,10' by='20,0' dur='1s'", 0.5, 20, 10)]
    [InlineData("to='30,0' dur='1s'", 0.5, 15, 0)]
    [InlineData("by='0,30' dur='1s'", 0.5, 0, 15)]
    [InlineData("path='M0 0 L100 0' keyPoints='0;1;0' keyTimes='0;0.5;1' calcMode='linear' dur='1s'", 0.75, 50, 0)]
    [InlineData("path='M0 0 L100 0' keyPoints='0;1' keyTimes='0;1' calcMode='spline' keySplines='0 0 1 1' dur='1s'", 0.3, 30, 0)]
    [InlineData("path='M0 0 L10 0' accumulate='sum' repeatCount='3' dur='1s'", 2.5, 25, 0)]
    [InlineData("path='M0 0 L10 0 M50 0 L60 0' dur='1s'", 0.75, 55, 0)]
    [InlineData("path='M0 0 L100 0' dur='1s' fill='freeze'", 5, 100, 0)]
    public void MotionFollowsItsPath(string attributes, double time, float x, float y)
    {
        SvgDocument document = Doc($"<rect id='r' width='10' height='10'><animateMotion {attributes}/></rect>");
        Near(new Vector2(x, y), Shape(document, time).Transform);
    }

    [Fact]
    public void MpathWinsOverThePathAttribute()
    {
        SvgDocument document = Doc("<path id='p' d='M0 0 L0 100'/><rect id='r' width='10' height='10'><animateMotion path='M0 0 L100 0' dur='2s'><mpath href='#p'/></animateMotion></rect>");
        Near(new Vector2(0, 50), Shape(document, 1).Transform);
        SvgDocument old = Doc("<path id='p' d='M0 0 L0 100'/><rect id='r' width='10' height='10'><animateMotion dur='2s'><mpath xlink:href='#p'/></animateMotion></rect>");
        Near(new Vector2(0, 50), Shape(old, 1).Transform);
    }

    [Theory]
    [InlineData("auto", 0, 1)]
    [InlineData("auto-reverse", 0, -1)]
    [InlineData("90", 0, 1)]
    [InlineData("0.25turn", 0, 1)]
    [InlineData("0", 1, 0)]
    public void MotionRotates(string rotate, float cos, float sin)
    {
        SvgDocument document = Doc($"<rect id='r' width='10' height='10'><animateMotion path='M0 0 L0 100' rotate='{rotate}' dur='2s'/></rect>");
        Matrix3x2 m = Shape(document, 1).Transform;
        Assert.Equal(cos, m.M11, 4);
        Assert.Equal(sin, m.M12, 4);
        Near(new Vector2(0, 50), m);
    }

    [Fact]
    public void MotionComesAfterTheElementsTransform()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10' transform='scale(2)'><animateMotion path='M0 0 L100 0' dur='1s' fill='freeze'/></rect>");
        Matrix3x2 m = Shape(document, 2).Transform;
        Assert.Equal(2, m.M11, 4);
        Near(new Vector2(100, 0), m);
        Assert.Equal(new Vector2(100, 0), Vector2.Transform(Vector2.Zero, m));
    }

    [Fact]
    public void AdditiveMotionsAdd()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10'><animateMotion path='M0 0 L100 0' dur='1s'/><animateMotion path='M0 0 L0 100' additive='sum' dur='1s'/></rect>");
        Near(new Vector2(50, 50), Shape(document, 0.5).Transform);
        SvgDocument replacing = Doc("<rect id='r' width='10' height='10'><animateMotion path='M0 0 L100 0' dur='1s'/><animateMotion path='M0 0 L0 100' dur='1s'/></rect>");
        Near(new Vector2(0, 50), Shape(replacing, 0.5).Transform);
    }

    [Theory]
    [InlineData("path='M0 0 L1 0' keyPoints='0;1' calcMode='linear'")]
    [InlineData("path='M0 0 L1 0' keyPoints='0;2' keyTimes='0;1' calcMode='linear'")]
    [InlineData("path='M0 0 L1 0' keyTimes='0;0.5;1' calcMode='linear'")]
    [InlineData("values='0,0;1'")]
    [InlineData("")]
    public void MotionsInErrorHaveNoEffect(string attributes)
    {
        SvgDocument document = Doc($"<rect width='10' height='10'><animateMotion dur='1s' {attributes}/></rect>");
        SvgReport report = new(strict: false);
        SvgTimeline timeline = SvgTimeline.Build(document, report);
        Assert.Null(timeline.Animations[0].Motion);
        Assert.Contains(report.Warnings, w => w.Id == "Svg.BadAnimation");
    }

    [Fact]
    public void DiscardRemovesItsTargetForGood()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10'><discard begin='1s'/></rect>");
        Assert.NotNull(Find(document, 0.5));
        Assert.Null(Find(document, 1.5));
        Assert.Null(Find(document, 500));
        Assert.NotNull(Paths(document.RenderTree).SingleOrDefault(p => p.Id == "r"));
    }

    [Fact]
    public void DiscardWithAnEarlierBeginRemovesFromTheStart()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10'><discard begin='-2s'/></rect>");
        Assert.Null(Find(document, 0));
    }

    [Fact]
    public void DiscardByHrefRemovesASubtree()
    {
        SvgDocument document = Doc("<g id='g'><rect id='r' width='10' height='10'/></g><discard href='#g' begin='a.end'/><set id='a' attributeName='x' to='0' dur='1s'/>");
        Assert.NotNull(Find(document, 0.5));
        Assert.Null(Find(document, 1));
    }

    [Fact]
    public void ADiscardedElementCannotBeUsed()
    {
        SvgDocument document = Doc("<defs><rect id='r' width='10' height='10'><discard begin='1s'/></rect></defs><use href='#r'/>");
        Assert.Single(Paths(document.RenderTreeAt(TimeSpan.FromSeconds(0.5))));
        Assert.Empty(Paths(document.RenderTreeAt(TimeSpan.FromSeconds(2))));
    }

    [Fact]
    public void DiscardStopsTheAnimationsItRemoves()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10' fill='#000000'/><g><set href='#r' attributeName='fill' to='#ffffff'/><discard begin='1s'/></g>");
        Assert.Equal(1f, ((SolidPaint)Shape(document, 0.5).Fill!).Color.R, 3);
        Assert.Equal(0f, ((SolidPaint)Shape(document, 2).Fill!).Color.R, 3);
    }

    [Fact]
    public void AToAnimationInheritsAnAnimatedAncestorsValue()
    {
        // The group is set to white, so the rect's fill animates from the white it inherits, not the black written on the group.
        SvgDocument document = Doc("<g fill='#000000'><set attributeName='fill' to='#ffffff'/><rect id='r' width='10' height='10'><animate attributeName='fill' to='#000000' dur='2s'/></rect></g>");
        Assert.Equal(128 / 255f, ((SolidPaint)Shape(document, 1).Fill!).Color.R, 3);
    }

    [Fact]
    public void StageBElementsDoNotChangeTheStaticDocument()
    {
        SvgDocument document = Doc("<rect id='r' width='10' height='10' transform='translate(5 0)'><animateTransform attributeName='transform' type='rotate' to='90' dur='1s'/><animateMotion path='M0 0 L50 50' dur='1s'/><discard begin='0s'/></rect>");
        Near(new Vector2(5, 0), Paths(document.RenderTree).Single(p => p.Id == "r").Transform);
        Assert.True(document.Animation.HasAnimations);
    }
}
