using Egami.Barcodes.ErrorCorrection;
using Egami.Barcodes.Matrix;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Reads a sampled Data Matrix ECC 200 grid (ISO/IEC 16022, ISO/IEC 21471): the codewords through the Annex F placement, the
/// interleaved Reed-Solomon blocks (144 x 144 in its de facto layout), and the ASCII, C40, Text, X12, EDIFACT and Base 256
/// encodations.
/// </summary>
internal static class DataMatrixDecoder
{
    public static DecodedData? Decode(BarcodeModules grid)
    {
        int index = Array.FindIndex(DataMatrixTables.Sizes, s => s.Rows == grid.Height && s.Columns == grid.Width);
        if (index < 0)
        {
            return null;
        }

        DataMatrixTables.Size size = DataMatrixTables.Sizes[index];
        int h = size.Rows, w = size.Columns, fh = size.RegionRows, fw = size.RegionColumns;
        int nc = w - (2 * (w / fw)), nr = h - (2 * (h / fh));
        int[] places = DataMatrixEncoder.Places(nr, nc);
        int blocks = (size.Data + 2) / size.DataPerBlock;
        byte[] codewords = new byte[size.Data + (blocks * size.CheckPerBlock)];
        for (int my = 0; my < nr; my++)
        {
            int y = nr - my - 1, row = h - 2 - y - (2 * (y / (fh - 2)));
            for (int x = 0; x < nc; x++)
            {
                int v = places[(my * nc) + x];
                if (v > 7 && grid[1 + x + (2 * (x / (fw - 2))), row])
                {
                    codewords[(v >> 3) - 1] |= (byte)(1 << (v & 7));
                }
            }
        }

        // Undo the interleaving; 144 x 144 shifts its check words as writers do.
        bool skew = index == DataMatrixTables.Sizes.Length - 1;
        int data = size.Data, corrected = 0;
        for (int b = 0; b < blocks; b++)
        {
            var dataIndex = new List<int>();
            for (int n = b; n < data; n += blocks)
            {
                dataIndex.Add(n);
            }

            int[] block = new int[dataIndex.Count + size.CheckPerBlock];
            for (int j = 0; j < dataIndex.Count; j++)
            {
                block[j] = codewords[dataIndex[j]];
            }

            for (int p = 0, n = b; p < size.CheckPerBlock; p++, n += blocks)
            {
                block[dataIndex.Count + p] = codewords[data + (skew ? (b < 8 ? n + 2 : n - 8) : n)];
            }

            int fixedCount = ReedSolomon.Decode(GaloisField.DataMatrix, block, size.CheckPerBlock);
            if (fixedCount < 0)
            {
                return null;
            }

            corrected += fixedCount;
            for (int j = 0; j < dataIndex.Count; j++)
            {
                codewords[dataIndex[j]] = (byte)block[j];
            }
        }

        return Parse(codewords.AsSpan(0, data)) is DecodedData parsed ? parsed with { ErrorsCorrected = corrected } : null;
    }

    private enum Mode
    {
        Ascii,
        C40,
        Text,
        X12,
        Edifact,
        Base256,
    }

