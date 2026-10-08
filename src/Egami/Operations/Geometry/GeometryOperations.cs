using System.Buffers;

using Egami.Pixels;

namespace Egami.Operations.Geometry;

/// <summary>A built-in operation that may be unable to write into the plane it reads, even when the size stays the same.</summary>
internal interface INeedsNewPlane
{
    /// <summary>Returns whether the step needs a separate destination plane for an input of <paramref name="input"/>.</summary>
    bool NeedsNewPlane(Size input);
}

/// <summary>A built-in operation that keeps pooled buffers, such as weight tables, for the frames of one chain.</summary>
internal interface IHoldsBuffers
{
    /// <summary>Returns the buffers once every frame has run.</summary>
    void ReleaseBuffers();
}

/// <summary>Helpers shared by the built-in operations.</summary>
internal static class OperationRows
{
    /// <summary>Copies every source row to the destination unless they are the same plane.</summary>
    internal static void CopyIfSeparate<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (input.IsInPlace)
        {
            return;
        }

        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                input.Source.Row(y).CopyTo(input.Destination.Row(y));
            }
        });
    }
}

/// <summary>Keeps one rectangle of the image.</summary>
internal sealed class CropOperation(Rect rect) : IRasterOperation
{
    /// <summary>Gets the rectangle kept.</summary>
    internal Rect Rect => rect;

    public Size OutputSize(Size input)
    {
        if (!new Rect(0, 0, input.Width, input.Height).Contains(rect))
        {
            throw new ArgumentOutOfRangeException($"The crop rectangle {rect} leaves the {input.Width}×{input.Height} image.", innerException: null);
        }

        return rect.Size;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (input.IsInPlace)
        {
            // Only a crop to the whole image keeps the size, and it changes nothing.
            return;
        }

        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                input.Source.Row(rect.Y + y).Slice(rect.X, rect.Width).CopyTo(input.Destination.Row(y));
            }
        });
    }
}

/// <summary>Enlarges the canvas, placing the image at an anchor and filling the rest with one color.</summary>
internal sealed class PadOperation(int width, int height, Rgba8 color, Anchor anchor) : IRasterOperation
{
    public Size OutputSize(Size input)
    {
        if (width < input.Width || height < input.Height)
        {
            throw new ArgumentOutOfRangeException(
                $"Pad to {width}×{height} is smaller than the {input.Width}×{input.Height} image. Use Crop to make it smaller.", innerException: null);
        }

        return new Size(width, height);
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source;
        if (source.Width == width && source.Height == height)
        {
            OperationRows.CopyIfSeparate(input);
            return;
        }

        TPixel fill = TPixel.FromRgba8(color);
        Point offset = anchor.Offset(new Size(width, height), new Size(source.Width, source.Height));
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<TPixel> row = input.Destination.Row(y);
                int sy = y - offset.Y;
                if ((uint)sy >= (uint)source.Height)
                {
                    row.Fill(fill);
                    continue;
                }

                row[..offset.X].Fill(fill);
                source.Row(sy).CopyTo(row[offset.X..]);
                row[(offset.X + source.Width)..].Fill(fill);
            }
        });
    }
}

/// <summary>Mirrors the image horizontally, vertically or both (which is a 180° rotation).</summary>
internal sealed class FlipOperation(bool horizontal, bool vertical) : IRasterOperation
{
    public Size OutputSize(Size input)
    {
        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source;
        PixelPlane<TPixel> destination = input.Destination;
        int height = source.Height;
        long rowBytes = (long)source.Width * TPixel.Layout.BytesPerPixel;
        if (!input.IsInPlace)
        {
            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    Span<TPixel> row = destination.Row(y);
                    source.Row(vertical ? height - 1 - y : y).CopyTo(row);
                    if (horizontal)
                    {
                        GeometryKernels.ReverseRow(row);
                    }
                }
            });
            return;
        }

        if (!vertical)
        {
            if (horizontal)
            {
                input.ForEachBand((start, end) =>
                {
                    for (int y = start; y < end; y++)
                    {
                        GeometryKernels.ReverseRow(destination.Row(y));
                    }
                });
            }

            return;
        }

        // In place, a vertical flip swaps row y with row height - 1 - y; the middle row of an odd height stays put.
        RowRunner.Run((height + 1) / 2, 2 * rowBytes, input.Context.MaxParallelism, (start, end) =>
        {
            TPixel[] buffer = ArrayPool<TPixel>.Shared.Rent(source.Width);
            try
            {
                Span<TPixel> temp = buffer.AsSpan(0, source.Width);
                for (int y = start; y < end; y++)
                {
                    Span<TPixel> top = destination.Row(y);
                    int other = height - 1 - y;
                    if (other != y)
                    {
                        Span<TPixel> bottom = destination.Row(other);
                        top.CopyTo(temp);
                        bottom.CopyTo(top);
                        temp.CopyTo(bottom);
                        if (horizontal)
                        {
                            GeometryKernels.ReverseRow(bottom);
                        }
                    }

                    if (horizontal)
                    {
                        GeometryKernels.ReverseRow(top);
                    }
                }
            }
            finally
            {
                ArrayPool<TPixel>.Shared.Return(buffer);
            }
        }, input.CancellationToken);
    }
}

