using Egami.Pixels;

namespace Egami.Tests.Pixels;

/// <summary>The 2.1.1 layout plumbing: new enum values, packed layouts, and the conversion routes.</summary>
public class PixelLayoutTests
{
    public static TheoryData<string, int, int, int, int> OneZeroLayouts() => new()
    {
        { nameof(Rgba8), 4, 8, 32, 4 },
        { nameof(Bgra8), 4, 8, 32, 4 },
        { nameof(Rgb8), 3, 8, 24, 3 },
        { nameof(Bgr8), 3, 8, 24, 3 },
        { nameof(Gray8), 1, 8, 8, 1 },
        { nameof(Gray16), 1, 16, 16, 2 },
        { nameof(GrayAlpha8), 2, 8, 16, 2 },
        { nameof(Rgba16), 4, 16, 64, 8 },
        { nameof(RgbaF32), 4, 32, 128, 16 },
    };

    [Theory]
    [MemberData(nameof(OneZeroLayouts))]
    public void OneZeroTypesKeepTheirSizes(string type, int channels, int bitsPerChannel, int bitsPerPixel, int bytesPerPixel)
    {
        PixelLayoutInfo layout = LayoutOf(type);
        Assert.Equal(channels, layout.ChannelCount);
        Assert.Equal(bitsPerChannel, layout.BitsPerChannel);
        Assert.Equal(bitsPerPixel, layout.BitsPerPixel);
        Assert.Equal(bytesPerPixel, layout.BytesPerPixel);
        Assert.True(layout.ChannelBits.IsEmpty);
    }

    [Fact]
    public void NewOrdersCountTheirChannels()
    {
        Assert.Equal(4, new PixelLayoutInfo(PixelChannelOrder.Argb, PixelChannelType.Unorm8, AlphaKind.Straight).ChannelCount);
        Assert.Equal(4, new PixelLayoutInfo(PixelChannelOrder.Abgr, PixelChannelType.Unorm8, AlphaKind.Straight).ChannelCount);
        Assert.Equal(4, new PixelLayoutInfo(PixelChannelOrder.Cmyk, PixelChannelType.Unorm16, AlphaKind.None).BytesPerPixel / 2);
        Assert.Equal(1, new PixelLayoutInfo(PixelChannelOrder.Alpha, PixelChannelType.Unorm8, AlphaKind.Straight).ChannelCount);
        Assert.Equal(8, new PixelLayoutInfo(PixelChannelOrder.Rgba, PixelChannelType.Float16, AlphaKind.Straight).BytesPerPixel);
        Assert.True(new PixelLayoutInfo(PixelChannelOrder.Rgba, PixelChannelType.Unorm8, AlphaKind.Premultiplied).HasAlpha);
    }

    [Fact]
    public void PackedLayoutsCarryTheirChannelWidths()
    {
        PixelLayoutInfo bgr565 = new(PixelChannelOrder.Bgr, PixelChannelType.Packed, AlphaKind.None, 16, 5, 6, 5);
        Assert.Equal(3, bgr565.ChannelCount);
        Assert.Equal(0, bgr565.BitsPerChannel);
        Assert.Equal(16, bgr565.BitsPerPixel);
        Assert.Equal(2, bgr565.BytesPerPixel);
        Assert.Equal([5, 6, 5], bgr565.ChannelBits.ToArray());

        PixelLayoutInfo bgra4444 = new(PixelChannelOrder.Bgra, PixelChannelType.Packed, AlphaKind.Straight, 16, 4, 4, 4, 4);
        Assert.Equal(4, bgra4444.BitsPerChannel);

        PixelLayoutInfo other = new(PixelChannelOrder.Other, PixelChannelType.Packed, AlphaKind.None, 24, 8, 8, 8);
        Assert.Equal(3, other.ChannelCount);
        Assert.Equal(3, other.BytesPerPixel);
    }

