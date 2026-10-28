using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Tests.Pixels;

/// <summary>Bgr565, Bgra4444 and Rgb10A2 (Phase 2.1.1 task 6), and Bgr555 and Bgra5551 (step 2.13 task 2).</summary>
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
    public void EveryFiveFiveFiveValueRoundTripsThroughRgba8AndBgra8()
    {
        // Bgra5551 keeps all 65,536 values; Bgr555 keeps the 32,768 with the unused top bit clear and clears it otherwise.
        Bgra5551[] b5551 = new Bgra5551[65536];
        Bgr555[] b555 = new Bgr555[65536];
        Bgr555[] expected555 = new Bgr555[65536];
        for (int v = 0; v < 65536; v++)
        {
            b5551[v] = new Bgra5551((ushort)v);
            b555[v] = new Bgr555((ushort)v);
            expected555[v] = new Bgr555((ushort)(v & 0x7FFF));
        }

        Rgba8[] rgba = new Rgba8[65536];
        Bgra8[] bgra = new Bgra8[65536];
        Bgra5551[] back5551 = new Bgra5551[65536];
        Bgr555[] back555 = new Bgr555[65536];
        PixelConvert.Convert<Bgra5551, Rgba8>(b5551, rgba);
        PixelConvert.Convert<Rgba8, Bgra5551>(rgba, back5551);
        Assert.Equal(b5551, back5551);
        PixelConvert.Convert<Bgra5551, Bgra8>(b5551, bgra);
        PixelConvert.Convert<Bgra8, Bgra5551>(bgra, back5551);
        Assert.Equal(b5551, back5551);

        PixelConvert.Convert<Bgr555, Rgba8>(b555, rgba);
        PixelConvert.Convert<Rgba8, Bgr555>(rgba, back555);
        Assert.Equal(expected555, back555);
        PixelConvert.Convert<Bgr555, Bgra8>(b555, bgra);
        PixelConvert.Convert<Bgra8, Bgr555>(bgra, back555);
        Assert.Equal(expected555, back555);
        Assert.All(bgra, p => Assert.Equal(255, p.A));
    }

    [Fact]
    public void FiveFiveFiveKernelsMatchTheScalarPath()
    {
        Bgra5551[] packed = new Bgra5551[65536];
        for (int v = 0; v < 65536; v++)
        {
            packed[v] = new Bgra5551((ushort)v);
        }

        Bgr555[] packed555 = MemoryMarshal.Cast<Bgra5551, Bgr555>(packed).ToArray();
        Assert.Equal(Scalar<Bgra5551, Rgba8>(packed), Convert<Bgra5551, Rgba8>(packed));
        Assert.Equal(Scalar<Bgra5551, Bgra8>(packed), Convert<Bgra5551, Bgra8>(packed));
        Assert.Equal(Scalar<Bgr555, Rgba8>(packed555), Convert<Bgr555, Rgba8>(packed555));
        Assert.Equal(Scalar<Bgr555, Bgra8>(packed555), Convert<Bgr555, Bgra8>(packed555));

        Rgba8[] colors = new Rgba8[1 << 16];
        for (int high = 0; high < 256; high += 3)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = new Rgba8((byte)high, (byte)(i >> 8), (byte)i, (byte)(i * 7));
            }

            Bgra8[] swapped = Convert<Rgba8, Bgra8>(colors);
            Assert.Equal(Scalar<Rgba8, Bgra5551>(colors), Convert<Rgba8, Bgra5551>(colors));
            Assert.Equal(Scalar<Rgba8, Bgr555>(colors), Convert<Rgba8, Bgr555>(colors));
            Assert.Equal(Scalar<Bgra8, Bgra5551>(swapped), Convert<Bgra8, Bgra5551>(swapped));
            Assert.Equal(Scalar<Bgra8, Bgr555>(swapped), Convert<Bgra8, Bgr555>(swapped));
        }

        static TTo[] Convert<TFrom, TTo>(TFrom[] source)
            where TFrom : unmanaged, IPixelType<TFrom>
            where TTo : unmanaged, IPixelType<TTo>
        {
            TTo[] result = new TTo[source.Length];
            PixelConvert.Convert<TFrom, TTo>(source, result);
            return result;
        }

        static TTo[] Scalar<TFrom, TTo>(TFrom[] source)
            where TFrom : unmanaged, IPixelType<TFrom>
            where TTo : unmanaged, IPixelType<TTo>
        {
            TTo[] result = new TTo[source.Length];
            PixelConvert.ConvertScalar<TFrom, TTo>(source, result);
            return result;
        }
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
            Bgr555 r = new((ushort)v);
            Assert.Equal(r.ToRgba8(), Rgba8.FromScaled(r.ToScaled()));
            Bgra5551 s = new((ushort)v);
            Assert.Equal(s.ToRgba8(), Rgba8.FromScaled(s.ToScaled()));
        }

        for (int v = 0; v < 256; v++)
        {
            Rgba8 color = new((byte)v, (byte)(255 - v), (byte)(v * 3), (byte)v);
            Assert.Equal(Bgr565.FromRgba8(color), Bgr565.FromScaled(color.ToScaled()));
            Assert.Equal(Bgra4444.FromRgba8(color), Bgra4444.FromScaled(color.ToScaled()));
            Assert.Equal(Bgr555.FromRgba8(color), Bgr555.FromScaled(color.ToScaled()));
            Assert.Equal(Bgra5551.FromRgba8(color), Bgra5551.FromScaled(color.ToScaled()));
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
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bgr555(0, 32, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bgra5551(0, 0, 0, 2));
        Assert.Equal(0x7C1F, new Bgr555(31, 0, 31).Value);
        Assert.Equal(0x83E0, new Bgra5551(0, 31, 0, 1).Value);
        Assert.Equal("Bgr555(31, 0, 31)", new Bgr555(31, 0, 31).ToString());
        Assert.Equal("Bgra5551(0, 31, 0, 1)", new Bgra5551(0, 31, 0, 1).ToString());
        Assert.True(Bgr555.Layout.ChannelBits.SequenceEqual([5, 5, 5]));
        Assert.True(Bgra5551.Layout.ChannelBits.SequenceEqual([5, 5, 5, 1]));
        Assert.Equal(new Rgba8(0, 0, 0, 0), new Bgra5551(0).ToRgba8());
        Assert.Equal(1, Bgra5551.FromRgba8(new Rgba8(0, 0, 0, 128)).A);
        Assert.Equal(0, Bgra5551.FromRgba8(new Rgba8(0, 0, 0, 127)).A);
    }
}
