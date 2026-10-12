using System.Numerics;

namespace Egami.Css.Tests;

public sealed class ValueTests
{
    [Theory]
    [InlineData("red", 255, 0, 0, 255, 3)]
    [InlineData("ReBeccaPurple", 102, 51, 153, 255, 4)]
    [InlineData("transparent", 0, 0, 0, 0, 3)]
    [InlineData("#0f0", 0, 255, 0, 255, 3)]
    [InlineData("#00ff0080", 0, 255, 0, 128, 4)]
    [InlineData("#abcd", 170, 187, 204, 221, 4)]
    [InlineData("rgb(255, 0, 0)", 255, 0, 0, 255, 3)]
    [InlineData("rgba(255, 0, 0, 0.5)", 255, 0, 0, 128, 3)]
    [InlineData("rgb(255, 0, 0, 0.5)", 255, 0, 0, 128, 4)]
    [InlineData("rgba(255, 0, 0, 50%)", 255, 0, 0, 128, 4)]
    [InlineData("rgb(100%, 0%, 300%)", 255, 0, 255, 255, 3)]
    [InlineData("rgb(255 0 0 / 50%)", 255, 0, 0, 128, 4)]
    [InlineData("rgb(none 128 none)", 0, 128, 0, 255, 4)]
    [InlineData("hsl(120, 100%, 25%)", 0, 128, 0, 255, 3)]
    [InlineData("hsl(0.5turn 100% 50%)", 0, 255, 255, 255, 4)]
    [InlineData("hsla(240deg, 100%, 50%, .25)", 0, 0, 255, 64, 3)]
    [InlineData("hwb(0 0% 0%)", 255, 0, 0, 255, 4)]
    [InlineData("hwb(0 60% 60%)", 128, 128, 128, 255, 4)]
    [InlineData("lab(54.29 80.82 69.89)", 255, 0, 0, 255, 4)]
    [InlineData("lch(54.29 106.84 40.85)", 255, 0, 0, 255, 4)]
    [InlineData("oklab(0.627955 0.224863 0.125846)", 255, 0, 0, 255, 4)]
    [InlineData("oklch(62.8% 0.2577 29.23 / 0.5)", 255, 0, 0, 128, 4)]
    [InlineData("color(srgb 1 0.5 0)", 255, 128, 0, 255, 4)]
    [InlineData("color(srgb-linear 0.2140 0.2140 0.2140)", 128, 128, 128, 255, 4)]
    [InlineData("color(display-p3 1 0 0)", 255, 0, 0, 255, 4)]
    [InlineData("color(xyz-d65 0.95047 1 1.08883)", 255, 255, 255, 255, 4)]
    [InlineData("color(xyz-d50 0.9642 1 0.8251)", 255, 255, 255, 255, 4)]
    [InlineData("color(rec2020 0 0 0)", 0, 0, 0, 255, 4)]
    [InlineData("color(a98-rgb 1 1 1)", 255, 255, 255, 255, 4)]
    [InlineData("color(prophoto-rgb 1 1 1)", 255, 255, 255, 255, 4)]
    [InlineData("ButtonText", 0, 0, 0, 255, 3)]
    [InlineData("Canvas", 255, 255, 255, 255, 4)]
    [InlineData("rgb(calc(100 + 155) 0 0)", 255, 0, 0, 255, 4)]
    public void Colors(string text, int r, int g, int b, int a, int level)
    {
        Assert.True(CssColor.TryParse(text, out CssColor color), text);
        (byte cr, byte cg, byte cb, byte ca) = color.ToRgba8();
        Assert.InRange(cr, r - 1, r + 1);
        Assert.InRange(cg, g - 1, g + 1);
        Assert.InRange(cb, b - 1, b + 1);
        Assert.InRange(ca, a - 1, a + 1);
        Assert.Equal(level, color.Level);
        Assert.False(color.IsCurrentColor);
    }

