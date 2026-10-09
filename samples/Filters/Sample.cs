using Egami.Operations;

namespace Egami.Samples.Filters;

/// <summary>Blurs, sharpens and convolves a photo.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        using Raster photo = Raster.Load(SampleRunner.Image("egret.png"));
        photo.Apply(ops => ops.Fit(240, 240));

        ConvolutionKernel edges = new(3, 3, [-1, -1, -1, -1, 8, -1, -1, -1, -1]);
        (string Name, Action<RasterOps> Build)[] steps =
        [
            ("gaussian-3", ops => ops.Blur(3)),
            ("gaussian-3-linear", ops => ops.Blur(3, ColorSpace.Linear)),
            ("box-4", ops => ops.BoxBlur(4)),
            ("sharpened", ops => ops.Sharpen(1, 1.5f)),
            ("edges", ops => ops.Convolve(edges, EdgeMode.Mirror, preserveAlpha: true)),
        ];
        foreach ((string name, Action<RasterOps> build) in steps)
        {
            using Raster result = photo.Derive(build);
            result.Save(Path.Combine(output, name + ".png"));
        }
    }
}
