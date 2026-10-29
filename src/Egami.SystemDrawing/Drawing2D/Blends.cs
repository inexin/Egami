using Egami.Drawing;
using Egami.GdiPlus;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

/// <summary>A gradient's falloff: at each position from 0 to 1, a factor from the start color (0) to the end color (1).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public sealed class Blend
{
    /// <summary>Initializes a new instance of the <see cref="Blend"/> class with one factor.</summary>
    public Blend()
        : this(1)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Blend"/> class with zeroed factors and positions.</summary>
    /// <param name="count">The number of each.</param>
    public Blend(int count)
    {
        Factors = new float[count];
        Positions = new float[count];
    }

#pragma warning disable CA1819 // System.Drawing's Blend exposes arrays.
    /// <summary>Gets or sets the factors, 0 for the start color to 1 for the end color.</summary>
    public float[] Factors { get; set; }

    /// <summary>Gets or sets the positions, from 0 to 1.</summary>
    public float[] Positions { get; set; }
#pragma warning restore CA1819
}

/// <summary>A gradient's colors at positions from 0 to 1, the first at 0 and the last at 1.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="brushes"/>
/// </example>
public sealed class ColorBlend
{
    /// <summary>Initializes a new instance of the <see cref="ColorBlend"/> class with one color.</summary>
    public ColorBlend()
        : this(1)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ColorBlend"/> class with empty colors and zeroed positions.</summary>
    /// <param name="count">The number of each.</param>
    public ColorBlend(int count)
    {
        Colors = new Color[count];
        Positions = new float[count];
    }

#pragma warning disable CA1819 // System.Drawing's ColorBlend exposes arrays.
    /// <summary>Gets or sets the colors.</summary>
    public Color[] Colors { get; set; }

    /// <summary>Gets or sets the positions, from 0 to 1.</summary>
    public float[] Positions { get; set; }
#pragma warning restore CA1819
}

/// <summary>The falloff and color settings the gradient brushes share.</summary>
internal sealed class GradientSettings
{
    /// <summary>The number of steps on each side of a sigma bell's focus, as GDI+ samples it.</summary>
    private const int BellSteps = 255;

    public float[] Factors { get; private set; } = [1];

    public float[] Positions { get; private set; } = [0];

    /// <summary>Gets the preset colors and their positions, or null for the blend.</summary>
    public (Color[] Colors, float[] Positions)? Preset { get; private set; }

    public GradientSettings Copy() => new() { Factors = (float[])Factors.Clone(), Positions = (float[])Positions.Clone(), Preset = Preset is { } p ? ((Color[])p.Colors.Clone(), (float[])p.Positions.Clone()) : null };

    /// <summary>The blend, or null while preset colors are set, as GDI+ gives it.</summary>
    public Blend? Blend => Preset is not null ? null : new Blend(Factors.Length) { Factors = (float[])Factors.Clone(), Positions = (float[])Positions.Clone() };

    /// <summary>Sets the blend and drops preset colors.</summary>
    /// <exception cref="ArgumentException">The arrays are empty or differ in length.</exception>
    public void SetBlend(Blend value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Factors is null || value.Positions is null || value.Factors.Length == 0 || value.Factors.Length != value.Positions.Length)
        {
            throw GdiErrors.InvalidParameter();
        }

