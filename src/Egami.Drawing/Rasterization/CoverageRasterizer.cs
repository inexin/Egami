using System.Runtime.CompilerServices;

namespace Egami.Drawing.Rasterization;

/// <summary>Receives a filled outline row by row, as runs of pixels with their coverage, 0 to 255.</summary>
internal interface ISpanSink
{
    /// <summary>Blends pixels <paramref name="x"/> onwards on row <paramref name="y"/> through per-pixel coverage, which the sink may change.</summary>
    void Blend(int y, int x, Span<byte> coverage);

    /// <summary>Blends <paramref name="length"/> pixels from <paramref name="x"/> on row <paramref name="y"/> through one coverage value; <paramref name="scratch"/> holds at least <paramref name="length"/> bytes.</summary>
    void Fill(int y, int x, int length, byte coverage, Span<byte> scratch);

    /// <summary>
    /// Gets a value indicating whether a fully covered run is cheaper as <see cref="Fill"/> (a plain write) than as part of a
    /// <see cref="Blend"/> row; otherwise the rasterizer sends each row's runs as one blend, which costs one call.
    /// </summary>
    bool FillsDirectly { get; }
}

/// <summary>
/// The exact-area scan converter. Each edge, in 24.8 fixed point, adds to the cells (pixels) it crosses the signed height it
/// covers there and that height times the sum of its x positions at the cell's top and bottom: twice the area to the cell's left
/// inside the trapezoid it cuts (the accumulation method of font rasterizers; see Levien's font-rs write-up). Sweeping a row
/// from the left, the running sum of heights is the winding of the spans between cells, and a cell's own coverage is that
/// winding less its area term. Cells are kept per row in linked lists, so memory follows the outline's length, not its size.
/// <para>
/// Rows are cut into bands that can run on separate threads. Every row's cells come from the edges' exact fixed-point positions
/// at that row's top and bottom, never from a running sum, so a row's coverage is the same whatever the band it falls in, and
/// the result is bit-identical at any core count.
/// </para>
/// </summary>
internal sealed class CoverageRasterizer : IDisposable
{
    /// <summary>Destination rows above this many bytes are worth banding across cores, as for Egami's own operations.</summary>
    internal const long ParallelBytes = 256 * 1024;

    /// <summary>Constant runs at least this long go to <see cref="ISpanSink.Fill"/> instead of being written out byte by byte.</summary>
    private const int FillRun = 16;

    /// <summary>The guard box's half-size in pixels: 2²² keeps every 24.8 coordinate within an int.</summary>
    private const double Guard = 1 << 22;

    /// <summary>One pixel of winding: 256 subpixels of cover times 2·256 for the doubled area.</summary>
    private const int FullCover = 256 << 9;

    private readonly WorkspacePool _workspaces;
    private readonly int _maxParallelism;
    private int[] _lines = new int[256];
    private int _lineCount;

    internal CoverageRasterizer(IPixelMemorySource source, int maxParallelism)
    {
        _workspaces = new WorkspacePool(source);
        _maxParallelism = Math.Max(1, maxParallelism);
    }

    /// <summary>Gets or sets the number of row bands to use regardless of size, for tests; 0 lets the size decide.</summary>
    internal int ForcedBands { get; set; }

    /// <summary>Gets a value indicating whether antialiased fills take GDI+'s point samples (<see cref="SampledRasterizer"/>) instead of exact coverage.</summary>
    internal bool GdiPlus { get; init; }

    public void Dispose() => _workspaces.Dispose();

