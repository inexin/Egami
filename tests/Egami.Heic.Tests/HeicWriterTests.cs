using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Heic;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Heic.Tests;

/// <summary>
/// The HEIC writer (HEIC encoder design, task 11): files Save writes with Egami.Heic registered load back through Egami's HEIC
/// reader, lossless ones exactly, with their alpha, metadata, grid, thumbnail and gain map, and core Egami alone still cannot write.
/// </summary>
public class HeicWriterTests
{
    private static readonly EgamiContext Context = new EgamiContext().WithHeic();

    [Theory]
    [InlineData(64, 48)]
    [InlineData(37, 21)]
    [InlineData(130, 70)]
    public void LossyRgbLoadsBackClose(int width, int height)
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(width, height, (x, y) => new Rgb8((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)(128 + (40 * Math.Sin(x / 5.0)))));
        byte[] file = Save(source, new HeicWriterOptions { Quality = 80 });
        using Raster<Rgb8> loaded = Raster.Load<Rgb8>(file, Context);
        Assert.Equal((width, height), (loaded.Width, loaded.Height));
        Assert.True(Psnr(source, loaded) > 30, $"PSNR {Psnr(source, loaded):F1} dB.");
        using Raster raw = Raster.Load(file, Context);
        HeicDetails details = Assert.IsType<HeicDetails>(raw.Metadata.FormatDetails);
        Assert.Equal(("heic", 1, ChromaFormat.Yuv420, 8), (details.MajorBrand, details.Profile, details.Chroma, details.BitDepth));
        Assert.Equal(["mif1", "heic", "miaf"], details.CompatibleBrands);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(37, 21)]
    [InlineData(70, 66)]
    public void LosslessRgbLoadsBackExactly(int width, int height)
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(width, height, (x, y) => new Rgb8((byte)(x * 7), (byte)((y * 5) + x), (byte)(x ^ y)));
        byte[] file = Save(source, new HeicWriterOptions { Lossless = true });
        using Raster<Rgb8> loaded = Raster.Load<Rgb8>(file, Context);
        Assert.Equal(Pixels(source), Pixels(loaded));
        using Raster raw = Raster.Load(file, Context);
        HeicDetails details = Assert.IsType<HeicDetails>(raw.Metadata.FormatDetails);
        Assert.Equal((4, ChromaFormat.Yuv444), (details.Profile, details.Chroma));
    }

    [Fact]
    public void DeepPixelsAreMain10AndLosslessKeeps12Bits()
    {
        using Raster<Rgb16> source = Pattern<Rgb16>(40, 24, (x, y) => new Rgb16((ushort)(x * 1600), (ushort)(y * 2700), (ushort)((x * y) * 61)));
        using (Raster raw = Raster.Load(Save(source, new HeicWriterOptions { Quality = 90 }), Context))
        {
            HeicDetails details = Assert.IsType<HeicDetails>(raw.Metadata.FormatDetails);
            Assert.Equal((2, 10), (details.Profile, details.BitDepth));
        }

        using Raster<Rgb16> loaded = Raster.Load<Rgb16>(Save(source, new HeicWriterOptions { Lossless = true }), Context);
        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                Rgb16 a = source.Row(y)[x], b = loaded.Row(y)[x];
                Assert.Equal((a.R * 4095 + 32767) / 65535, (b.R * 4095 + 32767) / 65535);
                Assert.Equal((a.G * 4095 + 32767) / 65535, (b.G * 4095 + 32767) / 65535);
            }
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(60)]
    public void AlphaLoadsBack(int alphaQuality)
    {
        using Raster<Rgba8> source = Pattern<Rgba8>(45, 30, (x, y) => new Rgba8((byte)(x * 5), 90, (byte)(y * 8), (byte)Math.Clamp((x * 6) - (y * 2), 0, 255)));
        byte[] file = Save(source, new HeicWriterOptions { Quality = 90, AlphaQuality = alphaQuality });
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(file, Context);
        double error = 0;
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 45; x++)
            {
                error = Math.Max(error, Math.Abs(source.Row(y)[x].A - loaded.Row(y)[x].A));
            }
        }

        Assert.True(alphaQuality == 100 ? error == 0 : error < 40, $"Alpha differs by up to {error}.");
        HeifFile heif = HeifFile.Parse(file, "HEIC");
        Assert.Contains(heif.Items, i => i.Type == IsoTypes.Of("hvc1") && i.Hidden);
    }

    [Fact]
    public void GridCellsLoadBack()
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(150, 100, (x, y) => new Rgb8((byte)(x + y), (byte)(x * 2), (byte)(y * 2)));
        byte[] file = Save(source, new HeicWriterOptions { Lossless = true, GridTileSize = new Size(64, 64) });
        using Raster loaded = Raster.Load(file, Context);
        Assert.Equal((3, 2), (((HeicDetails)loaded.Metadata.FormatDetails!).GridColumns, ((HeicDetails)loaded.Metadata.FormatDetails!).GridRows));
        using Raster<Rgb8> pixels = Raster.Load<Rgb8>(file, Context);
        Assert.Equal(Pixels(source), Pixels(pixels));
    }

    [Theory]
    [InlineData(ImageOrientation.Rotate90)]
    [InlineData(ImageOrientation.Transpose)]
    public void ExifXmpAndOrientationLoadBack(ImageOrientation orientation)
    {
        using Raster<Rgba8> source = Pattern<Rgba8>(40, 24, (x, y) => new Rgba8((byte)(x * 6), (byte)(y * 9), 80, (byte)(255 - x)));
        source.Metadata.Exif = new ExifData { Orientation = orientation };
        source.Metadata.Exif.Set(ExifKey.Artist, "Egami");
        source.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        source.Metadata.IccProfile = ColorProfile.AdobeRgb.ToIcc();
        using Raster loaded = Raster.Load(Save(source, new HeicWriterOptions()), Context);
        Assert.Equal(orientation, loaded.Metadata.Exif?.Orientation);
        Assert.True(loaded.Metadata.Exif!.TryGet(ExifKey.Artist, out _));
        Assert.Equal(source.Metadata.Xmp.ToArray(), loaded.Metadata.Xmp.ToArray());
        Assert.Equal(source.Metadata.IccProfile.ToArray(), loaded.Metadata.IccProfile.ToArray());
    }

    [Fact]
    public void AThumbnailIsLinkedToTheImage()
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(200, 120, (x, y) => new Rgb8((byte)x, (byte)y, (byte)(x ^ y)));
        byte[] file = Save(source, new HeicWriterOptions { ThumbnailSize = 64 });
        using Raster loaded = Raster.Load(file, Context);
        HeicItem thumbnail = Assert.Single(((HeicDetails)loaded.Metadata.FormatDetails!).Items, i => i.Role == HeicItemRole.Thumbnail);
        Assert.Equal(new Size(64, 38), thumbnail.Size);
        Assert.Equal((200, 120), (loaded.Width, loaded.Height));
    }

    [Fact]
    public void AGainMapLoadsBack()
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(48, 32, (x, y) => new Rgb8((byte)(x * 5), (byte)(y * 7), 60));
        using Raster<Gray8> map = Pattern<Gray8>(24, 16, (x, y) => new Gray8((byte)(x * 10)));
        source.Metadata.GainMap = new GainMap(new GainMapInfo { HdrCapacityMax = 2, GainMapMax = new GainMapValues(2) }, map);
        using Raster loaded = Raster.Load(Save(source, new HeicWriterOptions { Quality = 90, GainMapQuality = 100 }), Context);
        GainMap gain = Assert.IsType<GainMap>(loaded.Metadata.GainMap);
        Assert.Equal((24, 16), (gain.Width, gain.Height));
        Assert.Equal(map.Row(3)[11].L, gain.Pixels[(3 * 24) + 11]);
    }

    [Fact]
    public void ASequenceWritesItsFirstFrameAndSaysSo()
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(16, 16, (x, y) => new Rgb8((byte)(x * 16), 0, 0));
        List<EgamiDiagnostic> warnings = [];
        using Raster<Rgb8> sequence = Pattern<Rgb8>(16, 16, (x, y) => new Rgb8((byte)(x * 16), 0, 0), Context with { Diagnostics = warnings.Add });
        Frame<Rgb8> second = sequence.Frames.Add(sequence.Frames[0]);
        second.Row(3).Fill(new Rgb8(0, 200, 0));
        using MemoryStream stream = new();
        sequence.Save(stream, "heic", new HeicWriterOptions { Lossless = true });
        Assert.Contains(warnings, w => w.Id == "Save.FramesDropped");
        using Raster<Rgb8> loaded = Raster.Load<Rgb8>(stream.ToArray(), Context);
        Assert.Equal(1, loaded.FrameCount);
        Assert.Equal(Pixels(source), Pixels(loaded));
    }

    [Fact]
    public void ATargetSizeIsKept()
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(96, 64, (x, y) => new Rgb8((byte)((x * 37) ^ (y * 11)), (byte)(x * y), (byte)(y * 4)));
        byte[] file = Save(source, new HeicWriterOptions { TargetSize = 2500 });
        Assert.InRange(file.Length, 1500, 2500);
    }

    [Fact]
    public void CoreEgamiCannotWriteHeicAndSaysWhy()
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(8, 8, (x, y) => default, new EgamiContext());
        using MemoryStream stream = new();
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => source.Save(stream, "heic"));
        Assert.Contains("Egami.Heic", e.Message, StringComparison.Ordinal);
    }

    internal static byte[] Save<TPixel>(Raster<TPixel> raster, HeicWriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream stream = new();
        raster.Save(stream, "heic", options);
        return stream.ToArray();
    }

    internal static Raster<TPixel> Pattern<TPixel>(int width, int height, Func<int, int, TPixel> pixel, EgamiContext? context = null)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Raster.Create<TPixel>(width, height, context ?? Context);
        for (int y = 0; y < height; y++)
        {
            Span<TPixel> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = pixel(x, y);
            }
        }

        return raster;
    }

    private static TPixel[] Pixels<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<TPixel> pixels = [];
        for (int y = 0; y < raster.Height; y++)
        {
            pixels.AddRange(raster.Row(y).ToArray());
        }

        return [.. pixels];
    }

    private static double Psnr(Raster<Rgb8> a, Raster<Rgb8> b)
    {
        double sse = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Rgb8 p = a.Row(y)[x], q = b.Row(y)[x];
                sse += ((p.R - q.R) * (p.R - q.R)) + ((p.G - q.G) * (p.G - q.G)) + ((p.B - q.B) * (p.B - q.B));
            }
        }

        return 10 * Math.Log10(255.0 * 255 * 3 * a.Width * a.Height / Math.Max(sse, 1e-9));
    }
}
