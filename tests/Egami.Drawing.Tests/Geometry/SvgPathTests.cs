using System.Numerics;

namespace Egami.Drawing.Tests.Geometry;

/// <summary>
/// SVG path data, read and written. The cases follow what the W3C SVG 1.1 paths-data tests exercise (every command in both forms,
/// implicit repeats, compact numbers, arc flags, S and T reflection, closes followed by other commands), written out here as the
/// commands each should give.
/// </summary>
public class SvgPathTests
{
    [Fact]
    public void ReadsEveryCommandInAbsoluteForm()
    {
        Path path = Path.ParseSvg("M10 20 L30 40 H50 V60 C1 2 3 4 5 6 Q7 8 9 10 Z");
        Assert.Equal([PathVerb.Move, PathVerb.Line, PathVerb.Line, PathVerb.Line, PathVerb.Cubic, PathVerb.Quad, PathVerb.Close], path.Verbs.ToArray());
        Assert.Equal(
            [new(10, 20), new(30, 40), new(50, 40), new(50, 60), new(1, 2), new(3, 4), new(5, 6), new(7, 8), new(9, 10)],
            path.Points.ToArray());
    }

    [Fact]
    public void ReadsRelativeCommandsFromTheCurrentPoint()
    {
        Path path = Path.ParseSvg("m10 20 l5 5 h10 v-10 c1 1 2 2 3 3 q1 0 2 0 z l1 1");
        Assert.Equal(
            [new(10, 20), new(15, 25), new(25, 25), new(25, 15), new(26, 16), new(27, 17), new(28, 18), new(29, 18), new(30, 18), new(10, 20), new(11, 21)],
            path.Points.ToArray());
    }

    [Fact]
    public void ExtraPairsAfterAMoveAreLines()
    {
        Assert.Equal(Points("M0 0 L10 0 L10 10"), Points("M0 0 10 0 10 10"));
        Assert.Equal(Points("M1 1 L11 1 L11 11"), Points("m1 1 10 0 0 10"));
        Assert.Equal(Points("M0 0 L1 1 L2 2 L3 3"), Points("M0 0 L1 1 2 2, 3 3"));
    }

    [Fact]
    public void ReadsCompactNumbers()
    {
        Assert.Equal([new(0.5f, 0.5f), new(-1, -0.25f), new(100, 0.001f)], Points("M.5.5L-1-.25L1e2 1E-3"));
        Assert.Equal([new(1, 2), new(3, 4)], Points("M+1,+2\t\n\r L 3 , 4").ToArray());
    }

    [Fact]
    public void ReadsArcFlagsWithoutSeparators()
    {
        Path spaced = Path.ParseSvg("M0 0 A10 10 0 0 1 20 0");
        Path packed = Path.ParseSvg("M0 0A10 10 0 0120 0");
        Assert.Equal(spaced.Points.ToArray(), packed.Points.ToArray());
        Assert.All(spaced.Verbs[1..].ToArray(), v => Assert.Equal(PathVerb.Cubic, v));
        Assert.Equal(new Vector2(20, 0), spaced.Points[^1]);

        // A half circle clockwise on screen from (0,0) to (20,0) passes above, y down: through (10, -10).
        Assert.True(spaced.Contains(new Vector2(10, -9)));
        Assert.False(spaced.Contains(new Vector2(10, 9)));
    }

    [Fact]
    public void ArcsMatchTheBuilder()
    {
        Path parsed = Path.ParseSvg("M5 5 a20 10 30 1 0 15 -5");
        Path built = new PathBuilder().MoveTo(5, 5).ArcTo(new Vector2(20, 10), 30, true, false, new Vector2(20, 0)).ToPath();
        Assert.Equal(built.Points.ToArray(), parsed.Points.ToArray());
    }

