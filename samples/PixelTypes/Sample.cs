using Egami.Pixels;

namespace Egami.Samples.PixelTypes;

/// <summary>Loads one photo into several pixel types and saves what each holds: 16-bit, a mask, packed 5-6-5, floats and inks.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        using Raster<Rgb16> deep = Raster.Load<Rgb16>(SampleRunner.Image("duck.png"));
        deep.Save(Path.Combine(output, "duck-16bit.png"));

        using Raster<Alpha8> mask = Raster.Load<Alpha8>(SampleRunner.Image("duck.png"));
        mask.Save(Path.Combine(output, "duck-alpha.png"));

        using Raster<Bgr565> packed = deep.As<Bgr565>();
        using Raster<Rgba8> shown = packed.As<Rgba8>();
        shown.Save(Path.Combine(output, "duck-565.png"));

        using Raster<RgbF32> floats = deep.As<RgbF32>();
        floats.Save(Path.Combine(output, "duck.pfm"));

        using Raster<Cmyk8> inks = deep.As<Cmyk8>();
        Cmyk8 middle = inks.Row(inks.Height / 2)[inks.Width / 2];
        Console.WriteLine($"Middle pixel as inks: C {middle.C}, M {middle.M}, Y {middle.Y}, K {middle.K}");

        foreach (string file in Directory.GetFiles(output).Order(StringComparer.Ordinal))
        {
            Console.WriteLine($"{Path.GetFileName(file),-16} {Raster.Probe(file).Layout.ChannelOrder,-6} {new FileInfo(file).Length,8:N0} bytes");
        }
    }
}
