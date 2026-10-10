using Egami.Pixels;

namespace Egami.Colors;

/// <summary>
/// A Y'CbCr color: luma and two chroma differences of encoded sRGB, all 0 to 1 (chroma centered on 0.5). The members without
/// options use BT.601 at full range, as JPEG does; overloads take a <see cref="YuvMatrix"/> and <see cref="YuvRange"/>, from the
/// coefficient table planar YUV images share.
/// </summary>
/// <param name="Y">Luma.</param>
/// <param name="Cb">Blue difference, 0.5 for gray.</param>
/// <param name="Cr">Red difference, 0.5 for gray.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public readonly record struct YCbCrColor(float Y, float Cb, float Cr) : IColorValue<YCbCrColor>
{
    /// <inheritdoc/>
    public static YCbCrColor FromXyz(XyzColor xyz) => FromSrgb(ColorValueMath.SrgbFromXyz(xyz.Vector), YuvMatrix.Bt601, YuvRange.Full);

    /// <inheritdoc/>
    public static YCbCrColor From(Rgba8 color) => From(color, YuvMatrix.Bt601);

    /// <inheritdoc/>
    public static YCbCrColor From(RgbaF32 color) => From(color, YuvMatrix.Bt601);

    /// <summary>Returns the value of an sRGB pixel with a chosen matrix and range.</summary>
    /// <param name="color">The pixel.</param>
    /// <param name="matrix">The matrix.</param>
    /// <param name="range">The range. Defaults to full.</param>
    /// <returns>The value.</returns>
    public static YCbCrColor From(Rgba8 color, YuvMatrix matrix, YuvRange range = YuvRange.Full) => FromSrgb(new Vector3d(color.R / 255.0, color.G / 255.0, color.B / 255.0), matrix, range);

    /// <summary>Returns the value of an sRGB pixel with a chosen matrix and range.</summary>
    /// <param name="color">The pixel.</param>
    /// <param name="matrix">The matrix.</param>
    /// <param name="range">The range. Defaults to full.</param>
    /// <returns>The value.</returns>
    public static YCbCrColor From(RgbaF32 color, YuvMatrix matrix, YuvRange range = YuvRange.Full) => FromSrgb(new Vector3d(color.R, color.G, color.B), matrix, range);

    /// <inheritdoc/>
    public XyzColor ToXyz()
    {
        Vector3d c = ToSrgb(YuvMatrix.Bt601, YuvRange.Full);
        return XyzColor.Of(ColorValueMath.XyzFromSrgb(c.X, c.Y, c.Z));
    }

    /// <inheritdoc/>
    public Rgba8 ToRgba8() => ToRgba8(YuvMatrix.Bt601);

    /// <summary>Returns the color as an opaque sRGB pixel, read with a chosen matrix and range.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <param name="range">The range. Defaults to full.</param>
    /// <returns>The pixel, each channel clamped.</returns>
    public Rgba8 ToRgba8(YuvMatrix matrix, YuvRange range = YuvRange.Full) => ColorValueMath.ToRgba8(ToSrgb(matrix, range));

    /// <inheritdoc/>
    public RgbaF32 ToRgbaF32() => ToRgbaF32(YuvMatrix.Bt601);

    /// <summary>Returns the color as an opaque sRGB pixel, read with a chosen matrix and range, unclamped.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <param name="range">The range. Defaults to full.</param>
    /// <returns>The pixel.</returns>
    public RgbaF32 ToRgbaF32(YuvMatrix matrix, YuvRange range = YuvRange.Full) => ColorValueMath.ToRgbaF32(ToSrgb(matrix, range));

    /// <inheritdoc/>
    public bool IsCloseTo(YCbCrColor other, float tolerance = 1e-4f) =>
        ColorValueMath.Close(Y, other.Y, tolerance) && ColorValueMath.Close(Cb, other.Cb, tolerance) && ColorValueMath.Close(Cr, other.Cr, tolerance);

    internal static YCbCrColor FromSrgb(Vector3d c, YuvMatrix matrix, YuvRange range)
    {
        double y, cb, cr;
        if (matrix == YuvMatrix.Identity)
        {
            (y, cb, cr) = (c.Y, c.Z, c.X);
        }
        else
        {
            (double kr, double kb) = YuvCoefficients.Of(matrix);
            y = (kr * c.X) + ((1 - kr - kb) * c.Y) + (kb * c.Z);
            cb = ((c.Z - y) / (2 * (1 - kb))) + 0.5;
            cr = ((c.X - y) / (2 * (1 - kr))) + 0.5;
        }

        if (range == YuvRange.Limited)
        {
            y = (16 + (219 * y)) / 255;
            cb = (128 + (224 * (cb - 0.5))) / 255;
            cr = (128 + (224 * (cr - 0.5))) / 255;
        }

        return new YCbCrColor((float)y, (float)cb, (float)cr);
    }

    internal Vector3d ToSrgb(YuvMatrix matrix, YuvRange range)
    {
        double y = Y, cb = Cb, cr = Cr;
        if (range == YuvRange.Limited)
        {
            y = ((y * 255) - 16) / 219;
            cb = (((cb * 255) - 128) / 224) + 0.5;
            cr = (((cr * 255) - 128) / 224) + 0.5;
        }

        if (matrix == YuvMatrix.Identity)
        {
            return new Vector3d(cr, y, cb);
        }

        (double kr, double kb) = YuvCoefficients.Of(matrix);
        double r = y + (2 * (1 - kr) * (cr - 0.5));
        double b = y + (2 * (1 - kb) * (cb - 0.5));
        return new Vector3d(r, (y - (kr * r) - (kb * b)) / (1 - kr - kb), b);
    }
}

