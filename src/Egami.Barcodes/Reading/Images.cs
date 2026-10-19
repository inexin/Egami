using Egami.Pixels;

namespace Egami.Barcodes.Reading;

/// <summary>An 8-bit luminance plane, row by row, and where it sits in the source image.</summary>
internal sealed class LuminanceImage(byte[] pixels, int width, int height, int left = 0, int top = 0)
{
    public byte[] Pixels { get; } = pixels;

    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>Gets the left edge in the source image.</summary>
    public int Left { get; } = left;

    /// <summary>Gets the top edge in the source image.</summary>
    public int Top { get; } = top;

    public byte this[int x, int y] => Pixels[(y * Width) + x];

    public ReadOnlySpan<byte> Row(int y) => Pixels.AsSpan(y * Width, Width);

    /// <summary>Converts a raster's region to luminance through Egami's pixel conversion.</summary>
    public static LuminanceImage From<TPixel>(Raster<TPixel> raster, Rect? region)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rect area = region is Rect r ? r.Intersect(new Rect(0, 0, raster.Width, raster.Height)) : new Rect(0, 0, raster.Width, raster.Height);
        byte[] pixels = new byte[area.Width * area.Height];
        Span<Gray8> gray = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Gray8>(pixels.AsSpan());
        for (int y = 0; y < area.Height; y++)
        {
            PixelConvert.Convert<TPixel, Gray8>(raster.Row(area.Y + y).Slice(area.X, area.Width), gray.Slice(y * area.Width, area.Width));
        }

        return new LuminanceImage(pixels, area.Width, area.Height, area.X, area.Y);
    }

    /// <summary>Returns the image with light and dark swapped.</summary>
    public LuminanceImage Inverted()
    {
        byte[] pixels = new byte[Pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(255 - Pixels[i]);
        }

        return new LuminanceImage(pixels, Width, Height, Left, Top);
    }
}

/// <summary>A binarized image: 1 for dark, 0 for light.</summary>
internal sealed class BitImage(byte[] bits, int width, int height)
{
    public byte[] Bits { get; } = bits;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public bool this[int x, int y] => Bits[(y * Width) + x] != 0;

    /// <summary>Returns whether a point is inside and dark.</summary>
    public bool IsDark(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height && Bits[(y * Width) + x] != 0;

    public ReadOnlySpan<byte> Row(int y) => Bits.AsSpan(y * Width, Width);

    /// <summary>Returns the image mirrored about its main diagonal.</summary>
    public BitImage Transposed()
    {
        byte[] bits = new byte[Bits.Length];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                bits[(x * Height) + y] = Bits[(y * Width) + x];
            }
        }

        return new BitImage(bits, Height, Width);
    }
}

/// <summary>Thresholds a luminance image into dark and light.</summary>
internal static class Binarizer
{
    private const int BlockSize = 8, MinimumDynamicRange = 24;

    /// <summary>
    /// One threshold for the whole image, at the deepest valley between the two highest peaks of its histogram: good for clean
    /// scans and renders.
    /// </summary>
    public static BitImage Global(LuminanceImage image)
    {
        Span<int> histogram = stackalloc int[32];
        foreach (byte p in image.Pixels)
        {
            histogram[p >> 3]++;
        }

        int threshold = Valley(histogram) << 3;
        byte[] bits = new byte[image.Pixels.Length];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = image.Pixels[i] < threshold ? (byte)1 : (byte)0;
        }

