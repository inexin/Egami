using System.Buffers;
using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>
/// Pixel art mode, from Kopf and Lischinski, "Depixelizing Pixel Art" (SIGGRAPH 2011). A graph joins each pixel to its eight
/// neighbours of similar color (in YUV), crossing diagonals are resolved by the paper's curves, sparse pixels and islands votes,
/// the pixel cells are reshaped so that pixels joined along a diagonal share an edge, and the edges between colors become
/// quadratic B-splines through the cell corners that keep sharp corners sharp. <see cref="TraceOptions.Exact"/> keeps the pixel
/// squares instead. Shapes come out one per color, stacked or abutting as in color mode.
/// </summary>
internal static class PixelArtTracer
{
    /// <summary>The most distinct opaque colors an image may have.</summary>
    internal const int MaxColors = 256;

    // The similarity limits, after hqx: two colors are similar when they differ by at most this much in each of Y, U and V.
    private const float LumaLimit = 48 / 255f;
    private const float BlueLimit = 7 / 255f;
    private const float RedLimit = 6 / 255f;

    // An edge between colors at most this far apart in YUV is shading, not a contour.
    private const float ShadingLimit = 100 / 255f;

    // The islands vote, and the sparse pixels window (8 by 8 pixels around the 2 by 2 block).
    private const int IslandVote = 5;
    private const int Window = 8;

    // The shortest straight leg, in quarter pixels, either side of a turn that stays a sharp corner: one pixel edge.
    private const int MinLeg = 4;

    // The spline optimisation: the most sweeps over a piece's nodes, the tries per node and sweep, how far a try moves a node
    // (in pixels) and the least drop in energy a move must bring.
    private const int Sweeps = 8, Tries = 6;
    private const float Reach = 0.125f, MinGain = 1e-3f;

    // No node moves further than MaxMove from where it started, nor so far that a pixel center could come within Clearance of
    // the curve, in pixels.
    private const float Clearance = 0.15f, MaxMove = 0.5f;

    // The graph edges each pixel keeps, to its neighbours after it in scan order.
    private const byte East = 1, South = 2, SouthEast = 4, SouthWest = 8;

    // The points at a pixel corner, in quarter pixels: the corner itself, or where a diagonal crosses it the two ends of the edge
    // the joined pixels share, a quarter pixel along each axis towards the two pixels it leaves out.
    private const int Center = 0, UpRight = 1, DownLeft = 2, UpLeft = 3, DownRight = 4;
    private static readonly sbyte[] SlotX = [0, 1, -1, -1, 1];
    private static readonly sbyte[] SlotY = [0, -1, 1, -1, 1];

    /// <summary>Traces <paramref name="raster"/>; <paramref name="optimize"/> false skips the spline optimisation, for tests.</summary>
    internal static TraceResult Trace<TPixel>(
        Raster<TPixel> raster, TraceOptions options, CancellationToken cancellationToken, bool optimize = true)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int w = raster.Width, h = raster.Height, n = w * h;
        if (n == 0)
        {
            return new TraceResult([], w, h);
        }

