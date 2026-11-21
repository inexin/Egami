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
        for (int y = 0; y < raster.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        }

        return map;
    }

    /// <summary>
    /// Merges every region of fewer than <paramref name="minArea"/> pixels into its neighbours, smallest first, until each
    /// region left is at least that large or has no neighbour. In black and white the neighbours of a region are all of the
    /// other color, so merging flips the region.
    /// </summary>
    internal void RemoveNoise(int minArea, CancellationToken cancellationToken)
    {
        if (minArea <= 1)
        {
            return;
        }

        int[] labels = new int[_ink.Length];
        int count = Label(labels, cancellationToken);
        long[] size = new long[count];
        for (int i = 0; i < labels.Length; i++)
        {
            size[labels[i]]++;
        }

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
            return;
        }

        // The regions each small region touches across a pixel edge. Regions that touch only at a corner are both paper (ink is
        // 8-connected), so they never merge with each other directly.
        Dictionary<int, HashSet<int>> neighbours = [];
        foreach (int r in small)
        {
            neighbours[r] = [];
        }

        for (int y = 0; y < Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int a = labels[row + x];
                if (x + 1 < Width)
                {
                    Touch(neighbours, a, labels[row + x + 1]);
                }

                if (y + 1 < Height)
                {
                    Touch(neighbours, a, labels[row + Width + x]);
                }
            }
        }

        byte[] color = new byte[count];
        int[] parent = new int[count];
        for (int r = 0; r < count; r++)
        {
            parent[r] = r;
        }

        for (int i = 0; i < labels.Length; i++)
        {
            color[labels[i]] = _ink[i];
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

        for (int i = 0; i < _ink.Length; i++)
        {
            _ink[i] = color[Find(parent, labels[i])];
        }
    }

    /// <summary>
    /// Labels the connected regions, ink 8-connected and paper 4-connected, numbered in the order a row-by-row scan meets their
    /// first pixel. Returns the number of regions.
    /// </summary>
    internal int Label(int[] labels, CancellationToken cancellationToken)
    {
        // Two passes with union-find over provisional labels.
        List<int> parent = [];
        for (int y = 0; y < Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                byte c = _ink[row + x];
                int label = -1;
                label = Join(parent, label, x > 0 && _ink[row + x - 1] == c ? labels[row + x - 1] : -1);
                if (y > 0)
                {
                    int up = row - Width + x;
                    label = Join(parent, label, _ink[up] == c ? labels[up] : -1);
                    if (c != 0)
                    {
                        label = Join(parent, label, x > 0 && _ink[up - 1] != 0 ? labels[up - 1] : -1);
                        label = Join(parent, label, x + 1 < Width && _ink[up + 1] != 0 ? labels[up + 1] : -1);
                    }
                }

                if (label < 0)
                {
                    label = parent.Count;
                    parent.Add(label);
                }

                labels[row + x] = label;
            }
        }

        // Number the roots in scan order: a root is never later than the first pixel that carries its set.
        int[] number = new int[parent.Count];
        Array.Fill(number, -1);
        int count = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            int root = FindList(parent, labels[i]);
            if (number[root] < 0)
            {
                number[root] = count++;
            }

            labels[i] = number[root];
        }

        return count;
    }

    private static int Join(List<int> parent, int label, int other)
    {
        if (other < 0)
        {
            return label;
        }

        if (label < 0)
        {
            return other;
        }

        int a = FindList(parent, label), b = FindList(parent, other);
        if (a != b)
        {
            // The smaller root wins, so roots stay the earliest label of their set.
            if (a < b)
            {
                parent[b] = a;
            }
            else
            {
                parent[a] = b;
            }
        }

        return Math.Min(a, b);
    }

    private static int FindList(List<int> parent, int i)
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