    [Theory]
    [InlineData("notacolor")]
    [InlineData("#12345")]
    [InlineData("rgb(255, 0 0)")]
    [InlineData("rgb(100%, 0, 0)")]
    [InlineData("rgba(none, 0, 0)")]
    [InlineData("hsl(10%, 10%, 10%)")]
    [InlineData("hwb(0, 0%, 0%)")]
    [InlineData("color(unknown 1 1 1)")]
    [InlineData("rgb(from red r g b)")]
    [InlineData("red blue")]
    public void InvalidColors(string text)
    {
        Assert.False(CssColor.TryParse(text, out _));
    }

    [Fact]
    public void CurrentColorAndSerialization()
    {
        Assert.True(CssColor.TryParse("CurrentColor", out CssColor current));
        Assert.True(current.IsCurrentColor);
        Assert.Equal("currentcolor", current.ToString());
        Assert.True(CssColor.TryParse("#ff000080", out CssColor half));
        Assert.Equal("rgba(255, 0, 0, 0.5)", half.ToString());
        Assert.Equal("rgb(0, 128, 0)", new CssColor(0, 128 / 255f, 0).ToString());
        Assert.Equal(CssColor.Transparent, new CssColor(0, 0, 0, 0));
    }

    [Theory]
    [InlineData("12px", 12, CssUnit.Px)]
    [InlineData("1.5EM", 1.5, CssUnit.Em)]
    [InlineData("50%", 50, CssUnit.Percent)]
    [InlineData("3", 3, CssUnit.None)]
    [InlineData("2Q", 2, CssUnit.Q)]
    [InlineData(" 4vmin ", 4, CssUnit.Vmin)]
    public void Lengths(string text, double value, CssUnit unit)
    {
        Assert.True(CssLength.TryParse(text, out CssLength length));
        Assert.Equal(new CssLength(value, unit), length);
    }

    [Fact]
    public void AbsoluteLengths()
    {
        Assert.True(new CssLength(1, CssUnit.In).TryToPixels(out double inch));
        Assert.Equal(96, inch);
        Assert.True(new CssLength(72, CssUnit.Pt).TryToPixels(out double points));
        Assert.Equal(96, points, 1e-9);
        Assert.False(new CssLength(1, CssUnit.Em).TryToPixels(out _));
        Assert.False(CssLength.TryParse("12deg", out _));
        Assert.Equal("12px", new CssLength(12, CssUnit.Px).ToString());
    }

    [Theory]
    [InlineData("90deg", 90)]
    [InlineData("100grad", 90)]
    [InlineData("0.25turn", 90)]
    [InlineData("3.14159265358979rad", 180)]
    public void Angles(string text, double degrees)
    {
        Assert.True(CssAngle.TryParse(text, out CssAngle angle));
        Assert.Equal(degrees, angle.Degrees, 1e-6);
    }

    [Fact]
    public void Calc()
    {
        static double Px(CssUnit unit) => unit switch { CssUnit.Px => 1, CssUnit.Em => 16, CssUnit.Percent => 2, _ => double.NaN };
        Assert.True(CssCalc.TryParse("calc(100% - 2em)", out CssCalc? calc));
        Assert.Equal(168, calc.Resolve(Px));
        Assert.True(calc.HasPercentage);
        Assert.True(CssCalc.TryParse("calc((2 * 3em + 4px) / 2)", out calc));
        Assert.Equal(50, calc.Resolve(Px));
        Assert.True(CssCalc.TryParse("clamp(10px, 50%, max(1em, 20px))", out calc));
        Assert.Equal(20, calc.Resolve(Px));
        Assert.True(CssCalc.TryParse("min(1px, 2px, -3px)", out calc));
        Assert.Equal(-3, calc.Resolve(Px));
        Assert.True(CssCalc.TryParse("calc(2 * pi)", out calc));
        Assert.True(calc.IsNumber);
        Assert.Equal(2 * Math.PI, calc.Resolve(Px), 1e-12);
        Assert.True(CssCalc.TryParse("10px", out calc));
        Assert.Equal(10, calc.Resolve(Px));
        Assert.True(CssCalc.TryParse("calc(1px / 0)", out calc));
        Assert.Equal(double.MaxValue, calc.Resolve(Px));
        Assert.Equal("calc(100% - 2em)", CssCalc.TryParse("calc(100% - 2em)", out calc) ? calc.ToString() : null);

        Assert.False(CssCalc.TryParse("calc(1px + 2)", out _));
        Assert.False(CssCalc.TryParse("calc(1px * 2px)", out _));
        Assert.False(CssCalc.TryParse("calc(2 / 1px)", out _));
        Assert.False(CssCalc.TryParse("calc(1px -2px)", out _));
        Assert.False(CssCalc.TryParse("calc(1px +2px)", out _));
        Assert.False(CssCalc.TryParse("calc(1deg + 2px)", out _));
        Assert.False(CssCalc.TryParse("clamp(1px, 2px)", out _));
        Assert.False(CssCalc.TryParse("(1px)", out _));
    }

