using Egami.Operations;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>Phase 2.5.4 task 9: Morphology on every path against a direct evaluation, its properties, and its arguments.</summary>
public class MorphologyTests
{
    public static TheoryData<string> Shapes() => ["square-1", "square-6", "rect-0x3", "rect-9x1", "rect-20x17", "disk-1", "disk-5", "diamond-4", "cross-3", "mask-a", "mask-b", "mask-full"];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void BytePathMatchesADirectEvaluation(string name)
    {
        // A size that is not a multiple of the vector width, and tall enough for several blocks of the rectangle pass.
        StructuringElement shape = Shape(name);
        using Raster<Rgba8> source = Noise(133, 97, 7);
        foreach (MorphologyOp op in Enum.GetValues<MorphologyOp>())
        {
            using Raster<Rgba8> result = source.Derive(o => o.Morphology(op, shape), Ct);
            using Raster<Rgba8> expected = Direct(source, op, name, 1);
            Same(expected, result);
        }
    }

    [Theory]
    [InlineData("square-2")]
    [InlineData("disk-3")]
    [InlineData("mask-a")]
    public void FloatPathAndIterationsMatchADirectEvaluation(string name)
    {
        StructuringElement shape = Shape(name);
        using Raster<Rgba8> source = Noise(61, 45, 256);
        using Raster<Rgba16> wide = source.As<Rgba16>();
        foreach (MorphologyOp op in new[] { MorphologyOp.Dilate, MorphologyOp.Open, MorphologyOp.Gradient, MorphologyOp.BottomHat })
        {
            using Raster<Rgba16> result = wide.Derive(o => o.Morphology(op, shape, 2), Ct);
            using Raster<Rgba8> narrow = result.As<Rgba8>();
            using Raster<Rgba8> expected = Direct(source, op, name, 2);
            Same(expected, narrow);
        }
    }

    [Fact]
    public void GrayAndThreeByteTypesMatchADirectEvaluation()
    {
        using Raster<Rgba8> noise = Noise(77, 51, 256);
        using Raster<Gray8> gray = noise.As<Gray8>();
        using Raster<Rgb8> rgb = noise.As<Rgb8>();
        using Raster<Gray8> grayResult = gray.Derive(o => o.Morphology(MorphologyOp.Close, StructuringElement.Disk(3)), Ct);
        using Raster<Rgb8> rgbResult = rgb.Derive(o => o.Erode(4, 2), Ct);
        using Raster<Rgba8> grayExpected = Direct(gray.As<Rgba8>(), MorphologyOp.Close, "disk-3", 1);
        using Raster<Rgba8> rgbExpected = Direct(rgb.As<Rgba8>(), MorphologyOp.Erode, "rect-4x2", 1);
        using Raster<Rgba8> grayActual = grayResult.As<Rgba8>();
        using Raster<Rgba8> rgbActual = rgbResult.As<Rgba8>();
        Same(grayExpected, grayActual);
        Same(rgbExpected, rgbActual);
    }

    [Fact]
    public void CloseNeverDarkensAndOpenNeverBrightens()
    {
        // The property test in place of a fuzz run, on the byte and float paths. The factories' shapes hold the origin and are
        // convex along rows and columns, so the property holds up to the edges. For any mask it holds where no read reaches past
        // the image: repeating edge pixels (as ImageMagick does) breaks the erosion-dilation pairing there for lopsided shapes.
        Random random = new(2504);
        for (int trial = 0; trial < 60; trial++)
        {
            StructuringElement shape;
            if (trial % 2 == 0)
            {
                int r = random.Next(0, 6);
                shape = random.Next(5) switch
                {
                    0 => StructuringElement.Square(r),
                    1 => StructuringElement.Rectangle(r, random.Next(0, 6)),
                    2 => StructuringElement.Disk(r),
                    3 => StructuringElement.Diamond(r),
                    _ => StructuringElement.Cross(r),
                };
            }
            else
            {
                int w = random.Next(1, 8), h = random.Next(1, 8);
                bool[,] cells = new bool[h, w];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        cells[y, x] = random.Next(3) > 0;
                    }
                }

                cells[random.Next(h), random.Next(w)] = true;
                shape = StructuringElement.FromMask(cells, new Point(random.Next(w), random.Next(h)));
            }