        return new BitImage(bits, image.Width, image.Height);
    }

    /// <summary>
    /// A threshold per 8 x 8 block from the mean of the 5 x 5 blocks around it (the hybrid approach), so uneven light in photos
    /// does not swallow parts of a symbol. Blocks with little contrast take their neighbours' level.
    /// </summary>
    public static BitImage Local(LuminanceImage image, int threads = 1)
    {
        int width = image.Width, height = image.Height;
        if (width < BlockSize * 5 || height < BlockSize * 5)
        {
            return Global(image);
        }

        int blocksX = (width + BlockSize - 1) / BlockSize, blocksY = (height + BlockSize - 1) / BlockSize;
        int[] levels = new int[blocksX * blocksY];
        for (int by = 0; by < blocksY; by++)
        {
            int y0 = Math.Min(by * BlockSize, height - BlockSize);
            for (int bx = 0; bx < blocksX; bx++)
            {
                int x0 = Math.Min(bx * BlockSize, width - BlockSize);
                int sum = 0, min = 255, max = 0;
                for (int y = 0; y < BlockSize; y++)
                {
                    ReadOnlySpan<byte> row = image.Pixels.AsSpan(((y0 + y) * width) + x0, BlockSize);
                    foreach (byte p in row)
                    {
                        sum += p;
                        min = Math.Min(min, p);
                        max = Math.Max(max, p);
                    }
                }

                int average;
                if (max - min > MinimumDynamicRange)
                {
                    average = sum / (BlockSize * BlockSize);
                }
                else
                {
                    // A flat block: assume it is light, unless the blocks above and to the left say it sits in dark.
                    average = min / 2;
                    if (by > 0 && bx > 0)
                    {
                        int neighbours = (levels[((by - 1) * blocksX) + bx] + (2 * levels[(by * blocksX) + bx - 1]) + levels[((by - 1) * blocksX) + bx - 1]) / 4;
                        if (min < neighbours)
                        {
                            average = neighbours;
                        }
                    }
                }

                levels[(by * blocksX) + bx] = average;
            }
        }

        // Thresholding in parallel stripes of block rows: each writes only its own pixels, except the last, which may overlap
        // the one before it and so runs after the others.
        byte[] bits = new byte[width * height];
        Parallel.For(0, blocksY - 1, new ParallelOptions { MaxDegreeOfParallelism = threads }, by => Threshold(image, levels, bits, blocksX, blocksY, by));
        Threshold(image, levels, bits, blocksX, blocksY, blocksY - 1);
        return new BitImage(bits, width, height);
    }

    /// <summary>Thresholds one row of blocks by the mean level of the 5 x 5 blocks around each.</summary>
    private static void Threshold(LuminanceImage image, int[] levels, byte[] bits, int blocksX, int blocksY, int by)
    {
        int width = image.Width, height = image.Height;
        int y0 = Math.Min(by * BlockSize, height - BlockSize);
        int cy = Math.Clamp(by, 2, blocksY - 3);
        for (int bx = 0; bx < blocksX; bx++)
        {
            int x0 = Math.Min(bx * BlockSize, width - BlockSize);
            int cx = Math.Clamp(bx, 2, blocksX - 3);
            int sum = 0;
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    sum += levels[((cy + dy) * blocksX) + cx + dx];
                }
            }

            int threshold = sum / 25;
            for (int y = 0; y < BlockSize; y++)
            {
                int offset = ((y0 + y) * width) + x0;
                for (int x = 0; x < BlockSize; x++)
                {
                    bits[offset + x] = image.Pixels[offset + x] <= threshold ? (byte)1 : (byte)0;
                }
            }
        }
    }

    /// <summary>Thresholds one row by its own histogram valley, for scanning linear symbols.</summary>
    public static void Row(ReadOnlySpan<byte> row, Span<byte> dark)
    {
        Span<int> histogram = stackalloc int[32];
        foreach (byte p in row)
        {
            histogram[p >> 3]++;
        }

        int threshold = Valley(histogram) << 3;
        for (int i = 0; i < row.Length; i++)
        {
            dark[i] = row[i] < threshold ? (byte)1 : (byte)0;
        }
    }

    /// <summary>
    /// Cuts a row into runs, light first (possibly empty), with edges at sub-pixel positions: the row is sharpened with a
    /// -1 2 -1 kernel as blurred scans need, thresholded at its histogram valley, and each edge placed where the sharpened
    /// values cross the threshold. <paramref name="offsets"/> gets each run's start and, last, the row's end.
    /// </summary>
    public static void RowRuns(ReadOnlySpan<byte> row, List<float> runs, List<float> offsets)
    {
        runs.Clear();
        offsets.Clear();
        int n = row.Length;
        Span<int> histogram = stackalloc int[32];
        foreach (byte p in row)
        {
            histogram[p >> 3]++;
        }

        float threshold = Valley(histogram) << 3;
        float[] sharp = new float[n];
        for (int i = 0; i < n; i++)
        {
            int left = row[Math.Max(i - 1, 0)], right = row[Math.Min(i + 1, n - 1)];
            sharp[i] = ((4 * row[i]) - left - right) / 2f;
        }

        bool dark = false;
        float start = 0;
        for (int i = 0; i < n; i++)
        {
            bool d = sharp[i] < threshold;
            if (d == dark)
            {
                continue;
            }

            // The crossing between sample centers i - 0.5 and i + 0.5.
            float a = i > 0 ? sharp[i - 1] : sharp[i], b = sharp[i];
            float edge = a == b ? i : i - 0.5f + Math.Clamp((threshold - a) / (b - a), 0, 1);
            runs.Add(Math.Max(edge - start, 0));
            offsets.Add(start);
            start = edge;
            dark = d;
        }

        runs.Add(n - start);
        offsets.Add(start);
        offsets.Add(n);
    }

    /// <summary>
    /// Cuts a row into runs against a threshold that follows the light: the mean over a window around each sample (an eighth of
    /// the row, at least 15 samples), so a gradient across a label does not swallow its bars. Edges are at sub-pixel positions.
    /// </summary>
    public static void AdaptiveRowRuns(ReadOnlySpan<byte> row, List<float> runs, List<float> offsets)
    {
        runs.Clear();
        offsets.Clear();
        int n = row.Length, radius = Math.Max(7, n / 16);
        int[] prefix = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            prefix[i + 1] = prefix[i] + row[i];
        }

        float[] level = new float[n];
        int min = 255, max = 0;
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - radius), b = Math.Min(n, i + radius + 1);
            level[i] = row[i] - ((prefix[b] - prefix[a]) / (float)(b - a));
            min = Math.Min(min, row[i]);
            max = Math.Max(max, row[i]);
        }

        if (max - min < MinimumDynamicRange)
        {
            runs.Add(n);
            offsets.Add(0);
            offsets.Add(n);
            return;
        }

        bool dark = false;
        float start = 0;
        for (int i = 0; i < n; i++)
        {
            bool d = level[i] < 0;
            if (d == dark)
            {
                continue;
            }

            float a = i > 0 ? level[i - 1] : level[i], b = level[i];
            float edge = a == b ? i : i - 0.5f + Math.Clamp(-a / (b - a), 0, 1);
            runs.Add(Math.Max(edge - start, 0));
            offsets.Add(start);
            start = edge;
            dark = d;
        }

        runs.Add(n - start);
        offsets.Add(start);
        offsets.Add(n);
    }

    /// <summary>Cuts a row of dark and light bits into runs, light first, at whole-pixel edges.</summary>
    public static void BitRuns(ReadOnlySpan<byte> dark, List<float> runs, List<float> offsets)
    {
        runs.Clear();
        offsets.Clear();
        int color = 0, start = 0;
        for (int x = 0; x < dark.Length; x++)
        {
            if (dark[x] != color)
            {
                runs.Add(x - start);
                offsets.Add(start);
                start = x;
                color ^= 1;
            }
        }

        runs.Add(dark.Length - start);
        offsets.Add(start);
        offsets.Add(dark.Length);
    }

    /// <summary>The bucket between the two main peaks with the deepest valley, weighted toward the far side of the brighter peak.</summary>
    private static int Valley(ReadOnlySpan<int> histogram)
    {
        int first = 0, max = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            if (histogram[i] > max)
            {
                (first, max) = (i, histogram[i]);
            }
        }

        int second = 0, best = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            int distance = i - first, score = histogram[i] * distance * distance;
            if (score > best)
            {
                (second, best) = (i, score);
            }
        }

        if (first > second)
        {
            (first, second) = (second, first);
        }

        if (second - first <= 1)
        {
            return 16;
        }

        int valley = second - 1, bestValley = -1;
        for (int i = second - 1; i > first; i--)
        {
            int fromFirst = i - first;
            int score = fromFirst * fromFirst * (second - i) * (max - histogram[i]);
            if (score > bestValley)
            {
                (valley, bestValley) = (i, score);
            }
        }

        return valley;
    }
}
