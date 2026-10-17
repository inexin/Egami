using System.Diagnostics;

using Egami.Operations;
using Egami.Operations.Filters;
using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>Phase 2.5.7: the fast blur's stages timed apart at one thread. Run: <c>-- --blur-stages</c>.</summary>
internal static class BlurStages
{
    internal static void Run(string? only = null, int times = 0)
    {
        using Raster<Rgba8> photo = BenchImages.Photo(4000, 3000);
        EgamiContext one = EgamiContext.Default with { MaxParallelism = 1 };
        using Raster<Rgba8> source = Raster.Create<Rgba8>(4000, 3000, one);
        for (int y = 0; y < 3000; y++)
        {
            photo.Row(y).CopyTo(source.Row(y));
        }
        BoxCascade g = BoxCascade.For(20), none = BoxCascade.Boxes(0);
        (string, Action<RasterOps>)[] cases =
        [
            ("full", o => o.Add(new FastBlurOperation(g, g, false, FastBlur.Strip))),
            ("h only", o => o.Add(new FastBlurOperation(g, none, false, FastBlur.Strip))),
            ("v only", o => o.Add(new FastBlurOperation(none, g, false, FastBlur.Strip))),
            ("neither", o => o.Add(new FastBlurOperation(none, none, false, FastBlur.Strip))),
            ("invert", o => o.Invert()),
            ("copy op", o => o.Add(new CopyStep())),
            ("fast 60", o => o.Blur(60, ColorSpace.Encoded, BlurMethod.Fast)),
        ];
        if (only is not null)
        {
            Action<RasterOps> chain = cases.First(c => c.Item1 == only).Item2;
            for (int i = 0; i < times; i++)
            {
                using Raster<Rgba8> result = source.Derive(chain);
            }

            return;
        }

        double[] best = [.. cases.Select(_ => double.MaxValue)];
        for (int round = 0; round < 15; round++)
        {
            for (int c = 0; c < cases.Length; c++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                using Raster<Rgba8> result = source.Derive(cases[c].Item2);
                best[c] = Math.Min(best[c], watch.Elapsed.TotalMilliseconds);
            }
        }

        for (int c = 0; c < cases.Length; c++)
        {
            Console.WriteLine($"{cases[c].Item1,-10} {best[c],8:F1} ms");
        }
    }

    /// <summary>A step that only copies, to time what any step needing a new plane costs.</summary>
    private sealed class CopyStep : IRasterOperation, INeedsNewPlane
    {
        public bool NeedsNewPlane(Size input) => true;

        public Size OutputSize(Size input) => input;

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            for (int y = 0; y < input.Source.Height; y++)
            {
                input.Source.Row(y).CopyTo(input.Destination.Row(y));
            }
        }
    }
}
