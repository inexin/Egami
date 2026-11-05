using System.Buffers.Binary;
using System.Text;

namespace Egami.Formats.Psd;

/// <summary>
/// A Photoshop gradient, read from a gradient descriptor (class Grdn, in fill layers and effects) or from a gradient map block
/// (grdm, the specification's "Gradient settings"): color stops and transparency stops, each at a location from 0 to 1 with
/// a midpoint, the smoothness of the Classic method and the interpolation method Photoshop 2022 added. Noise gradients are
/// read as such; Egami does not draw them. <see cref="Table"/> samples the gradient for drawing.
/// </summary>
internal sealed class PsdGradient
{
    /// <summary>The number of steps <see cref="Table"/> samples, one more entry than this.</summary>
    public const int Steps = 1024;

    private PsdGradient(ColorStop[] colors, OpacityStop[] opacities, double smoothness, PsdGradientMethod method, bool noise)
    {
        Colors = colors;
        Opacities = opacities;
        Smoothness = smoothness;
        Method = method;
        IsNoise = noise;
    }

    public ColorStop[] Colors { get; }

    public OpacityStop[] Opacities { get; }

    /// <summary>Gets the Classic method's smoothness, 0 to 1.</summary>
    public double Smoothness { get; }

    public PsdGradientMethod Method { get; }

    /// <summary>Gets whether this is a noise gradient, whose colors are random.</summary>
    public bool IsNoise { get; }

    /// <summary>Reads a gradient descriptor (Grdn); <paramref name="owner"/> is the descriptor holding it, where Photoshop 2022 keeps the method.</summary>
    public static PsdGradient? Read(PsdDescriptor? gradient, PsdDescriptor? owner)
    {
        if (gradient is null)
        {
            return null;
        }

        PsdGradientMethod method = MethodOf(owner?.Items.GetValueOrDefault("gradientsInterpolationMethod") as string
            ?? gradient.Items.GetValueOrDefault("gradientsInterpolationMethod") as string);
        if (gradient.Items.GetValueOrDefault("GrdF") is "ClNs")
        {
            return new PsdGradient([], [], 0, method, true);
        }

        List<ColorStop> colors = [];
        foreach (PsdDescriptor stop in (gradient.Get<List<object>>("Clrs") ?? []).OfType<PsdDescriptor>())
        {
            if (stop.Get<PsdDescriptor>("Clr ") is PsdDescriptor color)
            {
                colors.Add(new ColorStop(Location(stop), Midpoint(stop), color));
            }
        }

        List<OpacityStop> opacities = [];
        foreach (PsdDescriptor stop in (gradient.Get<List<object>>("Trns") ?? []).OfType<PsdDescriptor>())
        {
            opacities.Add(new OpacityStop(Location(stop), Midpoint(stop), Math.Clamp((stop.Number("Opct") ?? 100) / 100, 0, 1)));
        }

        if (colors.Count == 0)
        {
            return null;
        }

        return new PsdGradient(
            [.. colors.OrderBy(s => s.Location)], [.. opacities.OrderBy(s => s.Location)], Math.Clamp((gradient.Number("Intr") ?? 4096) / 4096, 0, 1), method, false);

        static double Location(PsdDescriptor stop) => Math.Clamp((stop.Number("Lctn") ?? 0) / 4096, 0, 1);

        static double Midpoint(PsdDescriptor stop) => Math.Clamp((stop.Number("Mdpn") ?? 50) / 100, 0, 1);
    }

