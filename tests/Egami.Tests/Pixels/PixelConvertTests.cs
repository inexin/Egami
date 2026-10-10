using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;

using Egami.Pixels;

namespace Egami.Tests.Pixels;

public class PixelConvertTests
{
    // Colors with no channel near a rounding boundary in any type, so the expected value of every conversion
    // is unambiguous. Gray sources use the luma of each color, computed by hand from IEC 61966-2-1 and Rec. 709:
    // red 127, (0,128,255) 131, (10,200,30) 172.
    private static readonly (byte R, byte G, byte B, byte A, byte Luma)[] Palette =
    [
        (0, 0, 0, 255, 0),
        (255, 255, 255, 255, 255),
        (64, 64, 64, 0, 64),
        (255, 0, 0, 255, 127),
        (0, 128, 255, 200, 131),
        (10, 200, 30, 128, 172),
    ];

    private static readonly Type[] PixelTypes =
    [
        typeof(Rgba8), typeof(Bgra8), typeof(Rgb8), typeof(Bgr8), typeof(Gray8),
        typeof(Gray16), typeof(GrayAlpha8), typeof(Rgba16), typeof(RgbaF32), typeof(Rgb16), typeof(GrayAlpha16),
    ];

    public static TheoryData<string, string> AllPairs()
    {
        TheoryData<string, string> data = [];
        foreach (Type from in PixelTypes)
        {
            foreach (Type to in PixelTypes)
            {
                data.Add(from.Name, to.Name);
            }
        }

        return data;
    }

