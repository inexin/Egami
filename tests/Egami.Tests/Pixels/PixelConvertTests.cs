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
        typeof(Argb8), typeof(Abgr8), typeof(Alpha8), typeof(RgbaF16), typeof(RgbF32), typeof(GrayF32),
        typeof(Bgr565), typeof(Bgra4444), typeof(Rgb10A2), typeof(Cmyk8), typeof(Cmyk16),
        typeof(PremulRgba8), typeof(PremulBgra8), typeof(PremulRgbaF32),
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
        Assert.Equal(625, AllPairs().Count);
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
        Assert.Equal(4, Size<Argb8>());
        Assert.Equal(4, Size<Abgr8>());
        Assert.Equal(1, Size<Alpha8>());
        Assert.Equal(8, Size<RgbaF16>());
        Assert.Equal(12, Size<RgbF32>());
        Assert.Equal(4, Size<GrayF32>());
        Assert.Equal(2, Size<Bgr565>());
        Assert.Equal(2, Size<Bgra4444>());
        Assert.Equal(4, Size<Rgb10A2>());
        Assert.Equal(4, Size<Cmyk8>());
        Assert.Equal(8, Size<Cmyk16>());
        Assert.Equal(4, Size<PremulRgba8>());
        Assert.Equal(4, Size<PremulBgra8>());
        Assert.Equal(16, Size<PremulRgbaF32>());

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

        ConvertRoute route = PixelConvert.Route(TFrom.Layout, TTo.Layout);
        for (int i = 0; i < source.Length; i++)
        {
            // An exact route rounds to its intermediate first: packed levels are not all whole 8 or 16-bit values.
            double[] logical = Channels(source[i]);
            double step = route == ConvertRoute.Rgba8 ? 255 : route == ConvertRoute.Rgba16 ? 65535 : 0;
            if (step > 0)
            {
                logical = [.. logical.Select(v => Math.Floor((v * step) + 0.5) / step)];
            }

            double[] expected = Expected<TTo>(logical);
            double[] got = Channels(actual[i]);
            double tolerance = typeof(TTo) == typeof(RgbaF16) ? 1.0 / 2048 : typeof(TTo) == typeof(RgbaF32) || typeof(TTo) == typeof(RgbF32) || typeof(TTo) == typeof(GrayF32) ? 1e-6 : typeof(TTo) == typeof(PremulRgbaF32) ? 1e-5 : 0;

            if (typeof(TTo) == typeof(Cmyk8) || typeof(TTo) == typeof(Cmyk16))
            {
                tolerance = 1.001 / 255;
            }

            // Half-precision palette values can land within float rounding of a 16-bit step, so allow one level there.
            if (typeof(TFrom) == typeof(RgbaF16) && tolerance == 0)
            {
                tolerance = 1.0 / 65535;
            }

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
        bool gray = typeof(T) == typeof(Gray8) || typeof(T) == typeof(Gray16) || typeof(T) == typeof(GrayAlpha8) || typeof(T) == typeof(GrayAlpha16) || typeof(T) == typeof(GrayF32);
        bool alpha = T_HasAlpha<T>();
        double max = typeof(T) == typeof(Rgba16) || typeof(T) == typeof(Gray16) || typeof(T) == typeof(Rgb16) || typeof(T) == typeof(GrayAlpha16) ? 65535 : typeof(T) == typeof(RgbaF32) || typeof(T) == typeof(RgbaF16) || typeof(T) == typeof(RgbF32) || typeof(T) == typeof(GrayF32) || typeof(T) == typeof(PremulRgbaF32) ? 0 : 255;

        double[] result = (double[])rgba.Clone();
        if (typeof(T) == typeof(PremulRgba8) || typeof(T) == typeof(PremulBgra8))
        {
            return PremultipliedRoundTrip(rgba);
        }

        if (typeof(T) == typeof(PremulRgbaF32) && rgba[3] == 0)
        {
            return [0, 0, 0, 0];
        }

        if (typeof(T) == typeof(Cmyk8) || typeof(T) == typeof(Cmyk16))
        {
            return CmykRoundTrip(rgba, typeof(T) == typeof(Cmyk8) ? 255 : 65535);
        }

        if (typeof(T) == typeof(Alpha8))
        {
            result[0] = result[1] = result[2] = 0;
        }

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
                double levels = Levels<T>(c, max);
                result[c] = Math.Floor((result[c] * levels) + 0.5) / levels;
            }
        }

        return result;
    }

    /// <summary>The number of steps in channel <paramref name="c"/> of a packed type, or <paramref name="max"/> for the others.</summary>
    private static double Levels<T>(int c, double max)
    {
        return typeof(T).Name switch
        {
            nameof(Bgr565) => c == 1 ? 63 : c == 3 ? 1 : 31,
            nameof(Bgra4444) => 15,
            nameof(Rgb10A2) => c == 3 ? 3 : 1023,
            _ => max,
        };
    }

    /// <summary>The straight color an 8-bit premultiplied type gives back: alpha and premultiplied color rounded to 8 bits.</summary>
    private static double[] PremultipliedRoundTrip(double[] rgba)
    {
        double a = Math.Floor((rgba[3] * 255) + 0.5);
        double[] result = new double[4];
        for (int c = 0; c < 3; c++)
        {
            double stored = Math.Floor((rgba[c] * a) + 0.5);
            result[c] = a == 0 ? 0 : Math.Floor((stored * 255 / a) + 0.5) / 255;
        }

        result[3] = a / 255;
        return result;
    }

    /// <summary>The RGB a CMYK type holds for a color: inks by the plain formula, rounded to the type's levels, then back.</summary>
    private static double[] CmykRoundTrip(double[] rgba, double levels)
    {
        double max = Math.Max(rgba[0], Math.Max(rgba[1], rgba[2]));
        double Q(double v) => Math.Floor((v * levels) + 0.5) / levels;
        double k = Q(1 - max);
        double[] result = new double[4];
        for (int c = 0; c < 3; c++)
        {
            double ink = max <= 0 ? 0 : Q((max - rgba[c]) / max);
            result[c] = (1 - ink) * (1 - k);
        }

        result[3] = 1;
        return result;
    }

    private static bool T_HasAlpha<T>()
    {
        return typeof(T) != typeof(Rgb8) && typeof(T) != typeof(Bgr8) && typeof(T) != typeof(Gray8) && typeof(T) != typeof(Gray16) && typeof(T) != typeof(Rgb16) && typeof(T) != typeof(RgbF32) && typeof(T) != typeof(GrayF32) && typeof(T) != typeof(Bgr565) && typeof(T) != typeof(Cmyk8) && typeof(T) != typeof(Cmyk16);
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
            nameof(Argb8) => new Argb8(c.A, c.R, c.G, c.B),
            nameof(Abgr8) => new Abgr8(c.A, c.B, c.G, c.R),
            nameof(Alpha8) => new Alpha8(c.A),
            nameof(RgbaF16) => new RgbaF16((Half)(c.R / 255f), (Half)(c.G / 255f), (Half)(c.B / 255f), (Half)(c.A / 255f)),
            nameof(RgbF32) => new RgbF32(c.R / 255f, c.G / 255f, c.B / 255f),
            nameof(GrayF32) => new GrayF32(c.Luma / 255f),
            nameof(Bgr565) => new Bgr565(Level(c.R, 31), Level(c.G, 63), Level(c.B, 31)),
            nameof(Bgra4444) => new Bgra4444(Level(c.R, 15), Level(c.G, 15), Level(c.B, 15), Level(c.A, 15)),
            nameof(Rgb10A2) => new Rgb10A2(Level(c.R, 1023), Level(c.G, 1023), Level(c.B, 1023), Level(c.A, 3)),
            nameof(PremulRgba8) => new PremulRgba8(Times(c.R, c.A), Times(c.G, c.A), Times(c.B, c.A), c.A),
            nameof(PremulBgra8) => new PremulBgra8(Times(c.B, c.A), Times(c.G, c.A), Times(c.R, c.A), c.A),
            nameof(PremulRgbaF32) => new PremulRgbaF32(c.R / 255f * (c.A / 255f), c.G / 255f * (c.A / 255f), c.B / 255f * (c.A / 255f), c.A / 255f),
            nameof(Cmyk8) => Inks(c, 255, v => new Cmyk8((byte)v[0], (byte)v[1], (byte)v[2], (byte)v[3])),
            nameof(Cmyk16) => Inks(c, 65535, v => new Cmyk16((ushort)v[0], (ushort)v[1], (ushort)v[2], (ushort)v[3])),
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
            Argb8 p => [p.R / 255.0, p.G / 255.0, p.B / 255.0, p.A / 255.0],
            Abgr8 p => [p.R / 255.0, p.G / 255.0, p.B / 255.0, p.A / 255.0],
            Alpha8 p => [0, 0, 0, p.A / 255.0],
            RgbaF16 p => [(double)p.R, (double)p.G, (double)p.B, (double)p.A],
            RgbF32 p => [p.R, p.G, p.B, 1],
            GrayF32 p => [p.L, p.L, p.L, 1],
            Bgr565 p => [p.R / 31.0, p.G / 63.0, p.B / 31.0, 1],
            Bgra4444 p => [p.R / 15.0, p.G / 15.0, p.B / 15.0, p.A / 15.0],
            Rgb10A2 p => [p.R / 1023.0, p.G / 1023.0, p.B / 1023.0, p.A / 3.0],
            PremulRgba8 p => p.A == 0 ? [0, 0, 0, 0] : [Over(p.R, p.A), Over(p.G, p.A), Over(p.B, p.A), p.A / 255.0],
            PremulBgra8 p => p.A == 0 ? [0, 0, 0, 0] : [Over(p.R, p.A), Over(p.G, p.A), Over(p.B, p.A), p.A / 255.0],
            PremulRgbaF32 p => p.A == 0 ? [0, 0, 0, 0] : [p.R / p.A, p.G / p.A, p.B / p.A, p.A],
            Cmyk8 p => [(1 - (p.C / 255.0)) * (1 - (p.K / 255.0)), (1 - (p.M / 255.0)) * (1 - (p.K / 255.0)), (1 - (p.Y / 255.0)) * (1 - (p.K / 255.0)), 1],
            Cmyk16 p => [(1 - (p.C / 65535.0)) * (1 - (p.K / 65535.0)), (1 - (p.M / 65535.0)) * (1 - (p.K / 65535.0)), (1 - (p.Y / 65535.0)) * (1 - (p.K / 65535.0)), 1],
            _ => throw new ArgumentOutOfRangeException(nameof(pixel)),
        };
    }

    /// <summary>Inks for a palette color, by the plain formula worked out by hand.</summary>
    private static object Inks((byte R, byte G, byte B, byte A, byte Luma) c, int levels, Func<int[], object> make)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
        int Ink(byte v) => max == 0 ? 0 : (int)Math.Round((max - (v / 255.0)) / max * levels);
        return make([Ink(c.R), Ink(c.G), Ink(c.B), (int)Math.Round((1 - max) * levels)]);
    }

    private static byte Times(byte color, byte alpha) => (byte)Math.Floor((color * alpha / 255.0) + 0.5);

    private static double Over(byte color, byte alpha) => Math.Floor((Math.Min(color, alpha) * 255.0 / alpha) + 0.5) / 255;

    private static int Level(byte value, int levels)
    {
        return (int)Math.Round(value * levels / 255.0);
    }

    private static Type PixelType(string name)
    {
        return PixelTypes.Single(t => t.Name == name);
    }
}
