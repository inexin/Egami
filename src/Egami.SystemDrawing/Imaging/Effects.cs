using Egami.GdiPlus;
using Egami.Operations;
using Egami.Pixels;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Imaging.Effects;

/// <summary>
/// An image effect, as GDI+ 1.1 has them: applied to a bitmap with <see cref="Bitmap.ApplyEffect(Effect, Rectangle)"/> or while
/// drawing with <see cref="Graphics.DrawImage(Image, Effect)"/>. Egami computes them with its own operations, so the results
/// are close to GDI+'s rather than equal.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public abstract class Effect : IDisposable
{
    private protected Effect()
    {
    }

    /// <summary>Releases the effect.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the effect.</summary>
    /// <param name="disposing">Whether <see cref="Dispose()"/> was called.</param>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>Applies the effect to straight 8-bit pixels in place.</summary>
    internal abstract void Apply(Raster<Rgba8> pixels);

    /// <summary>Maps every pixel through a function of its color.</summary>
    private protected static void Map(Raster<Rgba8> pixels, Func<Rgba8, Rgba8> map)
    {
        for (int y = 0; y < pixels.Height; y++)
        {
            Span<Rgba8> row = pixels.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = map(row[x]);
            }
        }
    }

    /// <summary>Maps the color channels through one table each.</summary>
    private protected static void Tables(Raster<Rgba8> pixels, byte[] r, byte[] g, byte[] b, byte[]? a = null) =>
        Map(pixels, c => new Rgba8(r[c.R], g[c.G], b[c.B], a is null ? c.A : a[c.A]));

    private protected static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private protected static byte[] Table(Func<double, double> f)
    {
        byte[] t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            t[i] = Clamp(f(i / 255.0) * 255);
        }

        return t;
    }

    private protected static void CheckRange(int value, int min, int max)
    {
        if (value < min || value > max)
        {
            throw GdiErrors.InvalidParameter();
        }
    }
}

/// <summary>A Gaussian blur of a radius in pixels, 0 to 255.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class BlurEffect : Effect
{
    /// <summary>Initializes a new instance of the <see cref="BlurEffect"/> class.</summary>
    /// <param name="radius">The radius in pixels, 0 to 255.</param>
    /// <param name="expandEdge">Whether the blur spreads past the bitmap's edges; Egami keeps edges in place.</param>
    public BlurEffect(float radius, bool expandEdge)
    {
        if (radius is < 0 or > 255)
        {
            throw GdiErrors.InvalidParameter();
        }

        Radius = radius;
        ExpandEdge = expandEdge;
    }

    /// <summary>Gets the radius.</summary>
    public float Radius { get; }

    /// <summary>Gets whether the blur spreads past the edges.</summary>
    public bool ExpandEdge { get; }

    internal override void Apply(Raster<Rgba8> pixels)
    {
        if (Radius > 0)
        {
            // The radius covers about three standard deviations, as a Gaussian kernel of that radius does.
            pixels.Apply(ops => ops.Blur(Math.Max(0.1f, Radius / 3)));
        }
    }
}

/// <summary>An unsharp mask: detail at a radius in pixels raised by an amount, 0 to 100.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class SharpenEffect : Effect
{
    /// <summary>Initializes a new instance of the <see cref="SharpenEffect"/> class.</summary>
    /// <param name="radius">The radius in pixels, 0 to 255.</param>
    /// <param name="amount">How much, 0 to 100.</param>
    public SharpenEffect(float radius, float amount)
    {
        if (radius is < 0 or > 255 || amount is < 0 or > 100)
        {
            throw GdiErrors.InvalidParameter();
        }

        Radius = radius;
        Amount = amount;
    }

    /// <summary>Gets the radius.</summary>
    public float Radius { get; }

    /// <summary>Gets the amount.</summary>
    public float Amount { get; }

    internal override void Apply(Raster<Rgba8> pixels)
    {
        if (Radius > 0 && Amount > 0)
        {
            pixels.Apply(ops => ops.Sharpen(Amount / 100 * 2, Math.Max(0.1f, Radius / 3)));
        }
    }
}

