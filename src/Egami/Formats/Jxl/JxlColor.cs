namespace Egami.Formats.Jxl;

/// <summary>
/// Color for decoded frames: XYB to linear sRGB through the opsin inverse matrix (L.2), and the transfer functions that encode
/// linear light into the file's own color space. Ported from jxl-oxide's jxl-color (see THIRD-PARTY-NOTICES).
/// </summary>
internal static class JxlColor
{
    private static readonly float[] DefaultOpsin =
    [
        11.031566901960783f, -9.866943921568629f, -0.16462299647058826f,
        -3.254147380392157f, 4.418770392156863f, -0.16462299647058826f,
        -3.6588512862745097f, 2.7129230470588235f, 1.9459282392156863f,
        -0.0037930732552754493f, -0.0037930732552754493f, -0.0037930732552754493f,
    ];

    /// <summary>Converts X, Y, B planes in place to linear sRGB (R, G, B), with 1.0 at the intensity target scaled to 255 nits.</summary>
    internal static void XybToLinear(float[][] planes, int count, JxlImageHeader image)
    {
        float[] m = image.OpsinInverse ?? DefaultOpsin;
        float b0 = m[9];
        float b1 = m[10];
        float b2 = m[11];
        float c0 = MathF.Cbrt(b0);
        float c1 = MathF.Cbrt(b1);
        float c2 = MathF.Cbrt(b2);
        float scale = 255 / image.ToneMapping.IntensityTarget;
        float[] x = planes[0];
        float[] y = planes[1];
        float[] b = planes[2];
        for (int i = 0; i < count; i++)
        {
            float gl = y[i] + x[i] - c0;
            float gm = y[i] - x[i] - c1;
            float gs = b[i] - c2;
            float l = MathF.FusedMultiplyAdd(gl * gl, gl, b0) * scale;
            float mm = MathF.FusedMultiplyAdd(gm * gm, gm, b1) * scale;
            float s = MathF.FusedMultiplyAdd(gs * gs, gs, b2) * scale;
            x[i] = (m[0] * l) + (m[1] * mm) + (m[2] * s);
            y[i] = (m[3] * l) + (m[4] * mm) + (m[5] * s);
            b[i] = (m[6] * l) + (m[7] * mm) + (m[8] * s);
        }
    }

    /// <summary>Gets whether linear sRGB converts to the color encoding by a transfer function alone (sRGB primaries, D65).</summary>
    internal static bool TransferOnly(JxlColorEncoding c)
    {
        return !c.WantIcc && c.WhitePoint == 1 && (c.ColorSpace == JxlColorSpace.Gray || c.Primaries == 1) && c.TransferFunction is not (16 or 18);
    }

    /// <summary>Encodes linear light with the color encoding's transfer function.</summary>
    internal static void Encode(float[] plane, int count, JxlColorEncoding c)
    {
        if (c.Gamma != 0)
        {
            float g = c.Gamma / 1e7f;
            for (int i = 0; i < count; i++)
            {
                float v = plane[i];
                plane[i] = MathF.CopySign(MathF.Pow(MathF.Abs(v), g), v);
            }

            return;
        }

        switch (c.TransferFunction)
        {
            case 8:
                return;
            case 1:
                for (int i = 0; i < count; i++)
                {
                    float v = plane[i];
                    float a = MathF.Abs(v);
                    plane[i] = MathF.CopySign(a < 0.018053968510807f ? 4.5f * a : (1.09929682680944f * MathF.Pow(a, 0.45f)) - 0.09929682680944f, v);
                }

                return;
            case 17:
                for (int i = 0; i < count; i++)
                {
                    float v = plane[i];
                    plane[i] = MathF.CopySign(MathF.Pow(MathF.Abs(v), 1 / 2.6f), v);
                }

                return;
            default:
                for (int i = 0; i < count; i++)
                {
                    float v = plane[i];
                    float a = MathF.Abs(v);
                    plane[i] = MathF.CopySign(a <= 0.0031308f ? 12.92f * a : (1.055f * MathF.Pow(a, 1 / 2.4f)) - 0.055f, v);
                }

                return;
        }
    }
}