    /// <summary>Fills the edges inside <paramref name="clip"/>, calling the sink for each run with nonzero coverage.</summary>
    /// <param name="edges">The outline in device pixels.</param>
    /// <param name="clip">The device pixels that may be drawn.</param>
    /// <param name="rule">The fill rule.</param>
    /// <param name="antialias">Whether to compute exact coverage; otherwise each pixel center is sampled.</param>
    /// <param name="sink">Receives the runs.</param>
    /// <param name="bytesPerPixel">The destination's pixel size, which decides when to band.</param>
    internal void Fill(EdgeList edges, Rect clip, FillRule rule, bool antialias, ISpanSink sink, int bytesPerPixel)
    {
        if (edges.Count == 0 || clip.IsEmpty)
        {
            return;
        }

        Rect area = Area(edges, clip);
        if (area.IsEmpty)
        {
            return;
        }

        int bands = BandCount(area, bytesPerPixel);
        if (antialias && GdiPlus)
        {
            SampledRasterizer sampled = new(edges, area, rule == FillRule.EvenOdd);
            Run(bands, area.Height, (start, end) =>
            {
                Workspace workspace = _workspaces.Rent();
                try
                {
                    sampled.Band(start, end, workspace, sink);
                }
                finally
                {
                    _workspaces.Return(workspace);
                }
            });
        }
        else if (antialias)
        {
            PrepareLines(edges, area);
            if (_lineCount == 0)
            {
                return;
            }

            if (bands <= 1)
            {
                Band(area, 0, area.Height, rule == FillRule.EvenOdd, sink);
            }
            else
            {
                Run(bands, area.Height, (start, end) => Band(area, start, end, rule == FillRule.EvenOdd, sink));
            }
        }
        else
        {
            AliasedRasterizer aliased = new(edges, area, rule == FillRule.EvenOdd);
            Run(bands, area.Height, (start, end) =>
            {
                Workspace workspace = _workspaces.Rent();
                try
                {
                    aliased.Band(start, end, workspace, sink);
                }
                finally
                {
                    _workspaces.Return(workspace);
                }
            });
        }
    }

    private static Rect Area(EdgeList edges, Rect clip)
    {
        double left = Math.Max(clip.X, Math.Floor(edges.MinX)), top = Math.Max(clip.Y, Math.Floor(edges.MinY));
        double right = Math.Min(clip.Right, Math.Ceiling(edges.MaxX)), bottom = Math.Min(clip.Bottom, Math.Ceiling(edges.MaxY));
        return right > left && bottom > top ? new Rect((int)left, (int)top, (int)(right - left), (int)(bottom - top)) : default;
    }

    private int BandCount(Rect area, int bytesPerPixel)
    {
        if (ForcedBands > 0)
        {
            return Math.Min(ForcedBands, area.Height);
        }

        if (_maxParallelism == 1 || (long)area.Width * area.Height * bytesPerPixel <= ParallelBytes || area.Height < 32)
        {
            return 1;
        }

        return Math.Min(_maxParallelism * 4, area.Height / 16);
    }

