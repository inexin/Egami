using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.Dpx;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Dpx;

/// <summary>DPX and Cineon writing: depths, byte orders, layouts, the header fields kept, and Cineon reading and log.</summary>
public sealed class DpxWriterTests
{
    public static TheoryData<int, bool> Depths()
    {
        TheoryData<int, bool> data = [];
        foreach (int bits in (int[])[8, 10, 12, 16])
        {
            data.Add(bits, true);
            data.Add(bits, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Depths))]
    public void RoundTripsEveryDepthAndLayout(int bits, bool big)
    {
        DpxWriterOptions options = new() { BitsPerSample = bits, BigEndian = big };
        RoundTrip(Wide<Rgb16>(13, 5), bits, options, PixelChannelOrder.Rgb);
        RoundTrip(Wide<Rgba16>(13, 5), bits, options, PixelChannelOrder.Rgba);
        RoundTrip(Wide<Gray16>(13, 5), bits, options, PixelChannelOrder.Gray);
    }

    [Fact]
    public void WritesTenBitBigEndianMethodAByDefault()
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(4, 2);
        raster.Row(0)[0] = new Rgb8(255, 0, 255);
        byte[] file = TestImages.Save(raster, "DPX");
        Assert.True(file.AsSpan(0, 4).SequenceEqual("SDPX"u8));
        Assert.Equal((uint)file.Length, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(16)));
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(808)));
        Assert.Equal((50, 10), (file[800], file[803]));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(804)));
        Assert.Equal((1023u << 22) | (1023u << 2), BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(2048)));
        Assert.Equal(2048 + (4 * 4 * 2), file.Length);
    }

    [Fact]
    public void KeepsTheHeaderFieldsOfADpxFile()
    {
        using Raster<Rgb16> raster = Wide<Rgb16>(6, 4);
        raster.Metadata.FormatDetails = new DpxDetails
        {
            BitsPerSample = 12,
            Transfer = DpxTransfer.Rec709,
            FileName = "shot_0101.dpx",
            Creator = "Scanner",
            Project = "Egami",
            Timecode = "10:20:30:12",
            UserBits = 7,
            FrameRate = 23.976f,
            FilmFormat = "Academy",
            FrameId = "A001",
            SlateInfo = "Take 3",
            FramePosition = 101,
        };
        byte[] file = TestImages.Save(raster, "DPX");
        using Raster back = Raster.Load(file, TestImages.Context);
        DpxDetails details = Assert.IsType<DpxDetails>(back.Metadata.FormatDetails);
        Assert.Equal((12, DpxTransfer.Rec709, DpxTransfer.Rec709), (details.BitsPerSample, details.Transfer, details.Colorimetric));
        Assert.Equal(("shot_0101.dpx", "Scanner", "Egami"), (details.FileName, details.Creator, details.Project));
        Assert.Equal(("10:20:30:12", 7u, 23.976f), (details.Timecode, details.UserBits, details.FrameRate));
        Assert.Equal(("Academy", "A001", "Take 3", 101u), (details.FilmFormat, details.FrameId, details.SlateInfo, details.FramePosition));

        // Options win over the details.
        byte[] eight = TestImages.Save(raster, "DPX", new DpxWriterOptions { BitsPerSample = 8, Transfer = DpxTransfer.Linear });
        Assert.Equal((8, (byte)DpxTransfer.Linear), (eight[803], eight[801]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestImages.Save(raster, "DPX", new DpxWriterOptions { BitsPerSample = 11 }));
    }

    [Fact]
    public void WritesAndReadsCineon()
    {
        using Raster<Rgb16> raster = Wide<Rgb16>(11, 4);
        byte[] file = TestImages.Save(raster, "Cineon");
        Assert.Equal(CineonReader.Magic, BinaryPrimitives.ReadUInt32BigEndian(file));
        Assert.Equal(2048 + (11 * 4 * 4), file.Length);
        using Raster<Rgb16> back = Raster.Load<Rgb16>(file, TestImages.Context);
        Assert.Equal("Cineon", back.Metadata.FormatName);
        AssertWithin(raster, back, 10);
        CineonDetails details = Assert.IsType<CineonDetails>(back.Metadata.FormatDetails);
        Assert.Equal((3, 10, 5), (details.Channels, details.BitsPerSample, details.Packing));

        // Linearized, code 685 is 1.
        using Raster<Rgb16> white = Raster.Create<Rgb16>(1, 1);
        white.Row(0)[0] = new Rgb16(685 * 65535 / 1023, 95 * 65535 / 1023, 0);
        using Raster<RgbF32> linear = Raster.Load<RgbF32>(TestImages.Save(white, "Cineon"), TestImages.Context, new DpxReaderOptions { Linearize = true });
        Assert.Equal(1, linear.Row(0)[0].R, 3);
        Assert.Equal(0, linear.Row(0)[0].G, 3);
        Assert.True(linear.Row(0)[0].B < 0);

        // Text and film fields go back out; a DPX written from a Cineon raster is printing density.
        raster.Metadata.FormatDetails = new CineonDetails { Label = "Reel 4", FrameRate = 24, FilmFormat = "Full aperture", FramePosition = 9 };
        using Raster kept = Raster.Load(TestImages.Save(raster, "Cineon"), TestImages.Context);
        CineonDetails again = Assert.IsType<CineonDetails>(kept.Metadata.FormatDetails);
        Assert.Equal(("Reel 4", 24f, "Full aperture", 9u), (again.Label, again.FrameRate, again.FilmFormat, again.FramePosition));
        byte[] dpx = TestImages.Save(kept, "DPX");
        Assert.Equal((byte)DpxTransfer.PrintingDensity, dpx[801]);
    }

    [Fact]
    public void ReadsCineonPackingsAndOrientation()
    {
        // Rewrite a Cineon file Egami wrote as gray, packed bit to bit (packing 0, from the top of each word), bottom-up.
        using Raster<Gray16> raster = Wide<Gray16>(5, 3);
        byte[] rgb = TestImages.Save(raster, "Cineon");
        byte[] file = rgb.AsSpan(0, 2048).ToArray();
        file[193] = 1;
        file[681] = 0;
        file[192] = 1;
        ulong acc = 0;
        int have = 0;
        List<byte> data = [];
        ushort[] codes = new ushort[15];
        for (int y = 2; y >= 0; y--)
        {
            for (int x = 0; x < 5; x++)
            {
                ushort code = (ushort)(((raster.Row(y)[x].L * 1023L) + 32767) / 65535);
                codes[(y * 5) + x] = code;
                acc = (acc << 10) | code;
                have += 10;
                if (have >= 32)
                {
                    have -= 32;
                    uint word = (uint)(acc >> have);
                    data.AddRange([(byte)(word >> 24), (byte)(word >> 16), (byte)(word >> 8), (byte)word]);
                }
            }

            if (have > 0)
            {
                uint word = (uint)(acc << (32 - have));
                data.AddRange([(byte)(word >> 24), (byte)(word >> 16), (byte)(word >> 8), (byte)word]);
                acc = 0;
                have = 0;
            }
        }

        file = [.. file, .. data];
        using Raster<Gray16> back = Raster.Load<Gray16>(file, TestImages.Context);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                Assert.Equal((ushort)(((codes[(y * 5) + x] * 65535L) + 511) / 1023), back.Row(y)[x].L);
            }
        }
    }

    [Fact]
    public void ReportsWhatTheFormatsLose()
    {
        using Raster<Rgba16> raster = Wide<Rgba16>(3, 2);
        Assert.Equal(PixelChannelOrder.Rgba, Raster.Probe(TestImages.Save(raster, "DPX"), TestImages.Context).Layout.ChannelOrder);
        Assert.Equal(PixelChannelOrder.Rgb, Raster.Probe(TestImages.Save(raster, "Cineon"), TestImages.Context).Layout.ChannelOrder);
    }

    private static void RoundTrip<TPixel>(Raster<TPixel> raster, int bits, DpxWriterOptions options, PixelChannelOrder order)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using (raster)
        {
            byte[] file = TestImages.Save(raster, "DPX", options);
            RasterInfo info = Raster.Probe(file, TestImages.Context);
            Assert.Equal(order, info.Layout.ChannelOrder);
            Assert.Equal(bits > 8 ? 16 : 8, info.Layout.BitsPerChannel);
            using Raster<TPixel> back = Raster.Load<TPixel>(file, TestImages.Context);
            AssertWithin(raster, back, bits);
            Assert.Equal(bits, ((DpxDetails)back.Metadata.FormatDetails!).BitsPerSample);
        }
    }

    /// <summary>Asserts every 16-bit sample came back as its nearest code value of <paramref name="bits"/> bits.</summary>
    private static void AssertWithin<TPixel>(Raster<TPixel> expected, Raster<TPixel> actual, int bits)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        long max = (1L << bits) - 1;
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<ushort> a = MemoryMarshal.Cast<TPixel, ushort>(expected.Row(y));
            ReadOnlySpan<ushort> b = MemoryMarshal.Cast<TPixel, ushort>(actual.Row(y));
            for (int i = 0; i < a.Length; i++)
            {
                long code = ((a[i] * max) + 32767) / 65535;
                Assert.Equal((ushort)(((code * 65535) + (max >> 1)) / max), b[i]);
            }
        }
    }

    private static Raster<TPixel> Wide<TPixel>(int width, int height)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<Rgba16> source = Raster.Create<Rgba16>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int v = (x * 4877) + (y * 13001);
                source.Row(y)[x] = new Rgba16((ushort)v, (ushort)(v * 3), (ushort)(65535 - v), (ushort)(x * 5000));
            }
        }

        return source.As<TPixel>();
    }
}
