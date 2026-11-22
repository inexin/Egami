using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>
/// The black-and-white label image tracing works on: one byte per pixel, 1 for ink and 0 for paper. Ink is 8-connected (two ink
/// pixels that touch at a corner belong to one region) and paper 4-connected, the pairing the boundary follower's turn rule makes.
/// </summary>
internal sealed class InkMap
{
    private readonly byte[] _ink;

    internal InkMap(int width, int height)
    {
        Width = width;
        Height = height;
        _ink = new byte[checked(width * height)];
    }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>Gets the pixels, row by row.</summary>
    internal Span<byte> Pixels => _ink;

    /// <summary>Gets the map's array, 1 for ink, row by row, for loops that run in parallel.</summary>
    internal byte[] Data => _ink;

    /// <summary>Whether a pixel is ink; pixels outside the image are paper.</summary>
    internal bool this[int x, int y] => (uint)x < (uint)Width && (uint)y < (uint)Height && _ink[(y * Width) + x] != 0;

    /// <summary>
    /// Makes the map from a raster's first frame: a pixel is ink when its intensity, over white by its alpha, is at most
    /// <paramref name="level"/>. Intensity weighs red, green and blue as <see cref="Raster.FindThreshold"/> does.
    /// </summary>
    internal static InkMap FromRaster<TPixel>(Raster<TPixel> raster, float level, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return FromRaster(raster, level, null, cancellationToken);
    }

    /// <summary>
    /// Makes the map from a raster's first frame, as <see cref="FromRaster{TPixel}(Raster{TPixel}, float, CancellationToken)"/>
    /// does, and fills <paramref name="intensities"/>, when given, with each pixel's intensity over white from 0 to 255.
    /// </summary>
    internal static InkMap FromRaster<TPixel>(Raster<TPixel> raster, float level, byte[]? intensities, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        InkMap map = new(raster.Width, raster.Height);
        Parallel.For(0, raster.Height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            Span<TPixel> row = raster.Row(y);
            Span<byte> ink = map._ink.AsSpan(y * raster.Width, raster.Width);
            for (int x = 0; x < row.Length; x++)
            {
                Vector4 rgba = row[x].ToScaled();
                float intensity = (0.212656f * rgba.X) + (0.715158f * rgba.Y) + (0.072186f * rgba.Z);
                float alpha = Math.Clamp(rgba.W, 0, 1);
                float over = (alpha * intensity) + (1 - alpha);
                ink[x] = over <= level ? (byte)1 : (byte)0;
                if (intensities is not null)
                {
                    intensities[(y * raster.Width) + x] = (byte)((Math.Clamp(over, 0, 1) * 255) + 0.5f);
                }
            }
        });

