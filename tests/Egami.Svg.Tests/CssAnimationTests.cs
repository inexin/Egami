using System.Numerics;

using Egami.Css;
using Egami.Drawing;
using Egami.Svg.Animation;
using Egami.Svg.Styling;

namespace Egami.Svg.Tests;

/// <summary>SVG animation stage C: CSS animations (task 7).</summary>
public class CssAnimationTests
{
    private const string Ns = "xmlns='http://www.w3.org/2000/svg'";

    private static SvgDocument Doc(string css, string body) => SvgDocument.Parse($"<svg {Ns} width='100' height='100'><style>{css}</style>{body}</svg>");

    private static IEnumerable<SvgPath> Paths(SvgNode node) => node switch
    {
        SvgPath path => [path],
        SvgGroup group => group.Children.SelectMany(Paths),
        _ => [],
    };

    private static SvgPath Shape(SvgDocument document, double seconds, string id = "r") =>
        Paths(document.RenderTreeAt(TimeSpan.FromSeconds(seconds))).Single(p => p.Id == id);

    private static float OpacityAt(string css, double seconds, string rect = "<rect id='r' width='10' height='10'/>") =>
        Shape(Doc(css, rect), seconds).Opacity;

    private static (float R, float G, float B, float A) FillAt(SvgDocument document, double seconds)
    {
        SolidPaint paint = Assert.IsType<SolidPaint>(Shape(document, seconds).Fill);
        return (paint.Color.R, paint.Color.G, paint.Color.B, paint.Color.A);
    }

    private static readonly SvgCssValueContext s_context = new(16, 16, 100, 100, new CssColor(0, 0, 0));

    private const string Fade = "@keyframes fade { from { opacity: 0 } to { opacity: 1 } }";

    [Theory]
    [InlineData("#r { animation: fade 2s linear }", 1, 0.5)]
    [InlineData("#r { animation: fade 2s linear }", 0.5, 0.25)]
    [InlineData("#r { animation: fade 2s }", 1, 0.8024034)]
    [InlineData("#r { animation: fade 2s linear 1s }", 0.5, 1)]
    [InlineData("#r { animation: fade 2s linear 1s backwards }", 0.5, 0)]
    [InlineData("#r { animation: fade 2s linear }", 3, 1)]
    [InlineData("#r { opacity: 0.3; animation: fade 2s linear }", 3, 0.3)]
    [InlineData("#r { opacity: 0.3; animation: fade 2s linear forwards }", 3, 1)]
    [InlineData("#r { animation: fade 1s linear infinite }", 2.25, 0.25)]
    [InlineData("#r { animation: fade 1s linear infinite alternate }", 1.25, 0.75)]
    [InlineData("#r { animation: fade 1s linear 2 alternate-reverse forwards }", 5, 1)]
    [InlineData("#r { animation: fade 1s linear 3 alternate-reverse forwards }", 5, 0)]
    [InlineData("#r { animation: fade 1s linear reverse }", 0.25, 0.75)]
    [InlineData("#r { animation: fade 2s steps(4) }", 1.2, 0.5)]
    [InlineData("#r { animation: fade 2s linear -1s }", 0.5, 0.75)]
    [InlineData("#r { animation: fade 2s linear paused }", 1, 0)]
    [InlineData("#r { animation: fade 1s linear 0.5 forwards }", 3, 0.5)]
    public void OpacityFollowsTheTiming(string rule, double seconds, double expected) =>
        Assert.Equal(expected, OpacityAt(Fade + rule, seconds), 4);

    [Fact]
    public void KeyframesInTheMiddleAndImplicitEnds()
    {
        // The corpus's blinking animation: only 50% is given; 0% and 100% take the element's own value.
        const string Css = "@keyframes blinking { 50% { opacity: 0.0; } } .blink { animation: blinking 1s infinite linear; opacity: 0.8 }";
        const string Rect = "<rect id='r' class='blink' width='10' height='10'/>";
        Assert.Equal(0.8, OpacityAt(Css, 0, Rect), 4);
        Assert.Equal(0.4, OpacityAt(Css, 0.25, Rect), 4);
        Assert.Equal(0, OpacityAt(Css, 0.5, Rect), 4);
        Assert.Equal(0.4, OpacityAt(Css, 1.75, Rect), 4);
    }

    [Fact]
    public void PerKeyframeEasingAndSeveralSelectors()
    {
        const string Css = "@keyframes k { from, 50% { opacity: 0; animation-timing-function: steps(2, jump-end) } to { opacity: 1 } } #r { animation: k 2s linear }";
        Assert.Equal(0, OpacityAt(Css, 0.5), 4);
        Assert.Equal(0, OpacityAt(Css, 1.2), 4);
        Assert.Equal(0.5, OpacityAt(Css, 1.6), 4);
    }

