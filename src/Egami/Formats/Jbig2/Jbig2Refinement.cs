using System.Runtime.CompilerServices;

namespace Egami.Formats.Jbig2;

/// <summary>The parameters of the generic refinement region decoding procedure (T.88 6.3.2, Table 6).</summary>
internal sealed class Jbig2RefinementParams
{
    internal int Width { get; init; }

    internal int Height { get; init; }

    internal int Template { get; init; }

    internal Jbig2Bitmap Reference { get; init; } = null!;

    internal int ReferenceDx { get; init; }

    internal int ReferenceDy { get; init; }

    internal bool TypicalPrediction { get; init; }

    /// <summary>Gets RA1 and RA2 as (x, y, x, y), used by template 0.</summary>
    internal int[] At { get; init; } = [-1, -1, -1, -1];
}

/// <summary>
/// The generic refinement region decoding procedure (T.88 6.3). Contexts gather the refined bitmap's pixels and then the
/// reference's in reading order, the order of the T.88 example, so the SLTP contexts of Figures 14 and 15 (the reference pixel
/// under the current one) are 0x010 and 0x008.
/// </summary>
internal static class Jbig2Refinement
{
    internal static int ContextCount(int template) => template == 0 ? 1 << 13 : 1 << 10;

    internal static Jbig2Bitmap Decode(Jbig2RefinementParams p, MqDecoder mq, byte[] contexts)
    {
        Jbig2Bitmap bitmap = new(p.Width, p.Height);
        if (p.Width == 0 || p.Height == 0)
        {
            return bitmap;
        }

        Jbig2Bitmap reference = p.Reference;
        int sltp = p.Template == 0 ? 0x010 : 0x008;
        int ltp = 0;
        for (int y = 0; y < p.Height; y++)
        {
            if (mq.Overrun > Jbig2Generic.MqDecoderExhausted)
            {
                break;
            }

            if (p.TypicalPrediction)
            {
                ltp ^= mq.Decode(ref contexts[sltp]);
            }

            int yr = y - p.ReferenceDy;
            for (int x = 0; x < p.Width; x++)
            {
                int xr = x - p.ReferenceDx;
                if (ltp != 0)
                {
                    int center = reference.Get(xr, yr);
                    if (Uniform(reference, xr, yr, center))
                    {
                        if (center != 0)
                        {
                            bitmap.Set(x, y, 1);
                        }

                        continue;
                    }
                }

                int context = p.Template == 0 ? Context0(bitmap, reference, p.At, x, y, xr, yr) : Context1(bitmap, reference, x, y, xr, yr);
                if (mq.Decode(ref contexts[context]) != 0)
                {
                    bitmap.Set(x, y, 1);
                }
            }
        }

        return bitmap;
    }

    /// <summary>Returns whether the 3 × 3 reference pixels around (<paramref name="x"/>, <paramref name="y"/>) all equal <paramref name="value"/> (TPGR).</summary>
    private static bool Uniform(Jbig2Bitmap reference, int x, int y, int value)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (reference.Get(x + dx, y + dy) != value)
                {
                    return false;
                }
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Context0(Jbig2Bitmap g, Jbig2Bitmap r, int[] at, int x, int y, int xr, int yr)
    {
        return (g.Get(x + at[0], y + at[1]) << 12)
            | (g.Get(x, y - 1) << 11)
            | (g.Get(x + 1, y - 1) << 10)
            | (g.Get(x - 1, y) << 9)
            | (r.Get(xr + at[2], yr + at[3]) << 8)
            | (r.Get(xr, yr - 1) << 7)
            | (r.Get(xr + 1, yr - 1) << 6)
            | (r.Get(xr - 1, yr) << 5)
            | (r.Get(xr, yr) << 4)
            | (r.Get(xr + 1, yr) << 3)
            | (r.Get(xr - 1, yr + 1) << 2)
            | (r.Get(xr, yr + 1) << 1)
            | r.Get(xr + 1, yr + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Context1(Jbig2Bitmap g, Jbig2Bitmap r, int x, int y, int xr, int yr)
    {
        return (g.Get(x - 1, y - 1) << 9)
            | (g.Get(x, y - 1) << 8)
            | (g.Get(x + 1, y - 1) << 7)
            | (g.Get(x - 1, y) << 6)
            | (r.Get(xr, yr - 1) << 5)
            | (r.Get(xr - 1, yr) << 4)
            | (r.Get(xr, yr) << 3)
            | (r.Get(xr + 1, yr) << 2)
            | (r.Get(xr, yr + 1) << 1)
            | r.Get(xr + 1, yr + 1);
    }
}