    private static DecodedData? Parse(ReadOnlySpan<byte> codewords)
    {
        var text = new TextBuilder();
        var trailer = "";
        Mode mode = Mode.Ascii;
        bool upperShift = false;
        int i = 0, shift = 0;
        while (i < codewords.Length)
        {
            switch (mode)
            {
                case Mode.Ascii:
                    int c = codewords[i++];
                    if (c == 0 || c >= 242)
                    {
                        return null;
                    }

                    if (c <= 128)
                    {
                        text.Append((byte)(c - 1 + (upperShift ? 128 : 0)));
                        upperShift = false;
                    }
                    else if (c == 129)
                    {
                        i = codewords.Length;
                    }
                    else if (c <= 229)
                    {
                        text.AppendText((c - 130).ToString("00", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        switch (c)
                        {
                            case 230:
                                mode = Mode.C40;
                                break;
                            case 231:
                                mode = Mode.Base256;
                                break;
                            case 232:
                                if (i == 1)
                                {
                                    text.Gs1 = true;
                                }
                                else
                                {
                                    text.Append(0x1D);
                                }

                                break;
                            case 233:
                                i += 3;
                                break;
                            case 234:
                                break;
                            case 235:
                                upperShift = true;
                                break;
                            case 236 or 237:
                                text.AppendText(c == 236 ? "[)>\x1E" + "05\x1D" : "[)>\x1E" + "06\x1D");
                                trailer = "\x1E\x04";
                                break;
                            case 238:
                                mode = Mode.X12;
                                break;
                            case 239:
                                mode = Mode.Text;
                                break;
                            case 240:
                                mode = Mode.Edifact;
                                break;
                            default:
                                if (Eci(codewords, ref i) is not int eci)
                                {
                                    return null;
                                }

                                text.SetEci(eci);
                                break;
                        }
                    }

                    break;
                case Mode.C40 or Mode.Text or Mode.X12:
                    // The unlatch may be the last codeword; a single codeword left without it is ASCII.
                    if (codewords[i] == 254 || codewords.Length - i < 2)
                    {
                        i += codewords[i] == 254 ? 1 : 0;
                        mode = Mode.Ascii;
                        shift = 0;
                        break;
                    }

                    if (!Triplet(codewords[i], codewords[i + 1], mode, text, ref upperShift, ref shift))
                    {
                        return null;
                    }

                    i += 2;
                    break;
                case Mode.Edifact:
                    if (codewords.Length - i < 3)
                    {
                        mode = Mode.Ascii;
                        break;
                    }

                    // Four 6-bit values in three codewords; the unlatch (31) drops the rest of its codeword.
                    int start = i, bits = (codewords[i] << 16) | (codewords[i + 1] << 8) | codewords[i + 2];
                    i += 3;
                    for (int k = 0; k < 4; k++)
                    {
                        int v = (bits >> (18 - (6 * k))) & 0x3F;
                        if (v == 31)
                        {
                            mode = Mode.Ascii;
                            i = start + Math.Min(k + 1, 3);
                            break;
                        }

                        text.Append((byte)(v < 32 ? v + 64 : v));
                    }

                    break;
                default:
                    if (!Base256(codewords, ref i, text))
                    {
                        return null;
                    }

                    mode = Mode.Ascii;
                    break;
            }
        }

        if (trailer.Length > 0)
        {
            text.AppendText(trailer);
        }

        (string result, IReadOnlyList<Gs1ElementString>? elements) = text.Result();
        return new DecodedData(BarcodeFormat.DataMatrix, result, text.Bytes) { Eci = text.Eci, Gs1 = elements };
    }

    private static int? Eci(ReadOnlySpan<byte> codewords, ref int i)
    {
        if (i >= codewords.Length)
        {
            return null;
        }

        int c1 = codewords[i++];
        if (c1 <= 127)
        {
            return c1 - 1;
        }

        if (i >= codewords.Length)
        {
            return null;
        }

        int c2 = codewords[i++];
        if (c1 <= 191)
        {
            return ((c1 - 128) * 254) + c2 + 126;
        }

        if (i >= codewords.Length)
        {
            return null;
        }

        int c3 = codewords[i++];
        return ((c1 - 192) * 64516) + ((c2 - 1) * 254) + c3 - 1 + 16383;
    }

    /// <summary>Decodes a C40, Text or X12 pair of codewords into three values; a shift may carry over to the next pair.</summary>
    private static bool Triplet(byte c1, byte c2, Mode mode, TextBuilder text, ref bool upperShift, ref int shift)
    {
        int value = (c1 * 256) + c2 - 1;
        Span<int> values = [value / 1600, value / 40 % 40, value % 40];
        if (mode == Mode.X12)
        {
            foreach (int v in values)
            {
                text.Append((byte)(v switch { 0 => 13, 1 => '*', 2 => '>', 3 => ' ', < 14 => v - 4 + '0', _ => v - 14 + 'A' }));
            }

            return true;
        }

        foreach (int v in values)
        {
            int c;
            switch (shift)
            {
                case 0:
                    if (v < 3)
                    {
                        shift = v + 1;
                        continue;
                    }

                    c = v == 3 ? ' ' : v < 14 ? v - 4 + '0' : v - 14 + (mode == Mode.C40 ? 'A' : 'a');
                    break;
                case 1:
                    c = v;
                    break;
                case 2:
                    if (v == 27)
                    {
                        text.Append(0x1D);
                        shift = 0;
                        continue;
                    }

                    if (v == 30)
                    {
                        upperShift = true;
                        shift = 0;
                        continue;
                    }

                    c = v < 15 ? v + 33 : v < 22 ? v - 15 + 58 : v < 27 ? v - 22 + 91 : -1;
                    break;
                default:
                    c = mode == Mode.C40 ? v + 96 : v == 0 ? '`' : v < 27 ? v - 1 + 'A' : v - 27 + '{';
                    break;
            }

            if (c < 0)
            {
                return false;
            }

            text.Append((byte)(c + (upperShift ? 128 : 0)));
            upperShift = false;
            shift = 0;
        }

        return true;
    }

    /// <summary>Base 256: a randomized length field (one or two codewords), then that many randomized bytes.</summary>
    private static bool Base256(ReadOnlySpan<byte> codewords, ref int i, TextBuilder text)
    {
        static int Plain(byte value, int position) => (value - (((149 * position) % 255) + 1)) & 0xFF;

        if (i >= codewords.Length)
        {
            return false;
        }

        int length = Plain(codewords[i], i + 1);
        i++;
        if (length == 0)
        {
            length = codewords.Length - i;
        }
        else if (length > 249)
        {
            if (i >= codewords.Length)
            {
                return false;
            }

            length = (250 * (length - 249)) + Plain(codewords[i], i + 1);
            i++;
        }

        if (i + length > codewords.Length)
        {
            return false;
        }

        for (int k = 0; k < length; k++, i++)
        {
            text.Append((byte)Plain(codewords[i], i + 1));
        }

        return true;
    }
}
