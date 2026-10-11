using System.Globalization;
using System.Numerics;
using System.Text;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Tone;

/// <summary>Phase 2.5.3 stage B: 3D tables, Hald images and .cube files.</summary>
public class LutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Vector3 Grade(Vector3 c) => new(MathF.Pow(c.X, 0.8f), (c.Y * 0.9f) + (0.1f * c.Z), MathF.Sqrt(c.Z) * 0.95f);

    [Fact]
    public void TetrahedralMatchesTheDoubleReference()
    {
        Lut3D lut = Lut3D.FromFunction(Grade, 17);
        Random random = new(5);
        for (int i = 0; i < 20000; i++)
        {
            Vector3 c = new((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
            Vector3 actual = lut.Evaluate(c);
            Vector3 expected = TetrahedralReference(lut, c);
            Assert.True(Vector3.Distance(actual, expected) <= 1e-5f, $"{c}: {actual}, reference {expected}");
        }
    }

    [Fact]
    public void GraysStayGrayUnderTetrahedral()
    {
        // A table that is neutral on its gray axis keeps every gray input neutral only when read tetrahedrally.
        Lut3D lut = Lut3D.FromFunction(c => new Vector3(c.X * c.X, (c.X + c.Y + c.Z) / 3 * c.Y / Math.Max(c.Y, 1e-6f) * 1, c.Z * c.Z), 9);
        Lut3D squares = Lut3D.FromFunction(c => c * c, 9);
        for (int i = 0; i <= 100; i++)
        {
            float v = i / 100f;
            Vector3 t = squares.Evaluate(new Vector3(v), LutInterpolation.Tetrahedral);
            Assert.Equal(t.X, t.Y, 6);
            Assert.Equal(t.Y, t.Z, 6);
        }

        Assert.NotNull(lut);
    }

    [Fact]
    public void IdentityTablesChangeNothing()
    {
        Lut3D identity = Lut3D.FromFunction(c => c, 2);
        using Raster<Rgba8> source = ColorFilterTests.Noise8(64, 32);
        foreach (LutInterpolation mode in Enum.GetValues<LutInterpolation>().Where(m => m != LutInterpolation.Nearest))
        {
            using Raster<Rgba8> result = source.Derive(o => o.ApplyFilter(ColorFilter.FromLut(identity, mode)), Ct);
            Assert.True(ColorFilterTests.MaxDifference(source, result) == 0, mode.ToString());
        }
    }

    [Fact]
    public void FiltersBakeIntoTablesAndCurvesFoldIntoTheShaper()
    {
        ColorFilter grade = ColorFilter.Curves(ToneCurve.FromPoints([(0, 0), (0.25f, 0.2f), (0.75f, 0.82f), (1, 1)])).Then(ColorFilter.Saturation(1.1f));
        Lut3D baked = grade.ToLut3D(65);
        using Raster<Rgba8> source = ColorFilterTests.Noise8(80, 40);
        using Raster<Rgba8> direct = source.Derive(o => o.ApplyFilter(grade), Ct);
        using Raster<Rgba8> table = source.Derive(o => o.ApplyFilter(ColorFilter.FromLut(baked)), Ct);
        Assert.True(ColorFilterTests.MaxDifference(direct, table) <= 2, $"{ColorFilterTests.MaxDifference(direct, table)}");

        ColorFilter folded = ColorFilter.Gamma(1.5f).Then(ColorFilter.FromLut(Lut3D.FromFunction(Grade, 33)));
        Assert.Single(folded.Stages);
        // The separate steps run on floats, so neither side rounds between the curve and the table.
        using Raster<RgbaF32> floats = source.As<RgbaF32>();
        using Raster<RgbaF32> separateFloats = floats.Derive(o => o.Gamma(1.5f).Add(new SeparateStep(ColorFilter.FromLut(Lut3D.FromFunction(Grade, 33)))), Ct);
        using Raster<Rgba8> separate = separateFloats.As<Rgba8>();
        using Raster<Rgba8> together = source.Derive(o => o.ApplyFilter(folded), Ct);
        Assert.True(ColorFilterTests.MaxDifference(separate, together) <= 1, $"{ColorFilterTests.MaxDifference(separate, together)}");
    }

    [Fact]
    public void HaldImagesRoundTrip()
    {
        using Raster<Rgba8> identity = Raster.HaldIdentity(4);
        Assert.Equal((64, 64), (identity.Width, identity.Height));
        Assert.Equal(new Rgba8(0, 0, 0, 255), identity.Row(0)[0]);
        Assert.Equal(new Rgba8(17, 0, 0, 255), identity.Row(0)[1]);
        Assert.Equal(new Rgba8(0, 17, 0, 255), identity.Row(0)[16]);
        Assert.Equal(new Rgba8(255, 255, 255, 255), identity.Row(63)[63]);

        Lut3D fromIdentity = Lut3D.FromHald(identity);
        Assert.Equal(16, fromIdentity.Size);
        using Raster<Rgba8> source = ColorFilterTests.Noise8(50, 50);
        using Raster<Rgba8> same = source.Derive(o => o.ApplyFilter(ColorFilter.FromLut(fromIdentity, LutInterpolation.Trilinear)), Ct);
        Assert.True(ColorFilterTests.MaxDifference(source, same) <= 1);

        Lut3D graded = Lut3D.FromFunction(Grade, 33);
        using Raster<Rgba16> hald = graded.ToHald(8);
        Lut3D back = Lut3D.FromHald(hald);
        Assert.Equal(64, back.Size);
        Assert.True(Vector3.Distance(graded.Evaluate(new Vector3(0.3f, 0.6f, 0.2f)), back.Evaluate(new Vector3(0.3f, 0.6f, 0.2f))) < 2e-3f);
        Assert.Throws<ArgumentException>(() => Lut3D.FromHald(source));
    }

    [Fact]
    public void CubeFilesRoundTripExactly()
    {
        Lut3D lut = Lut3D.FromValues(3, [.. Enumerable.Range(0, 27).Select(i => new Vector3(i / 26f, (float)Math.Sin(i), 1e-7f * i))], new Vector3(-0.1f, 0, 0), new Vector3(1.5f, 1, 2));
        Lut3D back = RoundTrip(lut, "My grade");
        Assert.Equal(lut.Size, back.Size);
        Assert.Equal(lut.DomainMin, back.DomainMin);
        Assert.Equal(lut.DomainMax, back.DomainMax);
        Assert.True(lut.Values.SequenceEqual(back.Values));

        Lut3D shaped = Lut3D.FromFunction(Grade, 5).WithShaper([new(0, 0, 0), new(0.3f, 0.4f, 0.5f), new(1, 1, 1)], -0.5f, 2);
        Lut3D shapedBack = RoundTrip(shaped, null);
        Assert.Equal(3, shapedBack.ShaperSize);
        Assert.True(shaped.Shaper!.AsSpan().SequenceEqual(shapedBack.Shaper));
        Assert.Equal(shaped.Evaluate(new Vector3(0.7f, 0.1f, 1.2f)), shapedBack.Evaluate(new Vector3(0.7f, 0.1f, 1.2f)));
    }

    [Fact]
    public void OneDimensionalCubeFilesReadAsShapers()
    {
        string text = "# a comment\nTITLE \"curve # one\"\nLUT_1D_SIZE 3\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 2 1\n\n0 0 0\n0.25 0.5 0.75\r\n1 1 1\n";
        Lut3D lut = CubeFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(3, lut.ShaperSize);
        Vector3 v = lut.Evaluate(new Vector3(0.5f, 1, 0.25f));
        Assert.Equal(0.25f, v.X, 5);
        Assert.Equal(0.5f, v.Y, 5);
        Assert.Equal(0.375f, v.Z, 5);
        Lut3D back = RoundTrip(lut, null);
        Assert.Equal(lut.Evaluate(new Vector3(0.9f, 0.4f, 0.1f)), back.Evaluate(new Vector3(0.9f, 0.4f, 0.1f)));
    }

    public static TheoryData<string, string> Broken() => new()
    {
        { "no size", "TITLE \"x\"\n0 0 0\n" },
        { "too few lines", "LUT_3D_SIZE 2\n0 0 0\n1 0 0\n" },
        { "too many lines", "LUT_1D_SIZE 2\n0 0 0\n1 1 1\n1 1 1\n" },
        { "bad number", "LUT_1D_SIZE 2\n0 0 zero\n1 1 1\n" },
        { "two numbers", "LUT_1D_SIZE 2\n0 0\n1 1 1\n" },
        { "four numbers", "LUT_1D_SIZE 2\n0 0 0 0\n1 1 1\n" },
        { "infinite", "LUT_1D_SIZE 2\n0 0 1e39\n1 1 1\n" },
        { "size too big", "LUT_3D_SIZE 257\n" },
        { "size too small", "LUT_3D_SIZE 1\n0 0 0\n" },
        { "size twice", "LUT_3D_SIZE 2\nLUT_3D_SIZE 2\n" },
        { "keyword after data", "LUT_1D_SIZE 2\n0 0 0\nDOMAIN_MIN 0 0 0\n1 1 1\n" },
        { "empty domain", "LUT_1D_SIZE 2\nDOMAIN_MIN 1 0 0\nDOMAIN_MAX 1 1 1\n0 0 0\n1 1 1\n" },
        { "unquoted title", "TITLE x\nLUT_1D_SIZE 2\n0 0 0\n1 1 1\n" },
        { "long line", "LUT_1D_SIZE 2\n" + new string('1', 5000) + "\n" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void MalformedFilesThrowNamingTheLine(string label, string text)
    {
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => CubeFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(text))));
        Assert.True(label == "no size" || label.StartsWith("empty", StringComparison.Ordinal) || e.Message.Contains("line ", StringComparison.Ordinal), e.Message);
    }

    [Fact]
    public void LimitsAreCheckedBeforeTheTableIsAllocated()
    {
        EgamiContext tight = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 1000 } };
        Assert.Throws<LimitExceededException>(() => CubeFile.Read(new MemoryStream(Encoding.UTF8.GetBytes("LUT_3D_SIZE 256\n")), tight));
    }

    private static Lut3D RoundTrip(Lut3D lut, string? title)
    {
        using MemoryStream stream = new();
        CubeFile.Write(stream, lut, title);
        string text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal(title is not null, text.StartsWith("TITLE", StringComparison.Ordinal));
        stream.Position = 0;
        return CubeFile.Read(stream);
    }

    /// <summary>A double-precision tetrahedral read of the table's grid, written from Kasson and others (1995).</summary>
    private static Vector3 TetrahedralReference(Lut3D lut, Vector3 c)
    {
        int n = lut.Size;
        double pr = c.X * (n - 1), pg = c.Y * (n - 1), pb = c.Z * (n - 1);
        int r = Math.Min((int)pr, n - 2), g = Math.Min((int)pg, n - 2), b = Math.Min((int)pb, n - 2);
        double fr = pr - r, fg = pg - g, fb = pb - b;
        (double, double, double) P(int dr, int dg, int db)
        {
            Vector3 v = lut[r + dr, g + dg, b + db];
            return (v.X, v.Y, v.Z);
        }

        // Walk from the cell's origin to its far corner along the axes in order of their fractions.
        (int Axis, double F)[] order = [.. new[] { (0, fr), (1, fg), (2, fb) }.OrderByDescending(a => a.Item2)];
        int[] at = [0, 0, 0];
        (double x, double y, double z) = P(0, 0, 0);
        (double x, double y, double z) previous = (x, y, z);
        double[] sum = [x, y, z];
        foreach ((int axis, double f) in order)
        {
            at[axis] = 1;
            (double nx, double ny, double nz) = P(at[0], at[1], at[2]);
            sum[0] += f * (nx - previous.x);
            sum[1] += f * (ny - previous.y);
            sum[2] += f * (nz - previous.z);
            previous = (nx, ny, nz);
        }

        return new Vector3((float)sum[0], (float)sum[1], (float)sum[2]);
    }

    /// <summary>Runs a filter as a custom step, so the planner cannot fuse it with its neighbours.</summary>
    private sealed class SeparateStep(ColorFilter filter) : IRasterOperation
    {
        public Size OutputSize(Size input) => input;

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            for (int y = 0; y < input.Source.Height; y++)
            {
                input.Source.Row(y).CopyTo(input.Destination.Row(y));
                filter.Apply(input.Destination.Row(y));
            }
        }
    }
}
