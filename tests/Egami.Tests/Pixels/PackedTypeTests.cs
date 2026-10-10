using Egami.Pixels;

namespace Egami.Tests.Pixels;

/// <summary>Bgr565, Bgra4444 and Rgb10A2 (Phase 2.1.1 task 6).</summary>
public class PackedTypeTests
{
    [Fact]
    public void EverySixteenBitPackedValueRoundTripsThroughRgba8()
    {
        Bgr565[] b565 = new Bgr565[65536];
        Bgra4444[] b4444 = new Bgra4444[65536];
        for (int v = 0; v < 65536; v++)
        {
            b565[v] = new Bgr565((ushort)v);
            b4444[v] = new Bgra4444((ushort)v);
        }

        Rgba8[] rgba = new Rgba8[65536];
        Bgr565[] back565 = new Bgr565[65536];
        PixelConvert.Convert<Bgr565, Rgba8>(b565, rgba);
        PixelConvert.Convert<Rgba8, Bgr565>(rgba, back565);
        Assert.Equal(b565, back565);

        Bgra4444[] back4444 = new Bgra4444[65536];
        PixelConvert.Convert<Bgra4444, Rgba8>(b4444, rgba);
        PixelConvert.Convert<Rgba8, Bgra4444>(rgba, back4444);
        Assert.Equal(b4444, back4444);
    }

    [Fact]
    public void EveryTenBitLevelRoundTripsThroughRgba16()
    {
        Rgb10A2[] packed = new Rgb10A2[1024 * 4];
        for (int v = 0; v < 1024; v++)
        {
            for (int a = 0; a < 4; a++)
            {
                packed[(v * 4) + a] = new Rgb10A2(v, 1023 - v, v ^ 0x155, a);
            }
        }

        Rgba16[] wide = new Rgba16[packed.Length];
        Rgb10A2[] back = new Rgb10A2[packed.Length];
        PixelConvert.Convert<Rgb10A2, Rgba16>(packed, wide);
        PixelConvert.Convert<Rgba16, Rgb10A2>(wide, back);
        Assert.Equal(packed, back);
    }

    [Fact]
    public void WideningRoundsToNearest()
    {
        for (int v = 0; v < 32; v++)
        {
            Assert.Equal((int)Math.Round(v * 255 / 31.0), new Bgr565(v, 0, 0).ToRgba8().R);
        }

        for (int v = 0; v < 64; v++)
        {
            Assert.Equal((int)Math.Round(v * 255 / 63.0), new Bgr565(0, v, 0).ToRgba8().G);
        }

        for (int v = 0; v < 1024; v++)
        {
            Assert.Equal((int)Math.Round(v * 65535 / 1023.0), new Rgb10A2(v, 0, 0, 3).ToRgba16().R);
            Assert.Equal((int)Math.Round(v * 255 / 1023.0), new Rgb10A2(v, 0, 0, 3).ToRgba8().R);
        }

        Assert.Equal(new Rgba16(0, 0, 0, 43690), new Rgb10A2(0, 0, 0, 2).ToRgba16());
        Assert.Equal(new Rgba8(0, 0, 0, 170), new Rgb10A2(0, 0, 0, 2).ToRgba8());
    }

    [Fact]
    public void EveryRouteAgreesOnEveryValue()
    {
        // Converting through Rgba8, through Rgba16 and through scaled floats must give the same pixel.
        for (int v = 0; v < 65536; v++)
        {
            Bgr565 p = new((ushort)v);
            Assert.Equal(p.ToRgba8(), Rgba8.FromScaled(p.ToScaled()));
            Bgra4444 q = new((ushort)v);
            Assert.Equal(q.ToRgba8(), Rgba8.FromScaled(q.ToScaled()));
        }

        for (int v = 0; v < 256; v++)
        {
            Rgba8 color = new((byte)v, (byte)(255 - v), (byte)(v * 3), (byte)v);
            Assert.Equal(Bgr565.FromRgba8(color), Bgr565.FromScaled(color.ToScaled()));
            Assert.Equal(Bgra4444.FromRgba8(color), Bgra4444.FromScaled(color.ToScaled()));
            Assert.Equal(Rgb10A2.FromRgba8(color), Rgb10A2.FromScaled(color.ToScaled()));
        }
    }

    [Fact]
    public void FiveSixFiveKernelsMatchTheScalarPathForEveryColor()
    {
        if (PixelKernels.HardwareBits < 128)
        {
            return;
        }

        // Every 24-bit color, in both 8-bit orders, narrowed by the kernel and by scalar code.
        Rgba8[] colors = new Rgba8[1 << 16];
        Bgr565[] kernel = new Bgr565[colors.Length];
        Bgr565[] scalar = new Bgr565[colors.Length];
        for (int high = 0; high < 256; high++)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = new Rgba8((byte)high, (byte)(i >> 8), (byte)i, (byte)(i * 7));
            }

            PixelConvert.Convert<Rgba8, Bgr565>(colors, kernel);
            PixelConvert.ConvertScalar<Rgba8, Bgr565>(colors, scalar);
            Assert.Equal(scalar, kernel);
        }
    }

    [Fact]
    public void LevelConstructorsCheckTheirRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bgr565(32, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bgr565(0, 64, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bgra4444(0, 0, 0, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rgb10A2(0, 0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rgb10A2(0, 0, 0, 4));
        Assert.Equal(0xF81F, new Bgr565(31, 0, 31).Value);
        Assert.Equal("Bgr565(31, 0, 31)", new Bgr565(31, 0, 31).ToString());
        Assert.True(Bgr565.Layout.ChannelBits.SequenceEqual([5, 6, 5]));
    }
}