    [Fact]
    public void Transforms()
    {
        Assert.True(CssTransformList.TryParse("translate(10px, 50%) rotate(90deg) scale(2, 50%)", out CssTransformList? list));
        Assert.Equal(3, list.Functions.Count);
        Assert.Equal(new CssTranslateTransform(new CssLength(10, CssUnit.Px), new CssLength(50, CssUnit.Percent)), list.Functions[0]);
        Matrix3x2 matrix = list.ToMatrix((length, isX) => length.Unit == CssUnit.Percent ? length.Value * (isX ? 2 : 4) : length.Value);

        // (1, 0) scales to (2, 0), rotates to (0, 2), then translates by (10, 200).
        Vector2 point = Vector2.Transform(new Vector2(1, 0), matrix);
        Assert.Equal(10, point.X, 1e-4f);
        Assert.Equal(202, point.Y, 1e-4f);

        Assert.True(CssTransformList.TryParse("none", out list));
        Assert.True(list.IsNone);
        Assert.True(CssTransformList.TryParse("rotateX(10deg) translateX(5px)", out list));
        Assert.True(list.HasUnsupported);
        Assert.Equal(new Vector2(5, 0), Vector2.Transform(Vector2.Zero, list.ToMatrix()));
        Assert.True(CssTransformList.TryParse("rotate(0) skew(45deg)", out list));
        Assert.Equal(1, Vector2.Transform(new Vector2(0, 1), list.ToMatrix()).X, 1e-5f);

        Assert.False(CssTransformList.TryParse("translate(10 20)", out _));
        Assert.False(CssTransformList.TryParse("translate(10px 20px)", out _));
        Assert.False(CssTransformList.TryParse("rotate(45)", out _));
        Assert.False(CssTransformList.TryParse("foo(1)", out _));
        Assert.False(CssTransformList.TryParse("translate(1px,)", out _));
        Assert.False(CssTransformList.TryParse("", out _));
    }

    [Fact]
    public void SvgTransforms()
    {
        Assert.True(CssTransformList.TryParseSvg("translate(10,20) rotate(90 5 5), scale(2)skewX(45)", out CssTransformList? list));
        Assert.Equal(4, list.Functions.Count);
        Assert.Equal(new CssRotateTransform(90, 5, 5), list.Functions[1]);
        Assert.True(CssTransformList.TryParseSvg("matrix(1 0 0 1 3 4)", out list));
        Assert.Equal(new Vector2(3, 4), Vector2.Transform(Vector2.Zero, list.ToMatrix()));
        Assert.True(CssTransformList.TryParseSvg("rotate(90 5 5)", out list));
        Vector2 p = Vector2.Transform(new Vector2(10, 5), list.ToMatrix());
        Assert.Equal(5, p.X, 1e-4f);
        Assert.Equal(10, p.Y, 1e-4f);
        Assert.True(CssTransformList.TryParseSvg("translate (1.5.5) scale(-1e1)", out list));
        Assert.Equal(new CssTranslateTransform(new CssLength(1.5, CssUnit.Px), new CssLength(0.5, CssUnit.Px)), list.Functions[0]);
        Assert.True(CssTransformList.TryParseSvg("", out list));
        Assert.True(list.IsNone);
        Assert.False(CssTransformList.TryParseSvg("translate(1,,2)", out _));
        Assert.False(CssTransformList.TryParseSvg(", scale(2)", out _));
        Assert.False(CssTransformList.TryParseSvg("scale(2),", out _));
        Assert.False(CssTransformList.TryParseSvg("rotate(1 2)", out _));
    }

