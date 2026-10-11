using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Pixelate: the image is cut into cells of one size, their corners at the origin plus whole steps, and every pixel of a cell
/// takes the cell's premultiplied average, so transparent pixels add nothing to its color. Cells cut by the image's edges average
/// the pixels they hold. Rgba8 and Bgra8 sum whole numbers and round once, so their averages are exact; other types sum working
/// floats. Cells are in the whole image's coordinates, so inside a scope it runs on the full plane, reads whole cells and changes
/// only the area. Each cell reads only its own pixels, so it runs in place.
/// </summary>
internal sealed class PixelateOperation(int cellSize, Point origin) : IRasterOperation, Scopes.IAreaStep
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Process(input, input.Source, new Rect(0, 0, input.Destination.Width, input.Destination.Height));
    }

    public void RunArea<TPixel>(OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Process(input, input.Destination, area);
    }

    /// <summary>Returns where the cell holding <paramref name="i"/> starts: the origin plus a whole number of cells.</summary>
    private static int CellStart(int i, int origin, int size)
    {
        long offset = (long)i - origin;
        long cell = offset >= 0 ? offset / size : -((-offset + size - 1) / size);
        return (int)(origin + (cell * size));
    }

    private void Process<TPixel>(OperationInput<TPixel> input, PixelPlane<TPixel> source, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        area = area.Intersect(new Rect(0, 0, source.Width, source.Height));
        if (area.IsEmpty)
        {
            return;
        }

        PixelPlane<TPixel> destination = input.Destination;
        int width = source.Width, height = source.Height, size = cellSize;

        // The cells that cover the area, clipped to the image: their columns and rows.
        int left = Math.Max(0, CellStart(area.X, origin.X, size));
        int top = CellStart(area.Y, origin.Y, size);
        int firstRow = Math.Max(0, top);
        int columns = 0;
        for (int x = left; x < area.Right; x = CellStart(x, origin.X, size) + size)
        {
            columns++;
        }

        int rows = ((area.Bottom - 1 - top) / size) + 1;
        bool bytes = RowConverter<TPixel>.FourByte;
        RowConverter<TPixel> converter = new(null);
        Chunks.Run(rows, (long)area.Width * area.Height * 16, input.Context.MaxParallelism, (start, end) =>
        {
            float[] floats = ArrayPool<float>.Shared.Rent((width * 4) + (area.Width * 8));
            long[] sums = ArrayPool<long>.Shared.Rent(columns * 4);
            double[] totals = ArrayPool<double>.Shared.Rent(columns * 4);
            try
            {
                for (int r = start; r < end; r++)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    int y0 = Math.Max(firstRow, top + (r * size)), y1 = Math.Min(height, top + ((r + 1) * size));
                    if (bytes)
                    {
                        Bytes(source, destination, area, left, y0, y1, columns, sums);
                    }
                    else
                    {
                        Floats(source, destination, converter, area, left, y0, y1, columns, floats, totals);
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(floats);
                ArrayPool<long>.Shared.Return(sums);
                ArrayPool<double>.Shared.Return(totals);
            }
        }, input.CancellationToken);
    }

    /// <summary>Rgba8 and Bgra8: whole-number sums of alpha and of color times alpha, one rounding per channel.</summary>
    private void Bytes<TPixel>(PixelPlane<TPixel> source, PixelPlane<TPixel> destination, Rect area, int left, int y0, int y1, int columns, long[] sums)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = source.Width, size = cellSize;
        Span<long> s = sums.AsSpan(0, columns * 4);
        s.Clear();
        for (int y = y0; y < y1; y++)
        {
            ReadOnlySpan<byte> row = MemoryMarshal.AsBytes(source.Row(y));
            int x = left;
            for (int c = 0; c < columns; c++)
            {
                int x1 = Math.Min(width, CellStart(x, origin.X, size) + size);
                long c0 = 0, c1 = 0, c2 = 0, a = 0;
                for (int i = x * 4, e = x1 * 4; i < e; i += 4)
                {
                    int alpha = row[i + 3];
                    c0 += row[i] * alpha;
                    c1 += row[i + 1] * alpha;
                    c2 += row[i + 2] * alpha;
                    a += alpha;
                }

                s[c * 4] += c0;
                s[(c * 4) + 1] += c1;
                s[(c * 4) + 2] += c2;
                s[(c * 4) + 3] += a;
                x = x1;
            }
        }

        // Each cell's average: color = Σ(c·a) / Σa and alpha = Σa / n, both rounded to nearest.
        for (int y = Math.Max(y0, area.Y); y < Math.Min(y1, area.Bottom); y++)
        {
            Span<byte> row = MemoryMarshal.AsBytes(destination.Row(y));
            int x = left;
            for (int c = 0; c < columns; c++)
            {
                int x1 = Math.Min(width, CellStart(x, origin.X, size) + size);
                long n = (long)(x1 - x) * (y1 - y0), a = s[(c * 4) + 3];
                byte r = 0, g = 0, b = 0, alpha = (byte)(((2 * a) + n) / (2 * n));
                if (a > 0)
                {
                    r = (byte)(((2 * s[c * 4]) + a) / (2 * a));
                    g = (byte)(((2 * s[(c * 4) + 1]) + a) / (2 * a));
                    b = (byte)(((2 * s[(c * 4) + 2]) + a) / (2 * a));
                }

                for (int i = Math.Max(x, area.X), e = Math.Min(x1, area.Right); i < e; i++)
                {
                    row[i * 4] = r;
                    row[(i * 4) + 1] = g;
                    row[(i * 4) + 2] = b;
                    row[(i * 4) + 3] = alpha;
                }

                x = x1;
            }
        }
    }

    /// <summary>Other types: sums of premultiplied working floats in doubles, stored back through the row converter.</summary>
    private void Floats<TPixel>(
        PixelPlane<TPixel> source, PixelPlane<TPixel> destination, RowConverter<TPixel> converter, Rect area, int left, int y0, int y1, int columns, float[] floats, double[] totals)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = source.Width, size = cellSize;
        int right = Math.Min(width, CellStart(area.Right - 1, origin.X, size) + size);
        int loaded = right - left;
        Span<double> t = totals.AsSpan(0, columns * 4);
        t.Clear();
        bool opaque = true;
        Span<float> row = floats.AsSpan(0, loaded * 4);
        for (int y = y0; y < y1; y++)
        {
            opaque &= converter.Load(source.Row(y).Slice(left, loaded), row);
            int xc = left;
            for (int c = 0; c < columns; c++)
            {
                int x1 = Math.Min(width, CellStart(xc, origin.X, size) + size);
                double c0 = 0, c1 = 0, c2 = 0, a = 0;
                for (int i = (xc - left) * 4, e = (x1 - left) * 4; i < e; i += 4)
                {
                    c0 += row[i];
                    c1 += row[i + 1];
                    c2 += row[i + 2];
                    a += row[i + 3];
                }

                t[c * 4] += c0;
                t[(c * 4) + 1] += c1;
                t[(c * 4) + 2] += c2;
                t[(c * 4) + 3] += a;
                xc = x1;
            }
        }

        Span<float> output = floats.AsSpan(loaded * 4, area.Width * 4), scratch = floats.AsSpan((loaded + area.Width) * 4, area.Width * 4);
        int x = left;
        for (int c = 0; c < columns; c++)
        {
            int x1 = Math.Min(width, CellStart(x, origin.X, size) + size);
            double n = (double)(x1 - x) * (y1 - y0);
            for (int i = Math.Max(x, area.X), e = Math.Min(x1, area.Right); i < e; i++)
            {
                int o = (i - area.X) * 4;
                output[o] = (float)(t[c * 4] / n);
                output[o + 1] = (float)(t[(c * 4) + 1] / n);
                output[o + 2] = (float)(t[(c * 4) + 2] / n);
                output[o + 3] = (float)(t[(c * 4) + 3] / n);
            }

            x = x1;
        }

        for (int y = Math.Max(y0, area.Y); y < Math.Min(y1, area.Bottom); y++)
        {
            // Store uses its input as scratch, so each row stores a fresh copy.
            output.CopyTo(scratch);
            converter.Store(scratch, destination.Row(y).Slice(area.X, area.Width), opaque);
        }
    }
}
