using Egami.Svg.Animation;
using Egami.Svg.Reading;

namespace Egami.Svg.Tests;

/// <summary>SVG animation stage A, task 2: value kinds, calcMode, keyTimes, keySplines, from, to, by, additive, accumulate, the sandwich.</summary>
public class AnimationValueTests
{
    /// <summary>The animated value of the first rect's attribute at a time, or null when nothing animates it then.</summary>
    private static string? ValueAt(string animations, double time, string attribute = "x", string rect = "<rect x='7' fill='#008000' width='10' height='10'>")
    {
        string name = rect[1..rect.IndexOfAny([' ', '>'])];
        SvgDocument document = SvgDocument.Parse($"<svg xmlns='http://www.w3.org/2000/svg' width='100' height='100'>{rect}{animations}</{name}></svg>");
        SvgTimeline timeline = SvgTimeline.Build(document, new SvgReport(strict: false));
        SvgAnimationFrame frame = SvgAnimationFrame.Compute(timeline, time, new SvgBaseValues(document).Get);
        SvgElement target = document.Root.Elements(name).First();
        return frame.TryGet(target, attribute, out string? value) ? value : null;
    }

    [Theory]
    [InlineData("from='0' to='100' dur='10s'", 2.5, "25")]
    [InlineData("values='0;10;30' dur='1s'", 0.75, "20")]
    [InlineData("values='0;80;100' keyTimes='0;0.8;1' dur='1s'", 0.4, "40")]
    [InlineData("values='0;10;30' calcMode='paced' dur='1s'", 0.5, "15")]
    [InlineData("values='0;10;30' calcMode='discrete' dur='3s'", 1.5, "10")]
    [InlineData("values='0;10;30' calcMode='discrete' keyTimes='0;0.1;0.2' dur='1s'", 0.5, "30")]
    [InlineData("from='0' to='10' calcMode='discrete' dur='1s'", 0.49, "0")]
    [InlineData("from='0' to='10' calcMode='discrete' dur='1s'", 0.5, "10")]
    [InlineData("values='0;10' calcMode='spline' keySplines='0 0 1 1' dur='1s'", 0.3, "3")]
    [InlineData("from='10' by='5' dur='1s'", 0.5, "12.5")]
    [InlineData("by='4' dur='1s'", 0.5, "9")]
    [InlineData("to='17' dur='1s'", 0.5, "12")]
    [InlineData("from='0' to='10' additive='sum' dur='1s'", 0.5, "12")]
    [InlineData("values='0;10' accumulate='sum' dur='1s' repeatCount='3'", 2.5, "25")]
    [InlineData("from='0' to='10' dur='2s' fill='freeze'", 5, "10")]
    [InlineData("from='0' to='10' dur='2s' repeatCount='1.5' fill='freeze'", 5, "5")]
    [InlineData("values='0;10' accumulate='sum' dur='1s' repeatCount='2' fill='freeze'", 5, "20")]
    [InlineData("from='1in' to='0px' dur='1s'", 0.5, "48")]
    [InlineData("from='10%' to='20%' dur='1s'", 0.5, "15%")]
    [InlineData("from='10%' to='20px' dur='1s'", 0.25, "10%")]
    [InlineData("from='0' to='10' dur='1s' begin='2s'", 1, null)]
    [InlineData("from='0' to='10' dur='1s'", 1.5, null)]
    [InlineData("from='0' to='10'", 3, "0")]
    public void AnimateGivesItsValue(string attributes, double time, string? expected) =>
        Assert.Equal(expected, ValueAt($"<animate attributeName='x' {attributes}/>", time));

    [Fact]
    public void SplinesEaseTheSegment()
    {
        string? eased = ValueAt("<animate attributeName='x' values='0;100' calcMode='spline' keySplines='0.42 0 0.58 1' dur='1s'/>", 0.25);
        Assert.True(double.Parse(eased!, System.Globalization.CultureInfo.InvariantCulture) < 25);
        Assert.Equal(0.5, SvgAnimationFunction.Spline([0.42, 0, 0.58, 1], 0.5), 6);
        Assert.Equal(1, SvgAnimationFunction.Spline([0.1, 0.9, 0.2, 1], 1), 6);
    }

    [Theory]
    [InlineData("from='red' to='blue'", 0.5, "rgb(128, 0, 128)")]
    [InlineData("from='rgba(0,0,0,0)' to='#000'", 0.5, "rgba(0, 0, 0, 0.5)")]
    [InlineData("to='white'", 0.5, "rgb(128, 192, 128)")]
    [InlineData("from='red' to='none'", 0.25, "red")]
    [InlineData("from='red' to='none'", 0.75, "none")]
    [InlineData("values='red;url(#g);blue'", 0.5, "url(#g)")]
    public void ColorsAndPaintsAnimate(string attributes, double time, string expected) =>
        Assert.Equal(expected, ValueAt($"<animate attributeName='fill' dur='1s' {attributes}/>", time, "fill"));

    [Theory]
    [InlineData("M0 0 L10 0", "M0 0 L20 10", "M0 0 L15 5")]
    [InlineData("M0 0 l10 0", "M0 0 L20 0", "M0 0 L15 0")]
    [InlineData("M0 0 10 10", "M10 10 30 30", "M5 5 L20 20")]
    [InlineData("M0 0 A5 5 0 0 1 10 0", "M0 0 A5 5 0 1 1 20 0", "M0 0 A5 5 0 1 1 15 0")]
    [InlineData("M0 0 L10 0", "M0 0 C1 1 2 2 3 3", "M0 0 C1 1 2 2 3 3")]
    [InlineData("M0 0 L10 0", "M0 0 L10 0 L20 0", "M0 0 L10 0 L20 0")]
    public void PathsMorphWhenTheirCommandsMatch(string from, string to, string atHalfAndAfter)
    {
        string? value = ValueAt($"<animate attributeName='d' from='{from}' to='{to}' dur='1s'/>", 0.5, "d", "<path d='M0 0'>");
        Assert.Equal(atHalfAndAfter, value);
    }