    [Fact]
    public void TheLastKeyframesRuleOfANameWins() =>
        Assert.Equal(0.75, OpacityAt("@keyframes k { to { opacity: 0 } } @keyframes k { to { opacity: 0.5 } } #r { animation: k 1s linear }", 0.5), 4);

    [Fact]
    public void ImportantRulesBeatAnimationsAndAnimationsBeatSmil()
    {
        const string Css = "@keyframes red { to { fill: red } } #r { fill: blue; animation: red 1s linear forwards }";
        SvgDocument smil = Doc(Css, "<rect id='r' width='10' height='10'><set attributeName='fill' to='lime'/></rect>");
        Assert.Equal((1f, 0f, 0f, 1f), FillAt(smil, 2));

        SvgDocument important = Doc(Css + " rect { fill: green !important }", "<rect id='r' width='10' height='10'/>");
        (float r, float g, float _, float _) = FillAt(important, 2);
        Assert.Equal(0, r);
        Assert.True(g > 0.4f);
    }

    [Fact]
    public void SmilFeedsTheUnderlyingValue()
    {
        // The implicit from keyframe is the value below the animation, which SMIL sets.
        const string Css = "@keyframes k { to { opacity: 1 } } #r { animation: k 2s linear }";
        SvgDocument document = Doc(Css, "<rect id='r' width='10' height='10'><set attributeName='opacity' to='0'/></rect>");
        Assert.Equal(0.5, Shape(document, 1).Opacity, 4);
    }

    [Fact]
    public void StaticRenderingIgnoresAnimations()
    {
        SvgDocument document = Doc(Fade + "#r { animation: fade 2s linear }", "<rect id='r' width='10' height='10'/>");
        SvgPath shape = Paths(document.RenderTree).Single(p => p.Id == "r");
        Assert.Equal(1, shape.Opacity);
    }

    [Fact]
    public void ColorsInterpolateWithPremultipliedAlpha()
    {
        Assert.Equal("rgba(255, 0, 0, 0.5)", SvgCssValues.Interpolate("fill", "red", "rgba(0, 0, 255, 0)", 0.5, s_context));
        Assert.Equal("rgb(128, 0, 128)", SvgCssValues.Interpolate("stop-color", "red", "blue", 0.5, s_context));
        Assert.Equal("rgb(128, 0, 0)", SvgCssValues.Interpolate("fill", "currentcolor", "red", 0.5, s_context));
        Assert.Equal("url(#b)", SvgCssValues.Interpolate("fill", "url(#a)", "url(#b)", 0.6, s_context));
        Assert.Equal("url(#a)", SvgCssValues.Interpolate("fill", "url(#a)", "red", 0.4, s_context));

        SvgDocument document = Doc("@keyframes k { from { fill: red } to { fill: blue } } #r { animation: k 2s linear }", "<rect id='r' width='10' height='10'/>");
        (float r, float g, float b, float a) = FillAt(document, 1);
        Assert.Equal(128 / 255f, r, 2);
        Assert.Equal(0, g);
        Assert.Equal(128 / 255f, b, 2);
        Assert.Equal(1, a);
    }

    [Theory]
    [InlineData("stroke-width", "2px", "4px", "3px")]
    [InlineData("stroke-width", "1in", "48px", "72px")]
    [InlineData("stroke-width", "1em", "0", "0.5em")]
    [InlineData("stroke-width", "1em", "48px", "32px")]
    [InlineData("stroke-width", "10%", "20%", "15%")]
    [InlineData("stroke-width", "10%", "20px", "20px")]
    [InlineData("stroke-dasharray", "2 4", "4 8 12", "3 6 7 4 5 8")]
    [InlineData("opacity", "50%", "1", "0.75")]
    [InlineData("font-weight", "normal", "bold", "550")]
    [InlineData("visibility", "hidden", "visible", "visible")]
    [InlineData("--custom", "a", "b", "b")]
    [InlineData("stroke-linecap", "round", "square", "square")]
    public void ValuesInterpolate(string property, string from, string to, string expected) =>
        Assert.Equal(expected, SvgCssValues.Interpolate(property, from, to, 0.5, s_context));

    [Fact]
    public void DiscreteValuesFlipHalfway()
    {
        Assert.Equal("a", SvgCssValues.Interpolate("--custom", "a", "b", 0.49, s_context));
        Assert.Equal("hidden", SvgCssValues.Interpolate("visibility", "hidden", "visible", 0, s_context));
    }

