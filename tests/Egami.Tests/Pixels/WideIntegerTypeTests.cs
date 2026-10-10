using Egami.Pixels;

namespace Egami.Tests.Pixels;

/// <summary>Rgb16 and GrayAlpha16 (Phase 2.1.1 task 3): exact round trips with their 8 and 16-bit neighbours.</summary>
public class WideIntegerTypeTests
{
    [Fact]
    public void EverySixteenBitValueRoundTripsThroughRgba16()
    {
        Rgb16[] rgb = new Rgb16[65536];
        GrayAlpha16[] grayAlpha = new GrayAlpha16[65536];
        for (int v = 0; v < 65536; v++)
        {
            rgb[v] = new Rgb16((ushort)v, (ushort)(65535 - v), (ushort)(v ^ 0x5A5A));
            grayAlpha[v] = new GrayAlpha16((ushort)v, (ushort)(65535 - v));
        }

        Rgba16[] wide = new Rgba16[65536];
        PixelConvert.Convert<Rgb16, Rgba16>(rgb, wide);
        Rgb16[] rgbBack = new Rgb16[65536];
        PixelConvert.Convert<Rgba16, Rgb16>(wide, rgbBack);
        Assert.Equal(rgb, rgbBack);
        Assert.All(wide, p => Assert.Equal(65535, p.A));

        PixelConvert.Convert<GrayAlpha16, Rgba16>(grayAlpha, wide);
        GrayAlpha16[] grayBack = new GrayAlpha16[65536];
        PixelConvert.Convert<Rgba16, GrayAlpha16>(wide, grayBack);
        Assert.Equal(grayAlpha, grayBack);
    }

    [Fact]
    public void EveryEightBitValueWidensAndNarrowsExactly()
    {
        for (int v = 0; v < 256; v++)
        {
            byte b = (byte)v;
            Assert.Equal(new Rgb16((ushort)(v * 257), (ushort)(v * 257), (ushort)(v * 257)), Rgb16.FromRgba8(new Rgba8(b, b, b, 9)));
            Assert.Equal(new Rgb8(b, (byte)(255 - v), 7), Convert<Rgb16, Rgb8>(Convert<Rgb8, Rgb16>(new Rgb8(b, (byte)(255 - v), 7))));
            Assert.Equal(new GrayAlpha8(b, (byte)(255 - v)), Convert<GrayAlpha16, GrayAlpha8>(Convert<GrayAlpha8, GrayAlpha16>(new GrayAlpha8(b, (byte)(255 - v)))));
        }
    }

    [Fact]
    public void GrayAlpha16TakesTheSameLuminanceAsGray16()
    {
        Random random = new(3);
        for (int i = 0; i < 2000; i++)
        {
            Rgba8 color = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            Assert.Equal(Gray16.FromRgba8(color).L, GrayAlpha16.FromRgba8(color).L);
            Rgba16 wide = Rgba16.FromRgba8(color);
            Assert.Equal(Convert<Rgba16, Gray16>(wide).L, Convert<Rgba16, GrayAlpha16>(wide).L);
            Assert.Equal(wide.A, Convert<Rgba16, GrayAlpha16>(wide).A);
        }
    }

    [Fact]
    public void LayoutsDescribeTheStructs()
    {
        Assert.Equal(new PixelLayoutInfo(PixelChannelOrder.Rgb, PixelChannelType.Unorm16, AlphaKind.None), Rgb16.Layout);
        Assert.Equal(new PixelLayoutInfo(PixelChannelOrder.GrayAlpha, PixelChannelType.Unorm16, AlphaKind.Straight), GrayAlpha16.Layout);
        Assert.Equal("Rgb16(1, 2, 3)", new Rgb16(1, 2, 3).ToString());
        Assert.Equal("GrayAlpha16(4, 5)", new GrayAlpha16(4, 5).ToString());
    }

    private static TTo Convert<TFrom, TTo>(TFrom pixel)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        TTo[] result = new TTo[1];
        PixelConvert.Convert<TFrom, TTo>([pixel], result);
        return result[0];
    }
}
