using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Exr;

/// <summary>
/// Turns luminance and subsampled chroma (Y, RY, BY: OpenEXR's luminance/chroma images) into RGBA, as OpenEXR's
/// RgbaInputFile does, so the pixels match it bit for bit: chroma reconstructed horizontally on the lines that carry it and
/// vertically on the others with the same 27-tap filter, converted with the luminance weights of the chromaticities, and
/// over-saturated pixels pulled back toward their neighbors' saturation. Every intermediate value is rounded to half where
/// OpenEXR's is. Translated from OpenEXR's ImfRgbaYca.cpp and ImfRgbaFile.cpp (BSD-3-Clause; see THIRD-PARTY-NOTICES).
/// </summary>
internal static class ExrYca
{
    private const int N2 = 13;

    /// <summary>The reconstruction filter's taps, at offsets −13, −11, …, 13 (only samples that carry chroma).</summary>
    private static readonly float[] Taps =
        [0.002128f, -0.007540f, 0.019597f, -0.043159f, 0.087929f, -0.186077f, 0.627123f, 0.627123f, -0.186077f, 0.087929f, -0.043159f, 0.019597f, -0.007540f, 0.002128f];

    /// <summary>A pixel as OpenEXR's Rgba holds it: four halves; R and B carry chroma before conversion, G luminance.</summary>
    private struct Rgba
    {
        public Half R;
        public Half G;
        public Half B;
        public Half A;
    }

    /// <summary>Returns the data window's pixels, top row first.</summary>
    public static RgbaF16[] Convert(ExrPlanes planes, ExrPartHeader part, Selection selection)
    {
        int width = part.Width, height = part.Height;
        (float wx, float wy, float wz) = Weights(part);

        // The lines as OpenEXR buffers them: chroma reconstructed across on the lines that carry it.
        Rgba[][] lines = new Rgba[height][];
        for (int y = 0; y < height; y++)
        {
            lines[y] = ReadLine(planes, part, selection, part.MinY + y);
        }

        RgbaF16[] output = new RgbaF16[width * height];
        Rgba[] above = new Rgba[width], here = new Rgba[width], below = new Rgba[width];
        for (int y = part.MinY; y <= part.MaxY; y++)
        {
            ToRgb(lines, part, y - 1, wx, wy, wz, above);
            ToRgb(lines, part, y, wx, wy, wz, here);
            ToRgb(lines, part, y + 1, wx, wy, wz, below);
            Span<Rgba> target = MemoryMarshal.Cast<RgbaF16, Rgba>(output.AsSpan((y - part.MinY) * width, width));
            FixSaturation(wx, wy, wz, above, here, below, target);
        }

        return output;
    }

    /// <summary>Reads line <paramref name="y"/>: luminance and alpha, and on even lines chroma reconstructed horizontally.</summary>
    private static Rgba[] ReadLine(ExrPlanes planes, ExrPartHeader part, Selection selection, int y)
    {
        int width = part.Width;
        int yIndex = selection.Roles[0], ryIndex = selection.Roles[1], byIndex = selection.Roles[2], aIndex = selection.Roles[3];
        Rgba[] tmp = new Rgba[width + (2 * N2)];
        ReadOnlySpan<ushort> lum = Row(planes, yIndex, y - part.MinY, width);
        ReadOnlySpan<ushort> alpha = aIndex >= 0 ? Row(planes, aIndex, y - part.MinY, width) : [];
        for (int x = 0; x < width; x++)
        {
            tmp[N2 + x].G = BitConverter.UInt16BitsToHalf(lum[x]);
            tmp[N2 + x].A = aIndex >= 0 ? BitConverter.UInt16BitsToHalf(alpha[x]) : (Half)1f;
        }

        if ((y & 1) != 0)
        {
            return tmp[N2..(N2 + width)];
        }

        // Chroma samples sit on the even columns of even lines.
        int chromaRow = (y - planes.FirstY[Math.Max(ryIndex, byIndex)]) / 2;
        ReadOnlySpan<ushort> ry = ryIndex >= 0 ? Row(planes, ryIndex, chromaRow, planes.Widths[ryIndex]) : [];
        ReadOnlySpan<ushort> by = byIndex >= 0 ? Row(planes, byIndex, chromaRow, planes.Widths[byIndex]) : [];
        for (int x = 0; x < width; x += 2)
        {
            tmp[N2 + x].R = ryIndex >= 0 ? BitConverter.UInt16BitsToHalf(ry[x / 2]) : default;
            tmp[N2 + x].B = byIndex >= 0 ? BitConverter.UInt16BitsToHalf(by[x / 2]) : default;
        }

        // Pad with the first and the last chroma-carrying pixels.
        for (int i = 0; i < N2; i++)
        {
            tmp[i] = tmp[N2];
            tmp[width + N2 + i] = tmp[width + N2 - 2];
        }

        Rgba[] line = new Rgba[width];
        for (int j = 0; j < width; j++)
        {
            int i = N2 + j;
            if ((j & 1) != 0)
            {
                float r = 0, b = 0;
                for (int k = 0; k < 14; k++)
                {
                    int o = i - 13 + (2 * k);
                    r = k == 0 ? (float)tmp[o].R * Taps[k] : r + ((float)tmp[o].R * Taps[k]);
                    b = k == 0 ? (float)tmp[o].B * Taps[k] : b + ((float)tmp[o].B * Taps[k]);
                }

                line[j].R = (Half)r;
                line[j].B = (Half)b;
            }
            else
            {
                line[j].R = tmp[i].R;
                line[j].B = tmp[i].B;
            }

            line[j].G = tmp[i].G;
            line[j].A = tmp[i].A;
        }

        return line;
    }