    [Theory]
    [InlineData("translate(0px, 0px)", "translate(100px, 50px)", "translate(50px, 25px)")]
    [InlineData("none", "rotate(90deg) scale(3)", "rotate(45deg) scale(2, 2)")]
    [InlineData("translate(10px) rotate(10deg)", "translate(20px) rotate(30deg) skewX(10deg)", "translate(15px, 0px) rotate(20deg) skew(5deg, 0deg)")]
    [InlineData("skew(10deg, 20deg)", "skewY(40deg)", "skew(5deg, 30deg)")]
    [InlineData("translate(10%)", "translate(30%)", "translate(20%, 0px)")]
    public void TransformListsInterpolateFunctionByFunction(string from, string to, string expected) =>
        Assert.Equal(expected, SvgCssValues.Interpolate("transform", from, to, 0.5, s_context));

    [Fact]
    public void DifferentTransformListsInterpolateThroughMatrices()
    {
        string mixed = SvgCssValues.Interpolate("transform", "rotate(0deg)", "translate(10px) rotate(90deg)", 0.5, s_context);
        Assert.True(CssTransformList.TryParse(mixed, out CssTransformList? list), mixed);
        CssMatrixTransform m = Assert.IsType<CssMatrixTransform>(Assert.Single(list.Functions));
        Assert.Equal(5, m.E, 3);
        Assert.Equal(0, m.F, 3);
        Assert.Equal(Math.Cos(Math.PI / 4), m.A, 3);
        Assert.Equal(Math.Sin(Math.PI / 4), m.B, 3);

        // Decomposition turns the short way: 170° to -170° goes through 180°.
        Matrix3x2 half = SvgCssValues.InterpolateMatrix(Matrix3x2.CreateRotation(170 * MathF.PI / 180), Matrix3x2.CreateRotation(-170 * MathF.PI / 180), 0.5);
        Assert.Equal(-1, half.M11, 3);
        Assert.Equal(0, half.M12, 3);

        // Scales and shears survive the round trip.
        Matrix3x2 skewed = new(2, 0.5f, 1, 3, 4, 5);
        Matrix3x2 same = SvgCssValues.InterpolateMatrix(skewed, skewed, 0.3);
        Assert.Equal(skewed.M11, same.M11, 4);
        Assert.Equal(skewed.M12, same.M12, 4);
        Assert.Equal(skewed.M21, same.M21, 4);
        Assert.Equal(skewed.M22, same.M22, 4);
        Assert.Equal(skewed.M31, same.M31, 4);

        Matrix3x2 flipped = new(-1, 0, 0, 1, 0, 0);
        Matrix3x2 kept = SvgCssValues.InterpolateMatrix(flipped, flipped, 0.5);
        Assert.Equal(-1, kept.M11, 4);
        Assert.Equal(1, kept.M22, 4);
    }

    [Fact]
    public void TransformCompositionAddsAndAccumulates()
    {
        Assert.Equal("translate(10px, 0px) rotate(45deg)", SvgCssValues.Composite("transform", "translate(10px, 0px)", "rotate(45deg)", SvgCssComposite.Add, s_context));
        Assert.Equal("translate(15px, 5px)", SvgCssValues.Composite("transform", "translate(10px)", "translate(5px, 5px)", SvgCssComposite.Accumulate, s_context));
        Assert.Equal("scale(2.5, 2.5)", SvgCssValues.Composite("transform", "scale(2)", "scale(1.5)", SvgCssComposite.Accumulate, s_context));
        Assert.Equal("rotate(45deg)", SvgCssValues.Composite("transform", "none", "rotate(45deg)", SvgCssComposite.Add, s_context));
        Assert.Equal("3", SvgCssValues.Composite("stroke-width", "1", "2", SvgCssComposite.Add, s_context));
        Assert.Equal("red", SvgCssValues.Composite("stroke-linecap", "round", "red", SvgCssComposite.Add, s_context));
    }

    [Fact]
    public void TheTransformAnimationMovesTheShape()
    {
        SvgDocument document = Doc("@keyframes k { to { transform: translate(40px, 20px) } } #r { animation: k 2s linear }", "<rect id='r' width='10' height='10'/>");
        Assert.Equal(20, Shape(document, 1).Transform.M31, 3);
        Assert.Equal(10, Shape(document, 1).Transform.M32, 3);
    }

