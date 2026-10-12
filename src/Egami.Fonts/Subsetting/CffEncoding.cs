using System.Globalization;

using Egami.Fonts.Parsing;

namespace Egami.Fonts.Subsetting;

/// <summary>Writes CFF DICTs, INDEXes and charstring numbers.</summary>
internal static class CffEncoding
{
    /// <summary>Writes a DICT. Operators listed in <paramref name="fixedWidth"/> get 5-byte integer operands, so offsets can be filled in later without changing the size.</summary>
    public static byte[] Dict(IEnumerable<(int Op, double[] Operands)> entries, ISet<int>? fixedWidth = null)
    {
        var w = new ByteWriter();
        foreach ((int op, double[] operands) in entries)
        {
            // Only CFF is written: operators it reserves, such as CFF2's vsindex, blend, vstore and maxstack (22 to 25) that a
            // damaged CFF2 font may carry in the wrong DICT, are left out.
            if (op is >= 22 and <= 27)
            {
                continue;
            }

            bool wide = fixedWidth?.Contains(op) == true;
            foreach (double value in operands)
            {
                if (wide)
                {
                    WideInteger(w, (int)value);
                }
                else
                {
                    DictNumber(w, value);
                }
            }

            if (op >= 1200)
            {
                w.U8(12);
                w.U8(op - 1200);
            }
            else
            {
                w.U8(op);
            }
        }

        return w.ToArray();
    }

    public static void WideInteger(ByteWriter w, int value)
    {
        w.U8(29);
        w.U32((uint)value);
    }

