using BenchmarkDotNet.Attributes;

using Egami.Formats.Ani;
using Egami.Formats.Ico;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// The ANI design's speed scenarios, one thread: a 32-frame 48-pixel cursor (every frame distinct, one size) and a 64-step
/// cursor of 16 stored frames at the five cursor sizes. Decode, LoadAll, Probe and encode. Inputs are made in memory from fixed
/// formulas.
/// </summary>
[MemoryDiagnoser]
public class AniBenchmarks
{
    private static readonly AniWriterOptions MultiSize = new() { Sizes = AniWriterOptions.CursorSizes, Hotspot = new Point(64, 64) };

    private readonly EgamiContext _context = EgamiContext.Default with { MaxParallelism = 1 };
    private Raster<Rgba8> _spinner = null!;
    private Raster<Rgba8> _steps = null!;
    private byte[] _spinnerFile = [];
    private byte[] _stepsFile = [];

    [GlobalSetup]
    public void Setup()
    {
        _spinner = Animation(48, 32, frame => frame);
        _steps = Animation(128, 64, step => step % 16);
        _spinnerFile = Save(_spinner, AniWriterOptions.Default);
        _stepsFile = Save(_steps, MultiSize);
        Console.WriteLine($"// 32 frames at 48: {_spinnerFile.Length:N0} bytes; 64 steps, 16 frames at 5 sizes: {_stepsFile.Length:N0} bytes");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _spinner.Dispose();
        _steps.Dispose();
    }

    [Benchmark]
    public int Decode32Frames()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_spinnerFile, _context);
        return raster.FrameCount;
    }

    [Benchmark]
    public int Decode64StepsLargest()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_stepsFile, _context);
        return raster.FrameCount;
    }

    [Benchmark]
    public int LoadAll64StepsEverySize()
    {
        using RasterList all = Raster.LoadAll(_stepsFile, _context);
        return all.Count;
    }

    [Benchmark]
    public int Probe64Steps()
    {
        return Raster.Probe(_stepsFile, _context).FrameCount ?? 0;
    }

    [Benchmark]
    public int Encode32Frames()
    {
        return Save(_spinner, AniWriterOptions.Default).Length;
    }

    [Benchmark]
    public int Encode64StepsEverySize()
    {
        return Save(_steps, MultiSize).Length;
    }

    private static byte[] Save(Raster raster, AniWriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, AniFormat.Info, options);
        return stream.ToArray();
    }

    /// <summary>Draws <paramref name="steps"/> frames of a rotating, half-transparent wedge; <paramref name="picture"/> picks each step's drawing.</summary>
    private Raster<Rgba8> Animation(int size, int steps, Func<int, int> picture)
    {
        Raster<Rgba8> animation = Raster.Create<Rgba8>(size, size, _context);
        for (int step = 0; step < steps; step++)
        {
            Frame<Rgba8> frame = step == 0 ? animation.Frames[0] : animation.Frames.Add(animation.Frames[0]);
            int turn = picture(step);
            double angle = turn * Math.PI / 8;
            for (int y = 0; y < size; y++)
            {
                Span<Rgba8> row = frame.Row(y);
                for (int x = 0; x < size; x++)
                {
                    double dx = x - (size / 2.0);
                    double dy = y - (size / 2.0);
                    double a = Math.Atan2(dy, dx) - angle;
                    bool inside = (dx * dx) + (dy * dy) < size * size / 4.0 && Math.Cos(a) > 0.3;
                    row[x] = inside ? new Rgba8((byte)(x * 255 / size), (byte)(y * 255 / size), (byte)(turn * 15), (byte)(128 + (turn * 8))) : default;
                }
            }

            frame.Metadata.Delay = TimeSpan.FromMilliseconds(50 + (turn % 3 * 50));
            frame.Metadata.FormatDetails = new IcoDetails { Hotspot = new Point(size / 2, size / 2) };
        }

        return animation;
    }
}