/// <summary>Rotates by 90° clockwise or counter-clockwise through cache-sized tiles.</summary>
internal sealed class RotateQuarterOperation(bool clockwise) : IRasterOperation, INeedsNewPlane
{
    private const int Tile = GeometryKernels.Tile;

    public bool NeedsNewPlane(Size input)
    {
        return true;
    }

    public Size OutputSize(Size input)
    {
        return new Size(input.Height, input.Width);
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source;
        PixelPlane<TPixel> destination = input.Destination;
        int width = destination.Width;
        int height = destination.Height;
        long tileRowBytes = (long)Tile * width * TPixel.Layout.BytesPerPixel;

        // Bands are counted in rows of tiles, so no tile straddles two bands.
        RowRunner.Run((height + Tile - 1) / Tile, tileRowBytes, input.Context.MaxParallelism, (start, end) =>
        {
            TPixel[] buffer = ArrayPool<TPixel>.Shared.Rent(2 * Tile * Tile);
            try
            {
                Span<TPixel> gathered = buffer.AsSpan(0, Tile * Tile);
                Span<TPixel> tile = buffer.AsSpan(Tile * Tile, Tile * Tile);
                for (int tileRow = start; tileRow < end; tileRow++)
                {
                    int dy0 = tileRow * Tile;
                    int rows = Math.Min(Tile, height - dy0);
                    for (int dx0 = 0; dx0 < width; dx0 += Tile)
                    {
                        int columns = Math.Min(Tile, width - dx0);

                        // Destination column dx0 + c comes from one source row; its pixels for rows dy0.. become gathered row c.
                        for (int c = 0; c < columns; c++)
                        {
                            Span<TPixel> line = gathered.Slice(c * Tile, rows);
                            if (clockwise)
                            {
                                // dest(x, y) = source(y, sourceHeight - 1 - x)
                                source.Row(width - 1 - (dx0 + c)).Slice(dy0, rows).CopyTo(line);
                            }
                            else
                            {
                                // dest(x, y) = source(sourceWidth - 1 - y, x)
                                source.Row(dx0 + c).Slice(height - dy0 - rows, rows).CopyTo(line);
                                GeometryKernels.ReverseRow(line);
                            }
                        }

                        GeometryKernels.Transpose<TPixel>(gathered, tile);
                        for (int r = 0; r < rows; r++)
                        {
                            tile.Slice(r * Tile, columns).CopyTo(destination.Row(dy0 + r).Slice(dx0, columns));
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<TPixel>.Shared.Return(buffer);
            }
        }, input.CancellationToken);
    }
}

/// <summary>Sets every pixel in a rectangle, or the whole image, to one color.</summary>
internal sealed class FillColorOperation(Rgba8 color, Rect? rect) : IRasterOperation
{
    public Size OutputSize(Size input)
    {
        if (rect is Rect r && !new Rect(0, 0, input.Width, input.Height).Contains(r))
        {
            throw new ArgumentOutOfRangeException($"The fill rectangle {r} leaves the {input.Width}×{input.Height} image.", innerException: null);
        }

        return input;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        TPixel fill = TPixel.FromRgba8(color);
        Rect area = rect ?? new Rect(0, 0, input.Destination.Width, input.Destination.Height);
        bool copy = !input.IsInPlace;
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<TPixel> row = input.Destination.Row(y);
                bool inside = y >= area.Y && y < area.Bottom;
                if (copy && !(inside && area.Width == row.Length))
                {
                    input.Source.Row(y).CopyTo(row);
                }

                if (inside)
                {
                    row.Slice(area.X, area.Width).Fill(fill);
                }
            }
        });
    }
}
