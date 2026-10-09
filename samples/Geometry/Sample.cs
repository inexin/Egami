using Egami.Operations;
using Egami.Pixels;

namespace Egami.Samples.Geometry;

/// <summary>Resizes, fits, covers, crops, pads, rotates, flips and turns a sideways phone photo upright.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        using Raster photo = Raster.Load(SampleRunner.Image("egret.png"));

        using (Raster wide = photo.Derive(ops => ops.Resize(300, 0, ResampleFilter.Lanczos3)))
        {
            wide.Save(Path.Combine(output, "resize-300-wide.png"));
        }

        using (Raster fitted = photo.Derive(ops => ops.Fit(200, 200)))
        {
            fitted.Save(Path.Combine(output, "fit-200.png"));
        }

        using (Raster covered = photo.Derive(ops => ops.Cover(200, 200, Anchor.Top)))
        {
            covered.Save(Path.Combine(output, "cover-200-top.png"));
        }

        using (Raster framed = photo.Derive(ops => ops.Fit(200, 200).Pad(240, 240, Rgba8.White).Rotate(-5, ResampleFilter.Bicubic, Rgba8.Transparent)))
        {
            framed.Save(Path.Combine(output, "framed.png"));
        }

        using (Raster mirrored = photo.Derive(ops => ops.Crop(0, 0, 240, 240).FlipHorizontal()))
        {
            mirrored.Save(Path.Combine(output, "crop-mirrored.png"));
        }

        using Raster sideways = Raster.Load(SampleRunner.Image("sideways.jpg"));
        Console.WriteLine($"Stored {sideways.Width} × {sideways.Height}, orientation {sideways.Metadata.Exif?.Orientation}");
        sideways.Apply(ops => ops.AutoOrient().Fit(200, 200));
        Console.WriteLine($"Upright {sideways.Width} × {sideways.Height}");
        sideways.Save(Path.Combine(output, "upright.jpg"));
    }
}