    [Theory]
    [InlineData("left top", 0, CssUnit.Percent, 0, CssUnit.Percent)]
    [InlineData("top left", 0, CssUnit.Percent, 0, CssUnit.Percent)]
    [InlineData("center", 50, CssUnit.Percent, 50, CssUnit.Percent)]
    [InlineData("bottom", 50, CssUnit.Percent, 100, CssUnit.Percent)]
    [InlineData("10px 20%", 10, CssUnit.Px, 20, CssUnit.Percent)]
    [InlineData("right 5em 3px", 100, CssUnit.Percent, 5, CssUnit.Em)]
    public void TransformOrigins(string text, double x, CssUnit xUnit, double y, CssUnit yUnit)
    {
        Assert.True(CssTransformOrigin.TryParse(text, out CssTransformOrigin origin));
        Assert.Equal(new CssLength(x, xUnit), origin.X);
        Assert.Equal(new CssLength(y, yUnit), origin.Y);
        Assert.False(CssTransformOrigin.TryParse("top 10px", out _));
        Assert.False(CssTransformOrigin.TryParse("left right", out _));
        Assert.False(CssTransformOrigin.TryParse("1px 2px 3%", out _));
    }

    [Fact]
    public void Fonts()
    {
        Assert.True(CssFont.TryParse("italic small-caps bold condensed 12px/1.5 \"Open Sans\", Times New Roman, sans-serif", out CssFont? font));
        Assert.Equal("italic", font.Style);
        Assert.Equal("small-caps", font.VariantCaps);
        Assert.Equal(CssFontWeight.Bold, font.Weight);
        Assert.Equal("condensed", font.Stretch);
        Assert.Equal("12px", CssComponentValue.Serialize(font.Size));
        Assert.Equal("1.5", CssComponentValue.Serialize(font.LineHeight!));
        Assert.Equal([new CssFontFamily("Open Sans", false), new CssFontFamily("Times New Roman", false), new CssFontFamily("sans-serif", true)], font.Families!.Families);
        Assert.Equal("italic small-caps 700 condensed 12px/1.5 \"Open Sans\", \"Times New Roman\", sans-serif", font.ToString());

        Assert.True(CssFont.TryParse("oblique 10deg 600 larger serif", out font));
        Assert.Equal(10, font.ObliqueAngle);
        Assert.Equal(600, font.Weight.Value);
        Assert.True(CssFont.TryParse("menu", out font));
        Assert.Equal("menu", font.SystemFont);
        Assert.True(CssFont.TryParse("normal normal 0 serif", out font));

        Assert.False(CssFont.TryParse("12px", out _));
        Assert.False(CssFont.TryParse("bold serif", out _));
        Assert.False(CssFont.TryParse("bolder 12px serif", out _));
        Assert.False(CssFont.TryParse("italic italic 12px serif", out _));
        Assert.False(CssFont.TryParse("12px/ serif", out _));
        Assert.False(CssFont.TryParse("12px inherit", out _));

        Assert.True(CssFontFamilyList.TryParse("'serif', serif, Arial  Black", out CssFontFamilyList? families));
        Assert.Equal([new CssFontFamily("serif", false), new CssFontFamily("serif", true), new CssFontFamily("Arial Black", false)], families.Families);
        Assert.False(CssFontFamilyList.TryParse("a, , b", out _));
        Assert.False(CssFontFamilyList.TryParse("'a' b", out _));
    }

