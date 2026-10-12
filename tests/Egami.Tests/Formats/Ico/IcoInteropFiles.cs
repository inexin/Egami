using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Ico;
using Egami.Pixels;

namespace Egami.Tests.Formats.Ico;

/// <summary>
/// Writes icons and cursors in every layout the writer offers, each entry's pixels as Egami reads them back next to it, for
/// tools/check-ico-interop.py to read with ImageMagick, Pillow and Windows' own decoder (WIC). Runs only when EGAMI_INTEROP_DIR
/// is set.
/// </summary>
public class IcoInteropFiles
{
    [Fact]
    public void WriteFiles()
    {
        string? root = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(root is not null, "EGAMI_INTEROP_DIR is not set.");
        string directory = Path.Combine(root!, "ico");
        Directory.CreateDirectory(directory);
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(SimpleCorpus.Images, "ops", "egret.png"));
        using Raster<Rgba8> square = photo.Derive(ops => ops.Crop(0, 80, 480, 480).Resize(256, 256), TestContext.Current.CancellationToken);
        Soften(square);
        using Raster<Rgba8> wide = photo.Derive(ops => ops.Resize(39, 52), TestContext.Current.CancellationToken);
        Soften(wide);

        Write(directory, "favicon.ico", [square], IcoFormat.Info, new IcoWriterOptions { Sizes = IcoWriterOptions.FaviconSizes });
        Write(directory, "windows.ico", [square], IcoFormat.Info, new IcoWriterOptions { Sizes = IcoWriterOptions.WindowsSizes });
        Write(directory, "all-png.ico", [square], IcoFormat.Info, new IcoWriterOptions { Sizes = [16, 32, 256], Encoding = IcoEntryEncoding.Png });
        Write(directory, "all-bmp.ico", [square], IcoFormat.Info, new IcoWriterOptions { Sizes = [16, 32, 256], Encoding = IcoEntryEncoding.Bmp });
        Write(directory, "own-size.ico", [wide], IcoFormat.Info, IcoWriterOptions.Default);
        Write(directory, "padded.ico", [wide], IcoFormat.Info, new IcoWriterOptions { Sizes = [48] });
        Write(directory, "cursor.cur", [square], CurFormat.Info, new IcoWriterOptions { Sizes = [32, 48], Hotspot = new Point(128, 10) });
    }

    private static void Soften(Raster<Rgba8> raster)
    {
        for (int y = 0; y < raster.Height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < raster.Width; x++)
            {
                int d = Math.Min(Math.Min(x, raster.Width - 1 - x), Math.Min(y, raster.Height - 1 - y));
                row[x].A = (byte)Math.Min(255, d * 24);
            }
        }
    }

    private static void Write(string directory, string name, Raster[] rasters, FormatInfo format, IcoWriterOptions options)
    {
        string path = Path.Combine(directory, name);
        using (FileStream stream = File.Create(path))
        {
            Raster.SaveAll(rasters, stream, format, options);
        }

        using RasterList entries = Raster.LoadAll<Rgba8>(path);
        for (int i = 0; i < entries.Count; i++)
        {
            Raster<Rgba8> entry = (Raster<Rgba8>)entries[i];
            byte[] bytes = new byte[entry.Width * entry.Height * 4];
            for (int y = 0; y < entry.Height; y++)
            {
                MemoryMarshal.AsBytes(entry.Row(y)).CopyTo(bytes.AsSpan(y * entry.Width * 4));
            }

            File.WriteAllBytes($"{path}.{i}.{entry.Width}x{entry.Height}.rgba", bytes);
        }
    }
}