            int iterations = random.Next(1, 3);
            int margin = trial % 2 == 0 ? 0 : 2 * iterations * Math.Max(shape.Width, shape.Height);
            int width = trial % 2 == 0 ? random.Next(1, 90) : random.Next(2 * margin + 1, 2 * margin + 60);
            int height = trial % 2 == 0 ? random.Next(1, 70) : random.Next(2 * margin + 1, 2 * margin + 40);
            using Raster<Rgba8> source = Noise(width, height, random.Next(2, 256), random.Next());
            using Raster<Rgba8> closed = source.Derive(o => o.Morphology(MorphologyOp.Close, shape, iterations), Ct);
            using Raster<Rgba8> opened = source.Derive(o => o.Morphology(MorphologyOp.Open, shape, iterations), Ct);
            using Raster<RgbaF32> floats = source.As<RgbaF32>();
            using Raster<RgbaF32> closedF = floats.Derive(o => o.Morphology(MorphologyOp.Close, shape, iterations), Ct);
            using Raster<RgbaF32> openedF = floats.Derive(o => o.Morphology(MorphologyOp.Open, shape, iterations), Ct);
            for (int y = margin; y < height - margin; y++)
            {
                for (int x = margin; x < width - margin; x++)
                {
                    Rgba8 s = source.Row(y)[x], c = closed.Row(y)[x], o = opened.Row(y)[x];
                    Assert.True(c.R >= s.R && c.G >= s.G && c.B >= s.B && c.A >= s.A, $"trial {trial}: close darkened ({x}, {y})");
                    Assert.True(o.R <= s.R && o.G <= s.G && o.B <= s.B && o.A <= s.A, $"trial {trial}: open brightened ({x}, {y})");
                    RgbaF32 sf = floats.Row(y)[x], cf = closedF.Row(y)[x], of = openedF.Row(y)[x];
                    Assert.True(cf.R >= sf.R && cf.G >= sf.G && cf.B >= sf.B && cf.A >= sf.A, $"trial {trial}: float close darkened ({x}, {y})");
                    Assert.True(of.R <= sf.R && of.G <= sf.G && of.B <= sf.B && of.A <= sf.A, $"trial {trial}: float open brightened ({x}, {y})");
                }
            }
        }
    }

    [Fact]
    public void OpenAndCloseAreIdempotent()
    {
        using Raster<Rgba8> source = Load("shapes");
        StructuringElement disk = StructuringElement.Disk(3);
        using Raster<Rgba8> once = source.Derive(o => o.Morphology(MorphologyOp.Open, disk), Ct);
        using Raster<Rgba8> twice = once.Derive(o => o.Morphology(MorphologyOp.Open, disk), Ct);
        Same(once, twice);
        using Raster<Rgba8> closed = source.Derive(o => o.Morphology(MorphologyOp.Close, disk), Ct);
        using Raster<Rgba8> again = closed.Derive(o => o.Morphology(MorphologyOp.Close, disk), Ct);
        Same(closed, again);
    }

    [Fact]
    public void TheOriginAloneChangesNothing()
    {
        using Raster<Rgba8> source = Load("shapes");
        foreach (StructuringElement shape in new[] { default, StructuringElement.Square(0), StructuringElement.Disk(0) })
        {
            using Raster<Rgba8> result = source.Derive(o => o.Morphology(MorphologyOp.Dilate, shape).Morphology(MorphologyOp.Close, shape), Ct);
            Same(source, result);
        }

        Assert.Equal((1, 1, new Point(0, 0)), (default(StructuringElement).Width, default(StructuringElement).Height, default(StructuringElement).Origin));
    }

    [Fact]
    public void ShapesAreImageMagicksKernels()
    {
        // ImageMagick 7.1.1's showKernel output for Disk:3 and Disk:4 (rows of cells), Diamond:2 and Plus:2.
        Assert.Equal(["...#...", ".#####.", ".#####.", "#######", ".#####.", ".#####.", "...#..."], Picture(StructuringElement.Disk(3)));
        Assert.Equal(["....#....", "..#####..", ".#######.", ".#######.", "#########", ".#######.", ".#######.", "..#####..", "....#...."], Picture(StructuringElement.Disk(4)));
        Assert.Equal(["..#..", ".###.", "#####", ".###.", "..#.."], Picture(StructuringElement.Diamond(2)));
        Assert.Equal(["..#..", "..#..", "#####", "..#..", "..#.."], Picture(StructuringElement.Cross(2)));
        Assert.Equal(["#########", "#########", "#########", "#########", "#########"], Picture(StructuringElement.Rectangle(4, 2)));
        StructuringElement mask = StructuringElement.FromMask(new[,] { { true, false }, { true, true } }, new Point(1, 0));
        Assert.Equal((2, 2, new Point(1, 0)), (mask.Width, mask.Height, mask.Origin));
        Assert.Equal(["#.", "##"], Picture(mask));
    }

    [Fact]
    public void SameOnOneThreadAndInsideAnArea()
    {
        SameOnOneThread("duck", o => o.Dilate(9));
        SameOnOneThread("duck", o => o.Morphology(MorphologyOp.TopHat, StructuringElement.Disk(7)));
        SameInsideArea("duck", new Rect(100, 70, 150, 120), o => o.Erode(4, 2));
        SameInsideArea("egret", new Rect(30, 300, 200, 100), o => o.Morphology(MorphologyOp.Close, StructuringElement.Disk(3), 2));
        SameInsideArea("shapes", new Rect(20, 10, 90, 70), o => o.Morphology(MorphologyOp.Gradient, Shape("mask-a")));
        SameInsideArea("shapes", new Rect(0, 0, 60, 150), o => o.Morphology(MorphologyOp.Open, Shape("mask-full")));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => StructuringElement.Square(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StructuringElement.Disk(1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => StructuringElement.Rectangle(2, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StructuringElement.Diamond(-3));
        Assert.Throws<ArgumentOutOfRangeException>(() => StructuringElement.Cross(5000));
        Assert.Throws<ArgumentNullException>(() => StructuringElement.FromMask(null!, default));
        Assert.Throws<ArgumentException>(() => StructuringElement.FromMask(new bool[2, 2], default));
        Assert.Throws<ArgumentException>(() => StructuringElement.FromMask(new bool[0, 3], default));
        Assert.Throws<ArgumentOutOfRangeException>(() => StructuringElement.FromMask(new[,] { { true } }, new Point(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.Dilate(-1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.Erode(2, 1001), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.Morphology(MorphologyOp.Open, StructuringElement.Disk(1), 0), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.Morphology(MorphologyOp.Open, StructuringElement.Disk(1), 101), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.Morphology((MorphologyOp)99, StructuringElement.Disk(1)), Ct));
    }

    private static StructuringElement Shape(string name) => name switch
    {
        "rect-4x2" => StructuringElement.Rectangle(4, 2),
        "square-1" => StructuringElement.Square(1),
        "square-2" => StructuringElement.Square(2),
        "square-6" => StructuringElement.Square(6),
        "rect-0x3" => StructuringElement.Rectangle(0, 3),
        "rect-9x1" => StructuringElement.Rectangle(9, 1),
        "rect-20x17" => StructuringElement.Rectangle(20, 17),
        "disk-1" => StructuringElement.Disk(1),
        "disk-3" => StructuringElement.Disk(3),
        "disk-5" => StructuringElement.Disk(5),
        "diamond-4" => StructuringElement.Diamond(4),
        "cross-3" => StructuringElement.Cross(3),

        // Lopsided: two runs in one row, a gap, and the origin off the shape.
        _ => StructuringElement.FromMask(Masks[name].Cells, Masks[name].Origin),
    };

    private static readonly Dictionary<string, (bool[,] Cells, Point Origin)> Masks = new()
    {
        // Lopsided: two runs in one row, a gap, and the origin off the shape.
        ["mask-a"] = (new[,] { { true, false, true, true }, { false, false, true, false }, { true, true, true, true } }, new Point(3, 0)),
        ["mask-b"] = (new[,] { { false, true }, { false, false }, { false, false }, { true, false } }, new Point(0, 1)),

        // Every cell set with the origin off center: a rectangle that is not centered.
        ["mask-full"] = (new[,] { { true, true, true, true, true }, { true, true, true, true, true } }, new Point(4, 1)),
    };

    /// <summary>The cells of a named shape as offsets from its origin, from the shapes' definitions.</summary>
    private static List<(int X, int Y)> Cells(string name)
    {
        if (Masks.TryGetValue(name, out (bool[,] Cells, Point Origin) mask))
        {
            List<(int X, int Y)> set = [];
            for (int y = 0; y < mask.Cells.GetLength(0); y++)
            {
                for (int x = 0; x < mask.Cells.GetLength(1); x++)
                {
                    if (mask.Cells[y, x])
                    {
                        set.Add((x - mask.Origin.X, y - mask.Origin.Y));
                    }
                }
            }

            return set;
        }

        string[] parts = name.Split('-');
        int[] sizes = [.. parts[1].Split('x').Select(int.Parse)];
        int rx = sizes[0], ry = sizes.Length > 1 ? sizes[1] : sizes[0], r = Math.Max(rx, ry);
        Func<int, int, bool> inside = parts[0] switch
        {
            "disk" => (x, y) => (x * x) + (y * y) <= r * r,
            "diamond" => (x, y) => Math.Abs(x) + Math.Abs(y) <= r,
            "cross" => (x, y) => x == 0 || y == 0,
            _ => (_, _) => true,
        };
        List<(int X, int Y)> cells = [];
        for (int y = -ry; y <= ry; y++)
        {
            for (int x = -rx; x <= rx; x++)
            {
                if (inside(x, y))
                {
                    cells.Add((x, y));
                }
            }
        }

        return cells;
    }

    private static string[] Picture(StructuringElement shape)
    {
        string[] rows = new string[shape.Height];
        for (int y = 0; y < shape.Height; y++)
        {
            char[] row = new char[shape.Width];
            for (int x = 0; x < shape.Width; x++)
            {
                row[x] = Contains(shape, x - shape.Origin.X, y - shape.Origin.Y) ? '#' : '.';
            }

            rows[y] = new string(row);
        }

        return rows;
    }

    /// <summary>Whether the shape holds the cell at (dx, dy) from its origin, found by dilating a single white pixel.</summary>
    private static bool Contains(StructuringElement shape, int dx, int dy)
    {
        // Dilation reads f(p − c), so a lone white pixel at the center spreads to exactly the cells c around it.
        int size = (2 * Math.Max(shape.Width, shape.Height)) + 1, c = size / 2;
        using Raster<Gray8> dot = Raster.Create<Gray8>(size, size);
        dot.Row(c)[c] = new Gray8(255);
        using Raster<Gray8> spread = dot.Derive(o => o.Morphology(MorphologyOp.Dilate, shape), Ct);
        return spread.Row(c + dy)[c + dx].L == 255;
    }

    private static Raster<Rgba8> Noise(int width, int height, int levels, int seed = 0)
    {
        Random random = new(seed == 0 ? width * height : seed);
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte V() => (byte)(random.Next(levels) * 255 / Math.Max(1, levels - 1));
                row[x] = new Rgba8(V(), V(), V(), V());
            }
        }

        return raster;
    }

    /// <summary>Morphology by its definition: erosion the minimum of f(p + c), dilation the maximum of f(p − c), edges clamped.</summary>
    private static Raster<Rgba8> Direct(Raster<Rgba8> source, MorphologyOp op, string shape, int iterations)
    {
        List<(int X, int Y)> cells = Cells(shape);

        int w = source.Width, h = source.Height;
        byte[] original = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgba8 p = source.Row(y)[x];
                (original[((y * w) + x) * 4], original[(((y * w) + x) * 4) + 1], original[(((y * w) + x) * 4) + 2], original[(((y * w) + x) * 4) + 3]) = (p.R, p.G, p.B, p.A);
            }
        }

        byte[] Pass(byte[] f, bool max)
        {
            byte[] g = new byte[f.Length];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int ch = 0; ch < 4; ch++)
                    {
                        int best = max ? 0 : 255;
                        foreach ((int cx, int cy) in cells)
                        {
                            int sx = Math.Clamp(max ? x - cx : x + cx, 0, w - 1), sy = Math.Clamp(max ? y - cy : y + cy, 0, h - 1);
                            int v = f[(((sy * w) + sx) * 4) + ch];
                            best = max ? Math.Max(best, v) : Math.Min(best, v);
                        }

                        g[(((y * w) + x) * 4) + ch] = (byte)best;
                    }
                }
            }

            return g;
        }

        byte[] Repeat(byte[] f, bool max)
        {
            for (int i = 0; i < iterations; i++)
            {
                f = Pass(f, max);
            }

            return f;
        }

        byte[] result = op switch
        {
            MorphologyOp.Dilate => Repeat(original, true),
            MorphologyOp.Erode => Repeat(original, false),
            MorphologyOp.Open => Repeat(Repeat(original, false), true),
            MorphologyOp.Close => Repeat(Repeat(original, true), false),
            MorphologyOp.Gradient => Minus(Repeat(original, true), Repeat(original, false)),
            MorphologyOp.TopHat => Minus(original, Repeat(Repeat(original, false), true)),
            _ => Minus(Repeat(Repeat(original, true), false), original),
        };

        byte[] Minus(byte[] a, byte[] b)
        {
            byte[] d = new byte[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                d[i] = i % 4 == 3 ? original[i] : (byte)Math.Max(0, a[i] - b[i]);
            }

            return d;
        }

        Raster<Rgba8> raster = Raster.Create<Rgba8>(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = ((y * w) + x) * 4;
                raster.Row(y)[x] = new Rgba8(result[i], result[i + 1], result[i + 2], result[i + 3]);
            }
        }

        return raster;
    }
}
