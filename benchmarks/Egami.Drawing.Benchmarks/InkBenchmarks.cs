using BenchmarkDotNet.Attributes;

using Egami.Colors;
using Egami.Drawing;
using Egami.Operations;

namespace Egami.Benchmarks;

/// <summary>
/// The ink canvas's blend modes, whose math lives in core Egami's InkBlend since 2.3.10: a translucent CMYK and spot colour
/// filled over a 2000x2000 page of FOGRA39 plus one spot, in source-over, Multiply and Hue.
/// </summary>
[MemoryDiagnoser]
public class InkBenchmarks : IDisposable
{
    private InkSet _inks = null!;
    private InkRaster _plates = null!;
    private SolidPaint _paint = null!;

    [Params(BlendMode.SourceOver, BlendMode.Multiply, BlendMode.Hue)]
    public BlendMode Mode { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("Gold", null);
        _plates = new InkRaster(_inks, 2000, 2000);
        using (InkCanvas canvas = new(_plates))
        {
            canvas.FillRect(new RectF(0, 0, 2000, 2000), new SolidPaint(InkColor.Mix(new InkTint(Colorant.Cyan, 0.3f), new InkTint(Colorant.Yellow, 0.2f))));
        }

        _paint = new SolidPaint(InkColor.Mix(new InkTint(Colorant.Magenta, 0.6f), new InkTint(Colorant.Black, 0.1f), new InkTint(_inks[4], 0.4f)))
        {
            BlendMode = Mode,
            Opacity = 0.7f,
        };
    }

    [GlobalCleanup]
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing) => _plates?.Dispose();

    [Benchmark]
    public void Fill()
    {
        using InkCanvas canvas = new(_plates);
        canvas.FillRect(new RectF(100, 100, 1800, 1800), _paint);
    }
}