    [Fact]
    public void SmoothCurvesReflectThePreviousControlPoint()
    {
        Path s = Path.ParseSvg("M0 0 C10 0 20 10 20 20 S30 40 40 40");
        Assert.Equal(new Vector2(20, 30), s.Points[4]);

        // After a command of another kind there is nothing to reflect: the first control is the current point.
        Path sAfterLine = Path.ParseSvg("M0 0 L10 10 S20 20 30 10");
        Assert.Equal(new Vector2(10, 10), sAfterLine.Points[2]);

        Path t = Path.ParseSvg("M0 0 Q10 10 20 0 T40 0 T60 0");
        Assert.Equal(new Vector2(30, -10), t.Points[3]);
        Assert.Equal(new Vector2(50, 10), t.Points[5]);

        Path tAfterCubic = Path.ParseSvg("M0 0 C1 1 2 2 3 3 T10 10");
        Assert.Equal(new Vector2(3, 3), tAfterCubic.Points[4]);

        Path sRelative = Path.ParseSvg("M0 0 c10 0 20 10 20 20 s10 20 20 20");
        Assert.Equal(s.Points.ToArray(), sRelative.Points.ToArray());
    }

    [Fact]
    public void BlankDataIsAnEmptyPath()
    {
        Assert.True(Path.ParseSvg("").IsEmpty);
        Assert.True(Path.ParseSvg(" \n\t").IsEmpty);
        Assert.True(Path.TryParseSvg("", out Path? path) && path.IsEmpty);
    }

    [Theory]
    [InlineData("L10 10", 0)]
    [InlineData("10 10", 0)]
    [InlineData("M10", 3)]
    [InlineData("M10 10 L", 8)]
    [InlineData("M10 10 L20", 10)]
    [InlineData("M10 10 X20 20", 7)]
    [InlineData("M10 10 Z 5 5", 9)]
    [InlineData("M,10 10", 1)]
    [InlineData("M10,,10", 4)]
    [InlineData("M10 10,", 7)]
    [InlineData("M10 10 A1 1 0 2 1 5 5", 14)]
    [InlineData("M1e 2", 2)]
    [InlineData("M1e39 0", 1)]
    [InlineData("M. 1", 1)]
    public void ErrorsCarryTheirPosition(string data, int position)
    {
        SvgPathException e = Assert.Throws<SvgPathException>(() => Path.ParseSvg(data));
        Assert.Equal(position, e.Position);
        Assert.Contains($"character {position}", e.Message, StringComparison.Ordinal);
        Assert.False(Path.TryParseSvg(data, out Path? path));
        Assert.Null(path);
    }

    [Fact]
    public void LenientKeepsEveryCompleteCommandBeforeTheError()
    {
        Path path = Path.ParseSvg("M0 0 L10 0 L10 10 L20", lenient: true);
        Assert.Equal([new(0, 0), new(10, 0), new(10, 10)], path.Points.ToArray());

        Assert.Equal(3, Path.ParseSvg("M0 0 10 0 10 10 X", lenient: true).Points.Length);
        Assert.True(Path.ParseSvg("L1 1", lenient: true).IsEmpty);
        Assert.Equal(FillRule.EvenOdd, Path.ParseSvg("M0 0 L1 1", FillRule.EvenOdd, lenient: true).FillRule);
    }

    [Fact]
    public void OverflowIsAnErrorNotAnException()
    {
        // Every number is a finite float, but the reflection and the relative sum are not.
        Assert.Throws<SvgPathException>(() => Path.ParseSvg("M0 0 C0 0 -3e38 0 3e38 0 S0 0 0 0"));
        Assert.Throws<SvgPathException>(() => Path.ParseSvg("M3e38 0 l3e38 0"));
        Assert.Equal(4, Path.ParseSvg("M3e38 0 L0 0 l1 1 l3e38 0 l3e38 0", lenient: true).Points.Length);
    }

    [Fact]
    public void WritesAbsoluteByDefault()
    {
        Path path = new PathBuilder().MoveTo(10, 20).LineTo(30.25f, 40).QuadTo(1, 2, 3, 4).CubicTo(1, 2, 3, 4, 5.123456f, 6).Close().ToPath();
        Assert.Equal("M10 20 L30.25 40 Q1 2 3 4 C1 2 3 4 5.123 6 Z", path.ToSvg());
    }