    [Fact]
    public void AnAddedTransformBuildsOnTheTransformAttribute()
    {
        SvgDocument document = Doc(
            "@keyframes k { from { transform: translate(0px) } to { transform: translate(40px) } } #r { animation: k 2s linear; animation-composition: add }",
            "<rect id='r' width='10' height='10' transform='translate(0 30)'/>");
        Matrix3x2 m = Shape(document, 1).Transform;
        Assert.Equal(20, m.M31, 3);
        Assert.Equal(30, m.M32, 3);
    }

    [Fact]
    public void IndividualTransformPropertiesAnimate()
    {
        Assert.Equal("10px 2.5em", SvgCssValues.Interpolate("translate", "none", "20px 5em", 0.5, s_context));
        Assert.Equal("45deg", SvgCssValues.Interpolate("rotate", "none", "90deg", 0.5, s_context));
        Assert.Equal("2 1.5", SvgCssValues.Interpolate("scale", "1", "3 2", 0.5, s_context));

        SvgDocument document = Doc(
            "@keyframes k { to { translate: 40px 20px; rotate: 90deg; scale: 3 } } #r { animation: k 2s linear }",
            "<rect id='r' width='10' height='10'/>");
        Matrix3x2 m = Shape(document, 1).Transform;
        Matrix3x2 expected = Matrix3x2.CreateScale(2) * Matrix3x2.CreateRotation(MathF.PI / 4) * Matrix3x2.CreateTranslation(20, 10);
        Assert.Equal(expected.M11, m.M11, 3);
        Assert.Equal(expected.M12, m.M12, 3);
        Assert.Equal(expected.M21, m.M21, 3);
        Assert.Equal(20, m.M31, 3);
        Assert.Equal(10, m.M32, 3);
    }

    [Fact]
    public void OffsetPathMovesAlongThePath()
    {
        SvgDocument document = Doc(
            "@keyframes k { from { offset-distance: 0% } to { offset-distance: 100% } } #r { offset-path: path('M0 0 H50 V50'); animation: k 2s linear }",
            "<rect id='r' width='10' height='10'/>");
        Matrix3x2 early = Shape(document, 0.5).Transform;
        Assert.Equal(25, early.M31, 2);
        Assert.Equal(0, early.M32, 2);
        Assert.Equal(1, early.M11, 3);

        // Past the corner the path heads down, and offset-rotate auto turns the shape with it.
        Matrix3x2 late = Shape(document, 1.5).Transform;
        Assert.Equal(50, late.M31, 2);
        Assert.Equal(25, late.M32, 2);
        Assert.Equal(0, late.M11, 3);
        Assert.Equal(1, late.M12, 3);

        Assert.Equal("auto 45deg", SvgCssValues.Interpolate("offset-rotate", "auto", "auto 90deg", 0.5, s_context));
        Assert.Equal("path(\"M 0 0 L 15 5\")", SvgCssValues.Interpolate("offset-path", "path('M0 0 L10 0')", "path('M0 0 L20 10')", 0.5, s_context).Replace("M0", "M 0", StringComparison.Ordinal).Replace("L15", "L 15", StringComparison.Ordinal));
    }

    [Fact]
    public void PathDataMorphs()
    {
        SvgDocument document = Doc(
            "@keyframes k { from { d: path('M0 0 H10 V10 Z') } to { d: path('M0 0 H30 V30 Z') } } #r { animation: k 2s linear }",
            "<path id='r' d='M0 0 H1 V1 Z'/>");
        Assert.Equal(20, Shape(document, 1).Path.Bounds.Width, 2);
        Assert.Equal(1, Shape(document, 3).Path.Bounds.Width, 2);
    }

    [Fact]
    public void GeometryPropertiesAnimate()
    {
        SvgDocument document = Doc("@keyframes k { to { r: 30px } } #r { animation: k 2s linear }", "<circle id='r' r='10'/>");
        Assert.Equal(40, Shape(document, 1).Path.Bounds.Width, 1);
    }

    [Fact]
    public void CustomPropertiesAnimateDiscretelyThroughVar()
    {
        SvgDocument document = Doc(
            "@keyframes k { from { --c: red } to { --c: blue } } #r { fill: var(--c); animation: k 2s linear }",
            "<rect id='r' width='10' height='10'/>");
        Assert.Equal((1f, 0f, 0f, 1f), FillAt(document, 0.9));
        Assert.Equal((0f, 0f, 1f, 1f), FillAt(document, 1.1));
    }

