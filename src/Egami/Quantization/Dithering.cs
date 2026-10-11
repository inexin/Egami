using System.Buffers;

using Egami.Pixels;

namespace Egami.Quantization;

/// <summary>Fills <paramref name="row"/> with image row <paramref name="y"/> as Rgba8.</summary>
internal delegate void RowSource(int y, Span<Rgba8> row);

/// <summary>
/// Turns a frame's pixels into palette indices: exactly when the palette holds every color, otherwise to the nearest color with
/// Floyd–Steinberg error diffusion, an ordered pattern, or no dithering. Transparent pixels take the transparent index and never
/// receive or spread error.
/// </summary>
internal static class FrameMapper
{
    /// <summary>The 8 × 8 Bayer matrix, 0 to 63.</summary>
    internal static ReadOnlySpan<byte> Bayer =>
    [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ];

    /// <summary>Maps a <paramref name="width"/> × <paramref name="height"/> frame into <paramref name="indices"/>, row by row.</summary>
    internal static void Map(int width, int height, RowSource source, BuiltPalette palette, PaletteMapper mapper, DitherMode mode, Span<byte> indices)
    {
        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(width);
        try
        {
            if (palette.Exact)
            {
                for (int y = 0; y < height; y++)
                {
                    source(y, row.AsSpan(0, width));
                    Span<byte> target = indices.Slice(y * width, width);
                    for (int x = 0; x < width; x++)
                    {
                        int index = palette.ExactTable!.IndexOf(row[x]);
                        target[x] = (byte)(index < 0 ? palette.TransparentIndex : index);
                    }
                }

                return;
            }

            switch (mode)
            {
                case DitherMode.FloydSteinberg:
                    FloydSteinberg(width, height, source, mapper, row, indices);
                    break;
                case DitherMode.Ordered:
                    Ordered(width, height, source, mapper, row, indices);
                    break;
                default:
                    for (int y = 0; y < height; y++)
                    {
                        source(y, row.AsSpan(0, width));
                        mapper.MapRow(row.AsSpan(0, width), indices.Slice(y * width, width));
                    }

                    break;
            }
        }
        finally
        {
            ArrayPool<Rgba8>.Shared.Return(row);
        }
    }