    /// <summary>
    /// Converts nominal line <paramref name="n"/>, which may lie past the window (lines are fetched clamped as OpenEXR
    /// does), to RGB: odd lines get their chroma from the even lines around them.
    /// </summary>
    private static void ToRgb(Rgba[][] lines, ExrPartHeader part, int n, float wx, float wy, float wz, Rgba[] target)
    {
        int width = part.Width;
        Rgba[] center = Fetch(lines, part, n);
        if ((n & 1) != 0)
        {
            Rgba[][] taps = new Rgba[14][];
            for (int k = 0; k < 14; k++)
            {
                taps[k] = Fetch(lines, part, n - 13 + (2 * k));
            }

            for (int i = 0; i < width; i++)
            {
                float r = 0, b = 0;
                for (int k = 0; k < 14; k++)
                {
                    r = k == 0 ? (float)taps[k][i].R * Taps[k] : r + ((float)taps[k][i].R * Taps[k]);
                    b = k == 0 ? (float)taps[k][i].B * Taps[k] : b + ((float)taps[k][i].B * Taps[k]);
                }

                Rgba yca = new() { R = (Half)r, G = center[i].G, B = (Half)b, A = center[i].A };
                target[i] = ToRgba(yca, wx, wy, wz);
            }
        }
        else
        {
            for (int i = 0; i < width; i++)
            {
                target[i] = ToRgba(center[i], wx, wy, wz);
            }
        }
    }

    /// <summary>Returns line <paramref name="n"/> as OpenEXR reads it: above the window the first line, below it the last but one.</summary>
    private static Rgba[] Fetch(Rgba[][] lines, ExrPartHeader part, int n)
    {
        int y = n < part.MinY ? part.MinY : n > part.MaxY ? part.MaxY - 1 : n;
        return lines[y - part.MinY];
    }

    /// <summary>YCAtoRGBA: (RY + 1) × Y and (BY + 1) × Y, green from the luminance weights; no chroma gives gray.</summary>
    private static Rgba ToRgba(Rgba yca, float wx, float wy, float wz)
    {
        if ((float)yca.R == 0 && (float)yca.B == 0)
        {
            return new Rgba { R = yca.G, G = yca.G, B = yca.G, A = yca.A };
        }

        float y = (float)yca.G;
        float r = ((float)yca.R + 1) * y;
        float b = ((float)yca.B + 1) * y;
        float g = (y - (r * wx) - (b * wz)) / wy;
        return new Rgba { R = (Half)r, G = (Half)g, B = (Half)b, A = yca.A };
    }

    /// <summary>fixSaturation: a pixel much more saturated than the pixels around it is desaturated toward their mean.</summary>
    private static void FixSaturation(float wx, float wy, float wz, Rgba[] above, Rgba[] here, Rgba[] below, Span<Rgba> output)
    {
        int n = here.Length;
        float a2 = Saturation(above[0]), a1 = a2;
        float b2 = Saturation(below[0]), b1 = b2;
        for (int i = 0; i < n; i++)
        {
            float a0 = a1;
            a1 = a2;
            float b0 = b1;
            b1 = b2;
            if (i < n - 1)
            {
                a2 = Saturation(above[i + 1]);
                b2 = Saturation(below[i + 1]);
            }

            float mean = MathF.Min(1.0f, 0.25f * (a0 + a2 + b0 + b2));
            Rgba input = here[i];
            float s = Saturation(input);
            if (s > mean)
            {
                float max = MathF.Min(1.0f, 1 - ((1 - mean) * 0.25f));
                if (s > max)
                {
                    output[i] = Desaturate(input, max / s, wx, wy, wz);
                    continue;
                }
            }

            output[i] = input;
        }
    }

