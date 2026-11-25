using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>A symbol found in the image: its data and its corners in image coordinates.</summary>
internal sealed record class Found(DecodedData Data, Vector2[] Corners);

/// <summary>
/// Reads an image that holds one upright symbol and its quiet zone, as a rendered barcode does: the bounding box of the dark
/// pixels gives the symbol, its finder or clock pattern gives the module count, and the grid is sampled at module centers.
/// </summary>
internal static class PureReader
{
    /// <summary>
    /// Reads the image from its globally thresholded bits. The luminance is only sampled for PDF417, from the inverted image
    /// when the bits are swapped. Within a full search (<paramref name="searching"/>) an Aztec symbol that is not the whole
    /// image is left to the search's own Aztec detector.
    /// </summary>
    public static Found? Read(LuminanceImage image, BitImage bits, BarcodeFormat formats, bool inverted, bool searching = false)
    {
        if (Bounds(bits) is not (int left, int top, int right, int bottom))
        {
            return null;
        }

        int width = right - left + 1, height = bottom - top + 1;
        Vector2[] corners = [new(left + image.Left, top + image.Top), new(right + 1 + image.Left, top + image.Top), new(right + 1 + image.Left, bottom + 1 + image.Top), new(left + image.Left, bottom + 1 + image.Top)];

        if ((formats & BarcodeFormat.Qr) != 0 && Qr(bits, left, top, width, height) is DecodedData qr)
        {
            return new Found(qr, corners);
        }

        if ((formats & BarcodeFormat.DataMatrix) != 0 && DataMatrix(bits, left, top, width, height) is DecodedData dm)
        {
            return new Found(dm, corners);
        }

        if ((formats & BarcodeFormat.Aztec) != 0)
        {
            if (Aztec(bits, left, top, width, height) is DecodedData aztec)
            {
                return new Found(aztec, corners);
            }

            // Aztec has no quiet zone, so light modules on its edge can make the dark pixels' box smaller than the symbol: a
            // render without a margin is the whole image, and otherwise the bullseye finds it.
            if (Aztec(bits, 0, 0, bits.Width, bits.Height) is DecodedData whole)
            {
                return new Found(whole, [new(image.Left, image.Top), new(image.Left + bits.Width, image.Top), new(image.Left + bits.Width, image.Top + bits.Height), new(image.Left, image.Top + bits.Height)]);
            }

            if (!searching && AztecDetector.Detect(bits, 5, new Vector2(image.Left, image.Top)) is [Found found, ..])
            {
                return found;
            }
        }

        if ((formats & BarcodeFormat.Pdf417) != 0 && Pdf417(bits, inverted ? image.Inverted() : image, left, top, width, height) is Found pdf)
        {
            return pdf;
        }

        if ((formats & DataBarCollector.Formats) != 0 && DataBar(bits, top, height, formats) is (DecodedData dataBar, Vector2 from, Vector2 to))
        {
            Vector2 offset = new(image.Left, image.Top);
            return new Found(dataBar, [from + offset, to + offset]);
        }

        if ((formats & BarcodeFormat.Linear) != 0)
        {
            for (int k = 1; k <= 3; k++)
            {
                int y = top + (height * k / 4);
                if (Linear(bits, y, formats) is LinearRead read)
                {
                    return new Found(read.Data, [new(read.Start + image.Left, y + image.Top), new(read.End + image.Left, y + image.Top)]);
                }
            }
        }

        return null;
    }

    /// <summary>The bounding box of the dark pixels, or null when there are none.</summary>
    private static (int, int, int, int)? Bounds(BitImage bits)
    {
        int left = bits.Width, top = bits.Height, right = -1, bottom = -1;
        for (int y = 0; y < bits.Height; y++)
        {
            ReadOnlySpan<byte> row = bits.Row(y);
            int first = row.IndexOf((byte)1);
            if (first < 0)
            {
                continue;
            }

            int last = row.LastIndexOf((byte)1);
            left = Math.Min(left, first);
            right = Math.Max(right, last);
            top = Math.Min(top, y);
            bottom = y;
        }

        return right < 0 ? null : (left, top, right, bottom);
    }

    private static BarcodeModules Sample(BitImage bits, int left, int top, int width, int height, int columns, int rows)
    {
        bool[] cells = new bool[columns * rows];
        float mx = (float)width / columns, my = (float)height / rows;
        for (int y = 0; y < rows; y++)
        {
            int py = top + (int)((y + 0.5f) * my);
            for (int x = 0; x < columns; x++)
            {
                cells[(y * columns) + x] = bits.IsDark(left + (int)((x + 0.5f) * mx), py);
            }
        }

        return BarcodeModules.Wrap(columns, rows, cells);
    }

    private static DecodedData? Qr(BitImage bits, int left, int top, int width, int height)
    {
        // The top-left finder's top edge is 7 modules of dark.
        int run = 0;
        while (run < width && bits[left + run, top])
        {
            run++;
        }

        float module = run / 7f;
        if (module < 1)
        {
            return null;
        }

        int size = (int)MathF.Round(width / module);
        size += (size % 4 == 3) ? -2 : (size % 4 == 0) ? 1 : (size % 4 == 2) ? -1 : 0;
        if (size < 21 || size > 177 || MathF.Abs((height / module) - size) > 2)
        {
            return null;
        }

        return QrDecoder.Decode(Sample(bits, left, top, width, height, size, size));
    }

