using System.Buffers;

using Egami.Metadata;
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

/// <summary>A built-in operation that keeps the image's metadata in step with what it does to the pixels.</summary>
internal interface IUpdatesMetadata
{
    /// <summary>Updates <paramref name="metadata"/> after the step turned an <paramref name="input"/>-sized frame into <paramref name="output"/>.</summary>
    void UpdateMetadata(RasterMetadata metadata, Size input, Size output);
}

/// <summary>
/// The EXIF changes operations make (JPEG design, "What operations do to EXIF"): turning and mirroring set the orientation to
/// Normal, size changes update PixelXDimension and PixelYDimension where the block has them, and crops and resizes drop the
/// IFD1 thumbnail, which no longer matches.
/// </summary>
internal static class ExifUpkeep
{
    /// <summary>Sets the orientation tag to Normal, if the block has one.</summary>
    internal static void Upright(RasterMetadata metadata)
    {
        if (metadata.Exif is ExifData exif && exif.TryGet(ExifKey.Orientation, out _) && exif.Orientation != ImageOrientation.Normal)
        {
            exif.Orientation = ImageOrientation.Normal;
        }
    }

    /// <summary>Writes the new size into PixelXDimension and PixelYDimension, where the block has them, keeping their type.</summary>
    internal static void Dimensions(RasterMetadata metadata, Size size)
    {
        if (metadata.Exif is not ExifData exif)
        {
            return;
        }

        Dimension(exif, ExifKey.PixelXDimension, size.Width);
        Dimension(exif, ExifKey.PixelYDimension, size.Height);
    }

    /// <summary>Drops the thumbnail, which no longer shows the image.</summary>
    internal static void DropThumbnail(RasterMetadata metadata)
    {
        metadata.Exif?.RemoveThumbnail();
    }

    /// <summary>Swaps the horizontal and vertical density, after a quarter turn or transpose.</summary>
    internal static void SwapResolution(RasterMetadata metadata)
    {
        if (metadata.Resolution is Resolution r)
        {
            metadata.Resolution = r with { X = r.Y, Y = r.X };
        }
    }

    private static void Dimension(ExifData exif, ExifKey key, int value)
    {
        if (!exif.TryGet(key, out ExifEntry? entry) || (entry.Count == 1 && entry.Type is ExifType.Short or ExifType.Long && entry.AsInteger() == value))
        {
            return;
        }

        if (entry.Type == ExifType.Short && value <= ushort.MaxValue)
        {
            exif.Set(key, (ushort)value);
        }
        else
        {
            exif.Set(key, (uint)value);
        }
    }
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
internal sealed class CropOperation(Rect rect) : IRasterOperation, IUpdatesMetadata
{
    public void UpdateMetadata(RasterMetadata metadata, Size input, Size output)
    {
        ExifUpkeep.Dimensions(metadata, output);
        ExifUpkeep.DropThumbnail(metadata);
    }

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
internal sealed class PadOperation(int width, int height, Rgba8 color, Anchor anchor) : IRasterOperation, IUpdatesMetadata
{
    public void UpdateMetadata(RasterMetadata metadata, Size input, Size output)
    {
        ExifUpkeep.Dimensions(metadata, output);
    }

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
internal sealed class FlipOperation(bool horizontal, bool vertical) : IRasterOperation, IUpdatesMetadata
{
    public void UpdateMetadata(RasterMetadata metadata, Size input, Size output)
    {
        ExifUpkeep.Upright(metadata);
    }

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

/// <summary>
/// Swaps rows and columns through cache-sized tiles, with the source rows and the samples along them taken in either order: a
/// 90° turn either way, a transpose (mirror across the main diagonal) or a transverse (across the other diagonal). Each is one
/// pass.
/// </summary>
/// <param name="fromLastRow">Whether destination column x comes from source row (height - 1 - x) rather than row x.</param>
/// <param name="reversed">Whether each source row is read from its end.</param>
internal sealed class RotateQuarterOperation(bool fromLastRow, bool reversed) : IRasterOperation, INeedsNewPlane, IUpdatesMetadata
{
    private const int Tile = GeometryKernels.Tile;

    /// <summary>Gets a 90° clockwise turn: dest(x, y) = source(y, sourceHeight - 1 - x).</summary>
    internal static RotateQuarterOperation Clockwise { get; } = new(fromLastRow: true, reversed: false);

    /// <summary>Gets a 90° counter-clockwise turn: dest(x, y) = source(sourceWidth - 1 - y, x).</summary>
    internal static RotateQuarterOperation CounterClockwise { get; } = new(fromLastRow: false, reversed: true);

    /// <summary>Gets a transpose: dest(x, y) = source(y, x).</summary>
    internal static RotateQuarterOperation Transpose { get; } = new(fromLastRow: false, reversed: false);

    /// <summary>Gets a transverse: dest(x, y) = source(sourceWidth - 1 - y, sourceHeight - 1 - x).</summary>
    internal static RotateQuarterOperation Transverse { get; } = new(fromLastRow: true, reversed: true);

    public void UpdateMetadata(RasterMetadata metadata, Size input, Size output)
    {
        ExifUpkeep.Upright(metadata);
        ExifUpkeep.Dimensions(metadata, output);
        ExifUpkeep.SwapResolution(metadata);
    }

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
                            Span<TPixel> sourceRow = source.Row(fromLastRow ? width - 1 - (dx0 + c) : dx0 + c);
                            if (reversed)
                            {
                                sourceRow.Slice(height - dy0 - rows, rows).CopyTo(line);
                                GeometryKernels.ReverseRow(line);
                            }
                            else
                            {
                                sourceRow.Slice(dy0, rows).CopyTo(line);
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

/// <summary>
/// Turns the image upright by its EXIF orientation. The planner replaces it with the one-pass step the orientation needs, or
/// with nothing, before the chain runs; see <see cref="Orient"/>.
/// </summary>
internal sealed class AutoOrientOperation : IRasterOperation
{
    internal static AutoOrientOperation Instance { get; } = new();

    /// <summary>Returns the step that shows an image stored with <paramref name="orientation"/> upright, or null when none is needed.</summary>
    internal static IRasterOperation? Orient(ImageOrientation orientation) => orientation switch
    {
        ImageOrientation.FlipHorizontal => new FlipOperation(horizontal: true, vertical: false),
        ImageOrientation.Rotate180 => new FlipOperation(horizontal: true, vertical: true),
        ImageOrientation.FlipVertical => new FlipOperation(horizontal: false, vertical: true),
        ImageOrientation.Transpose => RotateQuarterOperation.Transpose,
        ImageOrientation.Rotate90 => RotateQuarterOperation.Clockwise,
        ImageOrientation.Transverse => RotateQuarterOperation.Transverse,
        ImageOrientation.Rotate270 => RotateQuarterOperation.CounterClockwise,
        _ => null,
    };

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        // Only reached outside a planned chain, which has no metadata to orient by.
        OperationRows.CopyIfSeparate(input);
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