    [Theory]
    [InlineData("normal", 400)]
    [InlineData("BOLD", 700)]
    [InlineData("1", 1)]
    [InlineData("1000", 1000)]
    [InlineData("550.5", 550.5)]
    public void FontWeights(string text, double value)
    {
        Assert.True(CssFontWeight.TryParse(text, out CssFontWeight weight));
        Assert.Equal(value, weight.Resolve(100));
    }

    [Theory]
    [InlineData(50, 400, 50)]
    [InlineData(300, 400, 100)]
    [InlineData(400, 700, 100)]
    [InlineData(600, 900, 400)]
    [InlineData(800, 900, 700)]
    [InlineData(950, 950, 700)]
    public void RelativeFontWeights(double parent, double bolder, double lighter)
    {
        Assert.True(CssFontWeight.TryParse("bolder", out CssFontWeight b));
        Assert.True(CssFontWeight.TryParse("lighter", out CssFontWeight l));
        Assert.Equal(bolder, b.Resolve(parent));
        Assert.Equal(lighter, l.Resolve(parent));
        Assert.False(CssFontWeight.TryParse("0", out _));
        Assert.False(CssFontWeight.TryParse("1001", out _));
    }

    [Fact]
    public void FontFaces()
    {
        CssStyleSheet sheet = CssStyleSheet.Parse("""
            @font-face {
              font-family: "My Font";
              src: local("My Font Regular"), url(a.woff2) format("woff2") tech(variations, color-COLRv1), url("b.ttf") format(truetype), bogus(1), url(c.otf) junk;
              font-weight: 100 900;
              font-style: oblique 20deg 10deg;
              font-stretch: condensed 150%;
              unicode-range: U+0-7F, U+4??, u+1e3-1F600, U+26;
              font-display: swap;
              font-feature-settings: "liga" 0;
            }
            """);
        CssFontFace face = Assert.IsType<CssFontFaceRule>(Assert.Single(sheet.Rules)).FontFace;
        Assert.Equal("My Font", face.Family);
        Assert.Equal(3, face.Sources.Count);
        Assert.Equal("My Font Regular", face.Sources[0].LocalName);
        Assert.Equal("a.woff2", face.Sources[1].Url);
        Assert.Equal("woff2", face.Sources[1].Format);
        Assert.Equal(["variations", "color-colrv1"], face.Sources[1].Techs);
        Assert.Equal("truetype", face.Sources[2].Format);
        Assert.Equal(new CssRange(100, 900), face.Weight);
        Assert.Equal("oblique", face.Style);
        Assert.Equal(new CssRange(10, 20), face.ObliqueAngle);
        Assert.Equal(new CssRange(75, 150), face.Stretch);
        Assert.Equal([new CssUnicodeRange(0, 0x7F), new CssUnicodeRange(0x400, 0x4FF), new CssUnicodeRange(0x1E3, 0x1F600), new CssUnicodeRange(0x26, 0x26)], face.UnicodeRanges);
        Assert.Equal("swap", face.Display);
        Assert.Equal("\"liga\" 0", face.FeatureSettings);

        CssFontFace defaults = CssFontFace.FromDeclarations(CssDeclaration.ParseList("font-family: serif; unicode-range: U+110000; font-weight: bolder"));
        Assert.Null(defaults.Family);
        Assert.Equal([new CssUnicodeRange(0, 0x10FFFF)], defaults.UnicodeRanges);
        Assert.Null(defaults.Weight);
        Assert.Equal("auto", defaults.Display);
    }

    [Fact]
    public void Urls()
    {
        Assert.True(CssUrl.TryParse("url(a.png)", out CssUrl url));
        Assert.Equal("a.png", url.Url);
        Assert.True(CssUrl.TryParse("url( 'b c.png' )", out url));
        Assert.Equal("b c.png", url.Url);
        Assert.True(CssUrl.TryParse("src(\"d.png\" crossorigin(anonymous))", out url));
        Assert.Equal("d.png", url.Url);
        Assert.Equal("url(\"d.png\")", url.ToString());
        Assert.False(CssUrl.TryParse("url(a b)", out _));
        Assert.False(CssUrl.TryParse("'a.png'", out _));
    }
}
