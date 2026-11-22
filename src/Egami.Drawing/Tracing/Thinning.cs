namespace Egami.Drawing.Tracing;

/// <summary>
/// Thins ink to an 8-connected skeleton one pixel wide, written from T. Y. Zhang and C. Y. Suen, "A fast parallel algorithm
/// for thinning digital patterns" (Communications of the ACM 27(3), 1984): two sub-iterations, each removing at once the border
/// pixels whose removal keeps the pattern connected and its ends, the first from the south-east and the second from the
/// north-west, until neither removes any. The staircase corners the method leaves are then removed, so every skeleton pixel
/// along a line has exactly two neighbours.
/// </summary>
internal static class Thinning
{
    /// <summary>Returns the skeleton of <paramref name="ink"/> (1 for ink) as a new map, 1 on the skeleton.</summary>
    internal static byte[] Skeleton(ReadOnlySpan<byte> ink, int width, int height, CancellationToken cancellationToken)
    {
        byte[] map = ink.ToArray();
        List<int> alive = [];
        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] != 0)
            {
                alive.Add(i);
            }
        }

        // Each sub-iteration only reads the map while it marks, so the marking runs in parallel, in chunks of the live pixels.
        bool[] marked = [];
        ParallelOptions parallel = new() { CancellationToken = cancellationToken };
        bool changed = true;
        while (changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed = false;
            for (int pass = 0; pass < 2; pass++)
            {
                int count = alive.Count;
                if (count == 0)
                {
                    break;
                }

                if (marked.Length < count)
                {
                    marked = new bool[count];
                }

                bool[] marks = marked;
                int sub = pass;
                Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, count, 4096), parallel, range =>
                {
                    for (int j = range.Item1; j < range.Item2; j++)
                    {
                        marks[j] = Removable(map, width, height, alive[j], sub);
                    }
                });

                int kept = 0;
                for (int j = 0; j < count; j++)
                {
                    if (marks[j])
                    {
                        map[alive[j]] = 0;
                    }
                    else
                    {
                        alive[kept++] = alive[j];
                    }
                }

                if (kept < count)
                {
                    changed = true;
                    alive.RemoveRange(kept, count - kept);
                }
            }
        }

        RemoveStairs(map, width, height, alive);
        return map;
    }

    /// <summary>The neighbours of pixel i, P2 (north) to P9 (north-west) clockwise as in the paper, as bits 0 to 7.</summary>
    internal static int Neighbours(byte[] map, int width, int height, int i)
    {
        int x = i % width, y = i / width;
        int bits = 0;
        for (int k = 0; k < 8; k++)
        {
            int nx = x + Dx[k], ny = y + Dy[k];
            if ((uint)nx < (uint)width && (uint)ny < (uint)height && map[(ny * width) + nx] != 0)
            {
                bits |= 1 << k;
            }
        }

        return bits;
    }

    /// <summary>The steps to P2 (north), P3, ..., P9 (north-west), clockwise on screen.</summary>
    internal static ReadOnlySpan<int> Dx => [0, 1, 1, 1, 0, -1, -1, -1];

    /// <summary>The steps to P2 (north), P3, ..., P9 (north-west), clockwise on screen.</summary>
    internal static ReadOnlySpan<int> Dy => [-1, -1, 0, 1, 1, 1, 0, -1];

    private static bool Removable(byte[] map, int width, int height, int i, int pass)
    {
        int p = Neighbours(map, width, height, i);
        int b = System.Numerics.BitOperations.PopCount((uint)p);
        if (b < 2 || b > 6)
        {
            return false;
        }

        // A: the 0 to 1 transitions round P2, P3, ..., P9, P2.
        int a = 0;
        for (int k = 0; k < 8; k++)
        {
            if ((p & (1 << k)) == 0 && (p & (1 << ((k + 1) & 7))) != 0)
            {
                a++;
            }
        }

        if (a != 1)
        {
            return false;
        }

        bool p2 = (p & 1) != 0, p4 = (p & 4) != 0, p6 = (p & 16) != 0, p8 = (p & 64) != 0;
        return pass == 0
            ? !(p2 && p4 && p6) && !(p4 && p6 && p8)
            : !(p2 && p4 && p8) && !(p2 && p6 && p8);
    }

    /// <summary>
    /// Removes each pixel that only turns a staircase corner: two 4-neighbours at right angles, which touch each other at a
    /// corner, with nothing on the opposite side. Its neighbours stay connected through each other.
    /// </summary>
    private static void RemoveStairs(byte[] map, int width, int height, List<int> alive)
    {
        foreach (int i in alive)
        {
            int p = Neighbours(map, width, height, i);

            // Bits: 0 N, 1 NE, 2 E, 3 SE, 4 S, 5 SW, 6 W, 7 NW. For each pair of 4-neighbours at right angles, the three pixels on
            // the far side must be paper.
            if (Corner(p, 0, 2, 0b0111_0000) || Corner(p, 2, 4, 0b1100_0001) || Corner(p, 4, 6, 0b0000_0111) || Corner(p, 6, 0, 0b0001_1100))
            {
                map[i] = 0;
            }
        }

        alive.RemoveAll(i => map[i] == 0);
    }

    private static bool Corner(int p, int first, int second, int opposite)
    {
        return (p & (1 << first)) != 0 && (p & (1 << second)) != 0 && (p & opposite) == 0;
    }
}
