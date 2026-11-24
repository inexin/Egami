namespace Egami.Css.Tests;

public sealed class AnimationTests
{
    private static CssKeyframesRule Keyframes(string css, List<CssParseError>? errors = null)
    {
        CssStyleSheet sheet = CssStyleSheet.Parse(css, new CssParseOptions { Errors = e => errors?.Add(e) });
        return Assert.IsType<CssKeyframesRule>(Assert.Single(sheet.Rules));
    }

    [Fact]
    public void KeyframesParseIntoARule()
    {
        CssKeyframesRule rule = Keyframes("@keyframes Spin { from { opacity: 0 } 25%, 75% { opacity: 0.5; fill: red } to { opacity: 1 } }");
        Assert.Equal("Spin", rule.Name);
        Assert.Equal(3, rule.Keyframes.Count);
        Assert.Equal([0.0], rule.Keyframes[0].Offsets);
        Assert.Equal([0.25, 0.75], rule.Keyframes[1].Offsets);
        Assert.Equal([1.0], rule.Keyframes[2].Offsets);
        Assert.Equal(["opacity", "fill"], rule.Keyframes[1].Declarations.Select(d => d.Name));
        Assert.Equal(1, rule.Line);
    }

    [Fact]
    public void WebkitKeyframesAndStringNames()
    {
        CssKeyframesRule rule = Keyframes("@-webkit-keyframes \"my name\" { TO { opacity: 1 } }");
        Assert.Equal("my name", rule.Name);
        Assert.Equal([1.0], Assert.Single(rule.Keyframes).Offsets);
    }

    [Fact]
    public void ImportantDeclarationsAreDropped()
    {
        CssKeyframesRule rule = Keyframes("@keyframes a { 50% { opacity: 0 !important; fill: blue } }");
        Assert.Equal("fill", Assert.Single(Assert.Single(rule.Keyframes).Declarations).Name);
    }

