namespace Egami.Operations.Compose;

/// <summary>
/// The scalar reference for every <see cref="BlendMode"/>, written from the W3C Compositing and Blending Level 1 formulas in
/// double precision on premultiplied RGBA in 0..1. The kernels are tested against it: the integer kernels bit for bit through
/// <see cref="Blend8"/>, the float kernels within a level. It depends on nothing but <see cref="BlendMode"/>, so egami-compare
/// links it to check it against other libraries.
/// </summary>
internal static class BlendReference
{
    // Rec. 601 luminosity weights, as the specification's Lum() gives them.
    private const double LumR = 0.3, LumG = 0.59, LumB = 0.11;

    /// <summary>Gets whether a mode blends colors and then composites source-over, rather than being a Porter–Duff operator, Plus or Modulate.</summary>
    internal static bool IsBlend(BlendMode mode)
    {
        return mode >= BlendMode.Multiply;
    }

    /// <summary>Gets whether a mode's blend function mixes channels (Hue, Saturation, Color, Luminosity).</summary>
    internal static bool IsNonSeparable(BlendMode mode)
    {
        return mode >= BlendMode.Hue;
    }

    /// <summary>
    /// Blends premultiplied <paramref name="s"/> onto premultiplied <paramref name="d"/> (R, G, B, A in 0..1) after scaling the
    /// source by <paramref name="opacity"/>, and returns the premultiplied result.
    /// </summary>
    internal static (double R, double G, double B, double A) Blend(
        BlendMode mode, (double R, double G, double B, double A) s, (double R, double G, double B, double A) d, double opacity = 1)
    {
        s = (s.R * opacity, s.G * opacity, s.B * opacity, s.A * opacity);
        double sa = s.A, da = d.A;
        if (!IsBlend(mode))
        {
            switch (mode)
            {
                case BlendMode.Plus:
                    return (Math.Min(1, s.R + d.R), Math.Min(1, s.G + d.G), Math.Min(1, s.B + d.B), Math.Min(1, sa + da));
                case BlendMode.Modulate:
                    return (s.R * d.R, s.G * d.G, s.B * d.B, sa * da);
            }

            (double fa, double fb) = Coefficients(mode, sa, da);
            return ((s.R * fa) + (d.R * fb), (s.G * fa) + (d.G * fb), (s.B * fa) + (d.B * fb), (sa * fa) + (da * fb));
        }

        // Straight colors for the blend function; a transparent pixel's color is taken as black, where its weight is 0 anyway.
        (double R, double G, double B) cs = sa > 0 ? (s.R / sa, s.G / sa, s.B / sa) : (0, 0, 0);
        (double R, double G, double B) cb = da > 0 ? (d.R / da, d.G / da, d.B / da) : (0, 0, 0);
        (double R, double G, double B) mixed = IsNonSeparable(mode)
            ? NonSeparable(mode, cb, cs)
            : (Separable(mode, cb.R, cs.R), Separable(mode, cb.G, cs.G), Separable(mode, cb.B, cs.B));
        double both = sa * da;
        return (
            (s.R * (1 - da)) + (d.R * (1 - sa)) + (both * mixed.R),
            (s.G * (1 - da)) + (d.G * (1 - sa)) + (both * mixed.G),
            (s.B * (1 - da)) + (d.B * (1 - sa)) + (both * mixed.B),
            sa + (da * (1 - sa)));
    }

    /// <summary>
    /// The 8-bit reference: premultiplied bytes in, the exact result rounded to nearest out. The opacity is applied first as the
    /// kernels apply it, scaling each source byte by <paramref name="opacity255"/> / 255 rounded to nearest.
    /// </summary>
    internal static (byte R, byte G, byte B, byte A) Blend8(
        BlendMode mode, (byte R, byte G, byte B, byte A) s, (byte R, byte G, byte B, byte A) d, int opacity255 = 255)
    {
        if (opacity255 != 255)
        {
            s = (Scale(s.R, opacity255), Scale(s.G, opacity255), Scale(s.B, opacity255), Scale(s.A, opacity255));
        }

        (double R, double G, double B, double A) result = Blend(mode, (s.R / 255.0, s.G / 255.0, s.B / 255.0, s.A / 255.0), (d.R / 255.0, d.G / 255.0, d.B / 255.0, d.A / 255.0));
        return (To8(result.R), To8(result.G), To8(result.B), To8(result.A));

        static byte Scale(byte v, int o) => (byte)(((v * o) + 127) / 255);
    }

    /// <summary>Rounds a value in 0..1 to the nearest byte, clamping.</summary>
    internal static byte To8(double v)
    {
        return (byte)Math.Clamp(Math.Round(v * 255, MidpointRounding.AwayFromZero), 0, 255);
    }

