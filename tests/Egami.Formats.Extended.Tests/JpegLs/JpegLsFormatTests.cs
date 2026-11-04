using Egami.Formats.Extended.JpegLs;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.JpegLs;

/// <summary>JPEG-LS through Egami's API: options, details, palettes, depths, restarts and damage.</summary>
public sealed class JpegLsFormatTests
{
    public static TheoryData<JpegLsInterleave, JpegLsColorTransform, int> Codings
    {
        get
        {
            TheoryData<JpegLsInterleave, JpegLsColorTransform, int> data = [];
            foreach (JpegLsInterleave interleave in Enum.GetValues<JpegLsInterleave>())
            {
                foreach (JpegLsColorTransform transform in Enum.GetValues<JpegLsColorTransform>())
                {
                    data.Add(interleave, transform, 0);
                }

                data.Add(interleave, JpegLsColorTransform.None, 3);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Codings))]
    public void RoundTripsEveryCoding(JpegLsInterleave interleave, JpegLsColorTransform transform, int near)
    {
        using Raster<Rgb8> photo = Photo<Rgb8>(37, 23);
        JpegLsWriterOptions options = new() { Interleave = interleave, ColorTransform = transform, Near = near, RestartInterval = 5 };
        byte[] file = TestImages.Save(photo, "JPEG-LS", options);
        using Raster<Rgb8> back = Raster.Load<Rgb8>(file, TestImages.Context);
        Assert.Empty(back.Metadata.Warnings);
        JpegLsDetails details = Assert.IsType<JpegLsDetails>(back.Metadata.FormatDetails);
        Assert.Equal(near, details.Near);
        Assert.Equal(interleave, details.Interleave);
        Assert.Equal(interleave == JpegLsInterleave.None || near > 0 ? JpegLsColorTransform.None : transform, details.ColorTransform);
        Assert.Equal(8, details.BitsPerSample);
        Assert.Equal(3, details.Components);
        for (int y = 0; y < photo.Height; y++)
        {
            for (int x = 0; x < photo.Width; x++)
            {
                Rgb8 a = photo.Row(y)[x], b = back.Row(y)[x];
                int error = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                Assert.True(error <= near, $"({x}, {y}): {b}, not {a}.");
            }
        }
    }

    [Fact]
    public void KeepsTheDetailsWhenSavedAgain()
    {
        using Raster<Gray16> gray = Photo<Gray16>(20, 10);
        byte[] file = TestImages.Save(gray, "JPEG-LS", new JpegLsWriterOptions { BitsPerSample = 12, Near = 2, Interleave = JpegLsInterleave.None });
        using Raster<Gray16> back = Raster.Load<Gray16>(file, TestImages.Context);
        JpegLsDetails details = Assert.IsType<JpegLsDetails>(back.Metadata.FormatDetails);
        Assert.Equal((12, 2, 1), (details.BitsPerSample, details.Near, details.Components));

        // Saved with default options, the details give the depth and NEAR again.
        byte[] again = TestImages.Save(back, "JPEG-LS");
        using Raster<Gray16> twice = Raster.Load<Gray16>(again, TestImages.Context);
        JpegLsDetails kept = Assert.IsType<JpegLsDetails>(twice.Metadata.FormatDetails);
        Assert.Equal((12, 2), (kept.BitsPerSample, kept.Near));
        JpegLsDetails copy = Assert.IsType<JpegLsDetails>(details.Copy());
        Assert.Equal((details.Near, details.BitsPerSample, details.Interleave), (copy.Near, copy.BitsPerSample, copy.Interleave));
    }