    [Theory]
    [InlineData("101%")]
    [InlineData("-1%")]
    [InlineData("50")]
    [InlineData("from to")]
    [InlineData("from,")]
    [InlineData("middle")]
    public void InvalidKeyframeSelectorsDropTheKeyframe(string selector)
    {
        List<CssParseError> errors = [];
        CssKeyframesRule rule = Keyframes("@keyframes a { " + selector + " { opacity: 0 } to { opacity: 1 } }", errors);
        Assert.Equal([1.0], Assert.Single(rule.Keyframes).Offsets);
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("@keyframes none { to { opacity: 1 } }")]
    [InlineData("@keyframes initial { to { opacity: 1 } }")]
    [InlineData("@keyframes 12 { to { opacity: 1 } }")]
    [InlineData("@keyframes a b { to { opacity: 1 } }")]
    [InlineData("@keyframes a;")]
    public void InvalidKeyframesRulesAreDropped(string css)
    {
        List<CssParseError> errors = [];
        CssStyleSheet sheet = CssStyleSheet.Parse(css, new CssParseOptions { Errors = errors.Add });
        Assert.Empty(sheet.Rules);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void NestedAtRulesInKeyframesAreDropped()
    {
        List<CssParseError> errors = [];
        CssKeyframesRule rule = Keyframes("@keyframes a { @media print { to { opacity: 1 } } to { opacity: 0 } }", errors);
        Assert.Single(rule.Keyframes);
        Assert.Single(errors);
    }

    [Fact]
    public void KeyframesInsideMediaRules()
    {
        CssStyleSheet sheet = CssStyleSheet.Parse("@media screen { @keyframes a { to { opacity: 1 } } }");
        CssMediaRule media = Assert.IsType<CssMediaRule>(Assert.Single(sheet.Rules));
        Assert.IsType<CssKeyframesRule>(Assert.Single(media.Rules));
    }

    [Theory]
    [InlineData("linear", 0.3, 0.3)]
    [InlineData("ease", 0, 0)]
    [InlineData("ease", 1, 1)]
    [InlineData("ease", 0.5, 0.8024033877399112)]
    [InlineData("ease-in", 0.5, 0.3153568125725365)]
    [InlineData("ease-out", 0.5, 0.6846431874274635)]
    [InlineData("ease-in-out", 0.5, 0.5)]
    [InlineData("cubic-bezier(0, 0, 1, 1)", 0.37, 0.37)]
    [InlineData("cubic-bezier(0.5, 0, 0.5, 1)", 0.5, 0.5)]
    public void CubicBezierCurves(string text, double input, double expected)
    {
        Assert.Equal(expected, CssEasingFunction.Parse(text).Evaluate(input), 1e-6);
    }

    [Fact]
    public void CubicBezierMatchesItsCurveEverywhere()
    {
        // Steep and flat stretches: the solver must find t such that x(t) is the input, whatever the curve.
        (double X1, double Y1, double X2, double Y2)[] curves = [(0, 1, 0, 1), (1, 0, 0, 1), (0.99, -2, 0.01, 3), (0, 0, 0, 1), (1, 1, 1, 1)];
        foreach ((double x1, double y1, double x2, double y2) in curves)
        {
            CssEasingFunction easing = CssEasingFunction.Parse(FormattableString.Invariant($"cubic-bezier({x1}, {y1}, {x2}, {y2})"));
            for (int i = 0; i <= 1000; i++)
            {
                double t = i / 1000.0;
                double x = Bezier(x1, x2, t);
                double y = Bezier(y1, y2, t);
                Assert.Equal(y, easing.Evaluate(x), Math.Abs(DyDx(x1, y1, x2, y2, t)) < 1e3 ? 1e-6 : 1e-2);
            }
        }

        static double Bezier(double p1, double p2, double t) => (3 * (1 - t) * (1 - t) * t * p1) + (3 * (1 - t) * t * t * p2) + (t * t * t);

        static double DyDx(double x1, double y1, double x2, double y2, double t)
        {
            double dx = (3 * (1 - t) * (1 - t) * x1) + (6 * (1 - t) * t * (x2 - x1)) + (3 * t * t * (1 - x2));
            double dy = (3 * (1 - t) * (1 - t) * y1) + (6 * (1 - t) * t * (y2 - y1)) + (3 * t * t * (1 - y2));
            return dx == 0 ? double.PositiveInfinity : dy / dx;
        }
    }

    [Fact]
    public void CubicBezierExtrapolatesAlongItsEndTangents()
    {
        CssEasingFunction easing = CssEasingFunction.Parse("cubic-bezier(0.5, 1, 0.5, 0.5)");
        Assert.Equal(-1, easing.Evaluate(-0.5), 1e-9);
        Assert.Equal(2, easing.Evaluate(2), 1e-9);

        // An end control point on the axis: the tangent comes from the other control point.
        CssEasingFunction flat = CssEasingFunction.Parse("cubic-bezier(0, 0, 0.5, 1)");
        Assert.Equal(-2, flat.Evaluate(-1), 1e-9);
    }

    [Theory]
    [InlineData("steps(4)", 0.0, 0.0)]
    [InlineData("steps(4)", 0.24, 0.0)]
    [InlineData("steps(4)", 0.25, 0.25)]
    [InlineData("steps(4)", 1.0, 1.0)]
    [InlineData("steps(4, end)", 0.99, 0.75)]
    [InlineData("steps(4, jump-start)", 0.0, 0.25)]
    [InlineData("steps(4, start)", 0.99, 1.0)]
    [InlineData("steps(4, jump-none)", 0.0, 0.0)]
    [InlineData("steps(4, jump-none)", 0.3, 1.0 / 3)]
    [InlineData("steps(4, jump-none)", 0.8, 1.0)]
    [InlineData("steps(4, jump-both)", 0.0, 0.2)]
    [InlineData("steps(4, jump-both)", 0.5, 0.6)]
    [InlineData("steps(4, jump-both)", 1.0, 1.0)]
    [InlineData("step-start", 0.0, 1.0)]
    [InlineData("step-end", 0.99, 0.0)]
    [InlineData("step-end", 1.0, 1.0)]
    public void StepFunctions(string text, double input, double expected)
    {
        Assert.Equal(expected, CssEasingFunction.Parse(text).Evaluate(input), 1e-12);
    }

    [Fact]
    public void TheBeforeFlagTakesTheLowerSideOfAStep()
    {
        CssEasingFunction easing = CssEasingFunction.Parse("steps(1, jump-start)");
        Assert.Equal(1, easing.Evaluate(0));
        Assert.Equal(0, easing.Evaluate(0, beforeFlag: true));
        Assert.Equal(0.5, CssEasingFunction.Parse("steps(2)").Evaluate(0.5));
        Assert.Equal(0, CssEasingFunction.Parse("steps(2)").Evaluate(0.5, beforeFlag: true));
    }

    [Theory]
    [InlineData("linear(0, 1)", 0.4, 0.4)]
    [InlineData("linear(0, 0.25, 1)", 0.5, 0.25)]
    [InlineData("linear(0, 0.25, 1)", 0.75, 0.625)]
    [InlineData("linear(0, 0.5 25% 75%, 1)", 0.5, 0.5)]
    [InlineData("linear(0, 0.5 25% 75%, 1)", 0.125, 0.25)]
    [InlineData("linear(0, 0.5 25% 75%, 1)", 0.875, 0.75)]
    [InlineData("linear(0 0%, 1 50%, 0)", 0.75, 0.5)]
    [InlineData("linear(0, 1)", 1.5, 1.5)]
    [InlineData("linear(0, 1)", -0.5, -0.5)]
    [InlineData("linear(0 20%, 1 10%)", 0.2, 1)]
    public void LinearFunctions(string text, double input, double expected)
    {
        Assert.Equal(expected, CssEasingFunction.Parse(text).Evaluate(input), 1e-9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bounce")]
    [InlineData("cubic-bezier(1.1, 0, 0, 1)")]
    [InlineData("cubic-bezier(0, 0, 1)")]
    [InlineData("steps(0)")]
    [InlineData("steps(1.5)")]
    [InlineData("steps(1, jump-none)")]
    [InlineData("steps(2, middle)")]
    [InlineData("linear(1)")]
    [InlineData("linear(0, 1 10% 20% 30%)")]
    [InlineData("linear(0 10%, , 1)")]
    [InlineData("linear(5%, 1)")]
    [InlineData("ease ease")]
    public void InvalidEasingFunctions(string text)
    {
        Assert.False(CssEasingFunction.TryParse(text, out _));
        Assert.Throws<FormatException>(() => CssEasingFunction.Parse(text));
    }

    [Theory]
    [InlineData("EASE-IN", "ease-in")]
    [InlineData("steps(3, end)", "steps(3)")]
    [InlineData("steps(3,jump-both)", "steps(3, jump-both)")]
    [InlineData("cubic-bezier(.1,.2,.3,.4)", "cubic-bezier(0.1, 0.2, 0.3, 0.4)")]
    [InlineData("cubic-bezier(calc(0.1 + 0.1), 0, 1, 1)", "cubic-bezier(0.2, 0, 1, 1)")]
    public void EasingFunctionsSerialize(string text, string expected)
    {
        Assert.Equal(expected, CssEasingFunction.Parse(text).ToString());
    }

    [Fact]
    public void AnimationValuesBeatNormalDeclarationsAndLoseToImportantOnes()
    {
        CssCascade cascade = new([(CssStyleSheet.Parse("rect { fill: red; stroke: blue !important }"), CssOrigin.Author)]);
        TestElement rect = TestElement.Parse("""<svg xmlns="http://www.w3.org/2000/svg"><rect id="r"/></svg>""").FindById("r")!;
        CssCascadedStyle style = cascade.Cascade(rect);
        CssCascadedStyle animated = style.WithAnimationValues([CssDeclaration.Create("fill", "green"), CssDeclaration.Create("stroke", "green"), CssDeclaration.Create("opacity", "0.5")]);

        Assert.True(animated.TryGetDeclaration("fill", out CssDeclaration? fill, out CssOrigin origin));
        Assert.Equal("green", fill.ValueText);
        Assert.Equal(CssOrigin.Animation, origin);
        Assert.Equal("blue", animated.GetValueText("stroke"));
        Assert.Equal("0.5", animated.GetValueText("opacity"));

        // The original is unchanged.
        Assert.Equal("red", style.GetValueText("fill"));
        Assert.Null(style.GetValueText("opacity"));
    }

    [Fact]
    public void ASheetCannotHaveTheAnimationOrigin()
    {
        Assert.Throws<ArgumentException>(() => new CssCascade([(CssStyleSheet.Parse("a { b: c }"), CssOrigin.Animation)]));
    }
}