    /// <summary>Reads a gradient map's block (grdm): its gradient, and whether it is reversed.</summary>
    public static PsdGradient? ReadMap(ReadOnlySpan<byte> data, out bool reverse)
    {
        reverse = false;
        if (data.Length < 8)
        {
            return null;
        }

        int version = BinaryPrimitives.ReadUInt16BigEndian(data);
        reverse = data[2] != 0;
        int at = 4;
        PsdGradientMethod method = PsdGradientMethod.Classic;
        if (version == 3)
        {
            method = MethodOf(Encoding.Latin1.GetString(data.Slice(at, 4)));
            at += 4;
        }
        else if (version != 1)
        {
            return null;
        }

        int name = (int)BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
        if (name < 0 || name > (data.Length - at - 4) / 2)
        {
            return null;
        }

        at += 4 + (2 * name);
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
        at += 2;
        ColorStop[] colors = new ColorStop[count];
        for (int i = 0; i < count; i++, at += 20)
        {
            ReadOnlySpan<byte> stop = data.Slice(at, 20);
            colors[i] = new ColorStop(
                Math.Clamp(BinaryPrimitives.ReadUInt32BigEndian(stop) / 4096.0, 0, 1),
                Math.Clamp(BinaryPrimitives.ReadUInt32BigEndian(stop[4..]) / 100.0, 0, 1),
                ColorOf(stop.Slice(8, 10)));
        }

        count = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
        at += 2;
        OpacityStop[] opacities = new OpacityStop[count];
        for (int i = 0; i < count; i++, at += 10)
        {
            ReadOnlySpan<byte> stop = data.Slice(at, 10);
            opacities[i] = new OpacityStop(
                Math.Clamp(BinaryPrimitives.ReadUInt32BigEndian(stop) / 4096.0, 0, 1),
                Math.Clamp(BinaryPrimitives.ReadUInt32BigEndian(stop[4..]) / 100.0, 0, 1),
                BinaryPrimitives.ReadUInt16BigEndian(stop[8..]) / 255.0);
        }

        // Expansion count, interpolation (the smoothness, of 4096), length, mode (0 solid, 1 noise).
        double smoothness = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]) / 4096.0;
        bool noise = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 6)..]) != 0;
        if (colors.Length == 0 && !noise)
        {
            return null;
        }

        return new PsdGradient([.. colors.OrderBy(s => s.Location)], [.. opacities.OrderBy(s => s.Location)], Math.Clamp(smoothness, 0, 1), method, noise);
    }

    /// <summary>
    /// Samples the gradient at <see cref="Steps"/> + 1 even locations into <paramref name="table"/> (at least that many times
    /// <paramref name="channels"/> + 1 floats): each entry its straight color, in the space <paramref name="convert"/> gives a
    /// stop's color in, then its opacity. <paramref name="srgb"/> says that space is sRGB, where the Linear and Perceptual
    /// methods mix in linear light and in Oklab; in any other space they mix as Classic.
    /// </summary>
    public void Table(Span<float> table, int channels, Func<PsdDescriptor, float[]> convert, bool srgb)
    {
        int stride = channels + 1;
        float[][] colors = [.. Colors.Select(s => convert(s.Color))];
        PsdGradientMethod method = Method is PsdGradientMethod.Linear or PsdGradientMethod.Perceptual && !srgb ? PsdGradientMethod.Classic : Method;
        if (method is PsdGradientMethod.Linear or PsdGradientMethod.Perceptual)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = method == PsdGradientMethod.Linear ? [.. colors[i].Select(v => (float)ToLinear(v))] : ToOklab(colors[i]);
            }
        }

        Span<float> mixed = stackalloc float[channels];
        for (int i = 0; i <= Steps; i++)
        {
            double t = i / (double)Steps;
            Span<float> entry = table.Slice(i * stride, stride);
            (int k, double u) = Segment(Colors, t, s => s.Location, s => s.Midpoint, method);
            float[] a = colors[k], b = colors[Math.Min(k + 1, colors.Length - 1)];
            for (int c = 0; c < channels; c++)
            {
                mixed[c] = (float)(a[c] + ((b[c] - a[c]) * u));
            }

            if (method == PsdGradientMethod.Linear)
            {
                for (int c = 0; c < channels; c++)
                {
                    entry[c] = (float)FromLinear(mixed[c]);
                }
            }
            else if (method == PsdGradientMethod.Perceptual)
            {
                FromOklab(mixed, entry);
            }
            else
            {
                mixed.CopyTo(entry);
            }

            if (Opacities.Length == 0)
            {
                entry[channels] = 1;
            }
            else
            {
                (int o, double v) = Segment(Opacities, t, s => s.Location, s => s.Midpoint, method);
                double from = Opacities[o].Opacity, to = Opacities[Math.Min(o + 1, Opacities.Length - 1)].Opacity;
                entry[channels] = (float)(from + ((to - from) * v));
            }
        }
    }

    /// <summary>
    /// Finds the stops either side of <paramref name="t"/>: the index of the one before and how far toward the next, after its
    /// midpoint (where the two mix half and half) and the method's easing. Before the first stop and after the last, the
    /// nearest stop holds.
    /// </summary>
    private (int Index, double Weight) Segment<T>(T[] stops, double t, Func<T, double> location, Func<T, double> midpoint, PsdGradientMethod method)
    {
        if (stops.Length == 1 || t <= location(stops[0]))
        {
            return (0, 0);
        }

        if (t >= location(stops[^1]))
        {
            return (stops.Length - 1, 0);
        }

        int k = 0;
        while (k + 2 < stops.Length && t >= location(stops[k + 1]))
        {
            k++;
        }

        double a = location(stops[k]), b = location(stops[k + 1]);
        double u = b > a ? (t - a) / (b - a) : 1;
        if (method == PsdGradientMethod.Stripes)
        {
            return (k, 0);
        }

        // The midpoint belongs to the stop on the right of the segment.
        double m = Math.Clamp(midpoint(stops[k + 1]), 0.01, 0.99);
        if (Math.Abs(m - 0.5) > 1e-9)
        {
            u = Math.Pow(u, Math.Log(0.5) / Math.Log(m));
        }

        double eased = u * u * (3 - (2 * u));
        return method switch
        {
            // Classic's smoothness eases toward the stops: at 100 % half the smooth step, fitted on Photoshop-saved fills.
            PsdGradientMethod.Classic => (k, u + ((eased - u) * Smoothness * 0.5)),
            PsdGradientMethod.Smooth => (k, eased),
            _ => (k, u),
        };
    }

    private static PsdGradientMethod MethodOf(string? key) => key switch
    {
        "Perc" => PsdGradientMethod.Perceptual,
        "Lnr " => PsdGradientMethod.Linear,
        "Smoo" => PsdGradientMethod.Smooth,
        "Strp" => PsdGradientMethod.Stripes,
        _ => PsdGradientMethod.Classic,
    };

    /// <summary>Reads a color structure (a color space id and four 16-bit values) as the color descriptor it stands for.</summary>
    public static PsdDescriptor ColorOf(ReadOnlySpan<byte> data)
    {
        int space = BinaryPrimitives.ReadUInt16BigEndian(data);
        double w = BinaryPrimitives.ReadUInt16BigEndian(data[2..]), x = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        double y = BinaryPrimitives.ReadUInt16BigEndian(data[6..]), z = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        return space switch
        {
            1 => PsdDescriptor.Of("HSBC", ("H   ", w / 65536 * 360), ("Strt", x / 655.35), ("Brgh", y / 655.35)),
            // CMYK values count down: 0 is 100 % ink.
            2 => PsdDescriptor.Of("CMYC", ("Cyn ", 100 - (w / 655.35)), ("Mgnt", 100 - (x / 655.35)), ("Ylw ", 100 - (y / 655.35)), ("Blck", 100 - (z / 655.35))),
            7 => PsdDescriptor.Of("LbCl", ("Lmnc", w / 100), ("A   ", (short)x / 100.0), ("B   ", (short)y / 100.0)),
            8 => PsdDescriptor.Of("Grsc", ("Gry ", w / 100)),
            _ => PsdDescriptor.Of("RGBC", ("Rd  ", w / 257), ("Grn ", x / 257), ("Bl  ", y / 257)),
        };
    }

    private static double ToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double FromLinear(double v) => v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(Math.Max(v, 0), 1 / 2.4)) - 0.055;

    /// <summary>Converts a straight sRGB color (and any further channels, kept) to Oklab.</summary>
    private static float[] ToOklab(float[] rgb)
    {
        double r = ToLinear(rgb[0]), g = ToLinear(rgb[1]), b = ToLinear(rgb[2]);
        double l = Math.Cbrt((0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b));
        double m = Math.Cbrt((0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b));
        double s = Math.Cbrt((0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b));
        float[] lab = (float[])rgb.Clone();
        lab[0] = (float)((0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s));
        lab[1] = (float)((1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s));
        lab[2] = (float)((0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s));
        return lab;
    }

    private static void FromOklab(ReadOnlySpan<float> lab, Span<float> rgb)
    {
        double l = lab[0] + (0.3963377774 * lab[1]) + (0.2158037573 * lab[2]);
        double m = lab[0] - (0.1055613458 * lab[1]) - (0.0638541728 * lab[2]);
        double s = lab[0] - (0.0894841775 * lab[1]) - (1.2914855480 * lab[2]);
        (l, m, s) = (l * l * l, m * m * m, s * s * s);
        rgb[0] = (float)Math.Clamp(FromLinear((4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s)), 0, 1);
        rgb[1] = (float)Math.Clamp(FromLinear((-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s)), 0, 1);
        rgb[2] = (float)Math.Clamp(FromLinear((-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s)), 0, 1);
        for (int c = 3; c < lab.Length; c++)
        {
            rgb[c] = lab[c];
        }
    }

    /// <summary>A color stop: its location and midpoint, 0 to 1, and its color descriptor.</summary>
    internal readonly record struct ColorStop(double Location, double Midpoint, PsdDescriptor Color);

    /// <summary>A transparency stop: its location and midpoint, 0 to 1, and its opacity, 0 to 1.</summary>
    internal readonly record struct OpacityStop(double Location, double Midpoint, double Opacity);
}

/// <summary>How a gradient mixes between its stops (Photoshop 2022 and later; older files are Classic).</summary>
internal enum PsdGradientMethod
{
    Classic,
    Perceptual,
    Linear,
    Smooth,
    Stripes,
}
