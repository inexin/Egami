using System.Numerics;

using BenchmarkDotNet.Attributes;

using Egami.Drawing;
using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// Bitmap tracing (Bitmap Tracing Build Plan task 13): each mode on its own kind of scene at 1000 × 800 and 3000 × 2400 —
/// overlapping antialiased discs for black and white, grayscale and color, flat-color logo art for logo, and round-pen strokes
/// for centerline.
/// </summary>
[MemoryDiagnoser]
public class TraceBenchmarks : IDisposable
{
    private Raster<Rgba8> _scene = null!;
    private TraceOptions _options = null!;

    [Params(TraceMode.BlackAndWhite, TraceMode.Grayscale, TraceMode.Color, TraceMode.Logo, TraceMode.Centerline)]
    public TraceMode Mode { get; set; }

    [Params(1000, 3000)]
    public int Width { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _scene = TraceScenes.For(Mode, Width);
        _options = TraceScenes.Options(Mode);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) => _scene?.Dispose();

    [Benchmark]
    public TraceResult Trace() => _scene.Trace(_options);
}

/// <summary>The tracing benchmarks' scenes, also timed by <c>--time-trace</c>.</summary>
public static class TraceScenes
{
    /// <summary>The scene for a mode, <paramref name="width"/> wide and four fifths as high.</summary>
    public static Raster<Rgba8> For(TraceMode mode, int width) => mode switch
    {
        TraceMode.Logo => Logo(width),
        TraceMode.Centerline => Strokes(width),
        _ => Discs(width),
    };

    /// <summary>The options each mode is timed with.</summary>
    public static TraceOptions Options(TraceMode mode) => mode switch
    {
        TraceMode.BlackAndWhite => TraceOptions.BlackAndWhite,
        TraceMode.Grayscale => new TraceOptions { Mode = TraceMode.Grayscale, Colors = 5 },
        TraceMode.Color => new TraceOptions { Mode = TraceMode.Color, Colors = 6 },
        TraceMode.Logo => TraceOptions.Logo,
        _ => TraceOptions.LineArt,
    };

    /// <summary>400 overlapping antialiased discs in five colors on white.</summary>
    public static Raster<Rgba8> Discs(int width)
    {
        Rgba8[] colors = [new(220, 30, 40, 255), new(30, 60, 200, 255), new(20, 150, 60, 255), new(240, 180, 20, 255), new(40, 40, 40, 255)];
        float s = width / 1000f;
        Raster<Rgba8> raster = Raster.Create(width, width * 4 / 5, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        Random random = new(5);
        for (int i = 0; i < 400; i++)
        {
            Vector2 center = new(random.NextSingle() * 1000 * s, random.NextSingle() * 800 * s);
            canvas.FillCircle(center, (4 + (random.NextSingle() * 36)) * s, colors[i % colors.Length]);
        }

        return raster;
    }

    /// <summary>Flat-color art: discs, a turned rectangle, a star and thin bars, antialiased, on white.</summary>
    public static Raster<Rgba8> Logo(int width)
    {
        float s = width / 125f;
        Raster<Rgba8> raster = Raster.Create(width, width * 4 / 5, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        canvas.Scale(s, s);
        canvas.FillCircle(new Vector2(30, 30), 20, new Rgba8(220, 30, 40, 255));
        canvas.FillCircle(new Vector2(75, 25), 8, new Rgba8(30, 60, 200, 255));
        canvas.FillCircle(new Vector2(100, 30), 3.5f, new Rgba8(20, 150, 60, 255));
        PathBuilder star = new();
        for (int i = 0; i < 10; i++)
        {
            double a = ((180.0 * i / 5) - 90) * Math.PI / 180;
            float r = (i & 1) == 0 ? 18 : 7;
            Vector2 p = new(30 + (float)(r * Math.Cos(a)), 80 + (float)(r * Math.Sin(a)));
            if (i == 0)
            {
                star.MoveTo(p);
            }
            else
            {
                star.LineTo(p);
            }
        }

        star.Close();
        canvas.FillPath(star.ToPath(), new Rgba8(120, 40, 160, 255));
        canvas.Save();
        canvas.Rotate(17, new Vector2(80, 67));
        canvas.FillRect(new RectF(60, 55, 40, 25), new Rgba8(240, 140, 20, 255));
        canvas.Restore();
        canvas.FillRect(new RectF(110, 50, 4, 40), new Rgba8(40, 40, 40, 255));
        canvas.FillRect(new RectF(118.5f, 50.5f, 1.5f, 40), new Rgba8(0, 160, 170, 255));
        return raster;
    }

    /// <summary>Round-pen strokes from 1.5 to 4 units wide: lines, a curve, a circle and a crossing.</summary>
    public static Raster<Rgba8> Strokes(int width)
    {
        float s = width / 200f;
        Raster<Rgba8> raster = Raster.Create(width, width * 4 / 5, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        (Egami.Drawing.Path Path, float Width)[] strokes =
        [
            (new PathBuilder().MoveTo(15, 15).LineTo(185, 32).ToPath(), 3),
            (new PathBuilder().MoveTo(20, 60).CubicTo(60, 30, 110, 95, 180, 55).ToPath(), 2),
            (new PathBuilder().AddCircle(new Vector2(55, 115), 24).ToPath(), 4),
            (new PathBuilder().MoveTo(100, 80).LineTo(170, 140).ToPath(), 2.5f),
            (new PathBuilder().MoveTo(100, 140).LineTo(170, 80).ToPath(), 2.5f),
            (new PathBuilder().MoveTo(15, 40).LineTo(90, 44).ToPath(), 1.5f),
        ];
        foreach ((Egami.Drawing.Path path, float pen) in strokes)
        {
            canvas.StrokePath(path.Transform(Matrix3x2.CreateScale(s)), new Stroke(pen * s) { Cap = LineCap.Round, Join = LineJoin.Round }, Rgba8.Black);
        }

        return raster;
    }

    /// <summary>A hash of a trace's shapes (kinds, colors, widths and every path point), to check that output did not change.</summary>
    public static ulong Hash(TraceResult result)
    {
        ulong h = 14695981039346656037;
        void Add(uint v)
        {
            h = (h ^ v) * 1099511628211;
        }

        foreach (TracedShape shape in result.Shapes)
        {
            Add((uint)shape.Kind);
            Add((uint)(shape.Color.R | (shape.Color.G << 8) | (shape.Color.B << 16) | (shape.Color.A << 24)));
            Add(BitConverter.SingleToUInt32Bits(shape.StrokeWidth));
            foreach (PathVerb verb in shape.Path.Verbs)
            {
                Add((uint)verb);
            }

            foreach (Vector2 p in shape.Path.Points)
            {
                Add(BitConverter.SingleToUInt32Bits(p.X));
                Add(BitConverter.SingleToUInt32Bits(p.Y));
            }
        }

        return h;
    }
}
