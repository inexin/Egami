namespace Egami.Formats.Jbig2;

/// <summary>The parameters of the halftone region decoding procedure (T.88 6.6.2, Table 20).</summary>
internal sealed class Jbig2HalftoneParams
{
    internal int Width { get; init; }

    internal int Height { get; init; }

    internal bool Mmr { get; init; }

    internal int Template { get; init; }

    internal Jbig2Bitmap[] Patterns { get; init; } = [];

    internal int DefaultPixel { get; init; }

    internal Jbig2Combine Combine { get; init; }

    internal bool EnableSkip { get; init; }

    internal int GridWidth { get; init; }

    internal int GridHeight { get; init; }

    internal int GridX { get; init; }

    internal int GridY { get; init; }

    internal int VectorX { get; init; }

    internal int VectorY { get; init; }
}

/// <summary>The halftone region decoding procedure (T.88 6.6) with the gray-scale image decoding procedure (Annex C).</summary>
internal static class Jbig2Halftone
{
    internal static Jbig2Bitmap Decode(Jbig2HalftoneParams p, byte[] data, int start, int end, Jbig2ArithState arith, Jbig2Budget budget)
    {
        Jbig2Bitmap region = budget.Bitmap(p.Width, p.Height);
        if (p.DefaultPixel != 0)
        {
            region.Fill(1);
        }

        if (p.Patterns.Length == 0)
        {
            throw new InvalidDataException("A halftone region's pattern dictionary has no patterns.");
        }

        int patternWidth = p.Patterns[0].Width;
        int patternHeight = p.Patterns[0].Height;
        Jbig2Bitmap? skip = null;
        if (p.EnableSkip)
        {
            skip = budget.Bitmap(p.GridWidth, p.GridHeight);
            for (int mg = 0; mg < p.GridHeight; mg++)
            {
                for (int ng = 0; ng < p.GridWidth; ng++)
                {
                    (long x, long y) = Position(p, mg, ng);
                    if (x + patternWidth <= 0 || x >= p.Width || y + patternHeight <= 0 || y >= p.Height)
                    {
                        skip.Set(ng, mg, 1);
                    }
                }
            }
        }

        int bpp = Jbig2Dictionaries.CeilLog2(p.Patterns.Length);
        budget.Charge((long)p.GridWidth * p.GridHeight * sizeof(int));
        int[] gray = GrayScale(p, bpp, skip, data, start, end, arith, budget);
        for (int mg = 0; mg < p.GridHeight; mg++)
        {
            for (int ng = 0; ng < p.GridWidth; ng++)
            {
                (long x, long y) = Position(p, mg, ng);
                if (x + patternWidth <= 0 || x >= p.Width || y + patternHeight <= 0 || y >= p.Height)
                {
                    continue;
                }

                int value = gray[(mg * p.GridWidth) + ng];
                if (value >= p.Patterns.Length)
                {
                    // A gray value past GRAYMAX names no pattern; T.88 leaves it undefined, so draw the last.
                    value = p.Patterns.Length - 1;
                }

                region.Compose(p.Patterns[value], (int)x, (int)y, p.Combine);
            }
        }

        return region;
    }

    /// <summary>Returns the grid position of a gray-scale value (6.6.5.2 step 1 a i).</summary>
    private static (long X, long Y) Position(Jbig2HalftoneParams p, long mg, long ng)
    {
        long x = (p.GridX + (mg * p.VectorY) + (ng * p.VectorX)) >> 8;
        long y = (p.GridY + (mg * p.VectorX) - (ng * p.VectorY)) >> 8;
        return (x, y);
    }

    /// <summary>Decodes the gray-scale image (Annex C): Gray-coded bit planes, most significant first.</summary>
    private static int[] GrayScale(Jbig2HalftoneParams p, int bpp, Jbig2Bitmap? skip, byte[] data, int start, int end, Jbig2ArithState arith, Jbig2Budget budget)
    {
        int w = p.GridWidth;
        int h = p.GridHeight;
        int[] gray = new int[checked(w * h)];
        if (bpp == 0 || w == 0 || h == 0)
        {
            return gray;
        }

        if (!p.Mmr)
        {
            arith.Mq.Start(data, start, end - start);
        }

        int at = start;
        Jbig2Bitmap? previous = null;
        int[] atPixels = p.Template switch
        {
            0 => [3, -1, -3, -1, 2, -2, -2, -2],
            1 => [3, -1],
            _ => [2, -1],
        };
        for (int j = bpp - 1; j >= 0; j--)
        {
            budget.Reserve(w, h);
            Jbig2Bitmap plane;
            if (p.Mmr)
            {
                (plane, int consumed) = Jbig2Generic.DecodeMmr(w, h, data.AsSpan(at, end - at));
                at += consumed;
            }
            else
            {
                Jbig2GenericParams generic = new()
                {
                    Width = w,
                    Height = h,
                    Template = p.Template,
                    At = atPixels,
                    Skip = skip,
                };
                plane = Jbig2Generic.DecodeArithmetic(generic, arith.Mq, arith.Gb);
            }

            if (previous is not null)
            {
                ulong[] words = plane.Words;
                ulong[] above = previous.Words;
                for (int i = 0; i < words.Length; i++)
                {
                    words[i] ^= above[i];
                }
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    gray[(y * w) + x] |= plane.Get(x, y) << j;
                }
            }

            previous = plane;
        }

        return gray;
    }
}
