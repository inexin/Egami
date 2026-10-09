using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Samples.Extending;

/// <summary>Writes a custom operation, a vignette, and runs it in a chain with built-in steps.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        using Raster photo = Raster.Load(SampleRunner.Image("egret.png"));
        photo.Apply(ops => ops.Fit(300, 300).Add(new Vignette(0.6f)).Saturation(1.1f));
        photo.Save(Path.Combine(output, "vignette.png"));
    }

    /// <summary>Darkens toward the corners: a custom operation, run in parallel bands of rows.</summary>
    /// <param name="strength">How dark the corners get, 0 to 1.</param>
    private sealed class Vignette(float strength) : IRasterOperation
    {
        public Size OutputSize(Size input) => input;

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            float cx = input.Destination.Width / 2f, cy = input.Destination.Height / 2f;
            float reach = MathF.Sqrt((cx * cx) + (cy * cy));
            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    Span<TPixel> source = input.Source.Row(y);
                    Span<TPixel> destination = input.Destination.Row(y);
                    for (int x = 0; x < source.Length; x++)
                    {
                        float d = MathF.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) / reach;
                        float factor = 1 - (strength * d * d);
                        Vector4 v = source[x].ToScaled();
                        destination[x] = TPixel.FromScaled(new Vector4(v.X * factor, v.Y * factor, v.Z * factor, v.W));
                    }
                }
            });
        }
    }
}