/// <summary>Brightness (-255 to 255) and contrast (-100 to 100).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class BrightnessContrastEffect : Effect
{
    /// <summary>Initializes a new instance of the <see cref="BrightnessContrastEffect"/> class.</summary>
    /// <param name="brightnessLevel">The brightness, -255 to 255.</param>
    /// <param name="contrastLevel">The contrast, -100 to 100.</param>
    public BrightnessContrastEffect(int brightnessLevel, int contrastLevel)
    {
        CheckRange(brightnessLevel, -255, 255);
        CheckRange(contrastLevel, -100, 100);
        BrightnessLevel = brightnessLevel;
        ContrastLevel = contrastLevel;
    }

    /// <summary>Gets the brightness.</summary>
    public int BrightnessLevel { get; }

    /// <summary>Gets the contrast.</summary>
    public int ContrastLevel { get; }

    internal override void Apply(Raster<Rgba8> pixels)
    {
        // Measured on GDI+: contrast scales about the middle by 1 / (1 - c) when raised and by 1 + c when lowered (a step at 100),
        // and brightness adds its level, scaled with the contrast by the mean of 1 and its factor.
        double c = ContrastLevel / 100.0;
        double k = c >= 1 ? 1e6 : c >= 0 ? 1 / (1 - c) : 1 + c;
        double shift = BrightnessLevel * (1 + k) / 2 / 255;
        byte[] t = Table(v => ((v - (128 / 255.0)) * k) + (128 / 255.0) + (ContrastLevel == 0 ? BrightnessLevel / 255.0 : shift));
        Tables(pixels, t, t, t);
    }
}

/// <summary>Shifts toward red, green and blue (or cyan, magenta and yellow), each -100 to 100.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class ColorBalanceEffect : Effect
{
    /// <summary>Initializes a new instance of the <see cref="ColorBalanceEffect"/> class.</summary>
    /// <param name="cyanRed">Toward red (positive) or cyan.</param>
    /// <param name="magentaGreen">Toward green (positive) or magenta.</param>
    /// <param name="yellowBlue">Toward blue (positive) or yellow.</param>
    public ColorBalanceEffect(int cyanRed, int magentaGreen, int yellowBlue)
    {
        CheckRange(cyanRed, -100, 100);
        CheckRange(magentaGreen, -100, 100);
        CheckRange(yellowBlue, -100, 100);
        (CyanRed, MagentaGreen, YellowBlue) = (cyanRed, magentaGreen, yellowBlue);
    }

    /// <summary>Gets the shift toward red.</summary>
    public int CyanRed { get; }

    /// <summary>Gets the shift toward green.</summary>
    public int MagentaGreen { get; }

    /// <summary>Gets the shift toward blue.</summary>
    public int YellowBlue { get; }

    internal override void Apply(Raster<Rgba8> pixels)
    {
        // Measured on GDI+: each channel is scaled by 1 plus its shift.
        byte[] Shift(int amount) => Table(v => v * (1 + (amount / 100.0)));
        Tables(pixels, Shift(CyanRed), Shift(MagentaGreen), Shift(YellowBlue));
    }
}

/// <summary>Colors through a 5 by 5 color matrix.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class ColorMatrixEffect : Effect
{
    /// <summary>Initializes a new instance of the <see cref="ColorMatrixEffect"/> class.</summary>
    /// <param name="matrix">The matrix.</param>
    public ColorMatrixEffect(ColorMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        Matrix = matrix;
    }

    /// <summary>Gets the matrix.</summary>
    public ColorMatrix Matrix { get; }

    internal override void Apply(Raster<Rgba8> pixels)
    {
        float[] m = Matrix.Values;
        Map(pixels, c => GdiPlusImageAttributes.Transform(c, m));
    }
}