    private void Run(int bands, int rows, Action<int, int> band)
    {
        if (bands <= 1)
        {
            band(0, rows);
            return;
        }

        Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = _maxParallelism }, b => band((int)((long)rows * b / bands), (int)((long)rows * (b + 1) / bands)));
    }

    /// <summary>
    /// Converts the edges to 24.8 fixed point in absolute device pixels. Only a guard box far outside any raster is clipped here, in
    /// double precision, so huge coordinates cannot overflow; parts left of it become vertical lines that keep their winding, and
    /// parts right of it are dropped. The clip is applied by the cell walk itself, which gives a clipped shape exactly the pixels of
    /// the unclipped one inside the clip. Edges wholly above, below or right of the area cannot reach it and are skipped.
    /// </summary>
    private void PrepareLines(EdgeList edges, Rect area)
    {
        _lineCount = 0;
        ReadOnlySpan<float> c = edges.Coordinates;
        double top = area.Y, bottom = area.Bottom, right = area.Right;
        for (int i = 0; i < c.Length; i += 4)
        {
            double x0 = c[i], y0 = c[i + 1], x1 = c[i + 2], y1 = c[i + 3];
            if ((y0 <= top && y1 <= top) || (y0 >= bottom && y1 >= bottom) || (x0 >= right && x1 >= right))
            {
                continue;
            }

            if (Math.Abs(x0) <= Guard && Math.Abs(x1) <= Guard && Math.Abs(y0) <= Guard && Math.Abs(y1) <= Guard)
            {
                AddFixed(x0, y0, x1, y1);
                continue;
            }

            // Cut to the guard's rows, then split where the edge crosses its sides; pieces left of it become vertical.
            double dxdy = (x1 - x0) / (y1 - y0);
            (x0, y0) = ClampRow(x0, y0, dxdy);
            (x1, y1) = ClampRow(x1, y1, dxdy);
            Span<double> cuts = [0, 0];
            Span<double> sides = [0, 0];
            int n = 0;
            foreach (double side in (ReadOnlySpan<double>)[-Guard, Guard])
            {
                if ((x0 < side && x1 > side) || (x0 > side && x1 < side))
                {
                    cuts[n] = y0 + ((side - x0) * (y1 - y0) / (x1 - x0));
                    sides[n++] = side;
                }
            }

            if (n == 2 && (cuts[0] > cuts[1]) == (y1 > y0))
            {
                (cuts[0], cuts[1]) = (cuts[1], cuts[0]);
                (sides[0], sides[1]) = (sides[1], sides[0]);
            }

            double px = x0, py = y0;
            for (int k = 0; k <= n; k++)
            {
                double qy = k < n ? cuts[k] : y1;
                double qx = k < n ? sides[k] : x1;
                double mid = (px + qx) / 2;
                if (mid < Guard)
                {
                    bool left = mid <= -Guard;
                    AddFixed(left ? -Guard : px, py, left ? -Guard : qx, qy);
                }

                px = qx;
                py = qy;
            }
        }

        static (double X, double Y) ClampRow(double x, double y, double dxdy)
        {
            double clamped = Math.Clamp(y, -Guard, Guard);
            return (x + ((clamped - y) * dxdy), clamped);
        }
    }

    private void AddFixed(double x0, double y0, double x1, double y1)
    {
        int fy0 = Fixed(y0), fy1 = Fixed(y1);
        if (fy0 == fy1)
        {
            return;
        }

        if ((_lineCount + 1) * 4 > _lines.Length)
        {
            Array.Resize(ref _lines, _lines.Length * 2);
        }

        Span<int> l = _lines.AsSpan(_lineCount * 4, 4);
        l[0] = Fixed(x0);
        l[1] = fy0;
        l[2] = Fixed(x1);
        l[3] = fy1;
        _lineCount++;
    }

    private static int Fixed(double v) => (int)Math.Round(v * 256);

    /// <summary>Accumulates and sweeps rows <paramref name="start"/> to <paramref name="end"/> of the area.</summary>
    private void Band(Rect area, int start, int end, bool evenOdd, ISpanSink sink)
    {
        Workspace workspace = _workspaces.Rent();
        try
        {
            CellAccumulator cells = new(workspace, area.Y + start, end - start, area.X, area.Right);
            ReadOnlySpan<int> lines = _lines.AsSpan(0, _lineCount * 4);
            int bandTop = (area.Y + start) << 8, bandBottom = (area.Y + end) << 8;
            for (int i = 0; i < lines.Length; i += 4)
            {
                int y0 = lines[i + 1], y1 = lines[i + 3];
                if (Math.Max(y0, y1) <= bandTop || Math.Min(y0, y1) >= bandBottom)
                {
                    continue;
                }

                cells.AddLine(lines[i], y0, lines[i + 2], y1);
            }

            cells.Flush();
            Sweep(ref cells, workspace, area, start, end, evenOdd, sink);
        }
        finally
        {
            _workspaces.Return(workspace);
        }
    }

    private static void Sweep(ref CellAccumulator cells, Workspace workspace, Rect area, int start, int end, bool evenOdd, ISpanSink sink)
    {
        int width = area.Width;
        Span<byte> coverage = workspace.Coverage.Get(width + FillRun);
        ReadOnlySpan<int> heads = cells.Heads;
        ReadOnlySpan<Cell> all = cells.All;
        for (int row = start; row < end; row++)
        {
            int head = heads[row - start];
            if (head < 0)
            {
                continue;
            }

            // Gather the row's cells and sort them by x; cells at the same x are merged as the sweep meets them.
            int count = 0;
            for (int c = head; c >= 0; c = all[c].Next)
            {
                count++;
            }

            Span<Cell> rowCells = workspace.RowCells.Get(count);
            int n = 0;
            for (int c = head; c >= 0; c = all[c].Next)
            {
                rowCells[n++] = all[c];
            }

            SortByX(rowCells);
            SweepRow(rowCells, width, evenOdd, area.Y + row, area.X, coverage, sink);
        }
    }

    /// <summary>Turns one row's sorted cells into runs: each cell's own coverage, and the constant winding between cells.</summary>
    private static void SweepRow(ReadOnlySpan<Cell> cells, int width, bool evenOdd, int y, int originX, Span<byte> coverage, ISpanSink sink)
    {
        RunWriter runs = new(coverage, y, originX, sink);
        int cover = 0;
        int i = 0;
        int next = 0;
        while (i < cells.Length)
        {
            int x = cells[i].X - originX;
            if (x >= width)
            {
                break;
            }

            // The span between the last cell and this one has the running winding.
            if (x > next)
            {
                runs.Constant(next, x - next, Alpha((long)cover << 9, evenOdd));
            }

            int area = 0;
            int cellCover = 0;
            while (i < cells.Length && cells[i].X - originX == x)
            {
                cellCover += cells[i].Cover;
                area += cells[i].Area;
                i++;
            }

            cover += cellCover;
            runs.Pixel(x, Alpha(((long)cover << 9) - area, evenOdd));
            next = x + 1;
        }

        // A winding left over at the row's end (an edge beyond the area's right side was dropped) runs to the end.
        if (cover != 0 && next < width)
        {
            runs.Constant(next, width - next, Alpha((long)cover << 9, evenOdd));
        }

        runs.Flush();
    }

    /// <summary>Converts a doubled area in 24.8 units to coverage: |winding| clamped to 1, or folded modulo 2, rounded to 1/255.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Alpha(long value, bool evenOdd)
    {
        if (value < 0)
        {
            value = -value;
        }

        if (evenOdd)
        {
            value &= (2L * FullCover) - 1;
            if (value > FullCover)
            {
                value = (2L * FullCover) - value;
            }
        }
        else if (value >= FullCover)
        {
            return 255;
        }

        return (byte)(((value * 255) + (FullCover / 2)) >> 17);
    }

    private static void SortByX(Span<Cell> cells)
    {
        if (cells.Length > 24)
        {
            cells.Sort(static (a, b) => a.X.CompareTo(b.X));
            return;
        }

        for (int i = 1; i < cells.Length; i++)
        {
            Cell c = cells[i];
            int j = i - 1;
            while (j >= 0 && cells[j].X > c.X)
            {
                cells[j + 1] = cells[j];
                j--;
            }

            cells[j + 1] = c;
        }
    }

    /// <summary>Collects one row's runs: consecutive pixels are written out and sent together, long constant runs are sent as fills.</summary>
    private ref struct RunWriter(Span<byte> buffer, int y, int originX, ISpanSink sink)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _start = -1;
        private int _length;

        internal void Pixel(int x, byte alpha)
        {
            if (alpha == 0)
            {
                Flush();
                return;
            }

            if (_start < 0)
            {
                _start = x;
            }

            _buffer[_length++] = alpha;
        }

        internal void Constant(int x, int length, byte alpha)
        {
            if (alpha == 0)
            {
                Flush();
                return;
            }

            if (length >= FillRun && (alpha != 255 || sink.FillsDirectly))
            {
                Flush();
                sink.Fill(y, originX + x, length, alpha, _buffer);
                return;
            }

            if (_start < 0)
            {
                _start = x;
            }

            _buffer.Slice(_length, length).Fill(alpha);
            _length += length;
        }

        internal void Flush()
        {
            if (_length > 0)
            {
                sink.Blend(y, originX + _start, _buffer[.._length]);
            }

            _start = -1;
            _length = 0;
        }
    }
}
