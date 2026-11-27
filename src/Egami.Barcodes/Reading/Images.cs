using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Pixels;

namespace Egami.Barcodes.Reading;

/// <summary>
/// An 8-bit luminance plane, row by row with a stride, and where it sits in the source image. It may view a caller's buffer,
/// which then must outlive the search.
/// </summary>
internal sealed unsafe class LuminanceImage
{
    // The pixels: an array from its start, or else a pinned caller's buffer (BarcodeReader keeps it fixed for the search).
    // Rows are cut from these directly, as ReadOnlyMemory<byte>.Span costs too much per row and pixel.
    private readonly byte[]? _array;
    private readonly int _start;
    private readonly byte* _pointer;

    public LuminanceImage(ReadOnlyMemory<byte> pixels, int width, int height, int stride, int left = 0, int top = 0)
    {
        if (MemoryMarshal.TryGetArray(pixels, out ArraySegment<byte> segment))
        {
            (_array, _start) = (segment.Array, segment.Offset);
        }
        else
        {
            using System.Buffers.MemoryHandle handle = pixels.Pin();
            _pointer = (byte*)handle.Pointer;
        }

        (Width, Height, Stride, Left, Top) = (width, height, stride, left, top);
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>Gets the left edge in the source image.</summary>
    public int Left { get; }

    /// <summary>Gets the top edge in the source image.</summary>
    public int Top { get; }

    public byte this[int x, int y]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _array is not null ? _array[_start + (y * Stride) + x] : _pointer[(y * Stride) + x];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> Row(int y) => _array is not null ? new ReadOnlySpan<byte>(_array, _start + (y * Stride), Width) : new ReadOnlySpan<byte>(_pointer + (y * Stride), Width);

    /// <summary>Converts a raster's region to luminance through Egami's pixel conversion.</summary>
    public static LuminanceImage From<TPixel>(Raster<TPixel> raster, Rect? region)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rect area = region is Rect r ? r.Intersect(new Rect(0, 0, raster.Width, raster.Height)) : new Rect(0, 0, raster.Width, raster.Height);
        byte[] pixels = new byte[area.Width * area.Height];
        Span<Gray8> gray = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Gray8>(pixels.AsSpan());
        for (int y = 0; y < area.Height; y++)
        {
            if (typeof(TPixel) == typeof(Gray8))
            {
                MemoryMarshal.AsBytes(raster.Row(area.Y + y).Slice(area.X, area.Width)).CopyTo(pixels.AsSpan(y * area.Width, area.Width));
            }
            else
            {
                PixelConvert.Convert<TPixel, Gray8>(raster.Row(area.Y + y).Slice(area.X, area.Width), gray.Slice(y * area.Width, area.Width));
            }
        }

        return new LuminanceImage(pixels, area.Width, area.Height, area.Width, area.X, area.Y);
    }

    /// <summary>Views a region of a luminance buffer without copying it.</summary>
    public static LuminanceImage View(ReadOnlyMemory<byte> luminance, int width, int height, int stride, Rect? region)
    {
        Rect area = region is Rect r ? r.Intersect(new Rect(0, 0, width, height)) : new Rect(0, 0, width, height);
        if (area.Width <= 0 || area.Height <= 0)
        {
            return new LuminanceImage(Array.Empty<byte>(), 0, 0, 0, area.X, area.Y);
        }

        int start = (area.Y * stride) + area.X;
        return new LuminanceImage(luminance[start..(start + ((area.Height - 1) * stride) + area.Width)], area.Width, area.Height, stride, area.X, area.Y);
    }

    private LuminanceImage? _inverted;

    /// <summary>Returns the image with light and dark swapped, made once and kept.</summary>
    public LuminanceImage Inverted() => LazyInitializer.EnsureInitialized(ref _inverted, () =>
    {
        byte[] pixels = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            ReadOnlySpan<byte> row = Row(y);
            Span<byte> target = pixels.AsSpan(y * Width, Width);
            int x = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                for (; x <= Width - 16; x += 16)
                {
                    (~Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(row), (nuint)x)).StoreUnsafe(ref MemoryMarshal.GetReference(target), (nuint)x);
                }
            }