/// <summary>Colors through a lookup table per channel.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class ColorLookupTableEffect : Effect
{
    private readonly byte[] _r;
    private readonly byte[] _g;
    private readonly byte[] _b;
    private readonly byte[] _a;

    /// <summary>Initializes a new instance of the <see cref="ColorLookupTableEffect"/> class.</summary>
    /// <param name="redLookupTable">The red table, 256 entries.</param>
    /// <param name="greenLookupTable">The green table, 256 entries.</param>
    /// <param name="blueLookupTable">The blue table, 256 entries.</param>
    /// <param name="alphaLookupTable">The alpha table, 256 entries.</param>
    public ColorLookupTableEffect(byte[] redLookupTable, byte[] greenLookupTable, byte[] blueLookupTable, byte[] alphaLookupTable)
        : this(Check(redLookupTable), Check(greenLookupTable), Check(blueLookupTable), Check(alphaLookupTable))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ColorLookupTableEffect"/> class.</summary>
    /// <param name="redLookupTable">The red table, up to 256 entries.</param>
    /// <param name="greenLookupTable">The green table, up to 256 entries.</param>
    /// <param name="blueLookupTable">The blue table, up to 256 entries.</param>
    /// <param name="alphaLookupTable">The alpha table, up to 256 entries.</param>
    /// <remarks>A shorter table is filled out with zeros, as GDI+ takes it.</remarks>
    public ColorLookupTableEffect(ReadOnlySpan<byte> redLookupTable, ReadOnlySpan<byte> greenLookupTable, ReadOnlySpan<byte> blueLookupTable, ReadOnlySpan<byte> alphaLookupTable)
    {
        (_r, _g, _b, _a) = (Full(redLookupTable), Full(greenLookupTable), Full(blueLookupTable), Full(alphaLookupTable));
    }

    /// <summary>Gets the red table.</summary>
    public ReadOnlyMemory<byte> RedLookupTable => _r;

    /// <summary>Gets the green table.</summary>
    public ReadOnlyMemory<byte> GreenLookupTable => _g;

    /// <summary>Gets the blue table.</summary>
    public ReadOnlyMemory<byte> BlueLookupTable => _b;

    /// <summary>Gets the alpha table.</summary>
    public ReadOnlyMemory<byte> AlphaLookupTable => _a;

    internal override void Apply(Raster<Rgba8> pixels) => Tables(pixels, _r, _g, _b, _a);

    private static byte[] Full(ReadOnlySpan<byte> table)
    {
        byte[] full = new byte[256];
        table[..Math.Min(256, table.Length)].CopyTo(full);
        return full;
    }

    private static ReadOnlySpan<byte> Check(byte[] table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table;
    }
}

/// <summary>Gray from each color's luminance.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class GrayScaleEffect : Effect
{
    internal override void Apply(Raster<Rgba8> pixels) =>
        Map(pixels, c => Gray(c));

    internal static Rgba8 Gray(Rgba8 c)
    {
        byte g = Clamp((0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B));
        return new Rgba8(g, g, g, c.A);
    }
}

/// <summary>Each color channel inverted.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class InvertEffect : Effect
{
    internal override void Apply(Raster<Rgba8> pixels) => Map(pixels, c => new Rgba8((byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B), c.A));
}

/// <summary>A sepia tone: gray tinted brown.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class SepiaEffect : Effect
{
    internal override void Apply(Raster<Rgba8> pixels) => Map(pixels, c =>
        new Rgba8(Clamp((0.393 * c.R) + (0.769 * c.G) + (0.189 * c.B)), Clamp((0.349 * c.R) + (0.686 * c.G) + (0.168 * c.B)), Clamp((0.272 * c.R) + (0.534 * c.G) + (0.131 * c.B)), c.A));
}