    [Fact]
    public void KeepsNearFromTheDetailsWithinTheDepth()
    {
        // A 2-bit file allows NEAR 1 at most; details that say 3 are clamped, options that say 3 are refused.
        using Raster<Gray8> small = Photo<Gray8>(6, 4);
        small.Metadata.FormatDetails = new JpegLsDetails { Near = 3, BitsPerSample = 2 };
        using Raster<Gray8> back = Raster.Load<Gray8>(TestImages.Save(small, "JPEG-LS"), TestImages.Context);
        Assert.Equal((2, 1), (Assert.IsType<JpegLsDetails>(back.Metadata.FormatDetails).BitsPerSample, Assert.IsType<JpegLsDetails>(back.Metadata.FormatDetails).Near));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestImages.Save(small, "JPEG-LS", new JpegLsWriterOptions { Near = 3 }));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(16)]
    public void ScalesOtherDepthsToTheFullRange(int bits)
    {
        int max = (1 << bits) - 1;
        ushort[] samples = [.. Enumerable.Range(0, 64).Select(i => (ushort)(i * max / 63))];
        byte[] file = JpegLsTesting.Encode(samples, 1, 8, bits);
        RasterInfo info = Raster.Probe(file, TestImages.Context);
        Assert.Equal(bits > 8 ? 16 : 8, info.Layout.BitsPerChannel);
        using Raster<Gray16> back = Raster.Load<Gray16>(file, TestImages.Context);
        Assert.Equal(0, back.Row(0)[0].L);
        Assert.Equal(65535, back.Row(7)[7].L);
        Assert.Equal(samples, JpegLsTesting.Interleaved(file));
    }

    [Fact]
    public void ReadsGrayAndAlphaAndSixteenBitColour()
    {
        using Raster<GrayAlpha8> translucent = Photo<GrayAlpha8>(9, 7);
        using Raster<GrayAlpha8> back = Raster.Load<GrayAlpha8>(TestImages.Save(translucent, "JPEG-LS"), TestImages.Context);
        TestImages.AssertEqual(translucent, back);

        using Raster<Rgb16> wide = Photo<Rgb16>(9, 7);
        byte[] file = TestImages.Save(wide, "JPEG-LS", new JpegLsWriterOptions { ColorTransform = JpegLsColorTransform.Hp2 });
        Assert.Equal(Rgb16.Layout, Raster.Probe(file, TestImages.Context).Layout);
        using Raster<Rgb16> wideBack = Raster.Load<Rgb16>(file, TestImages.Context);
        TestImages.AssertEqual(wide, wideBack);
    }

    [Theory]
    [InlineData(JpegLsInterleave.Line, true)]
    [InlineData(JpegLsInterleave.Sample, true)]
    [InlineData(JpegLsInterleave.None, false)]
    public void StreamsOneScanFiles(JpegLsInterleave interleave, bool streamed)
    {
        using Raster<Rgb8> photo = Photo<Rgb8>(16, 16);
        byte[] file = TestImages.Save(photo, "JPEG-LS", new JpegLsWriterOptions { Interleave = interleave });
        Assert.Equal(streamed ? RasterAccess.Streamed : RasterAccess.Buffered, Raster.Probe(file, TestImages.Context).Access);
    }

    [Fact]
    public void ShowsAPaletteAsItsColours()
    {
        ushort[] indices = [.. Enumerable.Range(0, 24).Select(i => (ushort)(i % 4))];
        byte[] plain = JpegLsTesting.Encode(indices, 1, 6, 8);
        byte[] colours = [255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30];
        byte[] file = WithTable(plain, colours, 3);
        using Raster<Rgb8> back = Raster.Load<Rgb8>(file, TestImages.Context);
        Assert.Empty(back.Metadata.Warnings);
        Assert.True(Assert.IsType<JpegLsDetails>(back.Metadata.FormatDetails).Palettized);
        Assert.Equal(new Rgb8(255, 0, 0), back.Row(0)[0]);
        Assert.Equal(new Rgb8(10, 20, 30), back.Row(0)[3]);
        Assert.Equal(new Rgb8(0, 255, 0), back.Row(0)[5]);

        // An index past the table is black, with one warning; strict reading refuses it.
        byte[] short3 = WithTable(plain, colours[..9], 3);
        using Raster<Rgb8> partial = Raster.Load<Rgb8>(short3, TestImages.Context);
        Assert.Single(partial.Metadata.Warnings, w => w.Id == "JpegLs.IndexMissing");
        Assert.Equal(new Rgb8(0, 0, 0), partial.Row(0)[3]);
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(short3, TestImages.Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void RecoversAtTheNextRestartMarker()
    {
        using Raster<Gray8> photo = Photo<Gray8>(32, 32);
        byte[] file = TestImages.Save(photo, "JPEG-LS", new JpegLsWriterOptions { RestartInterval = 8 });
        int sos = Find(file, 0xDA);
        int damage = sos + 12 + 20;
        for (int i = 0; i < 6; i++)
        {
            // Clear the top bit so no marker is made.
            file[damage + i] = (byte)((file[damage + i] ^ 0x35) & 0x7F);
        }

        // JPEG-LS has no check sums: the damaged interval decodes to something, and the next starts afresh.
        using Raster<Gray8> back = Raster.Load<Gray8>(file, TestImages.Context);
        for (int y = 8; y < 32; y++)
        {
            Assert.True(photo.Row(y).SequenceEqual(back.Row(y)), $"Row {y}, after the next marker, differs.");
        }
    }

    [Fact]
    public void ReadsLargeSizesFromAnLseSegment()
    {
        // LSE type 4 gives sizes above 65535; the frame header then says 0.
        byte[] file = [0xFF, 0xD8, 0xFF, 0xF7, 0, 11, 8, 0, 0, 0, 0, 1, 1, 0x11, 0, 0xFF, 0xF8, 0, 12, 4, 4, 0, 1, 0, 0, 0, 1, 0, 0, 0xFF, 0xDA, 0, 8, 1, 1, 0, 0, 0, 0, 0, 0];
        RasterInfo info = Raster.Probe(file, TestImages.Context);
        Assert.Equal((65536, 65536), (info.Width, info.Height));
        Assert.Throws<LimitExceededException>(() => Raster.Load(file, TestImages.Context));
    }

    [Fact]
    public void ChecksTheSampleWritersArguments()
    {
        ushort[] samples = new ushort[12];
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegLsTesting.Encode(samples, 5, 2, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegLsTesting.Encode(samples, 1, 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegLsTesting.Encode(samples, 1, 2, 17));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpegLsTesting.Encode(samples, 1, 2, 2, new JpegLsWriterOptions { Near = 2 }));
        Assert.Throws<ArgumentException>(() => JpegLsTesting.Encode(samples, 1, 5, 8));
        Assert.Throws<ArgumentException>(() => JpegLsTesting.Encode([300, 1], 1, 2, 8));
        Assert.Throws<ArgumentException>(() => JpegLsTesting.Encode([], 1, 2, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestImages.Save(Photo<Gray8>(2, 2), "JPEG-LS", new JpegLsWriterOptions { BitsPerSample = 17 }));
    }

    [Fact]
    public void WritesFourComponentsAndRestartsWithTheSampleWriter()
    {
        ushort[] samples = [.. Enumerable.Range(0, 4 * 13 * 9).Select(i => (ushort)((i * 7919) % 4096))];
        foreach (JpegLsInterleave interleave in Enum.GetValues<JpegLsInterleave>())
        {
            byte[] file = JpegLsTesting.Encode(samples, 4, 13, 12, new JpegLsWriterOptions { Interleave = interleave, RestartInterval = 2 });
            Assert.Equal(samples, JpegLsTesting.Interleaved(file));
        }
    }

    private static Raster<TPixel> Photo<TPixel>(int width, int height)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<Rgba16> source = Raster.Create<Rgba16>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int v = (x * 2311) + (y * 977) + ((x * y) % 13 * 4000);
                source.Row(y)[x] = new Rgba16((ushort)(v * 3), (ushort)(v * 5 + y), (ushort)(x * 1700), (ushort)(65535 - (y * 3000)));
            }
        }

        return source.As<TPixel>();
    }

    private static int Find(byte[] file, byte marker)
    {
        for (int i = 0; i + 1 < file.Length; i++)
        {
            if (file[i] == 0xFF && file[i + 1] == marker)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Marker {marker:X2} is not in the file.");
    }

    /// <summary>Puts mapping table 1 with <paramref name="entryBytes"/>-byte entries before the scan and points the scan at it.</summary>
    private static byte[] WithTable(byte[] file, byte[] entries, int entryBytes)
    {
        int sos = Find(file, 0xDA);
        byte[] lse = [0xFF, 0xF8, .. BigEndian(5 + entries.Length), 2, 1, (byte)entryBytes, .. entries];
        byte[] result = [.. file[..sos], .. lse, .. file[sos..]];
        result[sos + lse.Length + 6] = 1;
        return result;
    }

    private static byte[] BigEndian(int value) => [(byte)(value >> 8), (byte)value];
}