/// <summary>
/// A CMYK color: ink amounts 0 to 1, 0 being no ink, by the plain formula (K = 1 − max(R, G, B), C = (1 − R − K) / (1 − K)) on
/// encoded sRGB. Real inks need a print profile: <see cref="From(Rgba8, ColorProfile, ColorTransformOptions?)"/> converts
/// through one.
/// </summary>
/// <param name="C">Cyan.</param>
/// <param name="M">Magenta.</param>
/// <param name="Y">Yellow.</param>
/// <param name="K">Black.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public readonly record struct CmykColor(float C, float M, float Y, float K) : IColorValue<CmykColor>
{
    /// <inheritdoc/>
    public static CmykColor FromXyz(XyzColor xyz) => FromSrgb(ColorValueMath.SrgbFromXyz(xyz.Vector));

    /// <inheritdoc/>
    public static CmykColor From(Rgba8 color) => FromSrgb(new Vector3d(color.R / 255.0, color.G / 255.0, color.B / 255.0));

    /// <inheritdoc/>
    public static CmykColor From(RgbaF32 color) => FromSrgb(new Vector3d(color.R, color.G, color.B));

    /// <summary>Returns the inks a print profile uses for an sRGB pixel.</summary>
    /// <param name="color">The pixel.</param>
    /// <param name="cmyk">The CMYK profile.</param>
    /// <param name="options">The intent and black point compensation, or <see langword="null"/> for the defaults.</param>
    /// <returns>The inks.</returns>
    /// <exception cref="ArgumentException"><paramref name="cmyk"/> is not a CMYK profile.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="cmyk-ink"/>
    /// </example>
    public static CmykColor From(Rgba8 color, ColorProfile cmyk, ColorTransformOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cmyk);
        if (cmyk.Model != ColorModel.Cmyk)
        {
            throw new ArgumentException("The profile is not a CMYK profile.", nameof(cmyk));
        }

        Span<Cmyk16> inks = stackalloc Cmyk16[1];
        ColorTransform.Create(ColorProfile.Srgb, cmyk, options).Apply<Rgba8, Cmyk16>([color], inks);
        return new CmykColor(inks[0].C / 65535f, inks[0].M / 65535f, inks[0].Y / 65535f, inks[0].K / 65535f);
    }

    /// <inheritdoc/>
    public XyzColor ToXyz()
    {
        Vector3d c = ToSrgb();
        return XyzColor.Of(ColorValueMath.XyzFromSrgb(c.X, c.Y, c.Z));
    }

    /// <inheritdoc/>
    public Rgba8 ToRgba8() => ColorValueMath.ToRgba8(ToSrgb());

    /// <summary>Returns the sRGB pixel a print profile gives these inks.</summary>
    /// <param name="cmyk">The CMYK profile.</param>
    /// <param name="options">The intent and black point compensation, or <see langword="null"/> for the defaults.</param>
    /// <returns>The pixel.</returns>
    /// <exception cref="ArgumentException"><paramref name="cmyk"/> is not a CMYK profile.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="cmyk-ink"/>
    /// </example>
    public Rgba8 ToRgba8(ColorProfile cmyk, ColorTransformOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cmyk);
        if (cmyk.Model != ColorModel.Cmyk)
        {
            throw new ArgumentException("The profile is not a CMYK profile.", nameof(cmyk));
        }

        Span<Rgba8> rgb = stackalloc Rgba8[1];
        Cmyk16 inks = new(Word(C), Word(M), Word(Y), Word(K));
        ColorTransform.Create(cmyk, ColorProfile.Srgb, options).Apply<Cmyk16, Rgba8>([inks], rgb);
        return rgb[0];

        static ushort Word(float v) => (ushort)Math.Round(Math.Clamp(v, 0f, 1f) * 65535);
    }

    /// <inheritdoc/>
    public RgbaF32 ToRgbaF32() => ColorValueMath.ToRgbaF32(ToSrgb());

    /// <inheritdoc/>
    public bool IsCloseTo(CmykColor other, float tolerance = 1e-4f) =>
        ColorValueMath.Close(C, other.C, tolerance) && ColorValueMath.Close(M, other.M, tolerance) && ColorValueMath.Close(Y, other.Y, tolerance) && ColorValueMath.Close(K, other.K, tolerance);

    internal static CmykColor FromSrgb(Vector3d c)
    {
        double max = Math.Max(c.X, Math.Max(c.Y, c.Z));
        if (max <= 0)
        {
            return new CmykColor(0, 0, 0, 1);
        }

        return new CmykColor((float)((max - c.X) / max), (float)((max - c.Y) / max), (float)((max - c.Z) / max), (float)(1 - max));
    }

    internal Vector3d ToSrgb()
    {
        double k = 1 - K;
        return new Vector3d((1 - C) * k, (1 - M) * k, (1 - Y) * k);
    }
}
