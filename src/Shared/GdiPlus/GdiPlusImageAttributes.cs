using Egami.Operations;
using Egami.Pixels;

namespace Egami.GdiPlus;

/// <summary>
/// The color adjustments of one GDI+ color adjust type (default, bitmap, brush, pen or text): a color matrix with its gray
/// handling, gamma, threshold, a color key, a remap table and an output channel. Unset settings leave colors as they are.
/// </summary>
internal sealed class GdiPlusColorAdjust
{
    /// <summary>ColorMatrixFlagsDefault: every color goes through the color matrix.</summary>
    public const int MatrixDefault = 0;

    /// <summary>ColorMatrixFlagsSkipGrays: grays (red, green and blue equal) are left as they are.</summary>
    public const int MatrixSkipGrays = 1;

    /// <summary>ColorMatrixFlagsAltGray: grays go through the gray matrix instead.</summary>
    public const int MatrixAltGray = 2;

    /// <summary>Gets or sets a value indicating whether the type's adjustments are turned off (SetNoOp).</summary>
    public bool NoOp { get; set; }

    /// <summary>Gets or sets the 5 by 5 color matrix in GDI+ order (a row vector of red, green, blue, alpha and 1 times the matrix), or null.</summary>
    public float[]? ColorMatrix { get; set; }

    /// <summary>Gets or sets the 5 by 5 gray matrix used with <see cref="MatrixAltGray"/>, or null.</summary>
    public float[]? GrayMatrix { get; set; }

    /// <summary>Gets or sets how the color matrix treats grays: <see cref="MatrixDefault"/>, <see cref="MatrixSkipGrays"/> or <see cref="MatrixAltGray"/>.</summary>
    public int MatrixFlags { get; set; }

    /// <summary>Gets or sets the gamma each color channel is raised to, or null.</summary>
    public float? Gamma { get; set; }

    /// <summary>Gets or sets the threshold from 0 to 1: a color channel above it becomes full, else zero; or null.</summary>
    public float? Threshold { get; set; }

    /// <summary>Gets or sets the lowest color of the color key's range, or null for no key.</summary>
    public Rgba8? ColorKeyLow { get; set; }

    /// <summary>Gets or sets the highest color of the color key's range.</summary>
    public Rgba8 ColorKeyHigh { get; set; }

    /// <summary>Gets or sets the colors the remap table replaces and their replacements, or null.</summary>
    public (Rgba8 Old, Rgba8 New)[]? RemapTable { get; set; }

    /// <summary>Gets or sets the CMYK output channel (0 cyan, 1 magenta, 2 yellow, 3 black) shown as gray, or null.</summary>
    public int? OutputChannel { get; set; }

    /// <summary>Gets a value indicating whether any setting is made.</summary>
    public bool Any => NoOp || ColorMatrix is not null || Gamma is not null || Threshold is not null || ColorKeyLow is not null || RemapTable is not null || OutputChannel is not null;
}

/// <summary>
/// GDI+ image attributes: color adjustments per color adjust type, and the wrap mode and clamp color for drawing outside an
/// image. Adjustments run in this order: the remap table, the color key, the color matrix, gamma, threshold, then the output
/// channel.
/// </summary>
internal sealed class GdiPlusImageAttributes
{
    /// <summary>ColorAdjustTypeDefault: the adjustments for every type that has none of its own.</summary>
    public const int TypeDefault = 0;

    /// <summary>ColorAdjustTypeBitmap.</summary>
    public const int TypeBitmap = 1;

    /// <summary>ColorAdjustTypeBrush.</summary>
    public const int TypeBrush = 2;

    /// <summary>ColorAdjustTypePen.</summary>
    public const int TypePen = 3;

    /// <summary>ColorAdjustTypeText.</summary>
    public const int TypeText = 4;

    private readonly GdiPlusColorAdjust[] _types = [new(), new(), new(), new(), new()];

    /// <summary>Gets or sets the GDI+ wrap mode (0 tile to 4 clamp) for drawing beyond the image.</summary>
    public int WrapMode { get; set; } = GdiPlusBrushes.WrapClamp;

    /// <summary>Gets or sets the color drawn beyond the image with the clamp wrap mode.</summary>
    public Rgba8 ClampColor { get; set; }

    /// <summary>Gets the settings of one color adjust type.</summary>
    public GdiPlusColorAdjust this[int type] => _types[type];

    /// <summary>The settings that apply to a type: its own when it has any, else the default type's.</summary>
    public GdiPlusColorAdjust For(int type) => type != TypeDefault && _types[type].Any ? _types[type] : _types[TypeDefault];