/// <summary>Livelier colors: saturation raised.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class VividEffect : Effect
{
    internal override void Apply(Raster<Rgba8> pixels) => Map(pixels, c =>
    {
        Rgba8 g = GrayScaleEffect.Gray(c);
        const double k = 1.4;
        return new Rgba8(Clamp(g.R + ((c.R - g.R) * k)), Clamp(g.G + ((c.G - g.G) * k)), Clamp(g.B + ((c.B - g.B) * k)), c.A);
    });
}

/// <summary>A tint toward a hue (-180 to 180 degrees) by an amount (-100 to 100).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class TintEffect : Effect
{
    /// <summary>Initializes a new instance of the <see cref="TintEffect"/> class from a color's hue.</summary>
    /// <param name="color">The color.</param>
    /// <param name="amount">How much, -100 to 100.</param>
    public TintEffect(Color color, int amount)
        : this((int)MathF.Round(color.GetHue() > 180 ? color.GetHue() - 360 : color.GetHue()), amount)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TintEffect"/> class.</summary>
    /// <param name="hue">The hue, -180 to 180 degrees.</param>
    /// <param name="amount">How much, -100 to 100.</param>
    public TintEffect(int hue, int amount)
    {
        CheckRange(hue, -180, 180);
        CheckRange(amount, -100, 100);
        (Hue, Amount) = (hue, amount);
    }

    /// <summary>Gets the hue.</summary>
    public int Hue { get; }

    /// <summary>Gets the amount.</summary>
    public int Amount { get; }

    internal override void Apply(Raster<Rgba8> pixels)
    {
        // The tint color at full saturation, mixed in by the amount over each pixel's own luminance.
        double h = ((Hue % 360) + 360) % 360 / 60.0, x = 1 - Math.Abs((h % 2) - 1);
        (double r, double g, double b) = (int)h switch { 0 => (1.0, x, 0.0), 1 => (x, 1.0, 0.0), 2 => (0.0, 1.0, x), 3 => (0.0, x, 1.0), 4 => (x, 0.0, 1.0), _ => (1.0, 0.0, x) };
        double t = Amount / 100.0;
        Map(pixels, c =>
        {
            double l = ((0.299 * c.R) + (0.587 * c.G) + (0.114 * c.B)) / 255;
            return new Rgba8(Clamp(c.R + ((((r - 0.5) * l * 2 * 255) - 0) * t * 0.5)), Clamp(c.G + ((g - 0.5) * l * 255 * t)), Clamp(c.B + ((b - 0.5) * l * 255 * t)), c.A);
        });
    }
}

/// <summary>Levels: highlights (0 to 100) and shadows (0 to 100) stretched, midtones (-100 to 100) shifted.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class LevelsEffect : Effect
{
    /// <summary>Initializes a new instance of the <see cref="LevelsEffect"/> class.</summary>
    /// <param name="highlight">The share of the range that becomes white, 0 to 100.</param>
    /// <param name="midtone">The midtone shift, -100 to 100.</param>
    /// <param name="shadow">The share of the range that becomes black, 0 to 100.</param>
    public LevelsEffect(int highlight, int midtone, int shadow)
    {
        CheckRange(highlight, 0, 100);
        CheckRange(midtone, -100, 100);
        CheckRange(shadow, 0, 100);
        (Highlight, Midtone, Shadow) = (highlight, midtone, shadow);
    }

    /// <summary>Gets the highlight level.</summary>
    public int Highlight { get; }

    /// <summary>Gets the midtone shift.</summary>
    public int Midtone { get; }

    /// <summary>Gets the shadow level.</summary>
    public int Shadow { get; }

    internal override void Apply(Raster<Rgba8> pixels)
    {
        // Measured on GDI+: the highlight level becomes white and the shadow level black, linearly, and the midtones follow a
        // gamma of 1 / (1 + m / 50) when raised and 1 + |m| / 50 when lowered.
        double lo = Shadow / 100.0, hi = Math.Max(lo + 1e-3, Highlight / 100.0);
        double gamma = Gamma(Midtone);
        byte[] t = Table(v => Math.Pow(Math.Clamp((v - lo) / (hi - lo), 0, 1), gamma));
        Tables(pixels, t, t, t);
    }

    internal static double Gamma(int midtone) => midtone >= 0 ? 1 / (1 + (midtone / 50.0)) : 1 + (-midtone / 50.0);
}