    [Fact]
    public void ListsInterpolateItemByItem()
    {
        Assert.Equal("5 5 15 15", ValueAt("<animate attributeName='points' from='0,0 10,10' to='10 10 20 20' dur='1s'/>", 0.5, "points"));
        Assert.Equal("1 2 3", ValueAt("<animate attributeName='points' from='0 0 0 0' to='1 2 3' dur='1s'/>", 0.5, "points"));
        Assert.Equal("0 0 50 50", ValueAt("<animate attributeName='viewBox' from='0 0 100 100' to='0 0 0 0' dur='1s'/>", 0.5, "viewBox"));
    }

    [Fact]
    public void KeywordsStep()
    {
        Assert.Equal("visible", ValueAt("<animate attributeName='visibility' values='hidden;visible' dur='1s'/>", 0.6, "visibility"));
        Assert.Equal("hidden", ValueAt("<set attributeName='visibility' to='hidden' begin='1s'/>", 2, "visibility"));
        Assert.Null(ValueAt("<set attributeName='visibility' to='hidden' begin='1s'/>", 0.5, "visibility"));
    }

    [Theory]
    [InlineData("values='0;10' keyTimes='0;0.5'")]
    [InlineData("values='0;10' keyTimes='0.1;1'")]
    [InlineData("values='0;10' keyTimes='0;1;1'")]
    [InlineData("values='0;10' keyTimes='0;x'")]
    [InlineData("values='0;10' calcMode='spline'")]
    [InlineData("values='0;10' calcMode='spline' keySplines='0 0 1 2'")]
    [InlineData("values='0;;10'")]
    [InlineData("from='0'")]
    [InlineData("")]
    [InlineData("by='red'")]
    public void AnimationsInErrorHaveNoEffect(string attributes)
    {
        SvgDocument document = SvgDocument.Parse($"<svg xmlns='http://www.w3.org/2000/svg'><rect><animate attributeName='x' dur='1s' {attributes}/></rect></svg>");
        SvgReport report = new(strict: false);
        SvgTimeline timeline = SvgTimeline.Build(document, report);
        Assert.Null(timeline.Animations[0].Function);
        Assert.Contains(report.Warnings, w => w.Id == "Svg.BadAnimation");
    }

    [Fact]
    public void TheSandwichOrdersByBeginThenDocumentOrder()
    {
        // The later-beginning additive animation sits on top of the set, though it comes first in the document.
        Assert.Equal("35", ValueAt("<animate attributeName='x' from='0' to='10' additive='sum' begin='1s' dur='2s'/><set attributeName='x' to='30' begin='0s'/>", 2));

        // Same begin: document order, the later one on top.
        Assert.Equal("2", ValueAt("<set attributeName='x' to='1'/><set attributeName='x' to='2'/>", 1));

        // A replacing animation hides what is below it; a to animation starts from it.
        Assert.Equal("4", ValueAt("<set attributeName='x' to='50'/><animate attributeName='x' from='0' to='8' dur='2s'/>", 1));
        Assert.Equal("30", ValueAt("<set attributeName='x' to='50'/><animate attributeName='x' to='10' dur='2s'/>", 1));
    }

    [Fact]
    public void ARestartMovesAnAnimationUpTheSandwich()
    {
        const string Sets = "<set attributeName='x' to='5' begin='0; 3s' dur='10s'/><set attributeName='x' to='7' begin='1s' dur='10s'/>";
        Assert.Equal("7", ValueAt(Sets, 2));
        Assert.Equal("5", ValueAt(Sets, 4));
    }

    [Fact]
    public void ValueTypesWriteBackAsAttributeText()
    {
        Assert.Equal("1.5 2px 3% 4em", SvgNumericValue.TryParse("1.5, 2px 3% 4em")!.ToText());
        Assert.Null(SvgNumericValue.TryParse("1 auto"));
        Assert.Equal("M1 2 L3 4 Z", SvgPathValue.TryParse("M1,2 3,4z")!.ToText());
        Assert.Null(SvgPathValue.TryParse("L1 2"));
        Assert.Equal("rotate(45 10 10)", SvgTransformValue.TryParse("rotate", "45 10 10")!.ToText());
        Assert.Equal("scale(2 2)", SvgTransformValue.TryParse("scale", "2")!.ToText());
        Assert.Null(SvgTransformValue.TryParse("rotate", "1 2"));
        Assert.Equal("translate(5 10)", SvgTransformValue.TryParse("translate", "0 0")!.Interpolate(SvgTransformValue.TryParse("translate", "10 20")!, 0.5)!.ToText());
        Assert.Equal(5, SvgTransformValue.TryParse("rotate", "10")!.Distance(SvgTransformValue.TryParse("rotate", "15 3 3")!));
        Assert.Equal(255, SvgColorValue.TryParse("red")!.Distance(SvgColorValue.TryParse("black")!), 6);
        Assert.Equal("0", SvgAnimatedValue.Format(1e-9));
        Assert.Equal("-0.333333", SvgAnimatedValue.Format(-1 / 3.0));
    }
}