    [Fact]
    public void AllPairsCoversEveryCombination()
    {
        Assert.Equal(121, AllPairs().Count);
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void ConvertsEveryPairToTheExpectedValues(string from, string to)
    {
        MethodInfo check = typeof(PixelConvertTests).GetMethod(nameof(CheckPair), BindingFlags.NonPublic | BindingFlags.Static)!;
        check.MakeGenericMethod(PixelType(from), PixelType(to)).Invoke(null, null);
    }

    [Fact]
    public void LayoutMatchesStructSize()
    {
        Assert.Equal(4, Size<Rgba8>());
        Assert.Equal(4, Size<Bgra8>());
        Assert.Equal(3, Size<Rgb8>());
        Assert.Equal(3, Size<Bgr8>());
        Assert.Equal(1, Size<Gray8>());
        Assert.Equal(2, Size<Gray16>());
        Assert.Equal(2, Size<GrayAlpha8>());
        Assert.Equal(8, Size<Rgba16>());
        Assert.Equal(16, Size<RgbaF32>());
        Assert.Equal(6, Size<Rgb16>());
        Assert.Equal(4, Size<GrayAlpha16>());

        static int Size<T>()
            where T : unmanaged, IPixelType<T>
        {
            Assert.Equal(Unsafe.SizeOf<T>(), T.Layout.BytesPerPixel);
            return Unsafe.SizeOf<T>();
        }
    }

    [Fact]
    public void GrayLumaMatchesHandComputedValues()
    {
        // Hand-computed: Y = 0.2126 R + 0.7152 G + 0.0722 B on linear values, then sRGB-encoded.
        Assert.Equal(127, Gray8.FromRgba8(Rgba8.Red).L);
        Assert.Equal(220, Gray8.FromRgba8(Rgba8.Lime).L);
        Assert.Equal(76, Gray8.FromRgba8(Rgba8.Blue).L);
        Assert.Equal(32665, Gray16.FromRgba8(Rgba8.Red).L);
        Assert.Equal(56523, Gray16.FromRgba8(Rgba8.Lime).L);
        Assert.Equal(19522, Gray16.FromRgba8(Rgba8.Blue).L);
        Assert.Equal(new GrayAlpha8(131, 7), GrayAlpha8.FromRgba8(new Rgba8(0, 128, 255, 7)));
    }

    [Fact]
    public void BitDepthScalingIsExact()
    {
        for (int v = 0; v < 256; v++)
        {
            Assert.Equal((ushort)(v * 257), Rgba16.FromRgba8(new Rgba8((byte)v, 0, 0, 0)).R);
        }

        // 16 to 8 bits rounds to nearest: v * 255 / 65535, computed exactly with integers.
        for (int v = 0; v < 65536; v++)
        {
            int expected = ((v * 255 * 2) + 65535) / (65535 * 2);
            Assert.Equal(expected, new Rgba16((ushort)v, 0, 0, 0).ToRgba8().R);
        }
    }

    [Fact]
    public void EightBitValuesSurviveFloatRoundTrips()
    {
        for (int v = 0; v < 256; v++)
        {
            Rgba8 color = new((byte)v, (byte)v, (byte)v, (byte)v);
            Assert.Equal(color, Rgba8.FromScaled(color.ToScaled()));
            Assert.Equal(color, RgbaF32.FromRgba8(color).ToRgba8());
            Assert.Equal(new Gray8((byte)v), Gray8.FromScaled(color.ToScaled()));
        }
    }

    [Fact]
    public void SixteenBitValuesSurviveFloatRoundTrips()
    {
        for (int v = 0; v < 65536; v++)
        {
            Rgba16 color = new((ushort)v, (ushort)v, (ushort)v, (ushort)v);
            Assert.Equal(color, Rgba16.FromScaled(color.ToScaled()));
            Assert.Equal(new Gray16((ushort)v), Gray16.FromScaled(color.ToScaled()));
        }
    }

    [Fact]
    public void FromScaledClampsAndMapsNaNToZero()
    {
        Rgba8 clamped = Rgba8.FromScaled(new Vector4(-1f, 2f, float.NaN, float.PositiveInfinity));
        Assert.Equal(new Rgba8(0, 255, 0, 255), clamped);

        Rgba16 clamped16 = Rgba16.FromScaled(new Vector4(float.NegativeInfinity, 1.5f, float.NaN, 0.5f));
        Assert.Equal(new Rgba16(0, 65535, 0, 32768), clamped16);
    }

    [Fact]
    public void ConvertRejectsShortDestination()
    {
        Rgba8[] source = new Rgba8[3];
        Bgra8[] destination = new Bgra8[2];
        Assert.Throws<ArgumentException>(() => PixelConvert.Convert<Rgba8, Bgra8>(source, destination));
    }

    [Fact]
    public void ConvertLeavesTheRestOfALongerDestinationAlone()
    {
        Rgba8[] source = [Rgba8.Red];
        Bgra8[] destination = [default, new Bgra8(1, 2, 3, 4)];
        PixelConvert.Convert<Rgba8, Bgra8>(source, destination);
        Assert.Equal(new Bgra8(0, 0, 255, 255), destination[0]);
        Assert.Equal(new Bgra8(1, 2, 3, 4), destination[1]);
    }

    [Fact]
    public void ConvertHandlesEmptySpans()
    {
        PixelConvert.Convert<Rgba8, Gray16>([], []);
    }

    private static void CheckPair<TFrom, TTo>()
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        TFrom[] source = [.. Palette.Select(c => Make<TFrom>(c))];
        TTo[] actual = new TTo[source.Length];
        PixelConvert.Convert<TFrom, TTo>(source, actual);

        for (int i = 0; i < source.Length; i++)
        {
            double[] logical = Channels(source[i]);
            double[] expected = Expected<TTo>(logical);
            double[] got = Channels(actual[i]);
            double tolerance = typeof(TTo) == typeof(RgbaF32) ? 1e-6 : 0;
            for (int c = 0; c < 4; c++)
            {
                Assert.True(
                    Math.Abs(expected[c] - got[c]) <= tolerance,
                    $"{typeof(TFrom).Name} {source[i]} -> {typeof(TTo).Name}: expected channel {c} = {expected[c]}, got {actual[i]}");
            }
        }
    }

    /// <summary>The value a conversion to <typeparamref name="T"/> must produce, worked out with exact formulas in doubles.</summary>
    private static double[] Expected<T>(double[] rgba)
    {
        bool gray = typeof(T) == typeof(Gray8) || typeof(T) == typeof(Gray16) || typeof(T) == typeof(GrayAlpha8) || typeof(T) == typeof(GrayAlpha16);
        bool alpha = T_HasAlpha<T>();
        double max = typeof(T) == typeof(Rgba16) || typeof(T) == typeof(Gray16) || typeof(T) == typeof(Rgb16) || typeof(T) == typeof(GrayAlpha16) ? 65535 : typeof(T) == typeof(RgbaF32) ? 0 : 255;

        double[] result = (double[])rgba.Clone();
        if (gray)
        {
            double l = LumaExact(rgba[0], rgba[1], rgba[2]);
            result[0] = result[1] = result[2] = l;
        }

        if (!alpha)
        {
            result[3] = 1;
        }

        if (max > 0)
        {
            for (int c = 0; c < 4; c++)
            {
                result[c] = Math.Floor((result[c] * max) + 0.5) / max;
            }
        }

        return result;
    }