        return map;
    }

    /// <summary>
    /// Merges every region of fewer than <paramref name="minArea"/> pixels into its neighbours, smallest first, until each
    /// region left is at least that large or has no neighbour. In black and white the neighbours of a region are all of the
    /// other color, so merging flips the region. Returns the regions of the map it leaves, numbered as <see cref="Label(int[],
    /// CancellationToken)"/> would number them (each merged set is one region, first met where its first member was), or
    /// <see langword="null"/> when <paramref name="minArea"/> is 1 or less and nothing was labelled.
    /// </summary>
    internal int[]? RemoveNoise(int minArea, CancellationToken cancellationToken)
    {
        if (minArea <= 1)
        {
            return null;
        }

        int[] labels = new int[_ink.Length];
        int count = Label(labels, cancellationToken, out long[] size, out byte[] color);

        List<int> small = [];
        for (int r = 0; r < count; r++)
        {
            if (size[r] < minArea)
            {
                small.Add(r);
            }
        }
        if (small.Count == 0)
        {
            return labels;
        }

        // The regions each small region touches across a pixel edge. Regions that touch only at a corner are both paper (ink is
        // 8-connected), so they never merge with each other directly.
        Dictionary<int, HashSet<int>> neighbours = [];
        foreach (int r in small)
        {
            neighbours[r] = [];
        }

        // Rows in parallel bands, each keeping the pairs of touching regions where one is small (in pooled buffers, as two
        // region numbers in a long); then the pairs in band order.
        int width = Width, height = Height;
        int bands = Math.Min(height, Environment.ProcessorCount * 4);
        long[][] pairs = new long[bands][];
        int[] pairCount = new int[bands];
        Parallel.For(0, bands, new ParallelOptions { CancellationToken = cancellationToken }, band =>
        {
            long[] found = System.Buffers.ArrayPool<long>.Shared.Rent(256);
            int n = 0;
            for (int y = (int)((long)band * height / bands); y < (int)((long)(band + 1) * height / bands); y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int a = labels[row + x];
                    if (x + 1 < width && labels[row + x + 1] is int right && right != a && (size[a] < minArea || size[right] < minArea))
                    {
                        Add(a, right);
                    }

                    if (y + 1 < height && labels[row + width + x] is int below && below != a && (size[a] < minArea || size[below] < minArea))
                    {
                        Add(a, below);
                    }
                }
            }

            (pairs[band], pairCount[band]) = (found, n);

            void Add(int a, int b)
            {
                if (n == found.Length)
                {
                    long[] larger = System.Buffers.ArrayPool<long>.Shared.Rent(2 * n);
                    found.AsSpan(0, n).CopyTo(larger);
                    System.Buffers.ArrayPool<long>.Shared.Return(found);
                    found = larger;
                }

                found[n++] = ((long)a << 32) | (uint)b;
            }
        });

        for (int band = 0; band < bands; band++)
        {
            foreach (long pair in pairs[band].AsSpan(0, pairCount[band]))
            {
                Touch(neighbours, (int)(pair >> 32), (int)pair);
            }

            System.Buffers.ArrayPool<long>.Shared.Return(pairs[band]);
        }

        int[] parent = new int[count];
        for (int r = 0; r < count; r++)
        {
            parent[r] = r;
        }

        small.Sort((a, b) => size[a] != size[b] ? size[a].CompareTo(size[b]) : a.CompareTo(b));
        foreach (int r in small)
        {
            int root = Find(parent, r);
            if (size[root] >= minArea || !neighbours.TryGetValue(root, out HashSet<int>? around))
            {
                continue;
            }

            HashSet<int> roots = [];
            foreach (int n in around)
            {
                int nr = Find(parent, n);
                if (nr != root)
                {
                    roots.Add(nr);
                }
            }

            if (roots.Count == 0)
            {
                continue;
            }

            // Flip the region and join it with everything around it, keeping the largest set's id as the root.
            byte flipped = (byte)(1 - color[root]);
            int keep = root;
            foreach (int nr in roots)
            {
                if (size[nr] > size[keep])
                {
                    keep = nr;
                }
            }

            HashSet<int> joined = neighbours.TryGetValue(keep, out HashSet<int>? kept) ? kept : [];
            long total = 0;
            foreach (int member in roots.Append(root))
            {
                total += size[member];
                if (member != keep)
                {
                    parent[member] = keep;
                    if (neighbours.Remove(member, out HashSet<int>? theirs))
                    {
                        joined.UnionWith(theirs);
                    }
                }
            }

            size[keep] = total;
            color[keep] = flipped;
            neighbours[keep] = joined;
        }
        // Each region's final color once, and each merged set numbered by its least member, which the scan met first. The sizes
        // are done with: a root's holds its set's number, as -1 - number, once the set is met.
        byte[] final = new byte[count];
        int sets = 0;
        for (int r = 0; r < count; r++)
        {
            int root = Find(parent, r);
            parent[r] = root;
            final[r] = color[root];
            if (size[root] >= 0)
            {
                size[root] = -1 - sets++;
            }
        }

        // Then every pixel from its region's.
        byte[] ink = _ink;
        Parallel.For(0, Height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int i = y * width; i < (y + 1) * width; i++)
            {
                int label = labels[i];
                ink[i] = final[label];
                labels[i] = (int)(-1 - size[parent[label]]);
            }
        });

        return labels;
    }

    /// <summary>
    /// Labels the connected regions, ink 8-connected and paper 4-connected, numbered in the order a row-by-row scan meets their
    /// first pixel. Returns the number of regions.
    /// </summary>
    internal int Label(int[] labels, CancellationToken cancellationToken)
    {
        return Label(labels, false, out _, out _, cancellationToken);
    }

    /// <summary>
    /// Labels the regions as <see cref="Label(int[], CancellationToken)"/> does and gives each region's pixel count and value
    /// (1 for ink), summed from its runs.
    /// </summary>
    internal int Label(int[] labels, CancellationToken cancellationToken, out long[] size, out byte[] value)
    {
        return Label(labels, true, out size, out value, cancellationToken);
    }

    private int Label(int[] labels, bool measure, out long[] size, out byte[] value, CancellationToken cancellationToken)
    {
        // Each row's runs of one value are joined to the runs of the same value they touch in the row above: those that share a
        // column, and for ink also those that meet it at a corner. A region's number depends only on where the scan first meets
        // it, and each set's root is its earliest run, so any order of joining gives the same numbers.
        int width = Width, height = Height;
        byte[] ink = _ink;
        using RunTable runs = new();
        int previousFirst = 0, previousEnd = 0;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = y * width;
            int first = runs.Count;
            int above = previousFirst;
            for (int x = 0; x < width;)
            {
                byte c = ink[row + x];
                int stop = x + 1;
                while (stop < width && ink[row + stop] == c)
                {
                    stop++;
                }

                int run = runs.Add(x);
                int[] start = runs.Start;

                // The runs above that reach columns x − d to stop − 1 + d, d one for ink and none for paper.
                int d = c != 0 ? 1 : 0;
                while (above < previousEnd && (above + 1 < previousEnd ? start[above + 1] : width) <= x - d)
                {
                    above++;
                }

                for (int a = above; a < previousEnd && start[a] < stop + d; a++)
                {
                    if (ink[row - width + start[a]] == c)
                    {
                        runs.Join(a, run);
                    }
                }

                labels.AsSpan(row + x, stop - x).Fill(run);
                x = stop;
            }

            (previousFirst, previousEnd) = (first, runs.Count);
        }

        // Number the regions in the order their first runs come.
        int[] number = runs.Number(out int count);
        Parallel.For(0, height, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            for (int i = y * width; i < (y + 1) * width; i++)
            {
                labels[i] = number[labels[i]];
            }
        });

        size = measure ? new long[count] : [];
        value = measure ? new byte[count] : [];
        if (measure)
        {
            // A run's value is its first pixel's, and it ends where the next run starts or at the row's end: runs are numbered row
            // by row, so the row is found by walking with them.
            int y = 0, total = runs.Count;
            int[] starts = runs.Start;
            for (int r = 0, previous = -1; r < total; r++)
            {
                int start = starts[r];
                if (start <= previous)
                {
                    y++;
                }

                previous = start;
                int next = r + 1 < total ? starts[r + 1] : 0;
                size[number[r]] += (next > start ? next : width) - start;
                value[number[r]] = ink[(y * width) + start];
            }
        }

        System.Buffers.ArrayPool<int>.Shared.Return(number);
        return count;
    }

    private static int Find(int[] parent, int i)
    {
        int root = i;
        while (parent[root] != root)
        {
            root = parent[root];
        }

        while (parent[i] != root)
        {
            int next = parent[i];
            parent[i] = root;
            i = next;
        }

        return root;
    }

    private static void Touch(Dictionary<int, HashSet<int>> neighbours, int a, int b)
    {
        if (a == b)
        {
            return;
        }

        if (neighbours.TryGetValue(a, out HashSet<int>? na))
        {
            na.Add(b);
        }

        if (neighbours.TryGetValue(b, out HashSet<int>? nb))
        {
            nb.Add(a);
        }
    }
}
