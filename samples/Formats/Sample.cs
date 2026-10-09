using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Formats.Png;
using Egami.Formats.Pnm;
using Egami.Formats.Qoi;
using Egami.Formats.Tga;

namespace Egami.Samples.Formats;

/// <summary>Saves one photo in every format Egami writes, then reads each file back and prints what its header says.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        using Raster photo = Raster.Load(SampleRunner.Image("duck.png"));
        photo.Apply(ops => ops.Fit(320, 320));

        Save(photo, output, "duck.png", PngFormat.Info, new PngWriterOptions { Reduce = true });
        Save(photo, output, "duck.jpg", Egami.Formats.Jpeg.JpegFormat.Info, new Egami.Formats.Jpeg.JpegWriterOptions { Quality = 85 });
        Save(photo, output, "duck.gif", Egami.Formats.Gif.GifFormat.Info, WriterOptions.Default);
        Save(photo, output, "duck.bmp", BmpFormat.Info, BmpWriterOptions.Default);
        Save(photo, output, "duck-16-colors.bmp", BmpFormat.Info, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed4 });
        Save(photo, output, "duck.tga", TgaFormat.Info, TgaWriterOptions.Default);
        Save(photo, output, "duck.qoi", QoiFormat.Info, QoiWriterOptions.Default);
        Save(photo, output, "duck.ppm", PnmFormat.Info, new PnmWriterOptions { Kind = PnmKind.Pixmap });
        Save(photo, output, "duck.pam", PnmFormat.Info, PnmWriterOptions.Default);

        foreach (string file in Directory.GetFiles(output).Order(StringComparer.Ordinal))
        {
            RasterInfo info = Raster.Probe(file);
            Console.WriteLine($"{Path.GetFileName(file),-20} {info.Format.Name,-5} {info.Width} × {info.Height} {info.Layout.ChannelOrder} {new FileInfo(file).Length,8:N0} bytes");
        }
    }

    private static void Save(Raster raster, string folder, string name, FormatInfo format, WriterOptions options)
    {
        using FileStream file = File.Create(Path.Combine(folder, name));
        raster.Save(file, format, options);
    }
}
