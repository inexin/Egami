using Egami.Operations;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>Phase 2.5.4 task 8: every edge operator against its hand-computed response on steps and ramps, and its properties.</summary>
public class EdgesTests
{
    private const int Width = 48;
    private const int Height = 12;
    private const int Step = 24;

    /// <summary>
    /// The response to a vertical step from 0 (x &lt; 24) to 1, by distance from the step: each entry is (x − 24, value). Gradient
    /// pairs see the step in the two columns their kernel spans; Kirsch's compass gives 15 and 9 of 15; the Laplacians the
    /// column sums of their kernels on either side.
    /// </summary>
    public static TheoryData<EdgeOperator, float[]> StepResponses() => new()
    {
        { EdgeOperator.Sobel, [0, 1, 1, 0] },
        { EdgeOperator.Scharr, [0, 1, 1, 0] },
        { EdgeOperator.Prewitt, [0, 1, 1, 0] },
        { EdgeOperator.RobertsCross, [0, 1, 0, 0] },
        { EdgeOperator.Kirsch, [0, 1, 0.6f, 0] },
        { EdgeOperator.Robinson, [0, 1, 1, 0] },
        { EdgeOperator.Laplacian3x3, [0, 1, 1, 0] },
        { EdgeOperator.Laplacian5x5, [0.5f, 1, 1, 0.5f] },
        { EdgeOperator.LaplacianOfGaussian, [0.2f, 1, 1, 0.2f] },
    };

    /// <summary>The response to a ramp of slope s per pixel, as a multiple of s: Σ w·x over the kernel, times its scale.</summary>
    public static TheoryData<EdgeOperator, float> RampResponses() => new()
    {
        { EdgeOperator.Sobel, 2 },              // 8s / 4
        { EdgeOperator.Scharr, 2 },             // 32s / 16
        { EdgeOperator.Prewitt, 2 },            // 6s / 3
        { EdgeOperator.RobertsCross, 1 },       // |(−s, s)| / √2
        { EdgeOperator.Kirsch, 1.6f },          // 24s / 15, the east kernel
        { EdgeOperator.Robinson, 2 },           // 8s / 4, the east kernel
        { EdgeOperator.Laplacian3x3, 0 },
        { EdgeOperator.Laplacian5x5, 0 },
        { EdgeOperator.LaplacianOfGaussian, 0 },
    };

    [Theory]
    [MemberData(nameof(StepResponses))]
    public void StepGivesTheFormula(EdgeOperator op, float[] response)
    {
        // Columns 22 to 25 carry the response; further out it is 0. A horizontal step gives the same down the rows.
        using Raster<RgbaF32> vertical = Make((x, _) => x < Step ? 0 : 1);
        using Raster<RgbaF32> across = Raster.Create<RgbaF32>(Height, Width);
        Fill(across, (x, y) => y < Step ? 0 : 1);
        using Raster<RgbaF32> v = vertical.Derive(o => o.Edges(op), Ct);
        using Raster<RgbaF32> h = across.Derive(o => o.Edges(op), Ct);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int d = x - Step + 2;
                float expected = d is >= 0 and < 4 ? response[d] : 0;
                Gray(v.Row(y)[x], expected, $"{op} at ({x}, {y})");
            }
        }

        // Roberts' cross looks right and down, so a horizontal step lands on the row above it, as the vertical one on the column.
        for (int y = 0; y < Width; y++)
        {
            int d = y - Step + 2;
            float expected = d is >= 0 and < 4 ? response[d] : 0;
            for (int x = 0; x < Height; x++)
            {
                Gray(h.Row(y)[x], expected, $"{op} across at ({x}, {y})");
            }
        }
    }

    [Theory]
    [MemberData(nameof(RampResponses))]
    public void RampGivesTheFormula(EdgeOperator op, float factor)
    {
        const float S = 1f / 64;
        using Raster<RgbaF32> ramp = Make((x, _) => x * S);
        using Raster<RgbaF32> result = ramp.Derive(o => o.Edges(op), Ct);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 2; x < Width - 2; x++)
            {
                Gray(result.Row(y)[x], factor * S, $"{op} at ({x}, {y})");
            }
        }
    }

    [Fact]
    public void ColorChannelsGetTheirOwnMagnitudeAndAlphaIsKept()
    {
        // A red step on half-transparent pixels: per channel only red has an edge; in gray mode it is 0.2126 of the step.
        // The gradient is of premultiplied values, so the straight result is the step's height times alpha.
        using Raster<RgbaF32> red = Raster.Create<RgbaF32>(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                red.Row(y)[x] = new RgbaF32(x < Step ? 0.1f : 0.9f, 0.4f, 0.7f, 0.5f);
            }
        }

        using Raster<RgbaF32> color = red.Derive(o => o.Edges(EdgeOperator.Prewitt, grayscale: false), Ct);
        using Raster<RgbaF32> gray = red.Derive(o => o.Edges(EdgeOperator.Prewitt), Ct);
        RgbaF32 c = color.Row(5)[Step], g = gray.Row(5)[Step], flat = color.Row(5)[3];
        Assert.Equal(0.8f * 0.5f, c.R, 1e-6f);
        Assert.Equal(0, c.G, 1e-6f);
        Assert.Equal(0, c.B, 1e-6f);
        Assert.Equal(0.5f, c.A);
        Assert.Equal(0.2126f * 0.8f * 0.5f, g.R, 1e-6f);
        Assert.Equal(g.R, g.G);
        Assert.Equal(g.R, g.B);
        Assert.Equal(0.5f, g.A);
        Assert.Equal(0, flat.R + flat.G + flat.B, 1e-6f);
        Assert.Equal(0.5f, flat.A);
    }

    [Fact]
    public void BgraAndRgbaGiveTheSameEdges()
    {
        using Raster<Rgba8> duck = Load("duck");
        using Raster<Bgra8> bgra = duck.As<Bgra8>();
        using Raster<Rgba8> a = duck.Derive(o => o.Edges(EdgeOperator.Scharr), Ct);
        using Raster<Bgra8> b = bgra.Derive(o => o.Edges(EdgeOperator.Scharr), Ct);
        using Raster<Rgba8> c = b.As<Rgba8>();
        Same(a, c);
    }

    [Fact]
    public void SameOnOneThreadAndInsideAnArea()
    {
        SameOnOneThread("duck", o => o.Edges());
        SameOnOneThread("egret", o => o.Edges(EdgeOperator.Kirsch, grayscale: false));
        SameInsideArea("duck", new Rect(100, 70, 150, 120), o => o.Edges(EdgeOperator.LaplacianOfGaussian));
        SameInsideArea("shapes", new Rect(20, 10, 90, 70), o => o.Edges(EdgeOperator.RobertsCross, grayscale: false));
    }

    [Fact]
    public void UndefinedOperatorThrows()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.Edges((EdgeOperator)42), Ct));
    }

    private static Raster<RgbaF32> Make(Func<int, int, float> value)
    {
        Raster<RgbaF32> raster = Raster.Create<RgbaF32>(Width, Height);
        Fill(raster, value);
        return raster;
    }

    private static void Fill(Raster<RgbaF32> raster, Func<int, int, float> value)
    {
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                float v = value(x, y);
                raster.Row(y)[x] = new RgbaF32(v, v, v, 1);
            }
        }
    }

    private static void Gray(RgbaF32 pixel, float expected, string where)
    {
        Assert.True(
            Math.Abs(pixel.R - expected) <= 1e-6f && pixel.G == pixel.R && pixel.B == pixel.R && pixel.A == 1,
            $"{where}: expected {expected}, got {pixel}");
    }
}
