using Egami.Formats.Av1;
using Egami.Formats.Avif;
using Egami.Formats.Heif;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>The AVIF writer: lossless files load back exactly, lossy files load back close, and the container is what the reader expects.</summary>
public class AvifWriterTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(37, 21)]
    [InlineData(130, 70)]
    public void LosslessRgbLoadsBackExactly(int width, int height)
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(width, height, (x, y) => new Rgb8((byte)(x * 7), (byte)((y * 5) + x), (byte)(x ^ y)));
        byte[] file = Save(source, new AvifWriterOptions { Lossless = true });
        using Raster<Rgb8> loaded = Raster.Load<Rgb8>(file);
        AssertEqual(source, loaded);
        using Raster raw = Raster.Load(file);
        Assert.Equal(ChromaFormat.Yuv444, Assert.IsType<AvifDetails>(raw.Metadata.FormatDetails).Chroma);
    }

    [Theory]
    [InlineData(ChromaFormat.Yuv420, 8, YuvMatrix.Bt709, YuvRange.Limited, ChromaSiting.TopLeft, false)]
    [InlineData(ChromaFormat.Yuv422, 10, YuvMatrix.Bt2020Ncl, YuvRange.Full, ChromaSiting.Center, true)]
    [InlineData(ChromaFormat.Yuv444, 12, YuvMatrix.Bt601, YuvRange.Full, ChromaSiting.Center, false)]
    [InlineData(ChromaFormat.Yuv400, 8, YuvMatrix.Bt601, YuvRange.Full, ChromaSiting.Center, true)]
    public void YuvPlanesSaveLosslessAndLoadBackExactly(ChromaFormat chroma, int bitDepth, YuvMatrix matrix, YuvRange range, ChromaSiting siting, bool alpha)
    {
        YuvColor color = new(matrix, range, siting);
        using YuvRaster source = bitDepth == 8
            ? YuvRaster.Create(53, 31, chroma, color, alpha)
            : YuvRaster.Create(53, 31, chroma, bitDepth, color, alpha);
        Random random = new(bitDepth);
        int max = (1 << bitDepth) - 1;
        Fill(source, (_, x, y) => Math.Clamp(((x * 9) + (y * 4)) % (max + 1) + random.Next(-3, 4), 0, max));
        using MemoryStream stream = new();
        source.Save(stream, "avif", new AvifWriterOptions { Lossless = true });
        using YuvRaster loaded = YuvRaster.Load(stream.ToArray());
        Assert.Equal((chroma, bitDepth, alpha), (loaded.ChromaFormat, loaded.BitDepth, loaded.HasAlpha));
        if (chroma != ChromaFormat.Yuv400)
        {
            Assert.Equal(color with { Siting = chroma == ChromaFormat.Yuv420 ? siting : ChromaSiting.Center }, loaded.Color);
        }

        int[] planes = chroma == ChromaFormat.Yuv400 ? [0, 3] : [0, 1, 2, 3];
        foreach (int plane in planes)
        {
            Assert.Equal(Samples(source, plane), Samples(loaded, plane));
        }
    }

    [Fact]
    public void SharpYuvKeepsColoredEdges()
    {
        // Red stripes on blue: averaging chroma across their edges bleeds color into the luma a decoder rebuilds.
        using Raster<Rgb8> source = Pattern<Rgb8>(64, 64, (x, y) => (x / 3) % 2 == 0 ? new Rgb8(230, 20, 20) : new Rgb8(20, 20, 230));
        using Raster<Rgb8> plain = Raster.Load<Rgb8>(Save(source, new AvifWriterOptions { Quality = 85, Effort = 4 }));
        using Raster<Rgb8> sharp = Raster.Load<Rgb8>(Save(source, new AvifWriterOptions { Quality = 85, Effort = 5 }));
        Assert.True(Error(source, sharp) < Error(source, plain), $"sharp {Error(source, sharp)}, plain {Error(source, plain)}");

        static long Error(Raster<Rgb8> a, Raster<Rgb8> b)
        {
            long error = 0;
            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    Rgb8 p = a.Row(y)[x], q = b.Row(y)[x];
                    error += ((p.R - q.R) * (p.R - q.R)) + ((p.G - q.G) * (p.G - q.G)) + ((p.B - q.B) * (p.B - q.B));
                }
            }

            return error;
        }
    }

    private static void Fill(YuvRaster raster, Func<int, int, int, int> sample)
    {
        for (int plane = 0; plane < 4; plane++)
        {
            switch (raster)
            {
                case YuvRaster<byte> b when Plane(b, plane) is YuvPlane<byte> p:
                    for (int y = 0; y < p.Height; y++)
                    {
                        for (int x = 0; x < p.Width; x++)
                        {
                            p.Row(y)[x] = (byte)sample(plane, x, y);
                        }
                    }

                    break;
                case YuvRaster<ushort> w when Plane(w, plane) is YuvPlane<ushort> p:
                    for (int y = 0; y < p.Height; y++)
                    {
                        for (int x = 0; x < p.Width; x++)
                        {
                            p.Row(y)[x] = (ushort)sample(plane, x, y);
                        }
                    }

                    break;
            }
        }
    }

    private static int[] Samples(YuvRaster raster, int plane)
    {
        List<int> samples = [];
        if (raster is YuvRaster<byte> b && Plane(b, plane) is YuvPlane<byte> p8)
        {
            for (int y = 0; y < p8.Height; y++)
            {
                foreach (byte v in p8.Row(y))
                {
                    samples.Add(v);
                }
            }
        }
        else if (raster is YuvRaster<ushort> w && Plane(w, plane) is YuvPlane<ushort> p16)
        {
            for (int y = 0; y < p16.Height; y++)
            {
                foreach (ushort v in p16.Row(y))
                {
                    samples.Add(v);
                }
            }
        }

        return [.. samples];
    }

    private static YuvPlane<T>? Plane<T>(YuvRaster<T> raster, int plane)
        where T : unmanaged => plane switch { 0 => raster.Y, 1 => raster.U, 2 => raster.V, _ => raster.A };

    [Fact]
    public void LosslessFilesSayLossless()
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(20, 20, (x, y) => new Rgb8((byte)x, (byte)y, 9));
        using Raster loaded = Raster.Load(Save(source, new AvifWriterOptions { Lossless = true }));
        Assert.True(((AvifDetails)loaded.Metadata.FormatDetails!).Lossless);
        using Raster lossy = Raster.Load(Save(source, new AvifWriterOptions()));
        Assert.False(((AvifDetails)lossy.Metadata.FormatDetails!).Lossless);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void LosslessDeepRgbaLoadsBackExactly(int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        using Raster<Rgba16> source = Pattern<Rgba16>(45, 33, (x, y) => new Rgba16(Wide(x * 31, max), Wide(y * 17, max), Wide((x * y) + 5, max), Wide((x + y) * 40, max)));
        byte[] file = Save(source, new AvifWriterOptions { Lossless = true, BitDepth = bitDepth });
        using Raster<Rgba16> loaded = Raster.Load<Rgba16>(file);

        // Every sample at the coded depth is exact.
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgba16 a = source.Row(y)[x], b = loaded.Row(y)[x];
                Assert.Equal((Narrow(a.R), Narrow(a.G), Narrow(a.B), Narrow(a.A)), (Narrow(b.R), Narrow(b.G), Narrow(b.B), Narrow(b.A)));
            }
        }

        HeifFile heif = HeifFile.Parse(file, "AVIF");
        Assert.Equal(2, heif.Items.Count);
        Assert.True(heif.Item(2)!.Hidden);
        Assert.Equal([1u], heif.Item(2)!.ReferencesOf(FourCC.Of("auxl")));

        static ushort Wide(int value, int max) => (ushort)((value % (max + 1)) * 65535 / max);

        int Narrow(ushort value) => ((value * max) + 32767) / 65535;
    }

    [Fact]
    public void LosslessGrayLoadsBackExactly()
    {
        using Raster<Gray8> source = Pattern<Gray8>(50, 40, (x, y) => new Gray8((byte)((x * 3) + (y * 2))));
        using Raster<Gray8> loaded = Raster.Load<Gray8>(Save(source, new AvifWriterOptions { Lossless = true }));
        AssertEqual(source, loaded);
    }

    [Theory]
    [InlineData(60, 30)]
    [InlineData(95, 38)]
    public void LossyRgbLoadsBackClose(int quality, double minimumPsnr)
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(96, 64, (x, y) => new Rgb8((byte)(128 + (100 * Math.Sin(x * 0.1))), (byte)(y * 3), (byte)(x + y)));
        byte[] file = Save(source, new AvifWriterOptions { Quality = quality });
        using Raster<Rgb8> loaded = Raster.Load<Rgb8>(file);
        Assert.True(Psnr(source, loaded) >= minimumPsnr, $"PSNR {Psnr(source, loaded):F1} dB at quality {quality}.");
        HeifFile heif = HeifFile.Parse(file, "AVIF");
        Assert.True(heif.HasBrand(FourCC.Of("avif")));
        Assert.Equal(quality >= 90, !heif.HasBrand(FourCC.Of("MA1B")) && heif.HasBrand(FourCC.Of("MA1A")));
    }

    [Fact]
    public void SaveChoosesAvifByExtension()
    {
        using Raster<Rgb8> source = Pattern<Rgb8>(16, 16, (x, y) => new Rgb8((byte)(x * 16), (byte)(y * 16), 0));
        string path = Path.Combine(Path.GetTempPath(), $"egami-{Guid.NewGuid():N}.avif");
        try
        {
            source.Save(path);
            Assert.Equal("AVIF", Raster.Probe(path).Format.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void QualityMapsToLibaomQuantizers()
    {
        Assert.Equal(0, AvifWriter.QIndexFor(100));
        Assert.Equal(255, AvifWriter.QIndexFor(0));
        Assert.Equal(120, AvifWriter.QIndexFor(60));
        int previous = 256;
        for (int q = 0; q <= 100; q++)
        {
            Assert.True(AvifWriter.QIndexFor(q) <= previous);
            previous = AvifWriter.QIndexFor(q);
        }
    }

    [Fact]
    public void ScreenContentUsesPalettesAndIntraBlockCopy()
    {
        using Raster<Rgba8> screen = Screen(300, 200);
        byte[] file = Save(screen, new AvifWriterOptions());
        HeifFile heif = HeifFile.Parse(file, "AVIF");
        Av1Frame frame = Av1Decoder.DecodeFirstFrame(heif.ItemData(heif.PrimaryItem!).ToArray(), "AVIF", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(frame.Header.AllowIntrabc);
        Assert.Contains(frame.Grid, b => b is { PaletteSize: [> 0, _] });
        Assert.Contains(frame.Grid, b => b is { UseIntrabc: true });
    }

    /// <summary>Text-like strokes in three colors, one 12x16 cell repeated at shifting offsets: palettes and intra block copy.</summary>
    internal static Raster<Rgba8> Screen(int width, int height) => Pattern<Rgba8>(width, height, (x, y) =>
    {
        int cx = (x + ((y / 16) * 5)) % 12, cy = y % 16;
        bool stroke = (cx is 2 or 3 && cy is > 2 and < 13) || (cy is 7 or 8 && cx is > 2 and < 9) || (cx == 8 && cy > 8 && cy < 13);
        return stroke ? new Rgba8(20, 30, 40, 255) : cy == 15 ? new Rgba8(0, 120, 215, 255) : new Rgba8(245, 246, 248, 255);
    });

    internal static byte[] Save(Raster raster, AvifWriterOptions options)
    {
        using MemoryStream stream = new();
        raster.Save(stream, AvifFormat.Info, options);
        return stream.ToArray();
    }

    internal static Raster<TPixel> Pattern<TPixel>(int width, int height, Func<int, int, TPixel> pixel)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Raster.Create<TPixel>(width, height);
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

    private static void AssertEqual<TPixel>(Raster<TPixel> expected, Raster<TPixel> actual)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                if (!expected.Row(y)[x].Equals(actual.Row(y)[x]))
                {
                    Assert.Fail($"Pixel {x}, {y} is {actual.Row(y)[x]}, not {expected.Row(y)[x]}.");
                }
            }
        }
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

        double mse = sse / (3.0 * a.Width * a.Height);
        return mse == 0 ? 99 : 10 * Math.Log10(255 * 255 / mse);
    }
}
