namespace Egami.Formats.Av1;

/// <summary>
/// Hashes of every square luma block of the source, 8x8 to 64x64, at every sample position, so the intra block copy search finds
/// the earlier places a block's content repeats at in one lookup, as libaom's hash based search does (hash_motion.c; here a
/// polynomial rolling hash rather than libaom's CRC). Flat blocks are left out: intra prediction codes them as well.
/// </summary>
internal sealed class Av1BlockHash
{
    private const ulong P = 0x100000001b3UL, Q = 0x9e3779b97f4a7c15UL;
    private readonly ushort[] _luma;
    private readonly int _stride;
    private readonly (ulong Hash, int Position)[][] _entries = new (ulong, int)[4][];

    /// <summary>Hashes the luma plane, <paramref name="width"/> by <paramref name="height"/> samples at its stride.</summary>
    internal Av1BlockHash(ushort[] luma, int stride, int width, int height)
    {
        _luma = luma;
        _stride = stride;
        ulong[] rows = new ulong[width * height];
        List<(ulong, int)> entries = [];
        for (int level = 0; level < 4; level++)
        {
            int size = 8 << level;
            entries.Clear();
            if (size <= width && size <= height)
            {
                ulong pRoll = Power(P, size - 1), qRoll = Power(Q, size - 1), flat = Flat(size);

                // Each row's hash of the size samples from x, sliding along the row.
                for (int y = 0; y < height; y++)
                {
                    int at = y * stride;
                    ulong h = 0;
                    for (int i = 0; i < size; i++)
                    {
                        h = (h * P) + luma[at + i];
                    }

                    for (int x = 0; ; x++)
                    {
                        rows[(y * width) + x] = h;
                        if (x + size >= width)
                        {
                            break;
                        }

                        h = ((h - (luma[at + x] * pRoll)) * P) + luma[at + x + size];
                    }
                }

                // Each block's hash of the size row hashes from y, sliding down the column.
                for (int x = 0; x + size <= width; x++)
                {
                    ulong h = 0;
                    for (int j = 0; j < size; j++)
                    {
                        h = (h * Q) + rows[(j * width) + x];
                    }

                    for (int y = 0; ; y++)
                    {
                        if (h != flat * luma[(y * stride) + x])
                        {
                            entries.Add((h, (y << 16) | x));
                        }

                        if (y + size >= height)
                        {
                            break;
                        }

                        h = ((h - (rows[(y * width) + x] * qRoll)) * Q) + rows[((y + size) * width) + x];
                    }
                }
            }

            (ulong, int)[] sorted = [.. entries];
            Array.Sort(sorted);
            _entries[level] = sorted;
        }
    }

    /// <summary>Gets the hash level of a square block size, or -1 for a size without hashes.</summary>
    internal static int Level(int bsize) => bsize switch
    {
        Bs.B8x8 => 0,
        Bs.B16x16 => 1,
        Bs.B32x32 => 2,
        Bs.B64x64 => 3,
        _ => -1,
    };

    /// <summary>Gets the hash of the block at a place.</summary>
    internal ulong Hash(int level, int x, int y)
    {
        int size = 8 << level;
        ulong h = 0;
        for (int j = 0; j < size; j++)
        {
            ulong row = 0;
            int at = ((y + j) * _stride) + x;
            for (int i = 0; i < size; i++)
            {
                row = (row * P) + _luma[at + i];
            }

            h = (h * Q) + row;
        }

        return h;
    }

    /// <summary>Gets the places, packed as <c>y &lt;&lt; 16 | x</c>, of the blocks with a hash.</summary>
    internal ReadOnlySpan<(ulong Hash, int Position)> Find(int level, ulong hash)
    {
        (ulong Hash, int Position)[] entries = _entries[level];
        int lo = 0, hi = entries.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (entries[mid].Hash < hash)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        int end = lo;
        while (end < entries.Length && entries[end].Hash == hash)
        {
            end++;
        }

        return entries.AsSpan(lo, end - lo);
    }

    /// <summary>Gets whether the source blocks at two places are the same.</summary>
    internal bool Same(int level, int x0, int y0, int x1, int y1)
    {
        int size = 8 << level;
        for (int j = 0; j < size; j++)
        {
            if (!_luma.AsSpan(((y0 + j) * _stride) + x0, size).SequenceEqual(_luma.AsSpan(((y1 + j) * _stride) + x1, size)))
            {
                return false;
            }
        }

        return true;
    }

    private static ulong Power(ulong b, int e)
    {
        ulong r = 1;
        for (int i = 0; i < e; i++)
        {
            r *= b;
        }

        return r;
    }

    /// <summary>Gets the hash of a flat block of value 1; a flat block of value v hashes to v times it.</summary>
    private static ulong Flat(int size)
    {
        ulong row = 0;
        for (int i = 0; i < size; i++)
        {
            row = (row * P) + 1;
        }

        ulong h = 0;
        for (int j = 0; j < size; j++)
        {
            h = (h * Q) + row;
        }

        return h;
    }
}
