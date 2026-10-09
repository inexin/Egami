using Egami.Pixels;

namespace Egami.Samples.Compositing;

/// <summary>Watermarks a photo with a translucent logo, and tiles one area of an image like a sprite sheet.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(SampleRunner.Image("egret.png"));
        using Raster<Rgba8> logo = Raster.Load<Rgba8>(SampleRunner.Image("duck.png"));
        logo.Apply(ops => ops.Fit(96, 96));

        photo.Apply(ops => ops.DrawImage(logo, new Point(photo.Width - logo.Width - 12, photo.Height - logo.Height - 12), opacity: 0.7f));
        photo.Save(Path.Combine(output, "watermarked.png"));

        // A 4 × 4 grid of one 48 × 48 tile from the logo, drawn without copying it out first.
        using Raster<Rgba8> sheet = Raster.Create<Rgba8>(192, 192, Rgba8.White);
        Rect tile = new(24, 24, 48, 48);
        sheet.Apply(ops =>
        {
            for (int i = 0; i < 16; i++)
            {
                ops.DrawImage(logo, tile, new Point((i % 4) * 48, (i / 4) * 48));
            }
        });
        sheet.Save(Path.Combine(output, "tiles.png"));
    }
}
