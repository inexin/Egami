using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Exr;
using Egami.Pixels;

namespace Egami.Tests.Formats.Exr;

/// <summary>
/// The OpenEXR writer (Phase 2.9.1 task 8): every compression it writes round-trips through Egami's reader bit for bit, and,
/// when EGAMI_INTEROP_DIR is set, the files go there for tools/check-exr-interop.py to read with OpenEXR.
/// </summary>
public class ExrWriteTests
{
    private static readonly ExrCompression[] Compressions = [ExrCompression.None, ExrCompression.Rle, ExrCompression.Zips, ExrCompression.Zip, ExrCompression.Piz];

    public static TheoryData<ExrCompression, ExrPixelType> Cases()
    {
        TheoryData<ExrCompression, ExrPixelType> data = [];
        foreach (ExrCompression compression in Compressions)
        {
            data.Add(compression, ExrPixelType.Half);
            data.Add(compression, ExrPixelType.Float);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsBitForBit(ExrCompression compression, ExrPixelType type)
    {
        using Raster<RgbaF32> source = Pattern(97, 71);
        using MemoryStream file = new();
        source.Save(file, ExrFormat.Info, new ExrWriterOptions { Compression = compression, PixelType = type });
        file.Position = 0;
        using Raster loaded = Raster.Load(file);
        Assert.Equal(type == ExrPixelType.Half ? typeof(Raster<RgbaF16>) : typeof(Raster<RgbaF32>), loaded.GetType());
        ExrDetails details = (ExrDetails)loaded.Metadata.FormatDetails!;
        Assert.Equal(compression, details.Parts[0].Compression);
        Assert.Equal(["A", "B", "G", "R"], details.Parts[0].Channels.Select(c => c.Name));
        file.Position = 0;
        using Raster<RgbaF32> back = Raster.Load<RgbaF32>(file);
        for (int y = 0; y < source.Height; y++)
        {
            Span<RgbaF32> want = source.Row(y), got = back.Row(y);
            for (int x = 0; x < source.Width; x++)
            {
                RgbaF32 w = type == ExrPixelType.Half ? new RgbaF32((float)(Half)want[x].R, (float)(Half)want[x].G, (float)(Half)want[x].B, (float)(Half)want[x].A) : want[x];
                Assert.True(BitwiseEqual(w, got[x]), $"({x}, {y}): {got[x]} for {w}");
            }
        }
    }

    [Fact]
    public void ChoosesHalfOrFloatByPixelType()
    {
        using Raster<RgbaF16> half = Raster.Create<RgbaF16>(4, 4);
        using Raster<Rgb16> sixteen = Raster.Create<Rgb16>(4, 4);
        using Raster<Gray8> gray = Raster.Create<Gray8>(4, 4);
        Assert.Equal(ExrPixelType.Half, Written(half).Channels[0].PixelType);
        Assert.Equal(ExrPixelType.Float, Written(sixteen).Channels[0].PixelType);
        ExrPart grayPart = Written(gray);
        Assert.Equal(["Y"], grayPart.Channels.Select(c => c.Name));
        Assert.Equal(ExrPixelType.Half, grayPart.Channels[0].PixelType);
    }

    [Fact]
    public void WritesSrgbAsLinearLight()
    {
        using Raster<Rgb8> srgb = Raster.Create<Rgb8>(2, 1);
        srgb.Row(0)[0] = new Rgb8(128, 255, 0);
        using MemoryStream file = new();
        srgb.Save(file, ExrFormat.Info);
        file.Position = 0;
        using Raster<RgbaF32> linear = Raster.Load<RgbaF32>(file);
        Assert.Equal(0.2158, linear.Row(0)[0].R, 0.001);
        Assert.Equal(1, linear.Row(0)[0].G, 0.001);
        Assert.Equal(1, linear.Row(0)[0].A);
    }

    [Fact]
    public void NamesTheChromaticitiesOfWidePrimaries()
    {
        using Raster<RgbaF32> raster = Pattern(8, 8);
        raster.Metadata.ColorSpace = Egami.Metadata.ColorSpaceHint.Linear;
        raster.Metadata.Primaries = ColorPrimaries.Rec2020;
        using MemoryStream file = new();
        raster.Save(file, ExrFormat.Info);
        file.Position = 0;
        using Raster loaded = Raster.Load(file);
        Assert.True(loaded.Metadata.Primaries!.IsCloseTo(ColorPrimaries.Rec2020, 1e-6));
    }

    [Fact]
    public void RefusesCompressionsItDoesNotWrite()
    {
        using Raster<RgbaF32> raster = Pattern(8, 8);
        Assert.Throws<NotSupportedException>(() => raster.Save(new MemoryStream(), ExrFormat.Info, new ExrWriterOptions { Compression = ExrCompression.Dwaa }));
    }

    [Fact]
    public void WritesInteropFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        string folder = Path.Combine(directory!, "exr");
        Directory.CreateDirectory(folder);
        using Raster<RgbaF32> pattern = Pattern(173, 131);
        using Raster<RgbaF32> photo = Raster.Load<RgbaF32>(Path.Combine(AppContext.BaseDirectory, "images", "exr", "half-none.exr"));
        foreach ((string name, Raster<RgbaF32> raster) in new[] { ("pattern", pattern), ("desk", photo) })
        {
            foreach ((ExrCompression compression, ExrPixelType type) in Compressions.SelectMany(c => new[] { (c, ExrPixelType.Half), (c, ExrPixelType.Float) }))
            {
                string path = Path.Combine(folder, $"{name}-{compression}-{type}.exr".ToLowerInvariant());
                raster.Save(path, new ExrWriterOptions { Compression = compression, PixelType = type });

                // What OpenEXR must read: the pixels as float32 RGBA, rounded to half for half files.
                using FileStream expected = File.Create(Path.ChangeExtension(path, ".f32"));
                for (int y = 0; y < raster.Height; y++)
                {
                    foreach (RgbaF32 p in raster.Row(y))
                    {
                        foreach (float v in new[] { p.R, p.G, p.B, p.A })
                        {
                            expected.Write(BitConverter.GetBytes(type == ExrPixelType.Half ? (float)(Half)v : v));
                        }
                    }
                }
            }
        }
    }

    private static ExrPart Written<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream file = new();
        raster.Save(file, ExrFormat.Info);
        file.Position = 0;
        using Raster loaded = Raster.Load(file);
        return ((ExrDetails)loaded.Metadata.FormatDetails!).Parts[0];
    }

    /// <summary>Values from 0 to about 1000, negatives, tiny values, flat runs and noise, with alpha.</summary>
    private static Raster<RgbaF32> Pattern(int width, int height)
    {
        Raster<RgbaF32> raster = Raster.Create<RgbaF32>(width, height);
        raster.Metadata.ColorSpace = Egami.Metadata.ColorSpaceHint.Linear;
        Random random = new(5);
        for (int y = 0; y < height; y++)
        {
            Span<RgbaF32> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                float t = x / (float)width;
                row[x] = y < height / 4
                    ? new RgbaF32(t * 1000, 0.5f, 0.5f, 1)
                    : new RgbaF32((float)((random.NextDouble() * 4) - 1), t * t, (float)Math.Pow(10, -6 + (6 * t)), (float)random.NextDouble());
            }
        }

        return raster;
    }

    private static bool BitwiseEqual(RgbaF32 a, RgbaF32 b) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<RgbaF32>(in a)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<RgbaF32>(in b)));
}