    [Fact]
    public void PackedLayoutsCompareByValue()
    {
        PixelLayoutInfo a = new(PixelChannelOrder.Bgr, PixelChannelType.Packed, AlphaKind.None, 16, 5, 6, 5);
        PixelLayoutInfo b = new(PixelChannelOrder.Bgr, PixelChannelType.Packed, AlphaKind.None, 16, 5, 6, 5);
        PixelLayoutInfo c = new(PixelChannelOrder.Bgr, PixelChannelType.Packed, AlphaKind.None, 16, 5, 5, 5);
        PixelLayoutInfo plain = new(PixelChannelOrder.Bgr, PixelChannelType.Packed, AlphaKind.None);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, plain);
        Assert.Equal(Rgba8.Layout, new PixelLayoutInfo(PixelChannelOrder.Rgba, PixelChannelType.Unorm8, AlphaKind.Straight));
        Assert.Equal("PixelLayoutInfo { ChannelOrder = Bgr, ChannelType = Packed, Alpha = None, BitsPerPixel = 16, ChannelBits = [5, 6, 5] }", a.ToString());
        Assert.Equal("PixelLayoutInfo { ChannelOrder = Rgba, ChannelType = Unorm8, Alpha = Straight }", Rgba8.Layout.ToString());
    }

    [Fact]
    public void PackedLayoutsRejectBadSizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelLayoutInfo(PixelChannelOrder.Bgr, PixelChannelType.Packed, AlphaKind.None, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelLayoutInfo(PixelChannelOrder.Bgr, PixelChannelType.Packed, AlphaKind.None, 16, 5, -1, 5));
        Assert.Throws<ArgumentException>(() => new PixelLayoutInfo(PixelChannelOrder.Bgr, PixelChannelType.Packed, AlphaKind.None, 16, 8, 8, 8));
    }

    [Fact]
    public void EachPairTakesTheExactRoute()
    {
        PixelLayoutInfo bgr565 = new(PixelChannelOrder.Bgr, PixelChannelType.Packed, AlphaKind.None, 16, 5, 6, 5);
        PixelLayoutInfo rgb10a2 = new(PixelChannelOrder.Rgba, PixelChannelType.Packed, AlphaKind.Straight, 32, 10, 10, 10, 2);
        PixelLayoutInfo half = new(PixelChannelOrder.Rgba, PixelChannelType.Float16, AlphaKind.Straight);
        PixelLayoutInfo other = new(PixelChannelOrder.Other, PixelChannelType.Other, AlphaKind.None);

        Assert.Equal(ConvertRoute.Rgba8, PixelConvert.Route(Rgba8.Layout, Rgba16.Layout));
        Assert.Equal(ConvertRoute.Rgba8, PixelConvert.Route(bgr565, Rgba16.Layout));
        Assert.Equal(ConvertRoute.Rgba8, PixelConvert.Route(other, Rgba8.Layout));
        Assert.Equal(ConvertRoute.Rgba16, PixelConvert.Route(Gray16.Layout, Rgba16.Layout));
        Assert.Equal(ConvertRoute.Rgba16, PixelConvert.Route(rgb10a2, Rgba16.Layout));
        Assert.Equal(ConvertRoute.Scaled, PixelConvert.Route(half, Rgba8.Layout));
        Assert.Equal(ConvertRoute.Scaled, PixelConvert.Route(RgbaF32.Layout, Rgba16.Layout));
        Assert.Equal(ConvertRoute.Scaled, PixelConvert.Route(other, Rgba16.Layout));
        Assert.Equal(ConvertRoute.Scaled, PixelConvert.Route(other, other));
    }

    [Fact]
    public void SixteenBitConversionsMatchTheScaledRouteOfOneZero()
    {
        // 1.0 converted Gray16 and Rgba16 through scaled values; the Rgba16 route must give the same pixels.
        Random random = new(7);
        Rgba16[] colors = new Rgba16[4096];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = new Rgba16((ushort)random.Next(65536), (ushort)random.Next(65536), (ushort)random.Next(65536), (ushort)random.Next(65536));
        }

        Gray16[] gray = new Gray16[colors.Length];
        PixelConvert.Convert<Rgba16, Gray16>(colors, gray);
        Rgba16[] back = new Rgba16[colors.Length];
        PixelConvert.Convert<Gray16, Rgba16>(gray, back);
        for (int i = 0; i < colors.Length; i++)
        {
            Assert.Equal(Gray16.FromScaled(colors[i].ToScaled()), gray[i]);
            Assert.Equal(Rgba16.FromScaled(gray[i].ToScaled()), back[i]);
        }
    }

    [Fact]
    public void DefaultSixteenBitMembersGoThroughScaledValues()
    {
        Rgba8 color = new(10, 200, 30, 128);
        Assert.Equal(Rgba16.FromScaled(color.ToScaled()), Convert16(color));
        Assert.Equal(Rgba8.FromScaled(new Rgba16(2570, 51400, 7710, 32896).ToScaled()), From16<Rgba8>(new Rgba16(2570, 51400, 7710, 32896)));
    }

    private static Rgba16 Convert16<T>(T pixel)
        where T : unmanaged, IPixelType<T>
    {
        return pixel.ToRgba16();
    }

    private static T From16<T>(Rgba16 value)
        where T : unmanaged, IPixelType<T>
    {
        return T.FromRgba16(value);
    }

    private static PixelLayoutInfo LayoutOf(string type) => type switch
    {
        nameof(Rgba8) => Rgba8.Layout,
        nameof(Bgra8) => Bgra8.Layout,
        nameof(Rgb8) => Rgb8.Layout,
        nameof(Bgr8) => Bgr8.Layout,
        nameof(Gray8) => Gray8.Layout,
        nameof(Gray16) => Gray16.Layout,
        nameof(GrayAlpha8) => GrayAlpha8.Layout,
        nameof(Rgba16) => Rgba16.Layout,
        _ => RgbaF32.Layout,
    };
}
