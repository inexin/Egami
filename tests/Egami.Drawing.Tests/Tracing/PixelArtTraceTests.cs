using System.Numerics;

using Egami.Drawing.Tests.Geometry;
using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Tracing;

/// <summary>Pixel art mode: the similarity graph's diagonals, the reshaped cells, the exact squares, layering and limits.</summary>
public class PixelArtTraceTests
{
    private static readonly Dictionary<char, Rgba8> Colors = new()
    {
        ['.'] = Rgba8.White,
        ['K'] = new Rgba8(20, 20, 20, 255),
        ['R'] = new Rgba8(220, 30, 40, 255),
        ['Y'] = new Rgba8(250, 210, 30, 255),
        ['B'] = new Rgba8(40, 70, 200, 255),
        ['k'] = new Rgba8(30, 24, 22, 255),
        ['W'] = new Rgba8(250, 250, 250, 255),
        ['G'] = new Rgba8(120, 120, 130, 255),
        ['N'] = new Rgba8(140, 80, 30, 255),
        [' '] = Rgba8.Transparent,
    };

    /// <summary>An original sprite: a round face with eyes and a smile, its outline drawn in single diagonal steps.</summary>
    internal static readonly string[] Face =
    [
        "...KKKKK...",
        "..KYYYYYK..",
        ".KYYYYYYYK.",
        "KYYKYYYKYYK",
        "KYYKYYYKYYK",
        "KYYYYYYYYYK",
        "KYKYYYYYKYK",
        "KYYKKKKKYYK",
        ".KYYYYYYYK.",
        "..KYYYYYK..",
        "...KKKKK...",
    ];

    /// <summary>An original sprite: a sword whose blade, nearly the background's white, runs between two diagonal outlines.</summary>
    internal static readonly string[] Sword =
    [
        "..........KK",
        ".........KWK",
        "........KWK.",
        ".......KWK..",
        "......KWK...",
        ".KK..KWK....",
        ".KGKKWK.....",
        "..KGWK......",
        "..KNGK......",
        ".KNKKGK.....",
        "KNK...KK....",
        "KK..........",
    ];