    public static void DictNumber(ByteWriter w, double value)
    {
        if (value == Math.Floor(value) && value is >= int.MinValue and <= int.MaxValue)
        {
            int v = (int)value;
            if (v is >= -107 and <= 107)
            {
                w.U8(v + 139);
            }
            else if (v is >= 108 and <= 1131)
            {
                v -= 108;
                w.U8((v >> 8) + 247);
                w.U8(v & 0xFF);
            }
            else if (v is >= -1131 and <= -108)
            {
                v = -v - 108;
                w.U8((v >> 8) + 251);
                w.U8(v & 0xFF);
            }
            else if (v is >= short.MinValue and <= short.MaxValue)
            {
                w.U8(28);
                w.U16(v);
            }
            else
            {
                WideInteger(w, v);
            }

            return;
        }

        // A real: BCD nibbles of the shortest round-trip text.
        string text = value.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "E", StringComparison.Ordinal);
        var nibbles = new List<int>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= '0' and <= '9')
            {
                nibbles.Add(c - '0');
            }
            else if (c == '.')
            {
                nibbles.Add(0xA);
            }
            else if (c == '-')
            {
                nibbles.Add(0xE);
            }
            else if (c == 'E')
            {
                if (i + 1 < text.Length && text[i + 1] == '-')
                {
                    nibbles.Add(0xC);
                    i++;
                }
                else
                {
                    nibbles.Add(0xB);
                }
            }
        }

        nibbles.Add(0xF);
        if (nibbles.Count % 2 == 1)
        {
            nibbles.Add(0xF);
        }

        w.U8(30);
        for (int i = 0; i < nibbles.Count; i += 2)
        {
            w.U8((nibbles[i] << 4) | nibbles[i + 1]);
        }
    }

    /// <summary>Writes an INDEX of items.</summary>
    public static byte[] Index(IReadOnlyList<byte[]> items)
    {
        var w = new ByteWriter();
        if (items.Count == 0)
        {
            w.U16(0);
            return w.ToArray();
        }

        long total = 1;
        foreach (byte[] item in items)
        {
            total += item.Length;
        }

        int offSize = total <= 0xFF ? 1 : total <= 0xFFFF ? 2 : total <= 0xFFFFFF ? 3 : 4;
        w.U16(items.Count);
        w.U8(offSize);
        uint offset = 1;
        WriteOffset(w, offset, offSize);
        foreach (byte[] item in items)
        {
            offset += (uint)item.Length;
            WriteOffset(w, offset, offSize);
        }

        foreach (byte[] item in items)
        {
            w.Bytes(item);
        }

        return w.ToArray();
    }

    /// <summary>Writes a charstring integer operand in the shortest form.</summary>
    public static void CharStringInteger(ByteWriter w, int v)
    {
        if (v is >= -107 and <= 107)
        {
            w.U8(v + 139);
        }
        else if (v is >= 108 and <= 1131)
        {
            v -= 108;
            w.U8((v >> 8) + 247);
            w.U8(v & 0xFF);
        }
        else if (v is >= -1131 and <= -108)
        {
            v = -v - 108;
            w.U8((v >> 8) + 251);
            w.U8(v & 0xFF);
        }
        else
        {
            w.U8(28);
            w.U16(v);
        }
    }

    /// <summary>Writes a charstring operand: an integer when whole, else 16.16 fixed.</summary>
    public static void CharStringNumber(ByteWriter w, double value)
    {
        double rounded = Math.Round(value);
        if (Math.Abs(value - rounded) < 1e-6 && rounded is >= short.MinValue and <= short.MaxValue)
        {
            CharStringInteger(w, (int)rounded);
            return;
        }

        w.U8(255);
        w.U32((uint)(int)Math.Round(value * 65536));
    }

    /// <summary>Encodes an outline as a hintless Type 2 charstring: an optional width, rmoveto, rlineto, rrcurveto and endchar.</summary>
    public static byte[] CharString(OutlineRecorder outline, double? width)
    {
        var w = new ByteWriter();
        bool widthWritten = width is null;
        double x = 0, y = 0;
        ReadOnlySpan<float> c = outline.Coordinates;
        int k = 0;
        foreach (OutlineVerb verb in outline.Verbs)
        {
            switch (verb)
            {
                case OutlineVerb.Move:
                    if (!widthWritten)
                    {
                        CharStringNumber(w, width!.Value);
                        widthWritten = true;
                    }

                    CharStringNumber(w, c[k] - x);
                    CharStringNumber(w, c[k + 1] - y);
                    w.U8(21);
                    (x, y) = (c[k], c[k + 1]);
                    k += 2;
                    break;
                case OutlineVerb.Line:
                    CharStringNumber(w, c[k] - x);
                    CharStringNumber(w, c[k + 1] - y);
                    w.U8(5);
                    (x, y) = (c[k], c[k + 1]);
                    k += 2;
                    break;
                case OutlineVerb.Cubic:
                    CharStringNumber(w, c[k] - x);
                    CharStringNumber(w, c[k + 1] - y);
                    CharStringNumber(w, c[k + 2] - c[k]);
                    CharStringNumber(w, c[k + 3] - c[k + 1]);
                    CharStringNumber(w, c[k + 4] - c[k + 2]);
                    CharStringNumber(w, c[k + 5] - c[k + 3]);
                    w.U8(8);
                    (x, y) = (c[k + 4], c[k + 5]);
                    k += 6;
                    break;
                case OutlineVerb.Quad:
                    // Raised to a cubic; CFF has no quadratic operator.
                    double qx = c[k], qy = c[k + 1], ex = c[k + 2], ey = c[k + 3];
                    double c1x = x + (2.0 / 3 * (qx - x)), c1y = y + (2.0 / 3 * (qy - y));
                    double c2x = ex + (2.0 / 3 * (qx - ex)), c2y = ey + (2.0 / 3 * (qy - ey));
                    CharStringNumber(w, c1x - x);
                    CharStringNumber(w, c1y - y);
                    CharStringNumber(w, c2x - c1x);
                    CharStringNumber(w, c2y - c1y);
                    CharStringNumber(w, ex - c2x);
                    CharStringNumber(w, ey - c2y);
                    w.U8(8);
                    (x, y) = (ex, ey);
                    k += 4;
                    break;
            }
        }

        if (!widthWritten)
        {
            CharStringNumber(w, width!.Value);
        }

        w.U8(14);
        return w.ToArray();
    }

    private static void WriteOffset(ByteWriter w, uint offset, int size)
    {
        for (int i = size - 1; i >= 0; i--)
        {
            w.U8((int)(offset >> (8 * i)) & 0xFF);
        }
    }
}