    private static DecodedData? DataMatrix(BitImage bits, int left, int top, int width, int height)
    {
        // The top row and right column alternate from the corners: half their modules are dark runs.
        int columns = 2 * Runs(bits, left, top, width, horizontal: true);
        int rows = 2 * Runs(bits, left + width - 1, top, height, horizontal: false);
        if (columns < 10 || rows < 8)
        {
            return null;
        }

        return DataMatrixDecoder.Decode(Sample(bits, left, top, width, height, columns, rows));
    }

    private static int Runs(BitImage bits, int x0, int y0, int length, bool horizontal)
    {
        int count = 0;
        bool previous = false;
        for (int i = 0; i < length; i++)
        {
            bool dark = horizontal ? bits[x0 + i, y0] : bits[x0, y0 + i];
            if (dark && !previous)
            {
                count++;
            }

            previous = dark;
        }

        return count;
    }

    private static DecodedData? Aztec(BitImage bits, int left, int top, int width, int height)
    {
        if (Math.Abs(width - height) > Math.Max(2, width / 20))
        {
            return null;
        }

        // The bullseye's center module is one dark module between light ones.
        int cx = left + (width / 2), cy = top + (height / 2), l = cx, r = cx;
        while (l > left && bits[l - 1, cy])
        {
            l--;
        }

        while (r < left + width - 1 && bits[r + 1, cy])
        {
            r++;
        }

        float module = r - l + 1;
        int size = (int)MathF.Round(width / module);
        if (size % 2 == 0)
        {
            size += (width / module) > size ? 1 : -1;
        }

        return !AztecDecoder.IsSymbolSize(size) ? null : AztecDecoder.Decode(Sample(bits, left, top, width, height, size, size));
    }

    private static Found? Pdf417(BitImage bits, LuminanceImage image, int left, int top, int width, int height)
    {
        var lines = new List<Pdf417Decoder.Line>();
        var runs = new List<int>();
        var offsets = new List<int>();
        for (int y = top; y < top + height; y++)
        {
            Runs(bits.Row(y), runs, offsets);
            if (Pdf417Decoder.ReadLine([.. runs], 1, y, [.. offsets]) is Pdf417Decoder.Line line)
            {
                lines.Add(line);
            }
        }

        if (Pdf417Decoder.Decode(lines, out Vector2[] corners) is not DecodedData data)
        {
            return null;
        }

        for (int i = 0; i < corners.Length; i++)
        {
            corners[i] += new Vector2(image.Left, image.Top);
        }

        return new Found(data, corners);
    }

    /// <summary>GS1 DataBar from rows across the symbol, each read both ways, so that the pairs of stacked rows meet.</summary>
    private static (DecodedData, Vector2, Vector2)? DataBar(BitImage bits, int top, int height, BarcodeFormat formats)
    {
        var runs = new List<float>();
        var offsets = new List<float>();
        var collector = new DataBarCollector();
        var found = new List<(DecodedData Data, Vector2 From, Vector2 To)>();
        var seen = new Dictionary<string, int>();
        int lines = Math.Min(height, 24);
        for (int k = 0; k < lines; k++)
        {
            int y = top + (int)((k + 0.5f) * height / lines);
            Binarizer.BitRuns(bits.Row(y), runs, offsets);
            float[] forward = [.. runs], forwardOffsets = [.. offsets];
            collector.Add(forward, forwardOffsets, new Vector2(0, y), Vector2.UnitX, formats, found);
            (float[] back, float[] backOffsets) = Reversed(forward, bits.Width);
            collector.Add(back, backOffsets, new Vector2(bits.Width, y), -Vector2.UnitX, formats, found);
            // A symbol counts when two rows make it: pairs can meet by chance.
            foreach ((DecodedData data, Vector2 from, Vector2 to) in found)
            {
                if (seen.TryGetValue(data.Text, out int row) && row != k)
                {
                    return (data, from, to);
                }

                seen.TryAdd(data.Text, k);
            }

            found.Clear();
        }

        return null;
    }

    /// <summary>Runs read from the other end, light first again, with their offsets from that end.</summary>
    internal static (float[] Runs, float[] Offsets) Reversed(ReadOnlySpan<float> runs, int length)
    {
        int lead = runs.Length % 2 == 0 ? 1 : 0, count = runs.Length + lead;
        float[] back = new float[count], positions = new float[count + 1];
        for (int i = 0; i < runs.Length; i++)
        {
            back[lead + i] = runs[runs.Length - 1 - i];
        }

        float at = 0;
        for (int i = 0; i < count; i++)
        {
            positions[i] = at;
            at += back[i];
        }

        positions[count] = length;
        return (back, positions);
    }

    private static LinearRead? Linear(BitImage bits, int y, BarcodeFormat formats)
    {
        var runs = new List<float>();
        var offsets = new List<float>();
        Binarizer.BitRuns(bits.Row(y), runs, offsets);
        return LinearDecoder.Read([.. runs], [.. offsets], formats & BarcodeFormat.Linear);
    }

    /// <summary>Splits a row into runs starting with light (possibly empty) and their starting offsets.</summary>
    internal static void Runs(ReadOnlySpan<byte> row, List<int> runs, List<int> offsets)
    {
        runs.Clear();
        offsets.Clear();
        int color = 0, length = 0, start = 0;
        for (int x = 0; x < row.Length; x++)
        {
            if (row[x] == color)
            {
                length++;
                continue;
            }

            runs.Add(length);
            offsets.Add(start);
            start = x;
            color ^= 1;
            length = 1;
        }

        runs.Add(length);
        offsets.Add(start);
        offsets.Add(row.Length);
    }
}