/// <summary>A tone curve on some or all color channels; the kinds of curve derive from it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public abstract class ColorCurveEffect : Effect
{
    private protected ColorCurveEffect(CurveChannel channel)
    {
        if (channel is < CurveChannel.All or > CurveChannel.Blue)
        {
            throw new System.ComponentModel.InvalidEnumArgumentException(nameof(channel), (int)channel, typeof(CurveChannel));
        }

        Channel = channel;
    }

    /// <summary>Gets the channels the curve adjusts.</summary>
    public CurveChannel Channel { get; }

    internal override void Apply(Raster<Rgba8> pixels)
    {
        byte[] curve = Table(Curve);
        byte[] same = Table(v => v);
        Tables(pixels, Channel is CurveChannel.All or CurveChannel.Red ? curve : same, Channel is CurveChannel.All or CurveChannel.Green ? curve : same, Channel is CurveChannel.All or CurveChannel.Blue ? curve : same);
    }

    /// <summary>The curve, from 0 to 1 onto 0 to 1.</summary>
    private protected abstract double Curve(double v);

    /// <summary>
    /// A quadratic Bezier from (0, 0) to (1, 1) whose control point sits <paramref name="amount"/> above the diagonal's middle:
    /// the shape GDI+'s highlight, shadow and contrast curves come closest to (within about 10 levels where measured).
    /// </summary>
    private protected static double Bend(double amount, double t)
    {
        double control = Math.Clamp(0.5 + (amount / 2), 0, 1);
        return (2 * t * (1 - t) * control) + (t * t);
    }
}

/// <summary>Exposure, -255 to 255: added to each value, as GDI+ applies it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class ExposureCurveEffect : ColorCurveEffect
{
    /// <summary>Initializes a new instance of the <see cref="ExposureCurveEffect"/> class.</summary>
    /// <param name="channel">The channels.</param>
    /// <param name="exposure">The exposure, -255 to 255.</param>
    public ExposureCurveEffect(CurveChannel channel, int exposure)
        : base(channel)
    {
        CheckRange(exposure, -255, 255);
        Exposure = exposure;
    }

    /// <summary>Gets the exposure.</summary>
    public int Exposure { get; }

    private protected override double Curve(double v) => v + (Exposure / 255.0);
}

/// <summary>Density, -255 to 255: added to each value, as GDI+ applies it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class DensityCurveEffect : ColorCurveEffect
{
    /// <summary>Initializes a new instance of the <see cref="DensityCurveEffect"/> class.</summary>
    /// <param name="channel">The channels.</param>
    /// <param name="density">The density, -255 to 255.</param>
    public DensityCurveEffect(CurveChannel channel, int density)
        : base(channel)
    {
        CheckRange(density, -255, 255);
        Density = density;
    }

    /// <summary>Gets the density.</summary>
    public int Density { get; }

    private protected override double Curve(double v) => v + (Density / 255.0);
}

/// <summary>Contrast, -100 to 100: values pushed from or toward the middle.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class ContrastCurveEffect : ColorCurveEffect
{
    /// <summary>Initializes a new instance of the <see cref="ContrastCurveEffect"/> class.</summary>
    /// <param name="channel">The channels.</param>
    /// <param name="contrast">The contrast, -100 to 100.</param>
    public ContrastCurveEffect(CurveChannel channel, int contrast)
        : base(channel)
    {
        CheckRange(contrast, -100, 100);
        Contrast = contrast;
    }

    /// <summary>Gets the contrast.</summary>
    public int Contrast { get; }

    private protected override double Curve(double v) => v < 0.5 ? 0.5 * Bend(-Contrast / 100.0, v * 2) : 0.5 + (0.5 * Bend(Contrast / 100.0, (v * 2) - 1));
}