    /// <summary>Gets a Porter–Duff operator's source and destination coefficients.</summary>
    internal static (double Fa, double Fb) Coefficients(BlendMode mode, double sa, double da)
    {
        return mode switch
        {
            BlendMode.Clear => (0, 0),
            BlendMode.Source => (1, 0),
            BlendMode.Destination => (0, 1),
            BlendMode.SourceOver => (1, 1 - sa),
            BlendMode.DestinationOver => (1 - da, 1),
            BlendMode.SourceIn => (da, 0),
            BlendMode.DestinationIn => (0, sa),
            BlendMode.SourceOut => (1 - da, 0),
            BlendMode.DestinationOut => (0, 1 - sa),
            BlendMode.SourceAtop => (da, 1 - sa),
            BlendMode.DestinationAtop => (1 - da, sa),
            BlendMode.Xor => (1 - da, 1 - sa),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    /// <summary>A separable blend function B(Cb, Cs) on straight values in 0..1.</summary>
    internal static double Separable(BlendMode mode, double cb, double cs)
    {
        switch (mode)
        {
            case BlendMode.Multiply:
                return cb * cs;
            case BlendMode.Screen:
                return cb + cs - (cb * cs);
            case BlendMode.Overlay:
                return Separable(BlendMode.HardLight, cs, cb);
            case BlendMode.Darken:
                return Math.Min(cb, cs);
            case BlendMode.Lighten:
                return Math.Max(cb, cs);
            case BlendMode.ColorDodge:
                if (cb == 0)
                {
                    return 0;
                }

                return cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs));
            case BlendMode.ColorBurn:
                if (cb >= 1)
                {
                    return 1;
                }

                return cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs);
            case BlendMode.HardLight:
                return cs <= 0.5 ? cb * 2 * cs : Separable(BlendMode.Screen, cb, (2 * cs) - 1);
            case BlendMode.SoftLight:
                if (cs <= 0.5)
                {
                    return cb - ((1 - (2 * cs)) * cb * (1 - cb));
                }

                double dd = cb <= 0.25 ? ((((16 * cb) - 12) * cb) + 4) * cb : Math.Sqrt(cb);
                return cb + (((2 * cs) - 1) * (dd - cb));
            case BlendMode.Difference:
                return Math.Abs(cb - cs);
            case BlendMode.Exclusion:
                return cb + cs - (2 * cb * cs);
            case BlendMode.Add:
                return Math.Min(1, cb + cs);
            case BlendMode.Subtract:
                return Math.Max(0, cb - cs);
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    /// <summary>A non-separable blend function B(Cb, Cs) on straight colors in 0..1.</summary>
    internal static (double R, double G, double B) NonSeparable(BlendMode mode, (double R, double G, double B) cb, (double R, double G, double B) cs)
    {
        return mode switch
        {
            BlendMode.Hue => SetLum(SetSat(cs, Sat(cb)), Lum(cb)),
            BlendMode.Saturation => SetLum(SetSat(cb, Sat(cs)), Lum(cb)),
            BlendMode.Color => SetLum(cs, Lum(cb)),
            BlendMode.Luminosity => SetLum(cb, Lum(cs)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    internal static double Lum((double R, double G, double B) c)
    {
        return (LumR * c.R) + (LumG * c.G) + (LumB * c.B);
    }

    private static (double R, double G, double B) ClipColor((double R, double G, double B) c)
    {
        double l = Lum(c);
        double n = Math.Min(c.R, Math.Min(c.G, c.B));
        double x = Math.Max(c.R, Math.Max(c.G, c.B));
        if (n < 0)
        {
            c = (l + ((c.R - l) * l / (l - n)), l + ((c.G - l) * l / (l - n)), l + ((c.B - l) * l / (l - n)));
        }

        if (x > 1)
        {
            c = (l + ((c.R - l) * (1 - l) / (x - l)), l + ((c.G - l) * (1 - l) / (x - l)), l + ((c.B - l) * (1 - l) / (x - l)));
        }

        return c;
    }

    private static (double R, double G, double B) SetLum((double R, double G, double B) c, double l)
    {
        double d = l - Lum(c);
        return ClipColor((c.R + d, c.G + d, c.B + d));
    }

    private static double Sat((double R, double G, double B) c)
    {
        return Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
    }

    private static (double R, double G, double B) SetSat((double R, double G, double B) c, double s)
    {
        // The specification works on the channels sorted by value: max becomes s, mid scales, min becomes 0.
        double max = Math.Max(c.R, Math.Max(c.G, c.B));
        double min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max <= min)
        {
            return (0, 0, 0);
        }

        return (Channel(c.R), Channel(c.G), Channel(c.B));

        double Channel(double v) => v == max ? s : v == min ? 0 : (v - min) * s / (max - min);
    }
}