    [Fact]
    public void KeyframeValuesAreComputedInTheElementsContext()
    {
        // inherit and currentcolor resolve where the element is.
        SvgDocument document = Doc(
            "@keyframes k { from { fill: inherit } to { fill: currentcolor } } #g { fill: red; color: blue } #r { animation: k 2s linear }",
            "<g id='g'><rect id='r' width='10' height='10'/></g>");
        (float r, float _, float b, float _) = FillAt(document, 1);
        Assert.Equal(128 / 255f, r, 2);
        Assert.Equal(128 / 255f, b, 2);
    }

    [Fact]
    public void AnimationsStackInNameOrder()
    {
        const string Css = "@keyframes a { to { opacity: 0 } } @keyframes b { to { opacity: 1 } } #r { animation: a 2s linear forwards, b 2s linear; opacity: 0.5 }";
        // b starts from a's value: at 1 s a gives 0.25, b goes halfway from there to 1.
        Assert.Equal(0.625, OpacityAt(Css, 1), 4);
        Assert.Equal(0, OpacityAt(Css, 3), 4);
    }

    [Fact]
    public void ImportantKeyframeDeclarationsAreIgnored() =>
        Assert.Equal(1, OpacityAt("@keyframes k { to { opacity: 0 !important } } #r { animation: k 2s linear }", 1), 4);

    [Fact]
    public void TheShorthandSetsEveryLonghand()
    {
        CssPropertyRegistry registry = SvgProperties.CreateRegistry();
        Assert.True(registry.TryGetShorthand("animation", out CssShorthandDefinition? shorthand));
        IReadOnlyList<CssDeclaration>? longhands = shorthand.Expand(CssComponentValue.ParseList("spin 2s ease-in 1s infinite reverse both paused, none 3s"));
        Assert.NotNull(longhands);
        Dictionary<string, string> values = longhands.ToDictionary(d => d.Name, d => d.ValueText);
        Assert.Equal("spin, none", values["animation-name"]);
        Assert.Equal("2s, 3s", values["animation-duration"]);
        Assert.Equal("ease-in, ease", values["animation-timing-function"]);
        Assert.Equal("1s, 0s", values["animation-delay"]);
        Assert.Equal("infinite, 1", values["animation-iteration-count"]);
        Assert.Equal("reverse, normal", values["animation-direction"]);
        Assert.Equal("both, none", values["animation-fill-mode"]);
        Assert.Equal("paused, running", values["animation-play-state"]);
        Assert.Equal("replace, replace", values["animation-composition"]);

        // A keyword its own longhand already has becomes the name.
        IReadOnlyList<CssDeclaration>? named = shorthand.Expand(CssComponentValue.ParseList("ease ease 1s"));
        Assert.Equal("ease", named!.Single(d => d.Name == "animation-name").ValueText);
        Assert.Null(shorthand.Expand(CssComponentValue.ParseList("1s 2s 3s")));
    }

    [Fact]
    public void AnimationInfoCountsCssAnimations()
    {
        SvgDocument looping = Doc(Fade + "#r { animation: fade 2s infinite alternate }", "<rect id='r' width='10' height='10'/>");
        Assert.True(looping.Animation.HasAnimations);
        Assert.Null(looping.Animation.Duration);
        Assert.Equal(TimeSpan.FromSeconds(4), looping.Animation.LoopPeriod);

        SvgDocument finite = Doc(Fade + "#r { animation: fade 1s 0.5s 2 }", "<rect id='r' width='10' height='10'/>");
        Assert.Equal(TimeSpan.FromSeconds(2.5), finite.Animation.Duration);
        Assert.Null(finite.Animation.LoopPeriod);

        SvgDocument unused = Doc(Fade + ".none { animation: fade 1s }", "<rect id='r' width='10' height='10'/>");
        Assert.False(unused.Animation.HasAnimations);
    }

    [Fact]
    public void TimingFollowsWebAnimations()
    {
        SvgCssAnimation a = new(CssStyleSheet.Parse("@keyframes k { to { opacity: 0 } }").Rules.OfType<CssKeyframesRule>().Single(), 2, 1, 3, SvgCssDirection.Alternate, false, true, false, CssEasingFunction.Linear, SvgCssComposite.Replace);
        Assert.False(a.TryProgress(0.5, out _, out _));
        Assert.True(a.TryProgress(2, out double p, out double i));
        Assert.Equal(0.5, p, 9);
        Assert.Equal(0, i);
        Assert.True(a.TryProgress(4, out p, out i));
        Assert.Equal(0.5, p, 9);
        Assert.Equal(1, i);

        // After the active interval, forwards fill holds the end of the last iteration (the third, forwards).
        Assert.True(a.TryProgress(100, out p, out i));
        Assert.Equal(1, p, 9);
        Assert.Equal(2, i);
    }
}