    [Fact]
    public void WritesRelativeAndCompactForms()
    {
        Path path = new PathBuilder().MoveTo(10, 10).LineTo(20, 10).LineTo(20, 20).LineTo(10.5f, 19.5f).LineTo(10, 19).Close().MoveTo(0, 0).LineTo(1, 1).ToPath();
        Assert.Equal("m10 10 l10 0 l0 10 l-9.5 -0.5 l-0.5 -0.5 z m-10 -10 l1 1", path.ToSvg(new SvgPathFormat { Relative = true }));
        Assert.Equal("M10 10H20V20L10.5 19.5 10 19ZM0 0 1 1", path.ToSvg(new SvgPathFormat { Compact = true }));
        Assert.Equal("m10 10h10v10l-9.5-.5-.5-.5zm-10-10 1 1", path.ToSvg(new SvgPathFormat { Relative = true, Compact = true }));
        Assert.Equal("M.5.5.25.25", Path.Line(new(0.5f, 0.5f), new(0.25f, 0.25f)).ToSvg(new SvgPathFormat { Compact = true }));
        Assert.Equal("M0 0 L1 2", Path.Line(new(0.4f, -0.2f), new(1.4f, 1.6f)).ToSvg(new SvgPathFormat { Precision = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgPathFormat { Precision = 10 });
    }

    [Fact]
    public void RelativeOffsetsDoNotDrift()
    {
        PathBuilder builder = new PathBuilder().MoveTo(0, 0);
        for (int i = 1; i <= 1000; i++)
        {
            builder.LineTo(i * 0.3333f, i * 0.6667f);
        }

        Path path = builder.ToPath();
        Path back = Path.ParseSvg(path.ToSvg(new SvgPathFormat { Relative = true, Precision = 2 }));
        for (int i = 0; i < path.Points.Length; i++)
        {
            Assert.True(Vector2.Distance(path.Points[i], back.Points[i]) <= 0.0071f, $"point {i}: {path.Points[i]} vs {back.Points[i]}");
        }
    }

    [Fact]
    public void RoundTripsWithinThePrecision()
    {
        Random random = new(1);
        for (int trial = 0; trial < 300; trial++)
        {
            PathBuilder builder = new();
            int contours = random.Next(1, 4);
            for (int c = 0; c < contours; c++)
            {
                builder.MoveTo(R());
                int segments = random.Next(1, 8);
                for (int s = 0; s < segments; s++)
                {
                    switch (random.Next(3))
                    {
                        case 0:
                            builder.LineTo(R());
                            break;
                        case 1:
                            builder.QuadTo(R(), R());
                            break;
                        default:
                            builder.CubicTo(R(), R(), R());
                            break;
                    }
                }

                if (random.Next(2) == 0)
                {
                    builder.Close();
                }
            }

            Path path = builder.ToPath();
            foreach (SvgPathFormat format in new SvgPathFormat[] { new(), new() { Relative = true }, new() { Compact = true }, new() { Relative = true, Compact = true, Precision = 4 } })
            {
                string data = path.ToSvg(format);
                Path back = Path.ParseSvg(data);
                Assert.Equal(path.Verbs.ToArray(), back.Verbs.ToArray());
                float tolerance = (0.75f * MathF.Pow(10, -format.Precision)) + 2e-4f;
                for (int i = 0; i < path.Points.Length; i++)
                {
                    Assert.True(Vector2.Distance(path.Points[i], back.Points[i]) <= tolerance, $"{data}: point {i}");
                }
            }
        }

        Vector2 R() => new((float)((random.NextDouble() * 2000) - 1000), (float)((random.NextDouble() * 2000) - 1000));
    }

    [Fact]
    public void ReadsALargePathInOnePass()
    {
        System.Text.StringBuilder text = new("M0 0");
        for (int i = 0; i < 100_000; i++)
        {
            text.Append(" l1.5-.5");
        }

        Path path = Path.ParseSvg(text.ToString());
        Assert.Equal(100_001, path.Points.Length);
        Assert.Equal(new Vector2(150_000, -50_000), path.Points[^1]);
    }

    private static Vector2[] Points(string data) => Path.ParseSvg(data).Points.ToArray();
}
