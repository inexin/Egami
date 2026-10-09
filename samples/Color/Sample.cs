using Egami.Operations;

namespace Egami.Samples.Color;

/// <summary>Applies each color operation to a photo, and a combined color matrix.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        using Raster photo = Raster.Load(SampleRunner.Image("duck.png"));
        photo.Apply(ops => ops.Fit(240, 240));

        (string Name, Action<RasterOps> Build)[] steps =
        [
            ("brighter", ops => ops.Brightness(1.3f)),
            ("contrast", ops => ops.Contrast(1.4f)),
            ("saturated", ops => ops.Saturation(1.8f)),
            ("hue-120", ops => ops.HueRotate(120)),
            ("sepia", ops => ops.Sepia()),
            ("gray", ops => ops.Grayscale(LumaWeights.Rec709)),
            ("inverted", ops => ops.Invert()),
            ("half-opacity", ops => ops.Opacity(0.5f)),
            ("warm", ops => ops.ColorMatrix(ColorMatrix.Saturation(1.1f).Then(ColorMatrix.Brightness(1.05f)))),
        ];
        foreach ((string name, Action<RasterOps> build) in steps)
        {
            using Raster result = photo.Derive(build);
            result.Save(Path.Combine(output, name + ".png"));
        }
    }
}