    private static bool T_HasAlpha<T>()
    {
        return typeof(T) != typeof(Rgb8) && typeof(T) != typeof(Bgr8) && typeof(T) != typeof(Gray8) && typeof(T) != typeof(Gray16) && typeof(T) != typeof(Rgb16);
    }

    private static double LumaExact(double r, double g, double b)
    {
        if (r == g && g == b)
        {
            return r;
        }

        double linear = (0.2126 * ToLinear(r)) + (0.7152 * ToLinear(g)) + (0.0722 * ToLinear(b));
        return linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;

        static double ToLinear(double v)
        {
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
    }

    /// <summary>Builds a palette entry in type <typeparamref name="T"/> through its constructor, without any conversion code.</summary>
    private static T Make<T>((byte R, byte G, byte B, byte A, byte Luma) c)
    {
        object pixel = typeof(T).Name switch
        {
            nameof(Rgba8) => new Rgba8(c.R, c.G, c.B, c.A),
            nameof(Bgra8) => new Bgra8(c.B, c.G, c.R, c.A),
            nameof(Rgb8) => new Rgb8(c.R, c.G, c.B),
            nameof(Bgr8) => new Bgr8(c.B, c.G, c.R),
            nameof(Gray8) => new Gray8(c.Luma),
            nameof(Gray16) => new Gray16((ushort)(c.Luma * 257)),
            nameof(GrayAlpha8) => new GrayAlpha8(c.Luma, c.A),
            nameof(Rgba16) => new Rgba16((ushort)(c.R * 257), (ushort)(c.G * 257), (ushort)(c.B * 257), (ushort)(c.A * 257)),
            nameof(RgbaF32) => new RgbaF32(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f),
            nameof(Rgb16) => new Rgb16((ushort)(c.R * 257), (ushort)(c.G * 257), (ushort)(c.B * 257)),
            nameof(GrayAlpha16) => new GrayAlpha16((ushort)(c.Luma * 257), (ushort)(c.A * 257)),
            _ => throw new ArgumentOutOfRangeException(nameof(T)),
        };
        return (T)pixel;
    }

    /// <summary>Reads a pixel's red, green, blue and alpha as values in 0..1, straight from its fields.</summary>
    private static double[] Channels<T>(T pixel)
    {
        return pixel switch
        {
            Rgba8 p => [p.R / 255.0, p.G / 255.0, p.B / 255.0, p.A / 255.0],
            Bgra8 p => [p.R / 255.0, p.G / 255.0, p.B / 255.0, p.A / 255.0],
            Rgb8 p => [p.R / 255.0, p.G / 255.0, p.B / 255.0, 1],
            Bgr8 p => [p.R / 255.0, p.G / 255.0, p.B / 255.0, 1],
            Gray8 p => [p.L / 255.0, p.L / 255.0, p.L / 255.0, 1],
            Gray16 p => [p.L / 65535.0, p.L / 65535.0, p.L / 65535.0, 1],
            GrayAlpha8 p => [p.L / 255.0, p.L / 255.0, p.L / 255.0, p.A / 255.0],
            Rgba16 p => [p.R / 65535.0, p.G / 65535.0, p.B / 65535.0, p.A / 65535.0],
            RgbaF32 p => [p.R, p.G, p.B, p.A],
            Rgb16 p => [p.R / 65535.0, p.G / 65535.0, p.B / 65535.0, 1],
            GrayAlpha16 p => [p.L / 65535.0, p.L / 65535.0, p.L / 65535.0, p.A / 65535.0],
            _ => throw new ArgumentOutOfRangeException(nameof(pixel)),
        };
    }

    private static Type PixelType(string name)
    {
        return PixelTypes.Single(t => t.Name == name);
    }
}
