using System.Runtime.CompilerServices;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// Walks fixed-point edges through one band's rows and cells, adding each piece's cover (signed height) and area (height times
/// the sum of its x positions within the cell) to the cell it lies in. Consecutive pieces in the same cell merge before they are
/// stored. Coordinates are 24.8 fixed point in absolute device pixels. The walk clips to the area's columns: cells left of it fold
/// into its first column with no area of their own (only their winding reaches the pixels to their right), and cells right of it
/// are dropped. Where an edge crosses a column side is computed by one formula from the piece's ends, as the full walk would, so a
/// clipped shape has exactly the unclipped shape's pixels inside the clip.
/// </summary>
internal ref struct CellAccumulator
{
    private readonly Workspace _workspace;
    private readonly int _bandStart;
    private readonly int _rows;
    private readonly int _left;
    private readonly int _right;
    private Span<Cell> _cells;
    private Span<int> _heads;
    private int _count;
    private int _row;
    private int _x;
    private int _cover;
    private int _area;

    internal CellAccumulator(Workspace workspace, int bandStart, int rows, int left, int right)
    {
        _workspace = workspace;
        _bandStart = bandStart;
        _rows = rows;
        _left = left;
        _right = right;
        _heads = workspace.RowHeads.Get(rows);
        _heads.Fill(-1);
        _cells = workspace.Cells.All;
        _row = -1;
    }

    /// <summary>Gets each band row's first cell, or −1.</summary>
    internal readonly ReadOnlySpan<int> Heads => _heads;

    /// <summary>Gets every stored cell.</summary>
    internal readonly ReadOnlySpan<Cell> All => _cells[.._count];

    /// <summary>Adds an edge from (x0, y0) to (x1, y1); only the rows inside the band are walked.</summary>
    internal void AddLine(int x0, int y0, int x1, int y1)
    {
        int direction = 1;
        if (y0 > y1)
        {
            (x0, y0, x1, y1) = (x1, y1, x0, y0);
            direction = -1;
        }

        long dx = x1 - (long)x0, dy = y1 - (long)y0;
        int first = Math.Max(y0 >> 8, _bandStart);
        int last = Math.Min((y1 - 1) >> 8, _bandStart + _rows - 1);
        for (int row = first; row <= last; row++)
        {
            int top = row << 8;
            int ya = Math.Max(y0, top), yb = Math.Min(y1, top + 256);

            // Each row's ends come from the edge's own endpoints, so the result never depends on which rows were walked before.
            int xa = ya == y0 ? x0 : x0 + (int)RoundDivide(dx * (ya - y0), dy);
            int xb = yb == y1 ? x1 : x0 + (int)RoundDivide(dx * (yb - y0), dy);
            RowSegment(row - _bandStart, xa, ya - top, xb, yb - top, direction);
        }
    }

    /// <summary>Stores the cell being merged.</summary>
    internal void Flush()
    {
        if (_row < 0)
        {
            return;
        }

        if (_cover != 0 || _area != 0)
        {
            if (_count == _cells.Length)
            {
                _cells = _workspace.Cells.Get(_count + 1, _count);
                _cells = _workspace.Cells.All;
            }

            ref Cell cell = ref _cells[_count];
            cell.X = _x;
            cell.Cover = _cover;
            cell.Area = _area;
            cell.Next = _heads[_row];
            _heads[_row] = _count;
            _count++;
        }

        _row = -1;
    }

    /// <summary>Walks the piece of an edge inside one row, from (xa, fya) to (xb, fyb) with fya &lt; fyb within 0..256.</summary>
    private void RowSegment(int row, int xa, int fya, int xb, int fyb, int direction)
    {
        int ca = xa >> 8, cb = xb >> 8;
        if (ca == cb)
        {
            int h = fyb - fya;
            if (ca >= _right)
            {
                return;
            }

            if (ca < _left)
            {
                Add(row, _left, direction * h, 0);
                return;
            }

            Add(row, ca, direction * h, direction * h * ((xa - (ca << 8)) + (xb - (ca << 8))));
            return;
        }

        long ddx = xb - (long)xa, ddy = fyb - fya;
        int step = xb > xa ? 1 : -1;
        int x = xa, y = fya, c = ca;
        if (step > 0 && c < _left)
        {
            // Everything left of the area only winds: one cover for the whole stretch, up to where the edge enters.
            if (cb < _left)
            {
                Add(row, _left, direction * (fyb - fya), 0);
                return;
            }

            int boundary = _left << 8;
            int by = Math.Clamp(fya + (int)RoundDivide(ddy * (boundary - xa), ddx), y, fyb);
            Add(row, _left, direction * (by - y), 0);
            (x, y, c) = (boundary, by, _left);
        }
        else if (step < 0 && c >= _right)
        {
            if (cb >= _right)
            {
                return;
            }

            int boundary = _right << 8;
            int by = Math.Clamp(fya + (int)RoundDivide(ddy * (boundary - xa), ddx), y, fyb);
            (x, y, c) = (boundary, by, _right - 1);
        }

        while (c != cb)
        {
            if (step > 0 && c >= _right)
            {
                return;
            }

            if (step < 0 && c < _left)
            {
                Add(row, _left, direction * (fyb - y), 0);
                return;
            }

            int boundary = step > 0 ? (c + 1) << 8 : c << 8;
            int by = Math.Clamp(fya + (int)RoundDivide(ddy * (boundary - xa), ddx), y, fyb);
            int h = by - y;
            int local = c << 8;
            Add(row, c, direction * h, direction * h * ((x - local) + (boundary - local)));
            x = boundary;
            y = by;
            c += step;
        }

        int rest = fyb - y;
        if (cb >= _right)
        {
            return;
        }

        if (cb < _left)
        {
            Add(row, _left, direction * rest, 0);
            return;
        }

        int cellLeft = cb << 8;
        Add(row, cb, direction * rest, direction * rest * ((x - cellLeft) + (xb - cellLeft)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Add(int row, int x, int cover, int area)
    {
        if (row == _row && x == _x)
        {
            _cover += cover;
            _area += area;
            return;
        }

        Flush();
        _row = row;
        _x = x;
        _cover = cover;
        _area = area;
    }

    /// <summary>Divides, rounding to nearest (halves away from zero), for any signs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RoundDivide(long numerator, long denominator)
    {
        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        return numerator >= 0 ? (numerator + (denominator / 2)) / denominator : -((-numerator + (denominator / 2)) / denominator);
    }
}
