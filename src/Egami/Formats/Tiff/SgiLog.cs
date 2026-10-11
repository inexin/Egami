namespace Egami.Formats.Tiff;

/// <summary>
/// SGILog high dynamic range pixels (Greg Ward's LogLuv encoding): LogL16, a sign and 15 bits of log luminance; LogLuv32, that and
/// 8-bit u′ and v′, each row's pixels run-length coded a byte plane at a time, most significant first; and LogLuv24, 10 bits of
/// log luminance and a 14-bit index into a grid of the visible (u′, v′), not compressed. Rows decode to CIE XYZ (or Y) as 32-bit
/// floats, in linear light. Moved from Lars's ClassicMac (SgiLog.cs, MIT), without its display tone mapping.
/// </summary>
internal static class SgiLog
{
    /// <summary>
    /// Decodes one row of <paramref name="pixels"/> run-length coded values of <paramref name="planes"/> bytes from
    /// <paramref name="input"/> at <paramref name="at"/>; false when the data ends first.
    /// </summary>
    public static bool DecodeRow(ReadOnlySpan<byte> input, ref int at, Span<uint> pixels, int planes)
    {
        pixels.Clear();
        for (int shift = 8 * (planes - 1); shift >= 0; shift -= 8)
        {
            int i = 0;
            while (i < pixels.Length && at < input.Length)
            {
                int control = input[at++];
                if (control >= 128)
                {
                    // A run: the next byte, control − 126 times.
                    if (at >= input.Length)
                    {
                        return false;
                    }

                    uint value = (uint)input[at++] << shift;
                    for (int n = control - 126; n > 0 && i < pixels.Length; n--)
                    {
                        pixels[i++] |= value;
                    }
                }
                else
                {
                    // Literal bytes, as many as the control says.
                    for (int n = control; n > 0 && i < pixels.Length && at < input.Length; n--)
                    {
                        pixels[i++] |= (uint)input[at++] << shift;
                    }
                }
            }

            if (i < pixels.Length)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One row of LogLuv24 pixels: three bytes each, most significant first, not compressed.</summary>
    public static bool ReadRow24(ReadOnlySpan<byte> input, ref int at, Span<uint> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if (at + 3 > input.Length)
            {
                return false;
            }

            pixels[i] = (uint)((input[at] << 16) | (input[at + 1] << 8) | input[at + 2]);
            at += 3;
        }

        return true;
    }

    /// <summary>The luminance of a LogL16 value: 2^((Le + 0.5) / 256 − 64), 0 for Le 0, negative with the sign bit.</summary>
    public static double Luminance(uint logL)
    {
        uint le = logL & 0x7FFF;
        if (le == 0)
        {
            return 0;
        }

        double y = Math.Exp((Math.Log(2) / 256 * (le + 0.5)) - (Math.Log(2) * 64));
        return (logL & 0x8000) != 0 ? -y : y;
    }

    /// <summary>A LogLuv32 value as CIE XYZ: its luminance, and u′ = (ue + 0.5) / 410, v′ = (ve + 0.5) / 410.</summary>
    public static (double X, double Y, double Z) Xyz(uint luv)
    {
        double y = Luminance(luv >> 16);
        if (y <= 0)
        {
            return (0, 0, 0);
        }

        double u = (((luv >> 8) & 0xFF) + 0.5) / 410;
        double v = ((luv & 0xFF) + 0.5) / 410;
        return FromChromaticity(u, v, y);
    }

    /// <summary>
    /// A LogLuv24 value as CIE XYZ: Y = 2^((Le + 0.5) / 64 − 12) from its top 10 bits, 0 for Le 0; (u′, v′) the center of the grid
    /// cell its low 14 bits index, the equal-energy white for an index past the grid.
    /// </summary>
    public static (double X, double Y, double Z) Xyz24(uint luv)
    {
        uint le = (luv >> 14) & 0x3FF;
        if (le == 0)
        {
            return (0, 0, 0);
        }

        double y = Math.Exp((Math.Log(2) / 64 * (le + 0.5)) - (Math.Log(2) * 12));
        (double u, double v) = Cell((int)(luv & 0x3FFF));
        return FromChromaticity(u, v, y);
    }

    /// <summary>The center of a grid cell (libtiff's uv_decode): the last row whose cells before it are at most the index.</summary>
    private static (double U, double V) Cell(int index)
    {
        if (index < 0 || index >= LogLuvTable.Cells)
        {
            return (0.210526316, 0.473684211);
        }

        int lower = 0;
        int upper = LogLuvTable.Rows.Length;
        while (upper - lower > 1)
        {
            int middle = (lower + upper) >> 1;
            int past = index - LogLuvTable.Rows[middle].Before;
            if (past > 0)
            {
                lower = middle;
            }
            else if (past < 0)
            {
                upper = middle;
            }
            else
            {
                lower = middle;
                break;
            }
        }

        (float uStart, _, short before) = LogLuvTable.Rows[lower];
        double u = uStart + ((index - before + 0.5) * LogLuvTable.CellSize);
        double v = LogLuvTable.VStart + ((lower + 0.5) * LogLuvTable.CellSize);
        return (u, v);
    }

    private static (double X, double Y, double Z) FromChromaticity(double u, double v, double y)
    {
        double s = 1 / ((6 * u) - (16 * v) + 12);
        double x = 9 * u * s;
        double yc = 4 * v * s;
        return (x / yc * y, y, (1 - x - yc) / yc * y);
    }

    /// <summary>
    /// Decodes one strip of an SGILog page into rows of float Y (LogL) or XYZ (LogLuv), <paramref name="width"/> pixels each;
    /// returns the bytes of the rows decoded whole.
    /// </summary>
    public static int DecodeStrip(ReadOnlySpan<byte> input, Span<byte> output, int width, int rows, bool gray, bool packed24)
    {
        Span<uint> row = width <= 4096 ? stackalloc uint[width] : new uint[width];
        Span<float> samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(output);
        int channels = gray ? 1 : 3;
        int at = 0;
        int done = 0;
        for (; done < rows; done++)
        {
            bool whole = packed24 ? ReadRow24(input, ref at, row) : DecodeRow(input, ref at, row, gray ? 2 : 4);
            if (!whole)
            {
                break;
            }

            Span<float> line = samples.Slice(done * width * channels, width * channels);
            for (int c = 0; c < width; c++)
            {
                if (gray)
                {
                    line[c] = (float)Luminance(row[c]);
                    continue;
                }

                (double x, double y, double z) = packed24 ? Xyz24(row[c]) : Xyz(row[c]);
                line[c * 3] = (float)x;
                line[(c * 3) + 1] = (float)y;
                line[(c * 3) + 2] = (float)z;
            }
        }

        return done * width * channels * sizeof(float);
    }
}