    internal static Raster<Rgba8> Sprite(params string[] rows)
    {
        Raster<Rgba8> raster = Raster.Create(rows[0].Length, rows.Length, Rgba8.White);
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < rows[y].Length; x++)
            {
                raster.SetPixel(x, y, Colors[rows[y][x]]);
            }
        }

        return raster;
    }

    /// <summary>The face sprite tiled <paramref name="scale"/> × 4 times each way, with every other tile in other colors.</summary>
    internal static Raster<Rgba8> Tiles(int scale)
    {
        using Raster<Rgba8> face = Sprite(Face);
        int n = 4 * scale, size = Face.Length;
        Raster<Rgba8> raster = Raster.Create(n * size, n * size, Rgba8.White);
        for (int ty = 0; ty < n; ty++)
        {
            for (int tx = 0; tx < n; tx++)
            {
                bool swap = ((tx + ty) & 1) != 0;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        Rgba8 c = face.GetPixel(x, y);
                        raster.SetPixel((tx * size) + x, (ty * size) + y, swap && c == Colors['Y'] ? Colors['B'] : c);
                    }
                }
            }
        }

        return raster;
    }

    [Fact]
    public void ADiagonalLineKeepsItsDiagonals()
    {
        // The line's diagonals cross the white background's: the line is the longer curve, the sparser component and has ends
        // with no other edge, so it wins every block.
        using Raster<Rgba8> raster = Sprite("K...", ".K..", "..K.", "...K");
        byte[] diagonals = PixelArtTracer.Diagonals(raster);
        Assert.Equal((1, 1, 1), (diagonals[0], diagonals[4], diagonals[8]));

        // A block with one diagonal keeps it; an all-white block is shading and keeps none.
        Assert.Equal((1, 0), (diagonals[1], diagonals[2]));
    }

    [Fact]
    public void ACheckerboardOfTwoPixelsKeepsNeitherDiagonal()
    {
        // Every vote ties: both diagonals go, and the four cells meet at the corner.
        using Raster<Rgba8> raster = Sprite("K.", ".K");
        Assert.Equal([0], PixelArtTracer.Diagonals(raster));
    }

    [Fact]
    public void ShadingBlocksKeepNoDiagonal()
    {
        // Two near-blacks (similar in YUV) and black: the block is fully joined, a shading, so neither diagonal stays.
        using Raster<Rgba8> raster = Sprite("Kk", "kK");
        Assert.Equal([0], PixelArtTracer.Diagonals(raster));
    }

    [Fact]
    public void AnIslandBeatsTheBackground()
    {
        // A pixel joined to the rest of its color by one diagonal only keeps that diagonal.
        using Raster<Rgba8> raster = Sprite("....", ".K..", "..KK", "..KK");
        byte[] diagonals = PixelArtTracer.Diagonals(raster);
        Assert.Equal(1, diagonals[(1 * 3) + 1]);
    }

    [Fact]
    public void ExactGivesThePixelSquares()
    {
        using Raster<Rgba8> raster = Sprite(Face);
        TraceResult result = raster.Trace(TraceOptions.PixelArt with { Exact = true, Layering = TraceLayering.Abutting });

        // One shape per color, each as large as its pixels, every edge straight and on the pixel grid.
        Assert.Equal(3, result.Shapes.Count);
        foreach (TracedShape shape in result.Shapes)
        {
            int pixels = Count(raster, shape.Color);
            Assert.Equal(pixels, PathEffectTests.Area(shape.Path), 1e-6);
            Assert.All(shape.Path.Points.ToArray(), p => Assert.True(p.X == MathF.Round(p.X) && p.Y == MathF.Round(p.Y), $"{p}"));
            Assert.DoesNotContain(PathVerb.Quad, shape.Path.Verbs.ToArray());
        }

        // Drawn back at its size it is the image again.
        Raster<Rgba8> back = result.ToPicture().ToRaster<Rgba8>();
        Assert.Equal(0, Differ(raster, back, 1));
    }

    [Theory]
    [InlineData(TraceLayering.Stacked)]
    [InlineData(TraceLayering.Abutting)]
    public void SmoothShapesKeepEveryPixelCenter(TraceLayering layering)
    {
        // The cells change shape and their edges are smoothed, but each pixel's center stays in its own color: drawn back 8
        // times larger, the pixel at each center is the color it was.
        using Raster<Rgba8> raster = Sprite(Face);
        TraceResult result = raster.Trace(TraceOptions.PixelArt with { Layering = layering });
        Assert.Equal(3, result.Shapes.Count);
        Assert.Equal(0, Differ(raster, Draw(result, 8), 8));
        Assert.Contains(result.Shapes, s => s.Path.Verbs.ToArray().Contains(PathVerb.Quad));
    }

    [Fact]
    public void JunctionsOnLongEdgesStayNearThem()
    {
        // Where the outline meets the image's edge beside a long straight run, the curves join within a pixel of the junction:
        // the white pixels between the sword's tip and the edge stay white.
        using Raster<Rgba8> raster = Sprite(Sword);
        TraceResult result = raster.Trace(TraceOptions.PixelArt);
        Assert.Equal(0, Differ(raster, Draw(result, 8), 8));
    }

    [Fact]
    public void StackedLayersCoverTheLayersAbove()
    {
        using Raster<Rgba8> raster = Sprite(Face);
        TraceResult stacked = raster.Trace(TraceOptions.PixelArt);

        // Largest color first (the yellow), covering the whole image, outer edges clockwise; abutting areas add up to the image.
        Assert.Equal(Colors['Y'], stacked.Shapes[0].Color);
        Assert.Equal(raster.Width * raster.Height, PathEffectTests.Area(stacked.Shapes[0].Path), 1e-3);
        TraceResult abutting = raster.Trace(TraceOptions.PixelArt with { Layering = TraceLayering.Abutting });
        Assert.Equal(raster.Width * raster.Height, abutting.Shapes.Sum(s => PathEffectTests.Area(s.Path)), 1e-3);
    }

    [Fact]
    public void BlocksKeepTheirCornersAndSinglePixelsRound()
    {
        // A 3 by 3 block keeps its four corners; a single pixel becomes a round dot inside its square.
        using Raster<Rgba8> raster = Sprite(".......", ".RRR...", ".RRR.B.", ".RRR...", ".......");
        TraceResult result = raster.Trace(TraceOptions.PixelArt with { Layering = TraceLayering.Abutting });
        Path block = result.Shapes.Single(s => s.Color == Colors['R']).Path;
        foreach (Vector2 corner in new Vector2[] { new(1, 1), new(4, 1), new(4, 4), new(1, 4) })
        {
            Assert.Contains(corner, block.Points.ToArray());
        }

        Path dot = result.Shapes.Single(s => s.Color == Colors['B']).Path;
        RectF bounds = dot.TightBounds;
        Assert.True(bounds.Left >= 5 && bounds.Right <= 6 && bounds.Top >= 2 && bounds.Bottom <= 3, $"{bounds}");
        Assert.All(dot.Verbs.ToArray(), v => Assert.NotEqual(PathVerb.Line, v));
    }

    [Fact]
    public void TransparencyAndIgnoredWhiteStayEmpty()
    {
        using Raster<Rgba8> raster = Sprite("  KK  ", " K..K ", " K..K ", "  KK  ");
        TraceResult result = raster.Trace(TraceOptions.PixelArt);
        Assert.Equal(2, result.Shapes.Count);
        Assert.DoesNotContain(result.Shapes, s => s.Color.A == 0);
        TraceResult ink = raster.Trace(TraceOptions.PixelArt with { IgnoreWhite = true });
        Assert.Equal(Colors['K'], Assert.Single(ink.Shapes).Color);
    }

    [Fact]
    public void TinyAndUniformImages()
    {
        using Raster<Rgba8> one = Sprite("R");
        TraceResult traced = one.Trace(TraceOptions.PixelArt);
        Assert.Equal(1, PathEffectTests.Area(Assert.Single(traced.Shapes).Path), 1e-6);

        using Raster<Rgba8> flat = Raster.Create(9, 5, Colors['B']);
        traced = flat.Trace(TraceOptions.PixelArt);
        Assert.Equal(45, PathEffectTests.Area(Assert.Single(traced.Shapes).Path), 1e-6);

        using Raster<Rgba8> clear = Raster.Create(4, 4, Rgba8.Transparent);
        Assert.Empty(clear.Trace(TraceOptions.PixelArt).Shapes);
    }

    [Fact]
    public void MoreThan256ColorsIsRefused()
    {
        using Raster<Rgba8> raster = Raster.Create(32, 32, Rgba8.White);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                raster.SetPixel(x, y, new Rgba8((byte)(x * 8), (byte)(y * 8), 0, 255));
            }
        }

        ArgumentException error = Assert.Throws<ArgumentException>(() => raster.Trace(TraceOptions.PixelArt));
        Assert.Contains("256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherSettingsAreNotUsed()
    {
        // Noise, colors and palette belong to the other modes: pixel art keeps every pixel and its exact color.
        using Raster<Rgba8> raster = Sprite(Face);
        TraceResult plain = raster.Trace(TraceOptions.PixelArt);
        TraceResult changed = raster.Trace(TraceOptions.PixelArt with { Noise = 30, Colors = 2, Palette = [Rgba8.Black] });
        Assert.Equal(plain.Shapes.Select(s => s.Color), changed.Shapes.Select(s => s.Color));
        Assert.Equal(plain.Shapes.Select(s => s.Path.Points.Length), changed.Shapes.Select(s => s.Path.Points.Length));
    }

    private static int Count(Raster<Rgba8> raster, Rgba8 color)
    {
        int count = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                count += raster.GetPixel(x, y) == color ? 1 : 0;
            }
        }

        return count;
    }

    private static Raster<Rgba8> Draw(TraceResult result, int scale)
    {
        Raster<Rgba8> drawn = Raster.Create(result.Width * scale, result.Height * scale, Rgba8.Transparent);
        using RasterCanvas<Rgba8> canvas = drawn.CreateCanvas();
        canvas.Scale(scale);
        result.Draw(canvas);
        return drawn;
    }

    /// <summary>How many pixels' centers, sampled in <paramref name="drawn"/> at <paramref name="scale"/>, differ from the image.</summary>
    private static int Differ(Raster<Rgba8> raster, Raster<Rgba8> drawn, int scale)
    {
        using (drawn)
        {
            int differ = 0;
            for (int y = 0; y < raster.Height; y++)
            {
                for (int x = 0; x < raster.Width; x++)
                {
                    Rgba8 want = raster.GetPixel(x, y), got = drawn.GetPixel((x * scale) + (scale / 2), (y * scale) + (scale / 2));
                    differ += want == got || (want.A == 0 && got.A == 0) ? 0 : 1;
                }
            }

            return differ;
        }
    }
}