/// <summary>Highlights, -100 to 100: the upper values raised or lowered.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class HighlightCurveEffect : ColorCurveEffect
{
    /// <summary>Initializes a new instance of the <see cref="HighlightCurveEffect"/> class.</summary>
    /// <param name="channel">The channels.</param>
    /// <param name="highlight">The change, -100 to 100.</param>
    public HighlightCurveEffect(CurveChannel channel, int highlight)
        : base(channel)
    {
        CheckRange(highlight, -100, 100);
        Highlight = highlight;
    }

    /// <summary>Gets the change.</summary>
    public int Highlight { get; }

    private protected override double Curve(double v) => v <= 0.5 ? v : 0.5 + (0.5 * Bend(Highlight / 100.0, (v * 2) - 1));
}

/// <summary>Midtones, -100 to 100: the middle values raised or lowered.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class MidtoneCurveEffect : ColorCurveEffect
{
    /// <summary>Initializes a new instance of the <see cref="MidtoneCurveEffect"/> class.</summary>
    /// <param name="channel">The channels.</param>
    /// <param name="midtone">The change, -100 to 100.</param>
    public MidtoneCurveEffect(CurveChannel channel, int midtone)
        : base(channel)
    {
        CheckRange(midtone, -100, 100);
        Midtone = midtone;
    }

    /// <summary>Gets the change.</summary>
    public int Midtone { get; }

    private protected override double Curve(double v) => Math.Pow(v, LevelsEffect.Gamma(Midtone));
}

/// <summary>Shadows, -100 to 100: the lower values raised or lowered.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class ShadowCurveEffect : ColorCurveEffect
{
    /// <summary>Initializes a new instance of the <see cref="ShadowCurveEffect"/> class.</summary>
    /// <param name="channel">The channels.</param>
    /// <param name="shadow">The change, -100 to 100.</param>
    public ShadowCurveEffect(CurveChannel channel, int shadow)
        : base(channel)
    {
        CheckRange(shadow, -100, 100);
        Shadow = shadow;
    }

    /// <summary>Gets the change.</summary>
    public int Shadow { get; }

    private protected override double Curve(double v) => v >= 0.5 ? v : 0.5 * Bend(Shadow / 100.0, v * 2);
}

/// <summary>White saturation, 0 to 255: the value that becomes white.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class WhiteSaturationCurveEffect : ColorCurveEffect
{
    /// <summary>Initializes a new instance of the <see cref="WhiteSaturationCurveEffect"/> class.</summary>
    /// <param name="channel">The channels.</param>
    /// <param name="whiteSaturation">The value that becomes white, 0 to 255.</param>
    public WhiteSaturationCurveEffect(CurveChannel channel, int whiteSaturation)
        : base(channel)
    {
        CheckRange(whiteSaturation, 0, 255);
        WhiteSaturation = whiteSaturation;
    }

    /// <summary>Gets the value that becomes white.</summary>
    public int WhiteSaturation { get; }

    private protected override double Curve(double v) => WhiteSaturation == 0 ? 1 : v * 255 / WhiteSaturation;
}

/// <summary>Black saturation, 0 to 255: the value that becomes black.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public sealed class BlackSaturationCurveEffect : ColorCurveEffect
{
    /// <summary>Initializes a new instance of the <see cref="BlackSaturationCurveEffect"/> class.</summary>
    /// <param name="channel">The channels.</param>
    /// <param name="blackSaturation">The value that becomes black, 0 to 255.</param>
    public BlackSaturationCurveEffect(CurveChannel channel, int blackSaturation)
        : base(channel)
    {
        CheckRange(blackSaturation, 0, 255);
        BlackSaturation = blackSaturation;
    }

    /// <summary>Gets the value that becomes black.</summary>
    public int BlackSaturation { get; }

    private protected override double Curve(double v) => BlackSaturation == 255 ? 0 : ((v * 255) - BlackSaturation) / (255 - BlackSaturation);
}