    /// <summary>
    /// Floyd–Steinberg (1976): each pixel's error goes 7/16 to the right, 3/16 below left, 5/16 below and 1/16 below right, in
    /// sixteenths on encoded values, left to right on every row.
    /// </summary>
    private static void FloydSteinberg(int width, int height, RowSource source, PaletteMapper mapper, Rgba8[] row, Span<byte> indices)
    {
        int channels = mapper.WithAlpha ? 4 : 3;
        int stride = (width + 2) * channels;
        int[] current = ArrayPool<int>.Shared.Rent(stride);
        int[] next = ArrayPool<int>.Shared.Rent(stride);
        Rgba8[] palette = mapper.Palette;
        try
        {
            current.AsSpan(0, stride).Clear();
            next.AsSpan(0, stride).Clear();
            Span<int> value = stackalloc int[4];
            for (int y = 0; y < height; y++)
            {
                source(y, row.AsSpan(0, width));
                Span<byte> target = indices.Slice(y * width, width);
                if (channels == 3)
                {
                    DiffuseRow(row.AsSpan(0, width), target, current, next, mapper, palette);
                    (current, next) = (next, current);
                    next.AsSpan(0, stride).Clear();
                    continue;
                }

                for (int x = 0; x < width; x++)
                {
                    Rgba8 pixel = row[x];
                    if (mapper.IsTransparent(pixel))
                    {
                        target[x] = (byte)mapper.Map(pixel);
                        continue;
                    }

                    int at = (x + 1) * channels;
                    value[0] = Clamp(pixel.R + Round(current[at]));
                    value[1] = Clamp(pixel.G + Round(current[at + 1]));
                    value[2] = Clamp(pixel.B + Round(current[at + 2]));
                    value[3] = channels == 4 ? Clamp(pixel.A + Round(current[at + 3])) : pixel.A;
                    int index = mapper.Map(new Rgba8((byte)value[0], (byte)value[1], (byte)value[2], (byte)value[3]));
                    target[x] = (byte)index;
                    Rgba8 chosen = palette[index];
                    for (int c = 0; c < channels; c++)
                    {
                        int error = value[c] - Channel(chosen, c);
                        current[at + channels + c] += error * 7;
                        next[at - channels + c] += error * 3;
                        next[at + c] += error * 5;
                        next[at + channels + c] += error;
                    }
                }

                (current, next) = (next, current);
                next.AsSpan(0, stride).Clear();
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(current);
            ArrayPool<int>.Shared.Return(next);
        }
    }

    /// <summary>One row of <see cref="FloydSteinberg"/> for RGB palettes, with the three channels written out.</summary>
    private static void DiffuseRow(ReadOnlySpan<Rgba8> row, Span<byte> target, int[] current, int[] next, PaletteMapper mapper, Rgba8[] palette)
    {
        for (int x = 0; x < row.Length; x++)
        {
            Rgba8 pixel = row[x];
            if (mapper.IsTransparent(pixel))
            {
                target[x] = (byte)mapper.Map(pixel);
                continue;
            }

            int at = (x + 1) * 3;
            int r = Clamp(pixel.R + Round(current[at]));
            int g = Clamp(pixel.G + Round(current[at + 1]));
            int b = Clamp(pixel.B + Round(current[at + 2]));
            int index = mapper.Map(new Rgba8((byte)r, (byte)g, (byte)b, pixel.A));
            target[x] = (byte)index;
            Rgba8 chosen = palette[index];
            int er = r - chosen.R;
            int eg = g - chosen.G;
            int eb = b - chosen.B;
            current[at + 3] += er * 7;
            current[at + 4] += eg * 7;
            current[at + 5] += eb * 7;
            next[at - 3] += er * 3;
            next[at - 2] += eg * 3;
            next[at - 1] += eb * 3;
            next[at] += er * 5;
            next[at + 1] += eg * 5;
            next[at + 2] += eb * 5;
            next[at + 3] += er;
            next[at + 4] += eg;
            next[at + 5] += eb;
        }
    }

    /// <summary>
    /// Ordered dithering: an 8 × 8 Bayer threshold, scaled to the typical spacing between the palette's colors, is added before mapping.
    /// Rows do not depend on each other, and the same pixel always dithers the same way in every frame.
    /// </summary>
    private static void Ordered(int width, int height, RowSource source, PaletteMapper mapper, Rgba8[] row, Span<byte> indices)
    {
        double spread = Spacing(mapper);
        Span<int> offsets = stackalloc int[64];
        for (int i = 0; i < 64; i++)
        {
            offsets[i] = (int)Math.Round((((Bayer[i] + 0.5) / 64.0) - 0.5) * spread);
        }

        for (int y = 0; y < height; y++)
        {
            source(y, row.AsSpan(0, width));
            Span<byte> target = indices.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                Rgba8 pixel = row[x];
                if (mapper.IsTransparent(pixel))
                {
                    target[x] = (byte)mapper.Map(pixel);
                    continue;
                }

                int offset = offsets[((y & 7) * 8) + (x & 7)];
                target[x] = (byte)mapper.Map(new Rgba8((byte)Clamp(pixel.R + offset), (byte)Clamp(pixel.G + offset), (byte)Clamp(pixel.B + offset), pixel.A));
            }
        }
    }

    /// <summary>
    /// Returns the typical per-channel step between neighboring palette colors: the mean distance from each color to its nearest
    /// neighbor, divided by √3 since the pattern moves all three channels together.
    /// </summary>
    private static double Spacing(PaletteMapper mapper)
    {
        Rgba8[] palette = mapper.Palette;
        double total = 0;
        int count = 0;
        for (int i = 0; i < palette.Length; i++)
        {
            if (i == mapper.TransparentIndex)
            {
                continue;
            }

            int nearest = int.MaxValue;
            for (int j = 0; j < palette.Length; j++)
            {
                if (j == i || j == mapper.TransparentIndex)
                {
                    continue;
                }

                int dr = palette[i].R - palette[j].R;
                int dg = palette[i].G - palette[j].G;
                int db = palette[i].B - palette[j].B;
                nearest = Math.Min(nearest, (dr * dr) + (dg * dg) + (db * db));
            }

            if (nearest != int.MaxValue)
            {
                total += Math.Sqrt(nearest);
                count++;
            }
        }

        return count == 0 ? 0 : total / count / Math.Sqrt(3);
    }

    /// <summary>Sixteenths to whole levels, rounding half away from zero.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int Round(int sixteenths)
    {
        // (x + 8) >> 4 for x ≥ 0 and (x + 7) >> 4 below, which is −((−x + 8) >> 4).
        return (sixteenths + 8 - (int)((uint)sixteenths >> 31)) >> 4;
    }

    private static int Clamp(int value) => Math.Clamp(value, 0, 255);

    private static int Channel(Rgba8 color, int channel) => channel switch
    {
        0 => color.R,
        1 => color.G,
        2 => color.B,
        _ => color.A,
    };
}
