using System.Buffers;

namespace Egami.Formats.Bmp;

/// <summary>
/// Encodes RLE8 and RLE4 BMP pixels, written from Microsoft's description of the bitmap compression formats: encoded runs (a
/// count and the index, or for RLE4 two indices that alternate), absolute runs of 3 to 255 indices padded to a 16-bit boundary,
/// an end of line after each row and an end of bitmap after the last.
/// </summary>
internal static class BmpRunLength
{
    /// <summary>The shortest run written as a run; shorter ones stay in absolute runs, which cost no more.</summary>
    private const int MinRun = 3;

    /// <summary>Encodes <paramref name="indices"/>, top row first, as bottom-up rows of <paramref name="bits"/> (4 or 8) bits.</summary>
    public static ArrayBufferWriter<byte> Encode(ReadOnlySpan<byte> indices, int width, int height, int bits)
    {
        ArrayBufferWriter<byte> output = new(Math.Max(256, (int)Math.Min((long)width * height / 4, 1 << 24)));
        for (int y = height - 1; y >= 0; y--)
        {
            ReadOnlySpan<byte> row = indices.Slice(y * width, width);
            if (bits == 8)
            {
                Row8(row, output);
            }
            else
            {
                Row4(row, output);
            }

            Span<byte> end = output.GetSpan(2);
            end[0] = 0;
            end[1] = (byte)(y == 0 ? 1 : 0);
            output.Advance(2);
        }

        return output;
    }

    private static void Row8(ReadOnlySpan<byte> row, ArrayBufferWriter<byte> output)
    {
        int x = 0;
        int literal = 0;
        while (x < row.Length)
        {
            int run = Run8(row, x);
            if (run >= MinRun)
            {
                Absolute8(row.Slice(literal, x - literal), output);
                Span<byte> pair = output.GetSpan(2);
                pair[0] = (byte)run;
                pair[1] = row[x];
                output.Advance(2);
                x += run;
                literal = x;
            }
            else
            {
                x += run;
            }
        }

        Absolute8(row[literal..], output);
    }

    private static int Run8(ReadOnlySpan<byte> row, int x)
    {
        int end = Math.Min(row.Length, x + 255);
        int i = x + 1;
        while (i < end && row[i] == row[x])
        {
            i++;
        }

        return i - x;
    }

    /// <summary>Writes indices as absolute runs of up to 255, and fewer than 3 as encoded runs of one each.</summary>
    private static void Absolute8(ReadOnlySpan<byte> values, ArrayBufferWriter<byte> output)
    {
        while (values.Length > 0)
        {
            int n = Math.Min(values.Length, 255);
            if (n < MinRun)
            {
                for (int i = 0; i < n; i++)
                {
                    Span<byte> one = output.GetSpan(2);
                    one[0] = 1;
                    one[1] = values[i];
                    output.Advance(2);
                }
            }
            else
            {
                int padded = n + (n & 1);
                Span<byte> span = output.GetSpan(2 + padded);
                span[0] = 0;
                span[1] = (byte)n;
                values[..n].CopyTo(span[2..]);
                if ((n & 1) != 0)
                {
                    span[2 + n] = 0;
                }

                output.Advance(2 + padded);
            }

            values = values[n..];
        }
    }

    private static void Row4(ReadOnlySpan<byte> row, ArrayBufferWriter<byte> output)
    {
        int x = 0;
        int literal = 0;
        while (x < row.Length)
        {
            int run = Run4(row, x);
            if (run >= MinRun)
            {
                Absolute4(row.Slice(literal, x - literal), output);
                Span<byte> pair = output.GetSpan(2);
                pair[0] = (byte)run;
                pair[1] = (byte)((row[x] << 4) | (x + 1 < row.Length ? row[x + 1] : row[x]));
                output.Advance(2);
                x += run;
                literal = x;
            }
            else
            {
                x += run;
            }
        }

        Absolute4(row[literal..], output);
    }

    /// <summary>Returns how far the two indices at <paramref name="x"/> repeat in turn, one color being the case of both equal.</summary>
    private static int Run4(ReadOnlySpan<byte> row, int x)
    {
        int end = Math.Min(row.Length, x + 255);
        if (x + 1 >= end)
        {
            return end - x;
        }

        byte even = row[x];
        byte odd = row[x + 1];
        int i = x + 2;
        while (i < end && row[i] == ((i - x) % 2 == 0 ? even : odd))
        {
            i++;
        }

        return i - x;
    }

    private static void Absolute4(ReadOnlySpan<byte> values, ArrayBufferWriter<byte> output)
    {
        while (values.Length > 0)
        {
            // Absolute runs stay even: Pillow drops the last index of an odd one. Three indices cost the same as two runs.
            int n = Math.Min(values.Length, 254);
            if (n >= MinRun && (n & 1) != 0)
            {
                n = n == MinRun ? 2 : n - 1;
            }

            if (n < MinRun)
            {
                // One encoded run holds the one or two indices.
                Span<byte> pair = output.GetSpan(2);
                pair[0] = (byte)n;
                pair[1] = (byte)((values[0] << 4) | (n == 2 ? values[1] : 0));
                output.Advance(2);
            }
            else
            {
                int bytes = (n + 1) / 2;
                int padded = bytes + (bytes & 1);
                Span<byte> span = output.GetSpan(2 + padded);
                span[0] = 0;
                span[1] = (byte)n;
                span.Slice(2, padded).Clear();
                for (int i = 0; i < n; i++)
                {
                    span[2 + (i >> 1)] |= (byte)(values[i] << ((i & 1) == 0 ? 4 : 0));
                }

                output.Advance(2 + padded);
            }

            values = values[n..];
        }
    }
}
