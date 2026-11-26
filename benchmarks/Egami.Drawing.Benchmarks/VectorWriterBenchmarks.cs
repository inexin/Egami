using System.Numerics;

using BenchmarkDotNet.Attributes;

using Egami.Metafiles;
using Egami.Pixels;
using Egami.Svg;

namespace Egami.Drawing.Benchmarks;

/// <summary>
/// Writing 10,000 shapes (rectangles, ovals, lines and stars by turns, as the EMF writer's speed gate draws them) to EMF with
/// <see cref="EmfCanvas"/> and to SVG with <see cref="SvgCanvas"/>, into memory. The fresh-star cases build each star anew, so
/// the EMF writer cannot reuse the overlap-free outline it makes of a nonzero path.
/// </summary>
[MemoryDiagnoser]
public class VectorWriterBenchmarks
{
    private const int Width = 160, Height = 120;
    private readonly (int Kind, RectF Box, Rgba8 Color)[] _shapes = Shapes(10_000);
    private readonly Path _star = Star();
    private readonly Stroke _stroke = new(1.5f);

    [Benchmark(Baseline = true)]
    public long Svg() => Write(s => new SvgCanvas(s, Width, Height), fresh: false);

    [Benchmark]
    public long Emf() => Write(s => new EmfCanvas(s, Width, Height), fresh: false);

    [Benchmark]
    public long SvgFreshStars() => Write(s => new SvgCanvas(s, Width, Height), fresh: true);

    [Benchmark]
    public long EmfFreshStars() => Write(s => new EmfCanvas(s, Width, Height), fresh: true);

    private long Write(Func<Stream, Canvas> open, bool fresh)
    {
        using MemoryStream stream = new();
        using (Canvas c = open(stream))
        {
            foreach ((int kind, RectF box, Rgba8 color) in _shapes)
            {
                switch (kind)
                {
                    case 0:
                        c.FillRect(box, color);
                        break;
                    case 1:
                        c.FillOval(box, color);
                        break;
                    case 2:
                        c.DrawLine(new Vector2(box.X, box.Y), new Vector2(box.Right, box.Bottom), _stroke, color);
                        break;
                    default:
                        c.Save();
                        c.Translate(box.X, box.Y);
                        c.FillPath(fresh ? Star() : _star, color);
                        c.Restore();
                        break;
                }
            }
        }

        return stream.Length;
    }

    private static (int, RectF, Rgba8)[] Shapes(int count)
    {
        Random random = new(7);
        (int, RectF, Rgba8)[] shapes = new (int, RectF, Rgba8)[count];
        for (int i = 0; i < count; i++)
        {
            float x = random.NextSingle() * (Width - 20), y = random.NextSingle() * (Height - 20);
            RectF box = new(x, y, 4 + (random.NextSingle() * 16), 4 + (random.NextSingle() * 16));
            shapes[i] = (i % 4, box, new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)(128 + random.Next(128))));
        }

        return shapes;
    }

    /// <summary>A five-pointed star drawn in one stroke, filled nonzero: its middle overlaps itself.</summary>
    private static Path Star()
    {
        PathBuilder b = new();
        for (int i = 0; i < 5; i++)
        {
            float a = (i * 4 * MathF.PI / 5) - (MathF.PI / 2);
            Vector2 p = new Vector2(10, 10) + (new Vector2(MathF.Cos(a), MathF.Sin(a)) * 10);
            if (i == 0)
            {
                b.MoveTo(p);
            }
            else
            {
                b.LineTo(p);
            }
        }

        return b.Close().ToPath(FillRule.NonZero);
    }
}