    /// <summary>A copy of the image with the type's adjustments applied, or the image itself when none apply.</summary>
    public Raster<Rgba8> Apply(Raster<Rgba8> image, int type)
    {
        GdiPlusColorAdjust adjust = For(type);
        if (adjust.NoOp || !adjust.Any)
        {
            return image;
        }

        Raster<Rgba8> copy = image.Copy();
        for (int y = 0; y < copy.Height; y++)
        {
            Apply(copy.Row(y), adjust);
        }

        return copy;
    }

    /// <summary>Applies one type's adjustments to pixels in place.</summary>
    public static void Apply(Span<Rgba8> pixels, GdiPlusColorAdjust adjust)
    {
        if (adjust.NoOp)
        {
            return;
        }

        byte[]? gamma = adjust.Gamma is float g && g > 0 && g != 1 ? GammaTable(g) : null;
        int threshold = adjust.Threshold is float t ? (int)MathF.Round(Math.Clamp(t, 0, 1) * 255) : -1;
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba8 c = pixels[i];
            if (adjust.RemapTable is { } table)
            {
                foreach ((Rgba8 from, Rgba8 to) in table)
                {
                    if (c == from)
                    {
                        c = to;
                        break;
                    }
                }
            }

            if (adjust.ColorKeyLow is Rgba8 low && InRange(c, low, adjust.ColorKeyHigh))
            {
                c = default;
            }

            if (adjust.ColorMatrix is { } matrix)
            {
                bool gray = c.R == c.G && c.G == c.B;
                if (!gray || adjust.MatrixFlags == GdiPlusColorAdjust.MatrixDefault)
                {
                    c = Transform(c, matrix);
                }
                else if (adjust.MatrixFlags == GdiPlusColorAdjust.MatrixAltGray && adjust.GrayMatrix is { } grays)
                {
                    c = Transform(c, grays);
                }
            }

            if (gamma is not null)
            {
                c = new Rgba8(gamma[c.R], gamma[c.G], gamma[c.B], c.A);
            }

            if (threshold >= 0)
            {
                c = new Rgba8(c.R > threshold ? (byte)255 : (byte)0, c.G > threshold ? (byte)255 : (byte)0, c.B > threshold ? (byte)255 : (byte)0, c.A);
            }

            if (adjust.OutputChannel is int channel)
            {
                byte level = (byte)(255 - Ink(c, channel));
                c = new Rgba8(level, level, level, c.A);
            }

            pixels[i] = c;
        }
    }

    private static bool InRange(Rgba8 c, Rgba8 low, Rgba8 high) =>
        c.R >= low.R && c.R <= high.R && c.G >= low.G && c.G <= high.G && c.B >= low.B && c.B <= high.B;

    /// <summary>A color times a GDI+ 5 by 5 matrix: the row vector (red, green, blue, alpha, 1) in 0 to 1, clamped and rounded.</summary>
    public static Rgba8 Transform(Rgba8 c, ReadOnlySpan<float> m)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = c.A / 255f;
        Span<byte> o = stackalloc byte[4];
        for (int j = 0; j < 4; j++)
        {
            o[j] = (byte)((Math.Clamp((r * m[j]) + (g * m[5 + j]) + (b * m[10 + j]) + (a * m[15 + j]) + m[20 + j], 0, 1) * 255) + 0.5f);
        }

        return new Rgba8(o[0], o[1], o[2], o[3]);
    }

    /// <summary>A GDI+ 5 by 5 color matrix (row vectors: a color times the matrix) as Egami's four rows of five.</summary>
    public static ColorMatrix ToColorMatrix(ReadOnlySpan<float> m)
    {
        float[] v = new float[20];
        for (int output = 0; output < 4; output++)
        {
            for (int input = 0; input < 4; input++)
            {
                v[(output * 5) + input] = m[(input * 5) + output];
            }

            v[(output * 5) + 4] = m[20 + output];
        }

        return new ColorMatrix(v);
    }

    private static byte[] GammaTable(float gamma)
    {
        byte[] table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = (byte)((MathF.Pow(i / 255f, gamma) * 255) + 0.5f);
        }

        return table;
    }

    /// <summary>A color's ink in one CMYK channel, 0 to 255: cyan, magenta and yellow less the black they share, and the black.</summary>
    private static int Ink(Rgba8 c, int channel)
    {
        int cyan = 255 - c.R, magenta = 255 - c.G, yellow = 255 - c.B;
        int black = Math.Min(cyan, Math.Min(magenta, yellow));
        return channel switch
        {
            0 => cyan - black,
            1 => magenta - black,
            2 => yellow - black,
            _ => black,
        };
    }
}