        Factors = (float[])value.Factors.Clone();
        Positions = (float[])value.Positions.Clone();
        Preset = null;
    }

    /// <summary>The preset colors; one empty color when none are set, as GDI+ gives it.</summary>
    public ColorBlend Colors => Preset is (Color[] c, float[] p) ? new ColorBlend(c.Length) { Colors = (Color[])c.Clone(), Positions = (float[])p.Clone() } : new ColorBlend(1);

    /// <summary>Sets preset colors.</summary>
    /// <exception cref="ArgumentException">Fewer than two colors, different lengths, or positions that do not start at 0 and end at 1.</exception>
    public void SetColors(ColorBlend value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Colors is null || value.Positions is null || value.Colors.Length < 2 || value.Colors.Length != value.Positions.Length
            || value.Positions[0] != 0 || value.Positions[^1] != 1)
        {
            throw GdiErrors.InvalidParameter();
        }

        Preset = ((Color[])value.Colors.Clone(), (float[])value.Positions.Clone());
    }

    /// <summary>
    /// A bell from the normal curve (mean at the middle of each side, sigma a quarter), rising from 0 to <paramref name="scale"/>
    /// at the focus and falling back, in 255 steps a side, as GDI+ samples it.
    /// </summary>
    public void SigmaBell(float focus, float scale)
    {
        Check(focus, scale);
        float Bell(float t) => scale * (float)((Erf(2 * Math.Sqrt(2) * (t - 0.5)) + Erf(Math.Sqrt(2))) / (2 * Erf(Math.Sqrt(2))));
        List<float> f = [], p = [];
        if (focus > 0)
        {
            for (int i = 0; i <= BellSteps; i++)
            {
                p.Add(focus * i / BellSteps);
                f.Add(Bell((float)i / BellSteps));
            }
        }

        if (focus < 1)
        {
            for (int i = focus > 0 ? 1 : 0; i <= BellSteps; i++)
            {
                p.Add(focus + ((1 - focus) * i / BellSteps));
                f.Add(Bell(1 - ((float)i / BellSteps)));
            }
        }

        Factors = [.. f];
        Positions = [.. p];
        Preset = null;
    }

    /// <summary>A triangle: 0 at the ends, <paramref name="scale"/> at the focus; the side at an end focus is left out.</summary>
    public void Triangular(float focus, float scale)
    {
        Check(focus, scale);
        (Factors, Positions) = focus switch
        {
            0 => ([scale, 0], [0, 1]),
            1 => ([0, scale], [0, 1]),
            _ => ((float[])[0, scale, 0], (float[])[0, focus, 1]),
        };
        Preset = null;
    }

    /// <summary>The stops from <paramref name="start"/> to <paramref name="end"/>, from preset colors or the blend.</summary>
    public GradientStop[] Stops(Color start, Color end, bool gamma)
    {
        GradientStop[] stops;
        if (Preset is (Color[] colors, float[] positions))
        {
            stops = new GradientStop[colors.Length];
            for (int i = 0; i < stops.Length; i++)
            {
                stops[i] = GdiPlusBrushes.PresetStop(positions[i], SolidBrush.Rgba(colors[i]));
            }
        }
        else
        {
            stops = new GradientStop[Factors.Length];
            for (int i = 0; i < stops.Length; i++)
            {
                stops[i] = GdiPlusBrushes.BlendStop(Positions[i], Factors[i], SolidBrush.Rgba(start), SolidBrush.Rgba(end), gamma);
            }

            if (stops.Length == 1)
            {
                // One factor means the whole gradient is that mix; GDI+'s default single factor of 1 at 0 is a plain two-color ramp.
                stops = Factors[0] == 1 && Positions[0] == 0 ? [new(0, SolidBrush.Rgba(start)), new(1, SolidBrush.Rgba(end))] : [stops[0], new GradientStop(1, stops[0].Color)];
            }
        }

        // A gamma-corrected brush mixes between the stops with GradientInterpolation.GdiPlusGamma (see LinearGradientBrush).
        return stops;
    }

    private static void Check(float focus, float scale)
    {
        if (focus is < 0 or > 1 || scale is < 0 or > 1)
        {
            throw GdiErrors.InvalidParameter();
        }
    }

    /// <summary>The error function, to double precision (Abramowitz and Stegun 7.1.26 is not enough, so a series and a fraction).</summary>
    private static double Erf(double x)
    {
        double sign = Math.Sign(x);
        x = Math.Abs(x);
        if (x < 2.5)
        {
            // Taylor series: 2/sqrt(pi) * sum (-1)^n x^(2n+1) / (n! (2n+1)).
            double sum = 0, term = x;
            for (int n = 0; n < 60; n++)
            {
                sum += term / ((2 * n) + 1);
                term *= -x * x / (n + 1);
            }

            return sign * 2 / Math.Sqrt(Math.PI) * sum;
        }

        // Continued fraction for erfc.
        double t = 0;
        for (int n = 60; n >= 1; n--)
        {
            t = n / 2.0 / (x + t);
        }

        return sign * (1 - (Math.Exp(-x * x) / Math.Sqrt(Math.PI) / (x + t)));
    }
}