        int[] color = ArrayPool<int>.Shared.Rent(n);
        byte[] edges = ArrayPool<byte>.Shared.Rent(n);
        int[] region = ArrayPool<int>.Shared.Rent(n);
        try
        {
            Rgba8[] palette = ReadColors(raster, color, cancellationToken);
            Array.Clear(edges, 0, n);
            if (!options.Exact)
            {
                Connect(color, edges, w, h, palette, cancellationToken);
                Resolve(edges, w, h, cancellationToken);
            }

            int regions = LabelRegions(color, edges, region, w, h);
            int[] regionColor = new int[regions];
            long[] area = new long[palette.Length];
            for (int i = 0; i < n; i++)
            {
                regionColor[region[i]] = color[i];
                area[color[i]]++;
            }

            Outline outline = new(palette, regionColor, !options.Exact);
            using (Graph graph = new(w, h))
            {
                BuildGraph(graph, color, edges, region, w, h, cancellationToken);
                outline.Extract(graph, cancellationToken);
            }

            outline.Optimize(optimize, cancellationToken);
            outline.Join();
            outline.Build();

            bool[] skip = new bool[palette.Length];
            for (int k = 0; k < skip.Length; k++)
            {
                Rgba8 c = palette[k];
                skip[k] = c.A == 0 || (options.IgnoreWhite && c.R >= ColorTracer.WhiteLevel && c.G >= ColorTracer.WhiteLevel && c.B >= ColorTracer.WhiteLevel);
            }

            List<TracedShape> shapes = options.Layering == TraceLayering.Abutting
                ? Abutting(outline, skip, cancellationToken)
                : Stacked(outline, area, skip, cancellationToken);
            return new TraceResult(shapes, w, h);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(color);
            ArrayPool<byte>.Shared.Return(edges);
            ArrayPool<int>.Shared.Return(region);
        }
    }

    /// <summary>
    /// The diagonal each 2 by 2 block of pixels keeps after the crossings are resolved, (width - 1) by (height - 1) in scan
    /// order: 0 for none, 1 for top left to bottom right, 2 for top right to bottom left. For tests.
    /// </summary>
    internal static byte[] Diagonals(Raster<Rgba8> raster)
    {
        int w = raster.Width, h = raster.Height, n = w * h;
        int[] color = new int[n];
        byte[] edges = new byte[n];
        Rgba8[] palette = ReadColors(raster, color, CancellationToken.None);
        Connect(color, edges, w, h, palette, CancellationToken.None);
        Resolve(edges, w, h, CancellationToken.None);
        byte[] diagonals = new byte[Math.Max(0, w - 1) * Math.Max(0, h - 1)];
        for (int y = 0; y + 1 < h; y++)
        {
            for (int x = 0; x + 1 < w; x++)
            {
                diagonals[(y * (w - 1)) + x] = Diagonal(edges, w, h, x + 1, y + 1);
            }
        }

        return diagonals;
    }

    /// <summary>
    /// Reads each pixel's color index into <paramref name="color"/> and returns the colors, in the order a scan meets them. Pixels
    /// under half alpha share one transparent color (all zero); the others keep their color as it is.
    /// </summary>
    private static Rgba8[] ReadColors<TPixel>(Raster<TPixel> raster, int[] color, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int w = raster.Width;
        Dictionary<uint, int> index = [];
        List<Rgba8> palette = [];
        int opaque = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<TPixel> row = raster.Row(y);
            uint lastKey = 0;
            int last = -1;
            for (int x = 0; x < row.Length; x++)
            {
                Rgba8 c = row[x].ToRgba8();
                uint key = c.A < 128 ? 0 : c.R | ((uint)c.G << 8) | ((uint)c.B << 16) | ((uint)c.A << 24);
                if (last < 0 || key != lastKey)
                {
                    if (!index.TryGetValue(key, out last))
                    {
                        if (key != 0 && ++opaque > MaxColors)
                        {
                            throw new ArgumentException(
                                $"Pixel art mode takes images of at most {MaxColors} colors; this one has more. Trace photos and painted art in color mode.",
                                nameof(raster));
                        }

                        last = palette.Count;
                        index.Add(key, last);
                        palette.Add(key == 0 ? default : c);
                    }

                    lastKey = key;
                }

                color[(y * w) + x] = last;
            }
        }

        return [.. palette];
    }

    /// <summary>The color in YUV (BT.601, each channel 0 to 1 wide), in which the similarity limits are set.</summary>
    private static Vector3 Yuv(Rgba8 c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        return new Vector3(
            (0.299f * r) + (0.587f * g) + (0.114f * b),
            (-0.168736f * r) - (0.331264f * g) + (0.5f * b),
            (0.5f * r) - (0.418688f * g) - (0.081312f * b));
    }

    /// <summary>Whether two colors are similar: both transparent, or both opaque and within the limits in Y, U and V.</summary>
    private static bool[] Similarity(Rgba8[] palette)
    {
        int k = palette.Length;
        Vector3[] yuv = new Vector3[k];
        for (int i = 0; i < k; i++)
        {
            yuv[i] = Yuv(palette[i]);
        }

        bool[] similar = new bool[k * k];
        for (int i = 0; i < k; i++)
        {
            for (int j = 0; j < k; j++)
            {
                bool ti = palette[i].A == 0, tj = palette[j].A == 0;
                Vector3 d = Vector3.Abs(yuv[i] - yuv[j]);
                similar[(i * k) + j] = ti || tj ? ti && tj : d.X <= LumaLimit && d.Y <= BlueLimit && d.Z <= RedLimit;
            }
        }

        return similar;
    }

    /// <summary>The similarity graph: each pixel joined to each of its eight neighbours of similar color.</summary>
    private static void Connect(int[] color, byte[] edges, int w, int h, Rgba8[] palette, CancellationToken cancellationToken)
    {
        bool[] similar = Similarity(palette);
        int k = palette.Length;
        Parallel.For(0, h, new ParallelOptions { CancellationToken = cancellationToken }, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x, c = color[i] * k;
                byte flags = 0;
                if (x + 1 < w && similar[c + color[i + 1]])
                {
                    flags |= East;
                }

                if (y + 1 < h)
                {
                    int below = i + w;
                    if (similar[c + color[below]])
                    {
                        flags |= South;
                    }

                    if (x + 1 < w && similar[c + color[below + 1]])
                    {
                        flags |= SouthEast;
                    }

                    if (x > 0 && similar[c + color[below - 1]])
                    {
                        flags |= SouthWest;
                    }
                }

                edges[i] = flags;
            }
        });
    }

    /// <summary>
    /// Leaves at most one diagonal in each 2 by 2 block. A block whose four pixels are all joined is shading: both diagonals go.
    /// Otherwise each diagonal gets the paper's votes, taken on the graph as it stands once the shading blocks are cleared: the
    /// diagonal on the longer curve gets the difference in length, the one joining the smaller component in an 8 by 8 window
    /// around the block the difference in size, and one whose end has no other edge 5. The diagonal with more votes stays; on a
    /// tie both go.
    /// </summary>
    private static void Resolve(byte[] edges, int w, int h, CancellationToken cancellationToken)
    {
        int n = w * h;
        bool crossings = false;
        for (int y = 0; y + 1 < h; y++)
        {
            int row = y * w;
            for (int x = 0; x + 1 < w; x++)
            {
                int i = row + x;
                if ((edges[i] & SouthEast) != 0 && (edges[i + 1] & SouthWest) != 0)
                {
                    if ((edges[i] & (East | South)) == (East | South) && (edges[i + 1] & South) != 0 && (edges[i + w] & East) != 0)
                    {
                        edges[i] &= unchecked((byte)~SouthEast);
                        edges[i + 1] &= unchecked((byte)~SouthWest);
                    }
                    else
                    {
                        crossings = true;
                    }
                }
            }
        }

        if (!crossings)
        {
            return;
        }

        byte[] valence = ArrayPool<byte>.Shared.Rent(n);
        int[] curve = ArrayPool<int>.Shared.Rent(n);
        int[] nodes = ArrayPool<int>.Shared.Rent(n);
        int[] inner = ArrayPool<int>.Shared.Rent(n);
        byte[] verdict = ArrayPool<byte>.Shared.Rent(n);
        try
        {
            Span<int> near = stackalloc int[8];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    valence[i] = (byte)Neighbours(edges, w, x, y, near);
                }
            }

            Curves(edges, valence, curve, nodes, inner, w, h);
            Array.Clear(verdict, 0, n);
            Parallel.For(0, h - 1, new ParallelOptions { CancellationToken = cancellationToken }, y =>
            {
                Span<int> stack = stackalloc int[Window * Window];
                Span<bool> seen = stackalloc bool[Window * Window];
                Span<int> around = stackalloc int[8];
                int row = y * w;
                for (int x = 0; x + 1 < w; x++)
                {
                    int i = row + x;
                    if ((edges[i] & SouthEast) == 0 || (edges[i + 1] & SouthWest) == 0)
                    {
                        continue;
                    }

                    int a1 = i, b1 = i + w + 1, a2 = i + 1, b2 = i + w;
                    int vote1 = 0, vote2 = 0;
                    int length1 = CurveLength(valence, curve, nodes, inner, a1, b1), length2 = CurveLength(valence, curve, nodes, inner, a2, b2);
                    if (length1 > length2)
                    {
                        vote1 += length1 - length2;
                    }
                    else
                    {
                        vote2 += length2 - length1;
                    }

                    int size1 = Component(edges, w, x - 3, y - 3, a1, stack, seen, around);
                    int size2 = Component(edges, w, x - 3, y - 3, a2, stack, seen, around);
                    if (size1 < size2)
                    {
                        vote1 += size2 - size1;
                    }
                    else
                    {
                        vote2 += size1 - size2;
                    }

                    if (valence[a1] == 1 || valence[b1] == 1)
                    {
                        vote1 += IslandVote;
                    }

                    if (valence[a2] == 1 || valence[b2] == 1)
                    {
                        vote2 += IslandVote;
                    }

                    verdict[i] = (byte)(vote1 > vote2 ? 1 : vote2 > vote1 ? 2 : 3);
                }
            });

            for (int y = 0; y + 1 < h; y++)
            {
                int row = y * w;
                for (int x = 0; x + 1 < w; x++)
                {
                    int i = row + x;
                    byte v = verdict[i];
                    if ((v & 2) != 0)
                    {
                        edges[i] &= unchecked((byte)~SouthEast);
                    }

                    if ((v & 1) != 0)
                    {
                        edges[i + 1] &= unchecked((byte)~SouthWest);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(valence);
            ArrayPool<int>.Shared.Return(curve);
            ArrayPool<int>.Shared.Return(nodes);
            ArrayPool<int>.Shared.Return(inner);
            ArrayPool<byte>.Shared.Return(verdict);
        }
    }

    /// <summary>Writes the pixels joined to pixel (x, y) into <paramref name="into"/> and returns how many.</summary>
    private static int Neighbours(byte[] edges, int w, int x, int y, Span<int> into)
    {
        int i = (y * w) + x, count = 0;
        byte flags = edges[i];
        if ((flags & East) != 0)
        {
            into[count++] = i + 1;
        }

        if ((flags & South) != 0)
        {
            into[count++] = i + w;
        }

        if ((flags & SouthEast) != 0)
        {
            into[count++] = i + w + 1;
        }

        if ((flags & SouthWest) != 0)
        {
            into[count++] = i + w - 1;
        }

        if (x > 0 && (edges[i - 1] & East) != 0)
        {
            into[count++] = i - 1;
        }

        if (y > 0)
        {
            int up = i - w;
            if ((edges[up] & South) != 0)
            {
                into[count++] = up;
            }

            if (x > 0 && (edges[up - 1] & SouthEast) != 0)
            {
                into[count++] = up - 1;
            }

            if (x + 1 < w && (edges[up + 1] & SouthWest) != 0)
            {
                into[count++] = up + 1;
            }
        }

        return count;
    }

    /// <summary>
    /// The curves of the graph: runs of pixels with exactly two edges. Joins each such pixel to its neighbours of two edges, then
    /// counts each run's pixels and the edges inside it, so that a curve's length (in edges) is one more than its pixels, or as
    /// many as its pixels when it closes on itself.
    /// </summary>
    private static void Curves(byte[] edges, byte[] valence, int[] parent, int[] nodes, int[] inner, int w, int h)
    {
        int n = w * h;
        for (int i = 0; i < n; i++)
        {
            parent[i] = i;
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                if (valence[i] != 2)
                {
                    continue;
                }

                byte flags = edges[i];
                if ((flags & East) != 0 && valence[i + 1] == 2)
                {
                    Join(parent, i, i + 1);
                }

                if ((flags & South) != 0 && valence[i + w] == 2)
                {
                    Join(parent, i, i + w);
                }

                if ((flags & SouthEast) != 0 && valence[i + w + 1] == 2)
                {
                    Join(parent, i, i + w + 1);
                }

                if ((flags & SouthWest) != 0 && valence[i + w - 1] == 2)
                {
                    Join(parent, i, i + w - 1);
                }
            }
        }

        Array.Clear(nodes, 0, n);
        Array.Clear(inner, 0, n);
        for (int i = 0; i < n; i++)
        {
            // Every parent precedes its child, so one pass in order points each pixel at its root.
            parent[i] = parent[parent[i]];
            if (valence[i] != 2)
            {
                continue;
            }

            int root = parent[i];
            nodes[root]++;
            byte flags = edges[i];
            int x = i % w;
            inner[root] += ((flags & East) != 0 && valence[i + 1] == 2 ? 1 : 0)
                + ((flags & South) != 0 && valence[i + w] == 2 ? 1 : 0)
                + ((flags & SouthEast) != 0 && valence[i + w + 1] == 2 ? 1 : 0)
                + ((flags & SouthWest) != 0 && x > 0 && valence[i + w - 1] == 2 ? 1 : 0);
        }
    }

    /// <summary>The length in edges of the curve through the edge from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static int CurveLength(byte[] valence, int[] curve, int[] nodes, int[] inner, int a, int b)
    {
        if (valence[a] != 2 && valence[b] != 2)
        {
            return 1;
        }

        int root = curve[valence[a] == 2 ? a : b];
        return inner[root] == nodes[root] ? nodes[root] : nodes[root] + 1;
    }

    /// <summary>The size of the component holding <paramref name="start"/>, inside the 8 by 8 window from (x0, y0).</summary>
    private static int Component(byte[] edges, int w, int x0, int y0, int start, Span<int> stack, Span<bool> seen, Span<int> around)
    {
        seen.Clear();
        int top = 0, size = 0;
        stack[top++] = start;
        seen[(((start / w) - y0) * Window) + (start % w) - x0] = true;
        while (top > 0)
        {
            int p = stack[--top];
            size++;
            int count = Neighbours(edges, w, p % w, p / w, around);
            for (int k = 0; k < count; k++)
            {
                int q = around[k];
                int qx = (q % w) - x0, qy = (q / w) - y0;
                if ((uint)qx < Window && (uint)qy < Window && !seen[(qy * Window) + qx])
                {
                    seen[(qy * Window) + qx] = true;
                    stack[top++] = q;
                }
            }
        }

        return size;
    }

    private static void Join(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb)
        {
            parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }

    /// <summary>
    /// Numbers the regions: pixels of one color whose cells share an edge, that is, side by side or joined by a kept diagonal.
    /// Writes each pixel's region into <paramref name="region"/> and returns the count, regions numbered in scan order.
    /// </summary>
    private static int LabelRegions(int[] color, byte[] edges, int[] region, int w, int h)
    {
        int n = w * h;
        for (int i = 0; i < n; i++)
        {
            region[i] = i;
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x, c = color[i];
                if (x + 1 < w && color[i + 1] == c)
                {
                    Join(region, i, i + 1);
                }

                if (y + 1 < h)
                {
                    if (color[i + w] == c)
                    {
                        Join(region, i, i + w);
                    }

                    if ((edges[i] & SouthEast) != 0 && color[i + w + 1] == c)
                    {
                        Join(region, i, i + w + 1);
                    }

                    if ((edges[i] & SouthWest) != 0 && color[i + w - 1] == c)
                    {
                        Join(region, i, i + w - 1);
                    }
                }
            }
        }

        // Every parent precedes its child: point each pixel at its root, then number the roots in order, writing each number as
        // its complement so that it is not taken for a parent.
        for (int i = 0; i < n; i++)
        {
            region[i] = region[region[i]];
        }

        int count = 0;
        for (int i = 0; i < n; i++)
        {
            int p = region[i];
            region[i] = p == i ? ~count++ : region[p];
        }

        for (int i = 0; i < n; i++)
        {
            region[i] = ~region[i];
        }

        return count;
    }

    /// <summary>The diagonal kept at pixel corner (cx, cy): 0 for none (always on the image's edge), 1 or 2 as in <see cref="Diagonals"/>.</summary>
    private static byte Diagonal(byte[] edges, int w, int h, int cx, int cy)
    {
        if (cx < 1 || cy < 1 || cx >= w || cy >= h)
        {
            return 0;
        }

        int i = ((cy - 1) * w) + cx - 1;
        return (byte)((edges[i] & SouthEast) != 0 ? 1 : (edges[i + 1] & SouthWest) != 0 ? 2 : 0);
    }

    /// <summary>
    /// The reshaped cells' edges between colors: the simplified Voronoi diagram of the paper, whose nodes lie on quarter pixels.
    /// Where a kept diagonal crosses a pixel corner, the two pixels it joins share an edge across the corner, from a quarter pixel
    /// towards one pixel it leaves out to a quarter pixel towards the other, and the pixel edges that met at the corner end at
    /// those points instead, cutting off the corners of the pixels left out. Each edge runs straight from one corner's point to
    /// the next, and records the regions on its left and right as it runs (-1 off the image).
    /// </summary>
    private static void BuildGraph(Graph graph, int[] color, byte[] edges, int[] region, int w, int h, CancellationToken cancellationToken)
    {
        int ColorOf(int pixel) => pixel < 0 ? -1 : color[pixel];
        int RegionOf(int pixel) => pixel < 0 ? -1 : region[pixel];

        // Where an edge leaving a corner to the right (or down) starts, and one arriving from the left (or from above) ends.
        int Leaving(int cx, int cy, bool down) => Diagonal(edges, w, h, cx, cy) switch
        {
            1 => down ? DownLeft : UpRight,
            2 => DownRight,
            _ => Center,
        };

        int Arriving(int cx, int cy, bool down) => Diagonal(edges, w, h, cx, cy) switch
        {
            1 => down ? UpRight : DownLeft,
            2 => UpLeft,
            _ => Center,
        };

        for (int y = 0; y <= h; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int above = y > 0 ? ((y - 1) * w) + x : -1, below = y < h ? (y * w) + x : -1;
                if (ColorOf(above) != ColorOf(below))
                {
                    int a = graph.At(x, y, Leaving(x, y, false)), b = graph.At(x + 1, y, Arriving(x + 1, y, false));
                    graph.Add(a, b, RegionOf(above), RegionOf(below));
                }
            }
        }

        for (int y = 0; y < h; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x <= w; x++)
            {
                int left = x > 0 ? (y * w) + x - 1 : -1, right = x < w ? (y * w) + x : -1;
                if (ColorOf(left) != ColorOf(right))
                {
                    // Running down, the pixel to the right of the edge is on its left.
                    int a = graph.At(x, y, Leaving(x, y, true)), b = graph.At(x, y + 1, Arriving(x, y + 1, true));
                    graph.Add(a, b, RegionOf(right), RegionOf(left));
                }
            }
        }

        for (int cy = 1; cy < h; cy++)
        {
            for (int cx = 1; cx < w; cx++)
            {
                int tl = ((cy - 1) * w) + cx - 1, tr = tl + 1, bl = tl + w, br = bl + 1;
                switch (Diagonal(edges, w, h, cx, cy))
                {
                    case 1 when color[tl] != color[br]:
                        graph.Add(graph.At(cx, cy, UpRight), graph.At(cx, cy, DownLeft), region[br], region[tl]);
                        break;
                    case 2 when color[tr] != color[bl]:
                        graph.Add(graph.At(cx, cy, UpLeft), graph.At(cx, cy, DownRight), region[tr], region[bl]);
                        break;
                }
            }
        }
    }

    /// <summary>Stacked layers as in color mode: largest area first, each covering the connected sets of its color and the colors above it that hold its color.</summary>
    private static List<TracedShape> Stacked(Outline outline, long[] area, bool[] skip, CancellationToken cancellationToken)
    {
        int colors = area.Length, count = outline.RegionColor.Length;
        int[] order = [.. Enumerable.Range(0, colors).Where(k => !skip[k]).OrderByDescending(k => area[k]).ThenBy(k => k)];
        int[] rank = new int[colors];
        Array.Fill(rank, -1);
        for (int i = 0; i < order.Length; i++)
        {
            rank[order[i]] = i;
        }

        long[] adjacent = outline.Adjacent();
        TracedShape?[] layers = new TracedShape?[order.Length];
        Parallel.For(0, order.Length, new ParallelOptions { CancellationToken = cancellationToken }, layer =>
        {
            int label = order[layer];
            int[] parent = new int[count];
            for (int r = 0; r < count; r++)
            {
                parent[r] = r;
            }

            foreach (long pair in adjacent)
            {
                int a = (int)(pair >> 32), b = (int)(pair & 0xFFFFFFFF);
                if (rank[outline.RegionColor[a]] >= layer && rank[outline.RegionColor[b]] >= layer)
                {
                    Join(parent, a, b);
                }
            }

            bool[] holds = new bool[count];
            for (int r = 0; r < count; r++)
            {
                if (outline.RegionColor[r] == label)
                {
                    holds[Find(parent, r)] = true;
                }
            }

            bool[] inside = new bool[count];
            for (int r = 0; r < count; r++)
            {
                inside[r] = rank[outline.RegionColor[r]] >= layer && holds[Find(parent, r)];
            }

            Path path = outline.Contours(inside);
            layers[layer] = path.Points.Length > 0 ? new TracedShape(path, outline.Palette[label]) : null;
        });

        return [.. layers.OfType<TracedShape>()];
    }

    /// <summary>Abutting shapes, one per color in the order a scan meets them, each its own cells only; neighbours share each edge.</summary>
    private static List<TracedShape> Abutting(Outline outline, bool[] skip, CancellationToken cancellationToken)
    {
        int colors = skip.Length, count = outline.RegionColor.Length;
        TracedShape?[] shapes = new TracedShape?[colors];
        Parallel.For(0, colors, new ParallelOptions { CancellationToken = cancellationToken }, k =>
        {
            if (skip[k])
            {
                return;
            }

            bool[] inside = new bool[count];
            for (int r = 0; r < count; r++)
            {
                inside[r] = outline.RegionColor[r] == k;
            }

            Path path = outline.Contours(inside);
            shapes[k] = path.Points.Length > 0 ? new TracedShape(path, outline.Palette[k]) : null;
        });

        return [.. shapes.OfType<TracedShape>()];
    }

    private static void Grow<T>(ref T[] array, int needed)
    {
        if (needed <= array.Length)
        {
            return;
        }

        T[] bigger = ArrayPool<T>.Shared.Rent(Math.Max(needed, array.Length * 2));
        Array.Copy(array, bigger, array.Length);
        ArrayPool<T>.Shared.Return(array);
        array = bigger;
    }

    /// <summary>A point in quarter pixels.</summary>
    private readonly record struct Pt(int X, int Y)
    {
        public long Key => ((long)Y << 32) | (uint)X;

        public Vector2 ToVector() => new(X / 4f, Y / 4f);
    }

    /// <summary>A point where cell edges meet, with the edges (up to four) that meet there.</summary>
    private struct Vertex
    {
        public int X, Y, Degree, S0, S1, S2, S3;

        public readonly int Segment(int k) => k switch { 0 => S0, 1 => S1, 2 => S2, _ => S3 };
    }

    /// <summary>A straight cell edge between colors, from vertex A to vertex B, with the regions on its left and right.</summary>
    private struct Segment
    {
        public int A, B, Left, Right;
    }

    /// <summary>The cell edges between colors and the points where they meet, in arrays rented from the shared pool.</summary>
    private sealed class Graph : IDisposable
    {
        private readonly int _w;
        private readonly int[] _base;
        private readonly int _corners;
        internal Vertex[] Vertices = ArrayPool<Vertex>.Shared.Rent(1024);
        internal Segment[] Segments = ArrayPool<Segment>.Shared.Rent(1024);
        internal int VertexCount;
        internal int SegmentCount;

        internal Graph(int w, int h)
        {
            _w = w;
            _corners = (w + 1) * (h + 1);
            _base = ArrayPool<int>.Shared.Rent(_corners);
            Array.Fill(_base, -1, 0, _corners);
        }

        /// <summary>
        /// The vertex at <paramref name="slot"/> of pixel corner (cx, cy). A corner without a kept diagonal has the corner itself
        /// only; one with a diagonal has the two ends of the edge across it (slots 1 and 2, or 3 and 4).
        /// </summary>
        internal int At(int cx, int cy, int slot)
        {
            int c = (cy * (_w + 1)) + cx;
            int first = _base[c];
            if (first < 0)
            {
                int slots = slot == Center ? 1 : 2;
                Grow(ref Vertices, VertexCount + slots);
                first = _base[c] = VertexCount;
                for (int s = 0; s < slots; s++)
                {
                    int at = slot == Center ? Center : (slot is UpRight or DownLeft ? UpRight : UpLeft) + s;
                    Vertices[first + s] = new Vertex { X = (4 * cx) + SlotX[at], Y = (4 * cy) + SlotY[at] };
                }

                VertexCount += slots;
            }

            return slot is Center or UpRight or UpLeft ? first : first + 1;
        }

        internal void Add(int a, int b, int left, int right)
        {
            Grow(ref Segments, SegmentCount + 1);
            int s = SegmentCount++;
            Segments[s] = new Segment { A = a, B = b, Left = left, Right = right };
            Attach(a, s);
            Attach(b, s);
        }

        private void Attach(int v, int s)
        {
            ref Vertex vertex = ref Vertices[v];
            switch (vertex.Degree++)
            {
                case 0: vertex.S0 = s; break;
                case 1: vertex.S1 = s; break;
                case 2: vertex.S2 = s; break;
                default: vertex.S3 = s; break;
            }
        }

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(_base);
            ArrayPool<Vertex>.Shared.Return(Vertices);
            ArrayPool<Segment>.Shared.Return(Segments);
        }
    }

    /// <summary>
    /// A run of the outline between two ends (junctions where three or four colors meet, or sharp corners), or a closed loop,
    /// with the regions on its left and right, and the curve drawn for it.
    /// </summary>
    private struct Piece
    {
        public int Offset, Count, Left, Right, StartVertex, EndVertex;
        public bool Closed;

        // Where the curve starts and ends, and the point beyond each end of the piece it joins smoothly there (NaN for none).
        public Vector2 StartPoint, EndPoint, StartJoin, EndJoin;

        public int OpOffset, OpCount;
        public Vector2 First;

        public readonly long StartKey(List<Pt> points) => points[Offset].Key;

        public readonly long EndKey(List<Pt> points) => points[Offset + Count - 1].Key;
    }

    /// <summary>The outline: the cell edges between colors cut into pieces, smoothed, and put together into shapes.</summary>
    private sealed class Outline(Rgba8[] palette, int[] regionColor, bool smooth)
    {
        private readonly List<Pt> _points = [];
        private readonly List<Piece> _pieces = [];
        private readonly List<Vector2> _controls = [];
        private readonly List<Vector2> _ends = [];
        private readonly List<Pt> _raw = [];
        private readonly List<Pt> _merged = [];
        private readonly Vector3[] _yuv = [.. palette.Select(Yuv)];

        // Where each point is drawn, in pixels, after the optimisation; rented from the shared pool until the curves are built.
        private Vector2[] _at = [];

        internal Rgba8[] Palette { get; } = palette;

        internal int[] RegionColor { get; } = regionColor;

        /// <summary>Follows the cell edges between colors from junction to junction (or round a loop), and cuts them into pieces.</summary>
        internal void Extract(Graph graph, CancellationToken cancellationToken)
        {
            bool[] used = ArrayPool<bool>.Shared.Rent(Math.Max(1, graph.SegmentCount));
            try
            {
                Array.Clear(used, 0, graph.SegmentCount);
                for (int v = 0; v < graph.VertexCount; v++)
                {
                    Vertex vertex = graph.Vertices[v];
                    if (vertex.Degree is 0 or 2)
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    for (int k = 0; k < vertex.Degree; k++)
                    {
                        int s = vertex.Segment(k);
                        if (!used[s])
                        {
                            Walk(graph, used, v, s);
                        }
                    }
                }

                for (int s = 0; s < graph.SegmentCount; s++)
                {
                    if (!used[s])
                    {
                        Walk(graph, used, graph.Segments[s].A, s);
                    }
                }
            }
            finally
            {
                ArrayPool<bool>.Shared.Return(used);
            }
        }

        private void Walk(Graph graph, bool[] used, int start, int s)
        {
            Segment first = graph.Segments[s];
            bool forward = first.A == start;
            int left = forward ? first.Left : first.Right, right = forward ? first.Right : first.Left;
            List<Pt> raw = _raw;
            raw.Clear();
            Vertex v = graph.Vertices[start];
            raw.Add(new Pt(v.X, v.Y));
            int current = start, end;
            while (true)
            {
                used[s] = true;
                Segment segment = graph.Segments[s];
                int next = segment.A == current ? segment.B : segment.A;
                Vertex nv = graph.Vertices[next];
                raw.Add(new Pt(nv.X, nv.Y));
                if (next == start || nv.Degree != 2)
                {
                    end = next;
                    break;
                }

                s = nv.S0 == s ? nv.S1 : nv.S0;
                current = next;
            }

            bool closed = graph.Vertices[start].Degree == 2;
            AddChain(closed, left, right, closed ? -1 : start, closed ? -1 : end);
        }

        /// <summary>Merges the straight runs of the chain in <see cref="_raw"/>, finds its corners and adds its pieces.</summary>
        private void AddChain(bool closed, int left, int right, int startVertex, int endVertex)
        {
            List<Pt> raw = _raw, q = _merged;
            q.Clear();
            if (closed)
            {
                raw.RemoveAt(raw.Count - 1);
                int m = raw.Count, turn = 0;
                for (int i = 0; i < m; i++)
                {
                    if (!Straight(raw[(i + m - 1) % m], raw[i], raw[(i + 1) % m]))
                    {
                        turn = i;
                        break;
                    }
                }

                // Start at a turn and go round to it again.
                _merged.Clear();
                for (int i = 0; i <= m; i++)
                {
                    _merged.Add(raw[(turn + i) % m]);
                }

                raw.Clear();
                raw.AddRange(_merged);
                q.Clear();
            }

            q.Add(raw[0]);
            for (int i = 1; i < raw.Count - 1; i++)
            {
                if (!Straight(q[^1], raw[i], raw[i + 1]))
                {
                    q.Add(raw[i]);
                }
            }

            q.Add(raw[^1]);
            if (!smooth)
            {
                if (closed)
                {
                    q.RemoveAt(q.Count - 1);
                }

                AddPiece(q, 0, q.Count, closed, left, right, startVertex, endVertex);
                return;
            }

            if (closed)
            {
                // Go round from a corner, if the loop has one; a loop without corners is one smooth piece.
                q.RemoveAt(q.Count - 1);
                int m = q.Count, corner = -1;
                bool bevel = false;
                for (int i = 0; i < m && corner < 0; i++)
                {
                    if (Bevel(q[(i + m - 1) % m], q[i], q[(i + 1) % m], q[(i + 2) % m], q[(i + 3) % m], out _))
                    {
                        corner = i;
                        bevel = true;
                    }
                    else if (RightAngle(q[(i + m - 1) % m], q[i], q[(i + 1) % m]))
                    {
                        corner = i;
                    }
                }

                if (corner < 0)
                {
                    AddPiece(q, 0, q.Count, true, left, right, -1, -1);
                    return;
                }

                raw.Clear();
                if (bevel)
                {
                    _ = Bevel(q[(corner + m - 1) % m], q[corner], q[(corner + 1) % m], q[(corner + 2) % m], q[(corner + 3) % m], out Pt at);
                    raw.Add(at);
                    for (int i = 3; i < m; i++)
                    {
                        raw.Add(q[(corner + i) % m]);
                    }

                    raw.Add(at);
                }
                else
                {
                    for (int i = 0; i <= m; i++)
                    {
                        raw.Add(q[(corner + i) % m]);
                    }
                }

                q.Clear();
                q.AddRange(raw);
                startVertex = endVertex = -1;
            }

            // Cut the open chain at its corners: a right angle between straight legs, or a straight leg, a cut corner and a
            // straight leg at right angles, which gets its corner back.
            raw.Clear();
            raw.Add(q[0]);
            int from = 0, startAt = startVertex;
            for (int i = 1; i < q.Count - 1;)
            {
                Pt previous = raw[^1];
                if (i + 3 < q.Count && Bevel(previous, q[i], q[i + 1], q[i + 2], q[i + 3], out Pt at))
                {
                    raw.Add(at);
                    AddPiece(raw, from, raw.Count - from, false, left, right, startAt, -1);
                    from = raw.Count - 1;
                    startAt = -1;
                    i += 3;
                }
                else if (RightAngle(previous, q[i], q[i + 1]))
                {
                    raw.Add(q[i]);
                    AddPiece(raw, from, raw.Count - from, false, left, right, startAt, -1);
                    from = raw.Count - 1;
                    startAt = -1;
                    i++;
                }
                else
                {
                    raw.Add(q[i]);
                    i++;
                }
            }

            raw.Add(q[^1]);
            AddPiece(raw, from, raw.Count - from, false, left, right, startAt, endVertex);
        }

        private void AddPiece(List<Pt> points, int offset, int count, bool closed, int left, int right, int startVertex, int endVertex)
        {
            // Where a smooth piece ends at a junction, the curves there bend within a pixel of it: a straight run longer than that
            // gets a point a pixel from the junction, so that the point the joined curves meet at stays near the junction.
            int at = _points.Count;
            bool split = smooth && !closed && count >= 2;
            bool both = count == 2 && startVertex >= 0 && endVertex >= 0;
            for (int i = 0; i < count; i++)
            {
                _points.Add(points[offset + i]);
                if (split && i == 0 && startVertex >= 0 && Near(points[offset], points[offset + 1], both) is { } first)
                {
                    _points.Add(first);
                }

                if (split && i == count - 2 && endVertex >= 0 && Near(points[offset + count - 1], points[offset + i], both) is { } last)
                {
                    _points.Add(last);
                }
            }

            count = _points.Count - at;
            _pieces.Add(new Piece
            {
                Offset = at,
                Count = count,
                Left = left,
                Right = right,
                StartVertex = startVertex,
                EndVertex = endVertex,
                Closed = closed,
                StartPoint = _points[at].ToVector(),
                EndPoint = _points[at + count - 1].ToVector(),
                StartJoin = new Vector2(float.NaN),
                EndJoin = new Vector2(float.NaN),
            });
        }

        /// <summary>
        /// Moves the points of the smooth pieces to take the last stairs out of their curves, as the paper's section 3.4 does: each
        /// point's energy is the curvature of the curve near it plus the fourth power of its distance from where it started (in
        /// quarter pixels), and sweeps over the points try small random moves and keep those that lower it. The curvature of a
        /// quadratic B-spline span, integrated along it, is the angle its control polygon turns through, so a point's share is
        /// the turns at it and at its two neighbours. Piece ends (junctions and sharp corners) and pieces along the image's edge
        /// stay where they are, no point moves so far that a pixel center comes nearer the curve than <see cref="Clearance"/>,
        /// and the moves come from a fixed seed, so the same image always gives the same curves.
        /// </summary>
        internal void Optimize(bool optimize, CancellationToken cancellationToken)
        {
            int n = _points.Count;
            _at = ArrayPool<Vector2>.Shared.Rent(Math.Max(1, n));
            for (int i = 0; i < n; i++)
            {
                _at[i] = _points[i].ToVector();
            }

            if (!smooth || !optimize)
            {
                return;
            }

            // The pieces meet only at their ends, which stay put, so each is smoothed on its own, until a sweep moves nothing.
            float[] limit = ArrayPool<float>.Shared.Rent(Math.Max(1, n));
            try
            {
                uint state = 0x9E3779B9;
                for (int p = 0; p < _pieces.Count; p++)
                {
                    if ((p & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    Piece piece = _pieces[p];
                    if (piece.Left < 0 || piece.Right < 0 || piece.Count < (piece.Closed ? 4 : 3) || !Limits(piece, limit))
                    {
                        continue;
                    }

                    int from = piece.Closed ? 0 : 1, to = piece.Closed ? piece.Count : piece.Count - 1;
                    bool moved = true;
                    for (int sweep = 0; sweep < Sweeps && moved; sweep++)
                    {
                        moved = false;
                        for (int i = from; i < to; i++)
                        {
                            moved |= Relax(piece.Offset, piece.Count, piece.Closed, i, limit[piece.Offset + i], ref state);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(limit);
            }
        }

        /// <summary>
        /// How far each point of a piece may move from where it started, into <paramref name="limit"/>; returns whether any may.
        /// Each point of a span is a weighted mean of its three control points with weights that sum to one, so while none of
        /// them moves further than d, no point of the span moves further than d either: a span whose nearest pixel center is r
        /// away lets its control points move r - <see cref="Clearance"/>, and no center can come nearer than that or change side.
        /// </summary>
        private bool Limits(Piece piece, float[] limit)
        {
            int m = piece.Count, offset = piece.Offset;
            bool closed = piece.Closed, any = false;
            for (int i = 0; i < m; i++)
            {
                limit[offset + i] = MaxMove;
            }

            for (int j = closed ? 0 : 1; j < (closed ? m : m - 1); j++)
            {
                Vector2 previous = Start(j - 1), control = Start(j), next = Start(j + 1);
                Vector2 a = !closed && j == 1 ? previous : (previous + control) / 2;
                Vector2 c = !closed && j == m - 2 ? next : (control + next) / 2;
                float slack = MathF.Max(0, Distance(a, control, c) - Clearance);
                for (int k = j - 1; k <= j + 1; k++)
                {
                    ref float l = ref limit[offset + (closed ? (k + m) % m : k)];
                    l = MathF.Min(l, slack);
                }
            }

            // The point next to a junction stays too: the curves joined there meet at a point found from it.
            if (piece.StartVertex >= 0)
            {
                limit[offset + 1] = 0;
            }

            if (piece.EndVertex >= 0)
            {
                limit[offset + m - 2] = 0;
            }

            for (int i = closed ? 0 : 1; i < (closed ? m : m - 1); i++)
            {
                any |= limit[offset + i] > 0;
            }

            return any;

            Vector2 Start(int k) => _at[offset + (closed ? (k + m) % m : k)];
        }

        /// <summary>
        /// Tries a few random moves of point <paramref name="i"/> of a piece, each within <paramref name="limit"/> of where it
        /// started, and keeps the best, if it lowers the energy; returns whether it moved.
        /// </summary>
        private bool Relax(int offset, int m, bool closed, int i, float limit, ref uint state)
        {
            // A point on a straight run (one put near a junction) stays, to keep the run straight.
            if (limit <= 0 || Straight(_points[offset + ((i + m - 1) % m)], _points[offset + i], _points[offset + ((i + 1) % m)]))
            {
                return false;
            }

            // The points up to two either side, and whether the turns at the neighbours count: an open piece's ends have none.
            Vector2 a = Neighbour(-2), b = Neighbour(-1), c = Neighbour(1), e = Neighbour(2);
            bool before = closed || i > 1, after = closed || i < m - 2;
            Vector2 start = _points[offset + i].ToVector(), current = _at[offset + i];
            float best = Energy(current, start, a, b, c, e, before, after), limit2 = limit * limit;
            Vector2 bestAt = current;
            for (int t = 0; t < Tries; t++)
            {
                // A point in the square of side 2 Reach about the current one, from a xorshift generator.
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                float dx = (((state & 0xFFFF) / 65535f) * 2) - 1, dy = (((state >> 16) / 65535f) * 2) - 1;
                Vector2 tryAt = current + (new Vector2(dx, dy) * Reach);
                if (Vector2.DistanceSquared(tryAt, start) > limit2)
                {
                    continue;
                }

                float energy = Energy(tryAt, start, a, b, c, e, before, after);
                if (energy < best - MinGain)
                {
                    (best, bestAt) = (energy, tryAt);
                }
            }

            _at[offset + i] = bestAt;
            return bestAt != current;

            Vector2 Neighbour(int k) => _at[offset + (closed ? (i + k + m + m) % m : Math.Clamp(i + k, 0, m - 1))];
        }

        /// <summary>
        /// The distance from the quadratic span a, b, c (sampled 8 times) to the nearest pixel center, at (x + 1/2, y + 1/2), up
        /// to <see cref="Clearance"/> + <see cref="MaxMove"/>.
        /// </summary>
        private static float Distance(Vector2 a, Vector2 b, Vector2 c)
        {
            const float Far = Clearance + MaxMove;
            Vector2 min = Vector2.Min(a, Vector2.Min(b, c)), max = Vector2.Max(a, Vector2.Max(b, c));
            int x0 = (int)MathF.Ceiling(min.X - Far - 0.5f), x1 = (int)MathF.Floor(max.X + Far - 0.5f);
            int y0 = (int)MathF.Ceiling(min.Y - Far - 0.5f), y1 = (int)MathF.Floor(max.Y + Far - 0.5f);
            Span<Vector2> curve = stackalloc Vector2[9];
            for (int q = 0; q <= 8; q++)
            {
                float t = q / 8f, u = 1 - t;
                curve[q] = (u * u * a) + (2 * u * t * b) + (t * t * c);
            }

            float best = Far * Far;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    Vector2 center = new(x + 0.5f, y + 0.5f);
                    for (int q = 1; q <= 8; q++)
                    {
                        best = MathF.Min(best, DistanceToSegment(center, curve[q - 1], curve[q]));
                    }
                }
            }

            return MathF.Sqrt(best);
        }

        /// <summary>The squared distance from <paramref name="p"/> to the segment a-b.</summary>
        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            float length = d.LengthSquared();
            float t = length > 0 ? Math.Clamp(Vector2.Dot(p - a, d) / length, 0, 1) : 0;
            return Vector2.DistanceSquared(p, a + (t * d));
        }

        /// <summary>
        /// The energy of a point put at <paramref name="p"/>, which started at <paramref name="start"/>, between
        /// <paramref name="a"/>, <paramref name="b"/> before it and <paramref name="c"/>, <paramref name="e"/> after it.
        /// </summary>
        private static float Energy(Vector2 p, Vector2 start, Vector2 a, Vector2 b, Vector2 c, Vector2 e, bool before, bool after)
        {
            // The distance in quarter pixels, the grid the points start on: a quarter pixel away weighs as much as a turn of a
            // radian.
            float d = Vector2.DistanceSquared(p, start) * 16;
            float energy = (d * d) + Turn(b, p, c);
            if (before)
            {
                energy += Turn(a, b, p);
            }

            if (after)
            {
                energy += Turn(p, c, e);
            }

            return energy;
        }

        /// <summary>The angle, from 0 to pi, the polygon a-b-c turns through at b.</summary>
        private static float Turn(Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 u = b - a, v = c - b;
            float y = MathF.Abs((u.X * v.Y) - (u.Y * v.X)), x = Vector2.Dot(u, v);
            if (y == 0)
            {
                return x < 0 ? MathF.PI : 0;
            }

            // atan2(y, x) for y > 0 from a polynomial for atan on [-1, 1], good to about 1e-5.
            bool steep = y > MathF.Abs(x);
            float z = steep ? x / y : y / x, z2 = z * z;
            float atan = z * (0.9998660f + (z2 * (-0.3302995f + (z2 * (0.1801410f + (z2 * (-0.0851330f + (z2 * 0.0208351f))))))));
            return steep ? (MathF.PI / 2) - atan : x > 0 ? atan : MathF.PI + atan;
        }

        /// <summary>
        /// Where three colors meet, two of the pieces join in one smooth curve and the third ends on it, as the paper does: the
        /// two contours when the third edge is shading between similar colors, else the two most nearly opposite; on the image's
        /// edge, the edge itself. Where four meet, the pieces meet at the point.
        /// </summary>
        internal void Join()
        {
            if (!smooth)
            {
                return;
            }

            // The piece ends at junctions, grouped by junction.
            List<(int Vertex, int End)> ends = [];
            for (int p = 0; p < _pieces.Count; p++)
            {
                Piece piece = _pieces[p];
                if (piece.StartVertex >= 0)
                {
                    ends.Add((piece.StartVertex, 2 * p));
                }

                if (piece.EndVertex >= 0)
                {
                    ends.Add((piece.EndVertex, (2 * p) + 1));
                }
            }

            ends.Sort();
            Span<int> group = stackalloc int[3];
            for (int i = 0; i < ends.Count;)
            {
                int j = i;
                while (j < ends.Count && ends[j].Vertex == ends[i].Vertex)
                {
                    j++;
                }

                if (j - i == 3)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        group[k] = ends[i + k].End;
                    }

                    JoinThree(group);
                }

                i = j;
            }
        }

        private void JoinThree(Span<int> end)
        {
            int a = -1, b = -1;
            int border = 0, shading = 0, contourMask = 0;
            for (int k = 0; k < 3; k++)
            {
                Piece piece = _pieces[end[k] >> 1];
                if (piece.Left < 0 || piece.Right < 0)
                {
                    border |= 1 << k;
                }
                else if (Vector3.Distance(_yuv[RegionColor[piece.Left]], _yuv[RegionColor[piece.Right]]) <= ShadingLimit
                    && Palette[RegionColor[piece.Left]].A != 0 && Palette[RegionColor[piece.Right]].A != 0)
                {
                    shading++;
                }
                else
                {
                    contourMask |= 1 << k;
                }
            }

            if (BitOperations.PopCount((uint)border) == 2)
            {
                (a, b) = Pair(border);
            }
            else if (border == 0 && shading == 1)
            {
                (a, b) = Pair(contourMask);
            }
            else
            {
                float best = float.MaxValue;
                for (int k = 0; k < 3; k++)
                {
                    for (int l = k + 1; l < 3; l++)
                    {
                        Vector2 u = Vector2.Normalize(Beyond(end[k]) - Junction(end[k])), v = Vector2.Normalize(Beyond(end[l]) - Junction(end[l]));
                        float cos = Vector2.Dot(u, v);
                        if (cos < best)
                        {
                            (best, a, b) = (cos, k, l);
                        }
                    }
                }
            }

            Vector2 j = Junction(end[a]), before = Beyond(end[a]), after = Beyond(end[b]);
            Vector2 s = (before + (6 * j) + after) / 8;
            for (int k = 0; k < 3; k++)
            {
                Piece piece = _pieces[end[k] >> 1];
                bool atEnd = (end[k] & 1) != 0;
                Vector2 join = k == a ? after : k == b ? before : new Vector2(float.NaN);
                if (atEnd)
                {
                    piece.EndPoint = s;
                    piece.EndJoin = join;
                }
                else
                {
                    piece.StartPoint = s;
                    piece.StartJoin = join;
                }

                _pieces[end[k] >> 1] = piece;
            }
        }

        private static (int A, int B) Pair(int mask) => mask switch { 3 => (0, 1), 5 => (0, 2), _ => (1, 2) };

        /// <summary>The junction a piece end is at.</summary>
        private Vector2 Junction(int end)
        {
            Piece piece = _pieces[end >> 1];
            return _at[(end & 1) != 0 ? piece.Offset + piece.Count - 1 : piece.Offset];
        }

        /// <summary>The point next to the junction along the piece.</summary>
        private Vector2 Beyond(int end)
        {
            Piece piece = _pieces[end >> 1];
            return _at[(end & 1) != 0 ? piece.Offset + piece.Count - 2 : piece.Offset + 1];
        }

        /// <summary>Draws each piece's curve: quadratic B-splines through its points in smooth mode, its polygon otherwise.</summary>
        internal void Build()
        {
            // About one curve or line per point (a few more at junctions): sized once rather than grown.
            int capacity = _points.Count + (smooth ? _pieces.Count : 0);
            _controls.EnsureCapacity(capacity);
            _ends.EnsureCapacity(capacity);
            List<Vector2> e = [];
            for (int p = 0; p < _pieces.Count; p++)
            {
                Piece piece = _pieces[p];
                piece.OpOffset = _ends.Count;
                if (!smooth)
                {
                    piece.First = _at[piece.Offset];
                    for (int i = 1; i < piece.Count; i++)
                    {
                        Line(_at[piece.Offset + i]);
                    }

                    if (piece.Closed)
                    {
                        Line(piece.First);
                    }
                }
                else if (piece.Closed)
                {
                    // A closed uniform quadratic B-spline: from the midpoint of each side to the next, bent towards the corner
                    // between them.
                    int m = piece.Count;
                    Vector2 Point(int i) => _at[piece.Offset + (i % m)];
                    piece.First = (Point(m - 1) + Point(0)) / 2;
                    for (int i = 0; i < m; i++)
                    {
                        Quad(Point(i), (Point(i) + Point(i + 1)) / 2);
                    }
                }
                else
                {
                    e.Clear();
                    bool startJoin = !float.IsNaN(piece.StartJoin.X), endJoin = !float.IsNaN(piece.EndJoin.X);
                    if (startJoin)
                    {
                        e.Add(piece.StartJoin);
                    }

                    for (int i = 0; i < piece.Count; i++)
                    {
                        e.Add(_at[piece.Offset + i]);
                    }

                    if (endJoin)
                    {
                        e.Add(piece.EndJoin);
                    }
                    else
                    {
                        e[^1] = piece.EndPoint;
                    }

                    if (!startJoin)
                    {
                        e[0] = piece.StartPoint;
                    }

                    piece.First = piece.StartPoint;
                    int k = e.Count - 1;
                    if (k == 1)
                    {
                        Line(e[1]);
                    }

                    for (int j = 1; j < k; j++)
                    {
                        Vector2 from = j == 1 && !startJoin ? e[0] : (e[j - 1] + e[j]) / 2;
                        Vector2 to = j == k - 1 && !endJoin ? e[k] : (e[j] + e[j + 1]) / 2;
                        if (j == 1 && startJoin)
                        {
                            // The second half of the span about the junction: from the point where the joined pieces meet.
                            Vector2 middle = (e[1] + e[2]) / 2;
                            Quad((e[1] + middle) / 2, middle);
                            if (to != middle)
                            {
                                Line(to);
                            }
                        }
                        else if (j == k - 1 && endJoin)
                        {
                            Vector2 middle = (e[j - 1] + e[j]) / 2;
                            if (from != middle)
                            {
                                Line(middle);
                            }

                            Quad((middle + e[j]) / 2, piece.EndPoint);
                        }
                        else
                        {
                            Quad(e[j], to);
                        }
                    }
                }

                piece.OpCount = _ends.Count - piece.OpOffset;
                _pieces[p] = piece;
            }

            ArrayPool<Vector2>.Shared.Return(_at);
            _at = [];
        }

        private void Line(Vector2 to)
        {
            _controls.Add(new Vector2(float.NaN));
            _ends.Add(to);
        }

        private void Quad(Vector2 control, Vector2 to)
        {
            // A control point on the line between the ends draws a line.
            _controls.Add(control);
            _ends.Add(to);
        }

        /// <summary>The pairs of regions that share an edge, each as (lesser, greater).</summary>
        internal long[] Adjacent()
        {
            // Sorted and deduplicated rather than hashed: a pair's hash would mix two nearby region numbers into few values.
            long[] pairs = new long[_pieces.Count];
            int count = 0;
            foreach (Piece piece in _pieces)
            {
                if (piece.Left >= 0 && piece.Right >= 0)
                {
                    int a = Math.Min(piece.Left, piece.Right), b = Math.Max(piece.Left, piece.Right);
                    pairs[count++] = ((long)a << 32) | (uint)b;
                }
            }

            Array.Sort(pairs, 0, count);
            int unique = 0;
            for (int i = 0; i < count; i++)
            {
                if (unique == 0 || pairs[i] != pairs[unique - 1])
                {
                    pairs[unique++] = pairs[i];
                }
            }

            return pairs[..unique];
        }

        /// <summary>
        /// The outline of the regions <paramref name="inside"/> marks: every piece with the set on one side only, turned so the
        /// set is on its right (outer edges clockwise, holes counterclockwise), joined end to end into closed contours.
        /// </summary>
        internal Path Contours(bool[] inside)
        {
            List<(long Start, int Piece)> open = [];
            PathBuilder builder = new();
            for (int p = 0; p < _pieces.Count; p++)
            {
                Piece piece = _pieces[p];
                bool l = piece.Left >= 0 && inside[piece.Left], r = piece.Right >= 0 && inside[piece.Right];
                if (l == r)
                {
                    continue;
                }

                if (piece.Closed)
                {
                    builder.MoveTo(l ? Last(piece) : piece.First);
                    Emit(builder, piece, l);
                    builder.Close();
                }
                else
                {
                    open.Add((l ? piece.EndKey(_points) : piece.StartKey(_points), l ? ~p : p));
                }
            }

            open.Sort();
            bool[] done = new bool[open.Count];
            for (int i = 0; i < open.Count; i++)
            {
                if (done[i])
                {
                    continue;
                }

                long home = open[i].Start;
                int at = i;
                builder.MoveTo(Start(open[at].Piece));
                while (true)
                {
                    done[at] = true;
                    int entry = open[at].Piece;
                    bool reversed = entry < 0;
                    Piece piece = _pieces[reversed ? ~entry : entry];
                    Emit(builder, piece, reversed);
                    long key = reversed ? piece.StartKey(_points) : piece.EndKey(_points);
                    if (key == home)
                    {
                        break;
                    }

                    at = Next(open, done, key);
                    if (at < 0)
                    {
                        break;
                    }
                }

                builder.Close();
            }

            return builder.ToPath();
        }

        private Vector2 Start(int entry) => entry < 0 ? Last(_pieces[~entry]) : _pieces[entry].First;

        private Vector2 Last(Piece piece) => piece.OpCount > 0 ? _ends[piece.OpOffset + piece.OpCount - 1] : piece.First;

        private static int Next(List<(long Start, int Piece)> open, bool[] done, long key)
        {
            int lo = 0, hi = open.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (open[mid].Start < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (int i = lo; i < open.Count && open[i].Start == key; i++)
            {
                if (!done[i])
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Adds a piece's curve, forwards or backwards, from wherever the builder stands at its start.</summary>
        private void Emit(PathBuilder builder, Piece piece, bool reversed)
        {
            int o = piece.OpOffset, count = piece.OpCount;
            if (!reversed)
            {
                for (int i = 0; i < count; i++)
                {
                    To(builder, _controls[o + i], _ends[o + i]);
                }
            }
            else
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    To(builder, _controls[o + i], i > 0 ? _ends[o + i - 1] : piece.First);
                }
            }
        }

        private static void To(PathBuilder builder, Vector2 control, Vector2 end)
        {
            if (float.IsNaN(control.X))
            {
                builder.LineTo(end);
                return;
            }

            Vector2 from = builder.CurrentPoint;
            Vector2 d = end - from, c = control - from;
            if (MathF.Abs((d.X * c.Y) - (d.Y * c.X)) <= 1e-6f * MathF.Max(1, d.LengthSquared()))
            {
                builder.LineTo(end);
            }
            else
            {
                builder.QuadTo(control, end);
            }
        }

        private static bool Straight(Pt a, Pt b, Pt c)
        {
            long dx1 = b.X - a.X, dy1 = b.Y - a.Y, dx2 = c.X - b.X, dy2 = c.Y - b.Y;
            return (dx1 * dy2) - (dy1 * dx2) == 0 && (dx1 * dx2) + (dy1 * dy2) > 0;
        }

        /// <summary>
        /// The point on the straight run from <paramref name="from"/> towards <paramref name="to"/> at most a pixel from it, on the
        /// quarter-pixel grid, or <see langword="null"/> when the run is no longer than that (or than two pixels when both its ends
        /// get one).
        /// </summary>
        private static Pt? Near(Pt from, Pt to, bool both)
        {
            int dx = to.X - from.X, dy = to.Y - from.Y;
            if ((dx * dx) + (dy * dy) <= (both ? 64 : 16))
            {
                return null;
            }

            int g = Math.Abs(dx), r = Math.Abs(dy);
            while (r != 0)
            {
                (g, r) = (r, g % r);
            }

            int sx = dx / g, sy = dy / g;
            int steps = Math.Max(1, (int)(4 / MathF.Sqrt((sx * sx) + (sy * sy))));
            return steps >= g ? null : new Pt(from.X + (steps * sx), from.Y + (steps * sy));
        }

        private static bool Axis(Pt a, Pt b) => a.X == b.X || a.Y == b.Y;

        private static int Length(Pt a, Pt b) => Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y);

        private static bool RightAngle(Pt a, Pt b, Pt c) =>
            Axis(a, b) && Axis(b, c) && ((b.X - a.X) * (c.X - b.X)) + ((b.Y - a.Y) * (c.Y - b.Y)) == 0
            && Length(a, b) >= MinLeg && Length(b, c) >= MinLeg;

        /// <summary>
        /// Whether a straight leg a-b, the two edges b-c-d that cut a pixel corner off (each three quarters of a pixel along one
        /// axis and a quarter along the other) and a straight leg d-e turn a right angle; if so, <paramref name="corner"/> is the
        /// corner the cut took off.
        /// </summary>
        private static bool Bevel(Pt a, Pt b, Pt c, Pt d, Pt e, out Pt corner)
        {
            corner = default;
            if (!Axis(a, b) || !Axis(d, e) || Length(a, b) < MinLeg || Length(d, e) < MinLeg || !Cut(b, c) || !Cut(c, d))
            {
                return false;
            }

            int cx = d.X - b.X, cy = d.Y - b.Y;
            int ax = Math.Sign(b.X - a.X), ay = Math.Sign(b.Y - a.Y), ex = Math.Sign(e.X - d.X), ey = Math.Sign(e.Y - d.Y);
            if (Math.Abs(cx) != 4 || Math.Abs(cy) != 4 || (ax * ex) + (ay * ey) != 0 || (ax * cx) + (ay * cy) <= 0 || (cx * ex) + (cy * ey) <= 0)
            {
                return false;
            }

            corner = a.Y == b.Y ? new Pt(d.X, b.Y) : new Pt(b.X, d.Y);
            return true;
        }

        private static bool Cut(Pt a, Pt b)
        {
            int dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y);
            return (dx == 3 && dy == 1) || (dx == 1 && dy == 3);
        }
    }
}
