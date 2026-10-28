using Egami.Formats;
using Egami.Formats.Extended.Pcx;
using Egami.Formats.Extended.Sgi;
using Egami.Formats.Extended.SunRaster;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests;

/// <summary>What every format of the package must do: register, detect, round trip, stream, recover and stay in the limits.</summary>
public class ExtendedFormatsTests
{
    public static TheoryData<string> Formats => TestImages.Formats;

    [Fact]
    public void RegistersEveryStableFormatOnce()
    {
        EgamiContext core = new();
        EgamiContext context = core.WithExtendedFormats();
        Assert.Equal(core.Formats, context.Formats.Take(core.Formats.Count));
        Assert.Equal(["PCX", "SGI", "Sun raster", "XBM", "XPM", "WBMP", "Farbfeld"], context.Formats.Skip(core.Formats.Count).Select(f => f.Name));

        // Registering again replaces in place.
        Assert.Equal(context.Formats.Count, context.WithExtendedFormats().WithPcx().Formats.Count);
        Assert.Throws<ArgumentNullException>(() => ExtendedContextExtensions.WithExtendedFormats(null!));
        Assert.Throws<ArgumentNullException>(() => ExtendedContextExtensions.WithXpm(null!));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void DetectsAndProbes(string format)
    {
        using Raster<Rgba8> colors = TestImages.Colors();
        byte[] file = TestImages.Save(colors, format);
        RasterInfo info = Raster.Probe(file, TestImages.Context);
        Assert.Equal(format, info.Format.Name);
        Assert.Equal((colors.Width, colors.Height), (info.Width, info.Height));
        using Raster natural = Raster.Load(file, TestImages.Context);
        Assert.Equal(info.Layout, natural.Layout);
        Assert.Equal(format, natural.Metadata.FormatName);

        // Without the package the file is not an image Egami knows.
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(file));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void RoundTripsColors(string format)
    {
        using Raster<Rgba8> colors = TestImages.Colors();
        using Raster<Rgba8> back = Raster.Load<Rgba8>(TestImages.Save(colors, format), TestImages.Context);
        for (int y = 0; y < colors.Height; y++)
        {
            for (int x = 0; x < colors.Width; x++)
            {
                Rgba8 p = colors.Row(y)[x];
                Rgba8 expected = format switch
                {
                    "Farbfeld" or "SGI" => p,
                    "PCX" or "Sun raster" => p with { A = 255 },
                    "XPM" => p.A < 128 ? default : p,
                    _ => Gray(p) >= 128 ? new Rgba8(255, 255, 255, 255) : new Rgba8(0, 0, 0, 255),
                };
                Assert.True(expected == back.Row(y)[x], $"{format} ({x}, {y}): {back.Row(y)[x]}, not {expected}.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void RoundTripsBitmapsExactly(string format)
    {
        using Raster<Gray8> bitmap = TestImages.Bitmap();
        using Raster<Gray8> back = Raster.Load<Gray8>(TestImages.Save(bitmap, format), TestImages.Context);
        TestImages.AssertEqual(bitmap, back);
    }

    [Theory]
    [InlineData("SGI")]
    [InlineData("Farbfeld")]
    public void KeepsSixteenBits(string format)
    {
        using Raster<Rgba16> wide = Raster.Create<Rgba16>(5, 3);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                wide.Row(y)[x] = new Rgba16((ushort)(x * 13001), (ushort)(y * 20011), 65535, (ushort)(1000 * x));
            }
        }

        using Raster<Rgba16> back = Raster.Load<Rgba16>(TestImages.Save(wide, format), TestImages.Context);
        TestImages.AssertEqual(wide, back);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void StreamsRowsAsLoadReadsThem(string format)
    {
        using Raster<Rgba8> colors = TestImages.Colors(40, 30);
        byte[] file = TestImages.Save(colors, format);
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(file, TestImages.Context);
        using RasterDecoder decoder = RasterDecoder.Open(new ForwardOnlyStream(file), TestImages.Context);
        using Raster<Rgba8> band = Raster.Create<Rgba8>(40, 7);
        int y = 0;
        while (decoder.ReadRows(band) is int rows and > 0)
        {
            for (int i = 0; i < rows; i++)
            {
                Assert.True(loaded.Row(y + i).SequenceEqual(band.Row(i)), $"Row {y + i} differs.");
            }

            y += rows;
        }

        Assert.Equal(30, y);
        Assert.False(decoder.NextFrame());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void TranscodesAndSavesTheSameBytes(string format)
    {
        using Raster<Rgba8> colors = TestImages.Colors(40, 30);
        byte[] png = Save(colors, PngFormat.Info);
        byte[] saved = TestImages.Save(colors, format);
        using MemoryStream transcoded = new();
        Raster.Transcode(new MemoryStream(png), transcoded, TestImages.Format(format), context: TestImages.Context);
        Assert.Equal(saved, transcoded.ToArray());

        // IFormatWriter.Write writes what Save writes.
        using MemoryStream written = new();
        TestImages.Format(format).Writer!.Write(colors, written, WriterOptions.Default);
        Assert.Equal(saved, written.ToArray());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void KeepsTheRowsOfATruncatedFile(string format)
    {
        using Raster<Gray8> bitmap = TestImages.Bitmap(40, 40);
        byte[] file = TestImages.Save(bitmap, format);
        // A gray PCX ends with its palette: cut into the rows before it.
        byte[] cut = file[..(format == "PCX" ? 128 + ((file.Length - 128 - 769) / 2) : (int)(file.Length * 0.7))];
        List<EgamiDiagnostic> seen = [];
        using Raster<Gray8> back = Raster.Load<Gray8>(cut, TestImages.Context with { Diagnostics = seen.Add });
        Assert.Contains(back.Metadata.Warnings, w => w.Id.EndsWith(".RowsMissing", StringComparison.Ordinal));
        Assert.Contains(seen, w => w.Id.EndsWith(".RowsMissing", StringComparison.Ordinal));
        int kept = format == "SGI" ? 39 : 0;
        Assert.True(bitmap.Row(kept).SequenceEqual(back.Row(kept)), $"Row {kept}, which the file still has, is lost.");
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(cut, TestImages.Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));

        // A streaming decode recovers the same way.
        using RasterDecoder decoder = RasterDecoder.Open(new MemoryStream(cut), TestImages.Context);
        using Raster<Gray8>? frame = decoder.ReadFrame<Gray8>();
        Assert.NotNull(frame);
        TestImages.AssertEqual(back, frame);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void SurvivesDamage(string format)
    {
        using Raster<Rgba8> colors = TestImages.Colors(17, 11);
        byte[] file = TestImages.Save(colors, format);
        Random random = new(format.Length * 7919);
        EgamiContext context = TestImages.Context with { Limits = new SafetyLimits { MaxPixels = 1 << 20, MaxTotalBytes = 64 << 20 } };
        for (int i = 0; i < 400; i++)
        {
            byte[] damaged = (byte[])file.Clone();
            int edits = 1 + random.Next(8);
            for (int e = 0; e < edits; e++)
            {
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
            }

            if (i % 5 == 0)
            {
                Array.Resize(ref damaged, random.Next(damaged.Length));
            }

            foreach (RecoveryMode recovery in (RecoveryMode[])[RecoveryMode.Lenient, RecoveryMode.Strict])
            {
                try
                {
                    Raster.Probe(damaged, TestImages.Format(format), context);
                    using Raster raster = Raster.Load(damaged, TestImages.Format(format), context, new ReaderOptions { Recovery = recovery });
                }
                catch (EgamiException)
                {
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void ChecksTheSizeBeforeAllocating(string format)
    {
        byte[] file = format switch
        {
            "Farbfeld" => [.. "farbfeld"u8, 0, 1, 0x86, 0xA0, 0, 1, 0x86, 0xA0],
            "WBMP" => [0, 0, 0x86, 0x8D, 0x20, 0x86, 0x8D, 0x20],
            "PCX" => Pcx(60000, 60000),
            "Sun raster" => [0x59, 0xA6, 0x6A, 0x95, 0, 0, 0xEA, 0x60, 0, 0, 0xEA, 0x60, 0, 0, 0, 24, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0],
            "SGI" => [.. (byte[])[0x01, 0xDA, 0, 1, 0, 3, 0xEA, 0x60, 0xEA, 0x60, 0, 3], .. new byte[500]],
            "XBM" => "#define a_width 60000\n#define a_height 60000\nstatic char a_bits[] = { 0x00 };\n"u8.ToArray(),
            _ => "/* XPM */\nstatic char *a[] = {\n\"60000 60000 1 1\",\n\". c #000000\",\n};\n"u8.ToArray(),
        };
        EgamiContext context = TestImages.Context with { Limits = new SafetyLimits { MaxPixels = 1_000_000 } };
        RasterInfo info = Raster.Probe(file, context);
        Assert.True(info.Width >= 60000 && info.Height >= 60000);
        Assert.Throws<LimitExceededException>(() => Raster.Load(file, context));
        Assert.Throws<LimitExceededException>(() => RasterDecoder.Open(new MemoryStream(file), context).NextFrame());
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void LoadsWithoutLimits(string format)
    {
        using Raster<Gray8> bitmap = TestImages.Bitmap();
        EgamiContext unlimited = TestImages.Context with
        {
            Limits = new SafetyLimits { MaxPixels = long.MaxValue, MaxWidth = int.MaxValue, MaxHeight = int.MaxValue, MaxTotalBytes = long.MaxValue },
        };
        using Raster<Gray8> back = Raster.Load<Gray8>(TestImages.Save(bitmap, format), unlimited);
        TestImages.AssertEqual(bitmap, back);
    }

    [Fact]
    public void ReportsWhatASaveLoses()
    {
        List<EgamiDiagnostic> seen = [];
        using Raster<Rgba16> translucent = Raster.Create<Rgba16>(4, 4, new EgamiContext { Diagnostics = seen.Add });
        translucent.Row(0)[0] = new Rgba16(1, 2, 3, 4);
        translucent.Save(new MemoryStream(), PcxFormat.Info);
        Assert.Equal(["Save.AlphaDropped", "Save.DepthReduced"], seen.Select(d => d.Id));
        seen.Clear();
        translucent.Save(new MemoryStream(), SgiFormat.Info);
        Assert.Empty(seen);
    }

    [Fact]
    public void CoreFormatsStillLoadFirst()
    {
        using Raster<Rgba8> colors = TestImages.Colors();
        byte[] png = Save(colors, PngFormat.Info);
        Assert.Equal("PNG", Raster.Probe(png, TestImages.Context).Format.Name);
        Assert.Equal("PNG", Raster.Probe(png, new EgamiContext().WithSunRaster().WithPcx()).Format.Name);
    }

    [Fact]
    public void WriterOptionsChooseRle()
    {
        using Raster<Rgb8> flat = Raster.Create<Rgb8>(64, 64);
        byte[] plainSun = TestImages.Save(flat, "Sun raster");
        byte[] rleSun = TestImages.Save(flat, "Sun raster", new SunRasterWriterOptions { Rle = true });
        Assert.True(rleSun.Length < plainSun.Length / 10);
        byte[] plainSgi = TestImages.Save(flat, "SGI", new SgiWriterOptions { Rle = false });
        byte[] rleSgi = TestImages.Save(flat, "SGI");
        Assert.Equal(512 + (64 * 64 * 3), plainSgi.Length);
        Assert.True(rleSgi.Length < plainSgi.Length / 4);
        using Raster<Rgb8> back = Raster.Load<Rgb8>(rleSun, TestImages.Context);
        TestImages.AssertEqual(flat, back);
        Assert.True(((SunRasterDetails)back.Metadata.FormatDetails!).Rle);
    }

    private static byte[] Pcx(int width, int height)
    {
        byte[] header = new byte[128];
        header[0] = 0x0A;
        header[1] = 5;
        header[2] = 1;
        header[3] = 8;
        BitConverter.TryWriteBytes(header.AsSpan(8), (ushort)(width - 1));
        BitConverter.TryWriteBytes(header.AsSpan(10), (ushort)(height - 1));
        header[65] = 3;
        BitConverter.TryWriteBytes(header.AsSpan(66), (ushort)width);
        return header;
    }

    private static byte[] Save(Raster raster, FormatInfo format)
    {
        using MemoryStream output = new();
        raster.Save(output, format);
        return output.ToArray();
    }

    private static int Gray(Rgba8 p)
    {
        Span<Gray8> gray = stackalloc Gray8[1];
        PixelConvert.Convert<Rgba8, Gray8>([p], gray);
        return gray[0].L;
    }
}
