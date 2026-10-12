using System.Buffers;
using System.Runtime.CompilerServices;

using Egami.Pixels;

namespace Egami.Quantization;

/// <summary>
/// Maps a frame to palette indices with any <see cref="PaletteOptions"/>: a published or custom error-diffusion kernel, at any
/// strength and optionally serpentine; Riemersma's Hilbert-curve dithering; any ordered map; and exact or table matching.
/// Errors are kept in double precision on encoded 8-bit values. Transparent pixels take the transparent index and never receive
/// or spread error.
/// </summary>
internal static class GeneralMapper
{
    internal static void Map(int width, int height, RowSource source, PaletteMapper mapper, PaletteOptions options, Span<byte> indices)
    {
        Matcher match = new(mapper, options.ExactMatching);
        ErrorDiffusionKernel? kernel = options.Kernel ?? DitherKernels.Kernel(options.Dither);
        if (kernel is not null)
        {
            Diffuse(width, height, source, mapper, match, kernel, options.DitherStrength, options.Serpentine, indices);
        }
        else if (options.Dither == DitherMode.Riemersma)
        {
            Riemersma(width, height, source, mapper, match, options.DitherStrength, indices);
        }
        else if (DitherKernels.Ordered(options.Dither) is (byte[] map, int size))
        {
            Ordered(width, height, source, mapper, match, map, size, options.DitherStrength, indices);
        }
        else
        {
            Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(width);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    source(y, row.AsSpan(0, width));
                    Span<byte> target = indices.Slice(y * width, width);
                    for (int x = 0; x < width; x++)
                    {
                        target[x] = (byte)match.Index(row[x]);
                    }
                }
            }
            finally
            {
                ArrayPool<Rgba8>.Shared.Return(row);
            }
        }
    }

    /// <summary>Error diffusion with a kernel: rows of pending error for the current row and the ones below it.</summary>
    private static void Diffuse(
        int width, int height, RowSource source, PaletteMapper mapper, Matcher match, ErrorDiffusionKernel kernel, float strength, bool serpentine, Span<byte> indices)
    {
        int channels = mapper.WithAlpha ? 4 : 3;
        int reach = kernel.Reach;
        int stride = (width + (2 * reach)) * channels;
        int depth = kernel.Depth;
        double[][] rows = new double[depth + 1][];
        for (int i = 0; i <= depth; i++)
        {
            rows[i] = ArrayPool<double>.Shared.Rent(stride);
            rows[i].AsSpan(0, stride).Clear();
        }

        (int Dx, int Dy, double Weight)[] taps = kernel.Taps;
        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(width);
        Rgba8[] palette = mapper.Palette;
        Span<int> value = stackalloc int[4];
        try
        {
            for (int y = 0; y < height; y++)
            {
                source(y, row.AsSpan(0, width));
                Span<byte> target = indices.Slice(y * width, width);
                bool backwards = serpentine && (y & 1) == 1;
                double[] current = rows[0];
                for (int i = 0; i < width; i++)
                {
                    int x = backwards ? width - 1 - i : i;
                    Rgba8 pixel = row[x];
                    if (mapper.IsTransparent(pixel))
                    {
                        target[x] = (byte)mapper.Map(pixel);
                        continue;
                    }

                    int at = (x + reach) * channels;
                    value[0] = Clamp(pixel.R + current[at]);
                    value[1] = Clamp(pixel.G + current[at + 1]);
                    value[2] = Clamp(pixel.B + current[at + 2]);
                    value[3] = channels == 4 ? Clamp(pixel.A + current[at + 3]) : pixel.A;
                    int index = match.Index(new Rgba8((byte)value[0], (byte)value[1], (byte)value[2], (byte)value[3]));
                    target[x] = (byte)index;
                    Rgba8 chosen = palette[index];
                    for (int c = 0; c < channels; c++)
                    {
                        double error = (value[c] - Channel(chosen, c)) * (double)strength;
                        if (error == 0)
                        {
                            continue;
                        }

                        foreach ((int dx, int dy, double weight) in taps)
                        {
                            int tx = x + (backwards ? -dx : dx);
                            if (tx < 0 || tx >= width || y + dy >= height)
                            {
                                continue;
                            }

                            rows[dy][((tx + reach) * channels) + c] += error * weight;
                        }
                    }
                }

                // The next row's pending error moves up; the freed row becomes the deepest, cleared.
                double[] done = rows[0];
                for (int i = 0; i < depth; i++)
                {
                    rows[i] = rows[i + 1];
                }

                rows[depth] = done;
                done.AsSpan(0, stride).Clear();
            }
        }
        finally
        {
            foreach (double[] r in rows)
            {
                ArrayPool<double>.Shared.Return(r);
            }

            ArrayPool<Rgba8>.Shared.Return(row);
        }
    }

    /// <summary>
    /// Riemersma (1998): pixels are visited along a Hilbert curve over the smallest power-of-two square that covers the frame, and
    /// each is corrected by a weighted sum of the last 16 errors on the curve, the newest weighing 16 times the oldest and the
    /// weights summing to 1. As in Riemersma's article, an error is the original pixel minus its palette color, not the corrected one.
    /// </summary>
    private static void Riemersma(int width, int height, RowSource source, PaletteMapper mapper, Matcher match, float strength, Span<byte> indices)
    {
        const int History = 16;
        int channels = mapper.WithAlpha ? 4 : 3;
        double[] weights = new double[History];
        double sum = 0;
        for (int i = 0; i < History; i++)
        {
            weights[i] = Math.Pow(History, (double)i / (History - 1));
            sum += weights[i];
        }

        for (int i = 0; i < History; i++)
        {
            weights[i] /= sum;
        }

        Rgba8[] pixels = ArrayPool<Rgba8>.Shared.Rent(width * height);
        double[] errors = new double[History * channels];
        Rgba8[] palette = mapper.Palette;
        Span<int> value = stackalloc int[4];
        try
        {
            for (int y = 0; y < height; y++)
            {
                source(y, pixels.AsSpan(y * width, width));
            }

            int side = 1;
            while (side < Math.Max(width, height))
            {
                side *= 2;
            }

            int head = 0;
            long count = (long)side * side;
            for (long d = 0; d < count; d++)
            {
                (int x, int y) = HilbertPoint(side, d);
                if (x >= width || y >= height)
                {
                    continue;
                }

                Rgba8 pixel = pixels[(y * width) + x];
                if (mapper.IsTransparent(pixel))
                {
                    indices[(y * width) + x] = (byte)mapper.Map(pixel);
                    continue;
                }

                for (int c = 0; c < channels; c++)
                {
                    // Slot head holds the oldest error and slot head + 15 the newest; the newest weighs most.
                    double correction = 0;
                    for (int k = 0; k < History; k++)
                    {
                        correction += errors[(((head + k) % History) * channels) + c] * weights[k];
                    }

                    value[c] = Clamp(Channel(pixel, c) + correction);
                }

                if (channels == 3)
                {
                    value[3] = pixel.A;
                }

                int index = match.Index(new Rgba8((byte)value[0], (byte)value[1], (byte)value[2], (byte)value[3]));
                indices[(y * width) + x] = (byte)index;
                Rgba8 chosen = palette[index];
                for (int c = 0; c < channels; c++)
                {
                    errors[(head * channels) + c] = (Channel(pixel, c) - Channel(chosen, c)) * (double)strength;
                }

                head = (head + 1) % History;
            }
        }
        finally
        {
            ArrayPool<Rgba8>.Shared.Return(pixels);
        }
    }

    /// <summary>Returns point <paramref name="d"/> of the Hilbert curve over a <paramref name="side"/> × <paramref name="side"/> square.</summary>
    internal static (int X, int Y) HilbertPoint(int side, long d)
    {
        int x = 0, y = 0;
        long t = d;
        for (int s = 1; s < side; s *= 2)
        {
            int rx = (int)(1 & (t / 2));
            int ry = (int)(1 & (t ^ rx));
            if (ry == 0)
            {
                if (rx == 1)
                {
                    x = s - 1 - x;
                    y = s - 1 - y;
                }

                (x, y) = (y, x);
            }

            x += s * rx;
            y += s * ry;
            t /= 4;
        }

        return (x, y);
    }

    /// <summary>
    /// Ordered dithering with an n × n threshold map, scaled to the typical spacing of the palette's colors times the strength,
    /// as 1.x's 8 × 8 pattern is.
    /// </summary>
    private static void Ordered(int width, int height, RowSource source, PaletteMapper mapper, Matcher match, byte[] map, int size, float strength, Span<byte> indices)
    {
        double spread = FrameMapper.Spacing(mapper) * strength;
        int cells = size * size;
        int[] offsets = new int[cells];
        for (int i = 0; i < cells; i++)
        {
            offsets[i] = (int)Math.Round((((map[i] + 0.5) / cells) - 0.5) * spread);
        }

        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(width);
        try
        {
            for (int y = 0; y < height; y++)
            {
                source(y, row.AsSpan(0, width));
                Span<byte> target = indices.Slice(y * width, width);
                int line = (y % size) * size;
                for (int x = 0; x < width; x++)
                {
                    Rgba8 pixel = row[x];
                    if (mapper.IsTransparent(pixel))
                    {
                        target[x] = (byte)mapper.Map(pixel);
                        continue;
                    }

                    int offset = offsets[line + (x % size)];
                    target[x] = (byte)match.Index(new Rgba8(
                        (byte)Math.Clamp(pixel.R + offset, 0, 255), (byte)Math.Clamp(pixel.G + offset, 0, 255), (byte)Math.Clamp(pixel.B + offset, 0, 255), pixel.A));
                }
            }
        }
        finally
        {
            ArrayPool<Rgba8>.Shared.Return(row);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Clamp(double value) => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private static int Channel(Rgba8 color, int channel) => channel switch
    {
        0 => color.R,
        1 => color.G,
        2 => color.B,
        _ => color.A,
    };

    /// <summary>
    /// Finds a color's palette index: through the mapper's lookup table, or exactly, with a 64K-entry direct-mapped cache of the
    /// colors seen so far.
    /// </summary>
    internal sealed class Matcher(PaletteMapper mapper, bool exact)
    {
        private readonly uint[]? _keys = exact ? new uint[65536] : null;
        private readonly short[]? _values = exact ? new short[65536] : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Index(Rgba8 pixel)
        {
            if (_keys is null)
            {
                return mapper.Map(pixel);
            }

            if (mapper.IsTransparent(pixel) && mapper.TransparentIndex >= 0)
            {
                return mapper.TransparentIndex;
            }

            uint key = Unsafe.As<Rgba8, uint>(ref pixel);
            if (!mapper.WithAlpha)
            {
                key |= 0xFF000000u;
            }

            int slot = (int)((key * 2654435761u) >> 16);
            if (_values![slot] > 0 && _keys[slot] == key)
            {
                return _values[slot] - 1;
            }

            int index = mapper.Nearest(mapper.WithAlpha ? pixel : new Rgba8(pixel.R, pixel.G, pixel.B, 255));
            _keys[slot] = key;
            _values[slot] = (short)(index + 1);
            return index;
        }
    }
}