            for (; x < Width; x++)
            {
                target[x] = (byte)(255 - row[x]);
            }
        }

        return new LuminanceImage(pixels, Width, Height, Width, Left, Top);
    });
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

    /// <summary>Returns the bits with dark and light swapped, as the inverted image thresholds.</summary>
    public BitImage Complement()
    {
        byte[] bits = new byte[Bits.Length];
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> one = Vector128<byte>.One;
            for (; i <= bits.Length - 16; i += 16)
            {
                (Vector128.LoadUnsafe(ref Bits[0], (nuint)i) ^ one).StoreUnsafe(ref bits[0], (nuint)i);
            }
        }

        for (; i < bits.Length; i++)
        {
            bits[i] = (byte)(Bits[i] ^ 1);
        }

        return new BitImage(bits, Width, Height);
    }

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
        // The histogram of every other row (all rows of small images) is enough to place the valley; four histograms side by
        // side, so consecutive pixels do not wait on the same counter.
        Span<int> counts = stackalloc int[4 * 32];
        ref int count = ref MemoryMarshal.GetReference(counts);
        int rowStep = image.Height >= 64 ? 2 : 1;
        for (int y = 0; y < image.Height; y += rowStep)
        {
            ReadOnlySpan<byte> row = image.Row(y);
            ref byte pixel = ref MemoryMarshal.GetReference(row);
            int x = 0;
            for (; x <= row.Length - 4; x += 4)
            {
                // Each index is below 32 (a byte over 8), within its histogram.
                Unsafe.Add(ref count, Unsafe.Add(ref pixel, x) >> 3)++;
                Unsafe.Add(ref count, 32 + (Unsafe.Add(ref pixel, x + 1) >> 3))++;
                Unsafe.Add(ref count, 64 + (Unsafe.Add(ref pixel, x + 2) >> 3))++;
                Unsafe.Add(ref count, 96 + (Unsafe.Add(ref pixel, x + 3) >> 3))++;
            }

            for (; x < row.Length; x++)
            {
                counts[row[x] >> 3]++;
            }
        }

        Span<int> histogram = stackalloc int[32];
        for (int i = 0; i < 32; i++)
        {
            histogram[i] = counts[i] + counts[32 + i] + counts[64 + i] + counts[96 + i];
        }

        int threshold = Valley(histogram) << 3;
        byte[] bits = new byte[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            ThresholdRow(image.Row(y), bits.AsSpan(y * image.Width, image.Width), threshold - 1);
        }

        return new BitImage(bits, image.Width, image.Height);
    }

    /// <summary>Sets each bit to 1 where the pixel is at most <paramref name="threshold"/>, else 0.</summary>
    private static void ThresholdRow(ReadOnlySpan<byte> row, Span<byte> bits, int threshold)
    {
        if (threshold < 0)
        {
            bits.Clear();
            return;
        }

        int x = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> limit = Vector128.Create((byte)threshold), one = Vector128<byte>.One;
            ref byte source = ref MemoryMarshal.GetReference(row);
            ref byte target = ref MemoryMarshal.GetReference(bits);
            for (; x <= row.Length - 16; x += 16)
            {
                (Vector128.LessThanOrEqual(Vector128.LoadUnsafe(ref source, (nuint)x), limit) & one).StoreUnsafe(ref target, (nuint)x);
            }
        }

        for (; x < row.Length; x++)
        {
            bits[x] = row[x] <= threshold ? (byte)1 : (byte)0;
        }
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
        int pairs = width / (2 * BlockSize);
        Span<Vector128<byte>> mins = pairs <= 128 ? stackalloc Vector128<byte>[pairs] : new Vector128<byte>[pairs];
        Span<Vector128<byte>> maxs = pairs <= 128 ? stackalloc Vector128<byte>[pairs] : new Vector128<byte>[pairs];
        Span<Vector128<ushort>> lows = pairs <= 128 ? stackalloc Vector128<ushort>[pairs] : new Vector128<ushort>[pairs];
        Span<Vector128<ushort>> highs = pairs <= 128 ? stackalloc Vector128<ushort>[pairs] : new Vector128<ushort>[pairs];
        for (int by = 0; by < blocksY; by++)
        {
            int y0 = Math.Min(by * BlockSize, height - BlockSize);
            int bx = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                // Two blocks at a time, while both sit at their natural place (the last block may overlap the one before it):
                // each row of the band is read once, into each pair's running minimum, maximum and sums.
                mins.Fill(Vector128<byte>.AllBitsSet);
                maxs.Clear();
                lows.Clear();
                highs.Clear();
                for (int y = 0; y < BlockSize; y++)
                {
                    ref byte row = ref MemoryMarshal.GetReference(image.Row(y0 + y));
                    for (int pair = 0; pair < pairs; pair++)
                    {
                        Vector128<byte> v = Vector128.LoadUnsafe(ref row, (nuint)(pair * 2 * BlockSize));
                        mins[pair] = Vector128.Min(mins[pair], v);
                        maxs[pair] = Vector128.Max(maxs[pair], v);
                        (Vector128<ushort> l, Vector128<ushort> h) = Vector128.Widen(v);
                        lows[pair] += l;
                        highs[pair] += h;
                    }
                }

                for (int pair = 0; pair < pairs; pair++, bx += 2)
                {
                    // Each 8-byte half folded onto its first byte: the two blocks' minimum and maximum.
                    Vector128<byte> min = mins[pair], max = maxs[pair];
                    for (int shift = 32; shift >= 8; shift >>= 1)
                    {
                        min = Vector128.Min(min, Vector128.ShiftRightLogical(min.AsUInt64(), shift).AsByte());
                        max = Vector128.Max(max, Vector128.ShiftRightLogical(max.AsUInt64(), shift).AsByte());
                    }

                    levels[(by * blocksX) + bx] = Level(levels, blocksX, bx, by, Vector128.Sum(lows[pair]), min.GetElement(0), max.GetElement(0));
                    levels[(by * blocksX) + bx + 1] = Level(levels, blocksX, bx + 1, by, Vector128.Sum(highs[pair]), min.GetElement(8), max.GetElement(8));
                }
            }

            for (; bx < blocksX; bx++)
            {
                int x0 = Math.Min(bx * BlockSize, width - BlockSize);
                int sum = 0, min = 255, max = 0;
                for (int y = 0; y < BlockSize; y++)
                {
                    ReadOnlySpan<byte> row = image.Row(y0 + y).Slice(x0, BlockSize);
                    foreach (byte p in row)
                    {
                        sum += p;
                        min = Math.Min(min, p);
                        max = Math.Max(max, p);
                    }
                }

                levels[(by * blocksX) + bx] = Level(levels, blocksX, bx, by, sum, min, max);
            }
        }

        byte[] bits = new byte[width * height];
        ThresholdBlocks(image, levels, bits, blocksX, blocksY, threads);
        return new BitImage(bits, width, height);
    }

    /// <summary>
    /// Thresholds the image in stripes of block rows, in parallel when asked: each writes only its own pixels, except the last,
    /// which may overlap the one before it and so runs after the others. (Apart from <see cref="Local"/>, so that its loops
    /// keep their variables in registers rather than in a closure.)
    /// </summary>
    private static void ThresholdBlocks(LuminanceImage image, int[] levels, byte[] bits, int blocksX, int blocksY, int threads)
    {
        if (threads > 1)
        {
            Parallel.For(0, blocksY - 1, new ParallelOptions { MaxDegreeOfParallelism = threads }, by => Threshold(image, levels, bits, blocksX, blocksY, by));
        }
        else
        {
            for (int by = 0; by < blocksY - 1; by++)
            {
                Threshold(image, levels, bits, blocksX, blocksY, by);
            }
        }

        Threshold(image, levels, bits, blocksX, blocksY, blocksY - 1);
    }

    /// <summary>A block's level from its sum, minimum and maximum; a flat block takes its neighbours' level when it sits in dark.</summary>
    private static int Level(int[] levels, int blocksX, int bx, int by, int sum, int min, int max)
    {
        if (max - min > MinimumDynamicRange)
        {
            return sum / (BlockSize * BlockSize);
        }

        // A flat block: assume it is light, unless the blocks above and to the left say it sits in dark.
        int average = min / 2;
        if (by > 0 && bx > 0)
        {
            int neighbours = (levels[((by - 1) * blocksX) + bx] + (2 * levels[(by * blocksX) + bx - 1]) + levels[((by - 1) * blocksX) + bx - 1]) / 4;
            if (min < neighbours)
            {
                average = neighbours;
            }
        }

        return average;
    }

    /// <summary>Thresholds one row of blocks by the mean level of the 5 x 5 blocks around each.</summary>
    private static void Threshold(LuminanceImage image, int[] levels, byte[] bits, int blocksX, int blocksY, int by)
    {
        int width = image.Width, height = image.Height;
        int y0 = Math.Min(by * BlockSize, height - BlockSize);
        int cy = Math.Clamp(by, 2, blocksY - 3);
        Span<int> thresholds = blocksX <= 1024 ? stackalloc int[blocksX] : new int[blocksX];
        for (int bx = 0; bx < blocksX; bx++)
        {
            int cx = Math.Clamp(bx, 2, blocksX - 3);
            int sum = 0;
            for (int dy = -2; dy <= 2; dy++)
            {
                int at = ((cy + dy) * blocksX) + cx;
                sum += levels[at - 2] + levels[at - 1] + levels[at] + levels[at + 1] + levels[at + 2];
            }

            thresholds[bx] = sum / 25;
        }

        // Two blocks' thresholds side by side in a vector, for the pairs of blocks at their natural place.
        int pairs = Vector128.IsHardwareAccelerated ? width / (2 * BlockSize) : 0;
        Span<Vector128<byte>> limits = pairs <= 128 ? stackalloc Vector128<byte>[pairs] : new Vector128<byte>[pairs];
        for (int pair = 0; pair < pairs; pair++)
        {
            limits[pair] = Vector128.Create((ulong)thresholds[2 * pair] * 0x0101010101010101UL, (ulong)thresholds[(2 * pair) + 1] * 0x0101010101010101UL).AsByte();
        }

        for (int y = 0; y < BlockSize; y++)
        {
            ReadOnlySpan<byte> row = image.Row(y0 + y);
            Span<byte> target = bits.AsSpan((y0 + y) * width, width);
            int bx = 0;
            if (pairs > 0)
            {
                Vector128<byte> one = Vector128<byte>.One;
                ref byte source = ref MemoryMarshal.GetReference(row);
                ref byte destination = ref MemoryMarshal.GetReference(target);
                for (int pair = 0; pair < pairs; pair++, bx += 2)
                {
                    nuint x = (nuint)(pair * 2 * BlockSize);
                    (Vector128.LessThanOrEqual(Vector128.LoadUnsafe(ref source, x), limits[pair]) & one).StoreUnsafe(ref destination, x);
                }
            }

            for (; bx < blocksX; bx++)
            {
                int x0 = Math.Min(bx * BlockSize, width - BlockSize), threshold = thresholds[bx];
                for (int x = x0; x < x0 + BlockSize; x++)
                {
                    target[x] = row[x] <= threshold ? (byte)1 : (byte)0;
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
        byte[] work = [];
        RowRuns(row, runs, offsets, ref work);
    }

    /// <summary>As <see cref="RowRuns(ReadOnlySpan{byte}, List{float}, List{float})"/>, with a work buffer kept between calls.</summary>
    public static void RowRuns(ReadOnlySpan<byte> row, List<float> runs, List<float> offsets, ref byte[] work)
    {
        int n = row.Length;
        Span<int> histogram = stackalloc int[32];
        foreach (byte p in row)
        {
            histogram[p >> 3]++;
        }

        int threshold = Valley(histogram) << 3;
        Span<byte> dark = Grow(ref work, n);

        // A sample is dark where its sharpened value, (4 c - l - r) / 2, is below the threshold: the same test in whole
        // numbers, sixteen samples at a time; the sub-pixel edges are worked out at the changes of color only.
        SharpenedDark(row, dark, 2 * threshold);
        EmitRuns(dark, new SharpenedCrossing(row, threshold), runs, offsets);
    }

    /// <summary>
    /// Cuts a row into runs against a threshold that follows the light: the mean over a window around each sample (an eighth of
    /// the row, at least 15 samples), so a gradient across a label does not swallow its bars. Edges are at sub-pixel positions.
    /// </summary>
    public static void AdaptiveRowRuns(ReadOnlySpan<byte> row, List<float> runs, List<float> offsets, ref byte[] work, ref int[] prefixes)
    {
        int n = row.Length, radius = Math.Max(7, n / 16);
        if (MaxMinusMin(row) < MinimumDynamicRange)
        {
            runs.Clear();
            offsets.Clear();
            runs.Add(n);
            offsets.Add(0);
            offsets.Add(n);
            return;
        }

        if (prefixes.Length < n + 1)
        {
            prefixes = new int[n + 1];
        }

        Span<int> prefix = prefixes.AsSpan(0, n + 1);
        prefix[0] = 0;
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += row[i];
            prefix[i + 1] = sum;
        }

        Span<byte> dark = Grow(ref work, n);
        WindowDark(row, prefix, radius, dark);
        EmitRuns(dark, new WindowCrossing(row, prefix, radius), runs, offsets);
    }

    private static Span<byte> Grow(ref byte[] buffer, int length)
    {
        if (buffer.Length < length)
        {
            buffer = new byte[Math.Max(length, buffer.Length * 2)];
        }

        return buffer.AsSpan(0, length);
    }

    /// <summary>The row's brightest sample less its darkest; 0 for an empty row.</summary>
    private static int MaxMinusMin(ReadOnlySpan<byte> row)
    {
        int i = 0, min = 255, max = 0;
        if (Vector128.IsHardwareAccelerated && row.Length >= 16)
        {
            ref byte start = ref MemoryMarshal.GetReference(row);
            Vector128<byte> low = Vector128.LoadUnsafe(ref start), high = low;
            for (i = 16; i + 16 <= row.Length; i += 16)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref start, (nuint)i);
                low = Vector128.Min(low, v);
                high = Vector128.Max(high, v);
            }

            for (int k = 0; k < 16; k++)
            {
                min = Math.Min(min, low[k]);
                max = Math.Max(max, high[k]);
            }
        }

        for (; i < row.Length; i++)
        {
            min = Math.Min(min, row[i]);
            max = Math.Max(max, row[i]);
        }

        return Math.Max(max - min, 0);
    }

    /// <summary>Marks the samples whose 4 c - l - r (the ends repeated) is below <paramref name="limit"/>.</summary>
    private static void SharpenedDark(ReadOnlySpan<byte> row, Span<byte> dark, int limit)
    {
        int n = row.Length;
        if (n == 0)
        {
            return;
        }

        if (n == 1)
        {
            dark[0] = (byte)(2 * row[0] < limit ? 1 : 0);
            return;
        }

        dark[0] = (byte)((3 * row[0]) - row[1] < limit ? 1 : 0);
        int i = 1;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte r = ref MemoryMarshal.GetReference(row);
            ref byte d = ref MemoryMarshal.GetReference(dark);
            Vector128<short> bound = Vector128.Create((short)limit);
            for (; i + 17 <= n; i += 16)
            {
                (Vector128<ushort> leftLow, Vector128<ushort> leftHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref r, (nuint)(i - 1)));
                (Vector128<ushort> centerLow, Vector128<ushort> centerHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref r, (nuint)i));
                (Vector128<ushort> rightLow, Vector128<ushort> rightHigh) = Vector128.Widen(Vector128.LoadUnsafe(ref r, (nuint)(i + 1)));
                Vector128<short> low = ((centerLow << 2) - leftLow - rightLow).AsInt16(), high = ((centerHigh << 2) - leftHigh - rightHigh).AsInt16();
                Vector128<sbyte> mask = Vector128.Narrow(Vector128.LessThan(low, bound), Vector128.LessThan(high, bound));
                (mask.AsByte() & Vector128<byte>.One).StoreUnsafe(ref d, (nuint)i);
            }
        }

        for (; i < n - 1; i++)
        {
            dark[i] = (byte)((4 * row[i]) - row[i - 1] - row[i + 1] < limit ? 1 : 0);
        }

        dark[n - 1] = (byte)((3 * row[n - 1]) - row[n - 2] < limit ? 1 : 0);
    }

    /// <summary>
    /// Marks the samples darker than the mean of the window <paramref name="radius"/> to each side (cut at the ends): in whole
    /// numbers, c (b - a) &lt; sum, which has the sign of c - sum / (b - a) in floats while the window is under 2^15 samples.
    /// </summary>
    private static void WindowDark(ReadOnlySpan<byte> row, ReadOnlySpan<int> prefix, int radius, Span<byte> dark)
    {
        int n = row.Length, width = (2 * radius) + 1;
        if (width >= 1 << 15)
        {
            var crossing = new WindowCrossing(row, prefix, radius);
            for (int k = 0; k < n; k++)
            {
                dark[k] = (byte)(crossing.Level(k) < 0 ? 1 : 0);
            }

            return;
        }

        // Samples radius to n - radius - 1 have the whole window; the ends have it cut.
        int inner = Math.Min(radius, n), outer = Math.Max(inner, n - radius);
        for (int k = 0; k < inner; k++)
        {
            dark[k] = Mark(row, prefix, radius, k);
        }

        int i = inner;
        if (Vector128.IsHardwareAccelerated)
        {
            ref byte r = ref MemoryMarshal.GetReference(row);
            ref int p = ref MemoryMarshal.GetReference(prefix);
            ref byte d = ref MemoryMarshal.GetReference(dark);
            Vector128<int> w = Vector128.Create(width);
            for (; i + 16 <= outer; i += 16)
            {
                (Vector128<ushort> low, Vector128<ushort> high) = Vector128.Widen(Vector128.LoadUnsafe(ref r, (nuint)i));
                (Vector128<uint> c0, Vector128<uint> c1) = Vector128.Widen(low);
                (Vector128<uint> c2, Vector128<uint> c3) = Vector128.Widen(high);
                nuint from = (nuint)(i - radius), to = (nuint)(i + radius + 1);
                Vector128<int> m0 = Vector128.LessThan(c0.AsInt32() * w, Vector128.LoadUnsafe(ref p, to) - Vector128.LoadUnsafe(ref p, from));
                Vector128<int> m1 = Vector128.LessThan(c1.AsInt32() * w, Vector128.LoadUnsafe(ref p, to + 4) - Vector128.LoadUnsafe(ref p, from + 4));
                Vector128<int> m2 = Vector128.LessThan(c2.AsInt32() * w, Vector128.LoadUnsafe(ref p, to + 8) - Vector128.LoadUnsafe(ref p, from + 8));
                Vector128<int> m3 = Vector128.LessThan(c3.AsInt32() * w, Vector128.LoadUnsafe(ref p, to + 12) - Vector128.LoadUnsafe(ref p, from + 12));
                Vector128<sbyte> mask = Vector128.Narrow(Vector128.Narrow(m0, m1), Vector128.Narrow(m2, m3));
                (mask.AsByte() & Vector128<byte>.One).StoreUnsafe(ref d, (nuint)i);
            }
        }

        for (; i < n; i++)
        {
            dark[i] = Mark(row, prefix, radius, i);
        }

        static byte Mark(ReadOnlySpan<byte> row, ReadOnlySpan<int> prefix, int radius, int k)
        {
            int a = Math.Max(0, k - radius), b = Math.Min(row.Length, k + radius + 1);
            return (byte)(row[k] * (b - a) < prefix[b] - prefix[a] ? 1 : 0);
        }
    }

    /// <summary>Places an edge between samples i - 1 and i at a sub-pixel position.</summary>
    private interface ICrossing
    {
        float Edge(int i);
    }

    /// <summary>Where the sharpened values cross the threshold.</summary>
    private readonly ref struct SharpenedCrossing(ReadOnlySpan<byte> row, float threshold) : ICrossing
    {
        private readonly ReadOnlySpan<byte> _row = row;

        public float Edge(int i)
        {
            float a = i > 0 ? Sharp(i - 1) : Sharp(i), b = Sharp(i);
            return a == b ? i : i - 0.5f + Math.Clamp((threshold - a) / (b - a), 0, 1);
        }

        private float Sharp(int i)
        {
            int left = _row[Math.Max(i - 1, 0)], right = _row[Math.Min(i + 1, _row.Length - 1)];
            return ((4 * _row[i]) - left - right) / 2f;
        }
    }

    /// <summary>Where the samples cross their window's mean.</summary>
    private readonly ref struct WindowCrossing(ReadOnlySpan<byte> row, ReadOnlySpan<int> prefix, int radius) : ICrossing
    {
        private readonly ReadOnlySpan<byte> _row = row;
        private readonly ReadOnlySpan<int> _prefix = prefix;

        public float Edge(int i)
        {
            float a = i > 0 ? Level(i - 1) : Level(i), b = Level(i);
            return a == b ? i : i - 0.5f + Math.Clamp(-a / (b - a), 0, 1);
        }

        public float Level(int i)
        {
            int a = Math.Max(0, i - radius), b = Math.Min(_row.Length, i + radius + 1);
            return _row[i] - ((_prefix[b] - _prefix[a]) / (float)(b - a));
        }
    }

    /// <summary>Cuts marked samples into runs, light first (possibly empty), with each edge where the crossing places it.</summary>
    private static void EmitRuns<T>(ReadOnlySpan<byte> dark, T crossing, List<float> runList, List<float> offsetList)
        where T : ICrossing, allows ref struct
    {
        // At most one run per sample and one more, written straight into the lists' storage.
        int n = dark.Length, count = 0;
        CollectionsMarshal.SetCount(runList, n + 1);
        CollectionsMarshal.SetCount(offsetList, n + 2);
        Span<float> runs = CollectionsMarshal.AsSpan(runList), offsets = CollectionsMarshal.AsSpan(offsetList);
        float start = 0;
        if (n > 0 && dark[0] != 0)
        {
            Add(runs, offsets, ref count, ref start, crossing.Edge(0));
        }

        int x = 1;
        if (Vector128.IsHardwareAccelerated)
        {
            // Sixteen neighbours compared at a time; each set bit of the mask is a change of color.
            ref byte d = ref MemoryMarshal.GetReference(dark);
            for (; x + 16 <= n; x += 16)
            {
                uint changes = (~Vector128.Equals(Vector128.LoadUnsafe(ref d, (nuint)x), Vector128.LoadUnsafe(ref d, (nuint)(x - 1)))).ExtractMostSignificantBits();
                while (changes != 0)
                {
                    Add(runs, offsets, ref count, ref start, crossing.Edge(x + BitOperations.TrailingZeroCount(changes)));
                    changes &= changes - 1;
                }
            }
        }

        for (; x < n; x++)
        {
            if (dark[x] != dark[x - 1])
            {
                Add(runs, offsets, ref count, ref start, crossing.Edge(x));
            }
        }

        runs[count] = n - start;
        offsets[count] = start;
        offsets[count + 1] = n;
        CollectionsMarshal.SetCount(runList, count + 1);
        CollectionsMarshal.SetCount(offsetList, count + 2);

        static void Add(Span<float> runs, Span<float> offsets, ref int count, ref float start, float edge)
        {
            runs[count] = Math.Max(edge - start, 0);
            offsets[count++] = start;
            start = edge;
        }
    }

    /// <summary>
    /// Cuts a row of dark and light bits into run lengths, light first (possibly empty), and returns how many; the changes of
    /// color are found sixteen pixels at a time.
    /// </summary>
    public static int IntRuns(ReadOnlySpan<byte> dark, Span<int> runs)
    {
        int n = dark.Length, count = 0, start = 0, x = 1;
        if (n == 0)
        {
            runs[0] = 0;
            return 1;
        }

        if (dark[0] != 0)
        {
            runs[count++] = 0;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            // Sixteen neighbours compared at a time; each set bit of the mask is a change of color.
            ref byte row = ref MemoryMarshal.GetReference(dark);
            for (; x + 16 <= n; x += 16)
            {
                uint changes = (~Vector128.Equals(Vector128.LoadUnsafe(ref row, (nuint)x), Vector128.LoadUnsafe(ref row, (nuint)(x - 1)))).ExtractMostSignificantBits();
                while (changes != 0)
                {
                    int at = x + BitOperations.TrailingZeroCount(changes);
                    runs[count++] = at - start;
                    start = at;
                    changes &= changes - 1;
                }
            }
        }

        for (; x < n; x++)
        {
            if (dark[x] != dark[x - 1])
            {
                runs[count++] = x - start;
                start = x;
            }
        }

        runs[count++] = n - start;
        return count;
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
