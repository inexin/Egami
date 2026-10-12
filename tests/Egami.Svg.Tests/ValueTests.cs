using System.Numerics;

using Egami.Drawing;
using Egami.Svg.Values;

namespace Egami.Svg.Tests;

/// <summary>Task 4: numbers, lengths and units, transforms, viewBox and preserveAspectRatio, and the default size rule.</summary>
public class ValueTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("-1.5", -1.5)]
    [InlineData("+.5", 0.5)]
    [InlineData("5.", 5)]
    [InlineData("1e2", 100)]
    [InlineData("1E-2", 0.01)]
    [InlineData(" 7 ", 7)]
    public void NumbersRead(string text, double expected)
    {
        Assert.True(SvgNumbers.TryParse(text, out double value));
        Assert.Equal(expected, value, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("e5")]
    [InlineData("1 2")]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("1e400")]
    public void BadNumbersFail(string text) => Assert.False(SvgNumbers.TryParse(text, out _));

    [Fact]
    public void ListsSplitOnSignsAndPoints()
    {
        List<double> values = [];
        Assert.True(SvgNumbers.TryParseList("10-5,3 .5.5 1e1-2", values));
        Assert.Equal([10, -5, 3, 0.5, 0.5, 10, -2], values);

        values.Clear();
        Assert.False(SvgNumbers.TryParseList("1 2 x 3", values));
        Assert.Equal([1, 2], values);
    }

    [Theory]
    [InlineData("10", 10)]
    [InlineData("10px", 10)]
    [InlineData("1in", 96)]
    [InlineData("2.54cm", 96)]
    [InlineData("25.4mm", 96)]
    [InlineData("101.6Q", 96)]
    [InlineData("72pt", 96)]
    [InlineData("6pc", 96)]
    [InlineData("2em", 32)]
    [InlineData("2ex", 16)]
    [InlineData("2rem", 40)]
    [InlineData("50%", 100)]
    [InlineData("10vw", 30)]
    [InlineData("10vh", 40)]
    [InlineData("10vmin", 30)]
    [InlineData("10vmax", 40)]
    [InlineData("1PX", 1)]
    public void LengthsResolve(string text, float expected)
    {
        SvgLengthContext context = new(96, 16, 20, 200, 100, 300, 400);
        Assert.True(SvgLength.TryParse(text, out SvgLength length));
        Assert.Equal(expected, length.ToPixels(context, SvgAxis.X), 3);
    }

    [Fact]
    public void PercentagesUseTheRightViewportSide()
    {
        SvgLengthContext context = new(96, 16, 16, 300, 400, 300, 400);
        Assert.True(SvgLength.TryParse("10%", out SvgLength l));
        Assert.Equal(30, l.ToPixels(context, SvgAxis.X), 3);
        Assert.Equal(40, l.ToPixels(context, SvgAxis.Y), 3);
        Assert.Equal(MathF.Sqrt(((300 * 300) + (400 * 400)) / 2f) / 10, l.ToPixels(context, SvgAxis.Other), 3);
    }

    [Theory]
    [InlineData("1zz")]
    [InlineData("px")]
    [InlineData("1 px")]
    [InlineData("1%%")]
    public void BadLengthsFail(string text) => Assert.False(SvgLength.TryParse(text, out _));

    [Fact]
    public void LengthListsRead()
    {
        List<SvgLength> values = [];
        Assert.True(SvgLength.TryParseList("1 2px,3% 4em", values));
        Assert.Equal([new(1, SvgUnit.None), new(2, SvgUnit.Px), new(3, SvgUnit.Percent), new(4, SvgUnit.Em)], values);
    }

    [Fact]
    public void TransformsCompose()
    {
        Assert.True(SvgTransforms.TryParse("translate(10 20) scale(2)", out Matrix3x2 m));
        Assert.Equal(new Vector2(12, 22), Vector2.Transform(new Vector2(1, 1), m));

        Assert.True(SvgTransforms.TryParse("rotate(90 10 10)", out m));
        AssertNear(new Vector2(10, 10), Vector2.Transform(new Vector2(10, 10), m));
        AssertNear(new Vector2(10, 11), Vector2.Transform(new Vector2(11, 10), m));

        Assert.True(SvgTransforms.TryParse("matrix(1,0,0,1,5,6), skewX(45)", out m));
        AssertNear(new Vector2(7, 7), Vector2.Transform(new Vector2(1, 1), m));

        Assert.True(SvgTransforms.TryParse("skewY(45)translate(1)", out m));
        AssertNear(new Vector2(1, 1), Vector2.Transform(new Vector2(0, 0), m));

        Assert.True(SvgTransforms.TryParse("  ", out m));
        Assert.True(m.IsIdentity);
    }

    [Theory]
    [InlineData("translate()")]
    [InlineData("rotate(1 2)")]
    [InlineData("scale(1 2 3)")]
    [InlineData("matrix(1 2 3 4 5)")]
    [InlineData("translate(1")]
    [InlineData("foo(1)")]
    [InlineData("translate(1) )")]
    [InlineData("translate(1px)")]
    public void BadTransformsFail(string text) => Assert.False(SvgTransforms.TryParse(text, out _));

    [Fact]
    public void ViewBoxesRead()
    {
        Assert.True(SvgViewBox.TryParse(" 0,0 100 50 ", out RectF box));
        Assert.Equal(new RectF(0, 0, 100, 50), box);
        Assert.True(SvgViewBox.TryParse("0 0 0 10", out box));
        Assert.True(box.IsEmpty);
        Assert.False(SvgViewBox.TryParse("0 0 -1 10", out _));
        Assert.False(SvgViewBox.TryParse("0 0 1", out _));
        Assert.False(SvgViewBox.TryParse("0 0 1 1 1", out _));
    }

    [Theory]
    [InlineData("xMidYMid", 0, 25, 2, false)]
    [InlineData("xMinYMin meet", 0, 0, 2, false)]
    [InlineData("xMaxYMax", 0, 50, 2, false)]
    [InlineData("xMidYMid slice", -100, 0, 4, true)]
    [InlineData("xMaxYMax slice", -200, 0, 4, true)]
    [InlineData("defer xMinYMid", 0, 25, 2, false)]
    public void AspectRatiosFit(string text, float tx, float ty, float scale, bool slice)
    {
        // A 100 by 25 viewBox into a 200 by 100 viewport.
        Assert.True(SvgAspectRatio.TryParse(text, out SvgAspectRatio aspect));
        Assert.Equal(slice, aspect.Slice);
        Matrix3x2 m = aspect.Transform(new RectF(0, 0, 100, 25), new RectF(0, 0, 200, 100));
        Assert.Equal(scale, m.M11, 4);
        Assert.Equal(scale, m.M22, 4);
        Assert.Equal(tx, m.M31, 4);
        Assert.Equal(ty, m.M32, 4);
    }

    [Fact]
    public void NoneStretches()
    {
        Assert.True(SvgAspectRatio.TryParse("none", out SvgAspectRatio aspect));
        Matrix3x2 m = aspect.Transform(new RectF(10, 10, 100, 25), new RectF(0, 0, 200, 100));
        Assert.Equal(new Matrix3x2(2, 0, 0, 4, -20, -40), m);
        Assert.False(SvgAspectRatio.TryParse("xMidYMid sliced", out _));
        Assert.False(SvgAspectRatio.TryParse("", out _));
    }

    [Theory]
    [InlineData("width='30' height='40'", 30, 40)]
    [InlineData("width='1in' height='2.54cm'", 96, 96)]
    [InlineData("viewBox='0 0 20 10'", 20, 10)]
    [InlineData("viewBox='0 0 20 10' width='50%'", 10, 10)]
    [InlineData("viewBox='0 0 20 10' width='40'", 40, 10)]
    [InlineData("", 100, 100)]
    [InlineData("width='50%' height='10'", 50, 10)]
    [InlineData("width='-5' height='10' viewBox='0 0 8 9'", 8, 10)]
    [InlineData("font-size='10' width='2em' height='3em'", 20, 30)]
    public void DocumentsSize(string attributes, float width, float height)
    {
        SvgDocument document = SvgDocument.Parse($"<svg xmlns='http://www.w3.org/2000/svg' {attributes}/>");
        Assert.Equal(new Vector2(width, height), document.Size);
    }

    [Fact]
    public void DefaultSizeIsReported()
    {
        SvgDocument document = SvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg'/>");
        Assert.Equal("Svg.DefaultSize", Assert.Single(document.Warnings).Id);
        Assert.Null(document.ViewBox);

        document = SvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 5 6'/>");
        Assert.Empty(document.Warnings);
        Assert.Equal(new RectF(0, 0, 5, 6), document.ViewBox);

        document.Root.Attributes["width"] = "10";
        Assert.Equal(new Vector2(10, 6), document.Size);

        Assert.Throws<SvgException>(() => SvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg' width='x' height='1'/>", new SvgLoadOptions { Strict = true }));
    }

    private static void AssertNear(Vector2 expected, Vector2 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
    }
}