    private static float Saturation(Rgba p)
    {
        float max = (float)Max(p.R, Max(p.G, p.B));
        float min = (float)Min(p.R, Min(p.G, p.B));
        return max > 0 ? 1 - (min / max) : 0;
    }

    private static Rgba Desaturate(Rgba input, float f, float wx, float wy, float wz)
    {
        float max = (float)Max(input.R, Max(input.G, input.B));
        Rgba output = new()
        {
            R = (Half)MathF.Max(max - ((max - (float)input.R) * f), 0.0f),
            G = (Half)MathF.Max(max - ((max - (float)input.G) * f), 0.0f),
            B = (Half)MathF.Max(max - ((max - (float)input.B) * f), 0.0f),
            A = input.A,
        };
        float yIn = ((float)input.R * wx) + ((float)input.G * wy) + ((float)input.B * wz);
        float yOut = ((float)output.R * wx) + ((float)output.G * wy) + ((float)output.B * wz);
        if (yOut > 0)
        {
            output.R = (Half)((float)output.R * (yIn / yOut));
            output.G = (Half)((float)output.G * (yIn / yOut));
            output.B = (Half)((float)output.B * (yIn / yOut));
        }

        return output;
    }

    /// <summary>std::max on halves: the first unless the second is greater.</summary>
    private static Half Max(Half a, Half b) => (float)a < (float)b ? b : a;

    /// <summary>std::min on halves: the first unless the second is less.</summary>
    private static Half Min(Half a, Half b) => (float)b < (float)a ? b : a;

    /// <summary>
    /// The luminance weights: the Y row of the RGB to XYZ matrix of the part's chromaticities (Rec. 709 without them),
    /// normalized to sum to 1, in single precision as OpenEXR computes them (Imf::RGBtoXYZ, RgbaYca::computeYw).
    /// </summary>
    internal static (float X, float Y, float Z) Weights(ExrPartHeader part)
    {
        float rx = 0.64f, ry = 0.33f, gx = 0.3f, gy = 0.6f, bx = 0.15f, by = 0.06f, wx = 0.3127f, wy = 0.329f;
        foreach (ExrHeaderEntry attribute in part.Info.Attributes)
        {
            if (attribute.Name == "chromaticities" && attribute.Type == "chromaticities" && attribute.Value.Length >= 32)
            {
                ReadOnlySpan<byte> v = attribute.Value.Span;
                rx = F(v, 0);
                ry = F(v, 4);
                gx = F(v, 8);
                gy = F(v, 12);
                bx = F(v, 16);
                by = F(v, 20);
                wx = F(v, 24);
                wy = F(v, 28);
            }
        }

        const float Y = 1;
        float x = wx * Y / wy;
        float z = (1 - wx - wy) * Y / wy;
        float d = (rx * (by - gy)) + (bx * (gy - ry)) + (gx * (ry - by));
        float srN = (x * (by - gy)) - (gx * ((Y * (by - 1)) + (by * (x + z)))) + (bx * ((Y * (gy - 1)) + (gy * (x + z))));
        float sgN = (x * (ry - by)) + (rx * ((Y * (by - 1)) + (by * (x + z)))) - (bx * ((Y * (ry - 1)) + (ry * (x + z))));
        float sbN = (x * (gy - ry)) - (rx * ((Y * (gy - 1)) + (gy * (x + z)))) + (gx * ((Y * (ry - 1)) + (ry * (x + z))));
        float sr = srN / d, sg = sgN / d, sb = sbN / d;
        float m01 = sr * ry, m11 = sg * gy, m21 = sb * by;
        float sum = m01 + m11 + m21;
        return (m01 / sum, m11 / sum, m21 / sum);

        static float F(ReadOnlySpan<byte> v, int at) => BinaryPrimitives.ReadSingleLittleEndian(v[at..]);
    }

    private static ReadOnlySpan<ushort> Row(ExrPlanes planes, int channel, int row, int width) =>
        MemoryMarshal.Cast<byte, ushort>(planes.Plane(channel)).Slice(row * width, width);
}
