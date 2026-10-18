namespace Egami.Formats.Av1;

/// <summary>
/// Decides whether a picture is screen content (text, graphics, user interfaces) that palettes and intra block copy pay off on,
/// after libaom's anti-aliasing aware detection (estimate_screen_content_antialiasing_aware, encoder.c; BSD 2-clause, see
/// THIRD-PARTY-NOTICES): luma is cut into 16x16 blocks, at 8 bits; a block of 2 to 4 colors counts for palettes, and for intra
/// block copy when it varies; a block of up to 40 colors whose dominant color, grown by one sample, leaves at most 6 counts for
/// both when it varies; a block of more than 40 colors counts as photo, a sixteenth against the others.
/// </summary>
internal static class Av1ScreenContent
{
    private const int Block = 16;

    /// <summary>Gets whether screen content tools, and intra block copy with them, should be on for a luma plane.</summary>
    internal static (bool Tools, bool IntraBlockCopy) Detect(ReadOnlySpan<ushort> luma, int width, int height, int bitDepth)
    {
        long palette = 0, intrabc = 0, photo = 0;
        Span<byte> block = stackalloc byte[Block * Block];
        Span<byte> dilated = stackalloc byte[Block * Block];
        Span<int> counts = stackalloc int[256];
        int shift = bitDepth - 8;
        for (int r = 0; r + Block <= height; r += Block)
        {
            for (int c = 0; c + Block <= width; c += Block)
            {
                long sum = 0, squares = 0;
                for (int y = 0; y < Block; y++)
                {
                    ReadOnlySpan<ushort> row = luma.Slice(((r + y) * width) + c, Block);
                    for (int x = 0; x < Block; x++)
                    {
                        int v = row[x] >> shift;
                        block[(y * Block) + x] = (byte)v;
                        sum += v;
                        squares += v * v;
                    }
                }

                int colors = Colors(block, counts, 40);
                if (colors > 1 && colors <= 40)
                {
                    // Per-sample variance, at 8 bits (av1_get_perpixel_variance).
                    long variance = (squares - ((sum * sum) >> 8) + 128) >> 8;
                    if (colors <= 4)
                    {
                        palette++;
                        intrabc += variance > 5 ? 1 : 0;
                    }
                    else
                    {
                        Dilate(block, dilated, counts);
                        if (Colors(dilated, counts, 6) <= 6 && variance > 5)
                        {
                            palette++;
                            intrabc++;
                        }
                    }
                }
                else if (colors > 40)
                {
                    photo++;
                }
            }
        }

        long area = (long)width * height;
        bool tools = (palette - (photo / 16)) * Block * Block * 10 > area;
        return (tools, tools && (intrabc - (photo / 16)) * Block * Block * 12 > area);
    }

    /// <summary>Counts a block's distinct values, stopping one past <paramref name="limit"/>.</summary>
    private static int Colors(ReadOnlySpan<byte> block, Span<int> counts, int limit)
    {
        counts.Clear();
        int colors = 0;
        foreach (byte v in block)
        {
            if (counts[v]++ == 0 && ++colors > limit)
            {
                break;
            }
        }

        return colors;
    }

    /// <summary>Grows the block's most frequent value by one sample in all eight directions (av1_dilate_block).</summary>
    private static void Dilate(ReadOnlySpan<byte> block, Span<byte> dilated, Span<int> counts)
    {
        counts.Clear();
        int dominant = 0, best = 0;
        foreach (byte v in block)
        {
            if (++counts[v] > best)
            {
                (dominant, best) = (v, counts[v]);
            }
        }

        block.CopyTo(dilated);
        for (int y = 0; y < Block; y++)
        {
            for (int x = 0; x < Block; x++)
            {
                if (block[(y * Block) + x] != dominant)
                {
                    continue;
                }

                for (int dy = Math.Max(0, y - 1); dy <= Math.Min(Block - 1, y + 1); dy++)
                {
                    for (int dx = Math.Max(0, x - 1); dx <= Math.Min(Block - 1, x + 1); dx++)
                    {
                        dilated[(dy * Block) + dx] = (byte)dominant;
                    }
                }
            }
        }
    }
}
