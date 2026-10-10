using System.Buffers;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Streaming;

/// <summary>
/// One streamable step of a transcode: it takes the rows of its input top-down and hands the rows of its output, top-down, to the
/// next step. <see cref="End"/> follows the last row.
/// </summary>
internal abstract class RowStep<TPixel> : IDisposable
    where TPixel : unmanaged, IPixelType<TPixel>
{
    /// <summary>Gets or sets the step that takes this one's rows.</summary>
    public RowStep<TPixel> Next { get; set; } = null!;

    /// <summary>Takes the next input row.</summary>
    public abstract void Take(ReadOnlySpan<TPixel> row);

    /// <summary>Says the input has ended, handing on any rows still held.</summary>
    public virtual void End()
    {
        Next.End();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

/// <summary>Hands on the rows inside a rectangle, cut to its columns.</summary>
internal sealed class CropRows<TPixel>(Rect rect) : RowStep<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private int _y;

    public override void Take(ReadOnlySpan<TPixel> row)
    {
        if (_y >= rect.Y && _y < rect.Bottom)
        {
            Next.Take(row.Slice(rect.X, rect.Width));
        }

        _y++;
    }
}

/// <summary>Mirrors each row.</summary>
internal sealed class FlipRows<TPixel>(int width) : RowStep<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly TPixel[] _row = ArrayPool<TPixel>.Shared.Rent(width);

    public override void Take(ReadOnlySpan<TPixel> row)
    {
        Span<TPixel> flipped = _row.AsSpan(0, width);
        row.CopyTo(flipped);
        flipped.Reverse();
        Next.Take(flipped);
    }

    protected override void Dispose(bool disposing)
    {
        ArrayPool<TPixel>.Shared.Return(_row);
        base.Dispose(disposing);
    }
}

/// <summary>
/// Runs an operation that changes each pixel on its own (a color matrix, grayscale) on bands of 16 rows, which is the same as
/// running it on the whole image.
/// </summary>
internal sealed class PixelRows<TPixel>(IRasterOperation operation, int width, EgamiContext context, CancellationToken cancellationToken) : RowStep<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private const int Band = 16;

    private readonly PixelPlane<TPixel> _band = PixelPlane.Allocate<TPixel>(width, Band, context.Memory);
    private int _rows;

    public override void Take(ReadOnlySpan<TPixel> row)
    {
        row.CopyTo(_band.Row(_rows++));
        if (_rows == Band)
        {
            Flush(_band);
        }
    }

    public override void End()
    {
        if (_rows > 0)
        {
            // The last, short band runs on a plane of its own height, since operations run on every row of their plane.
            PixelPlane<TPixel> rest = PixelPlane.Allocate<TPixel>(width, _rows, context.Memory);
            try
            {
                for (int y = 0; y < _rows; y++)
                {
                    _band.Row(y).CopyTo(rest.Row(y));
                }

                Flush(rest);
            }
            finally
            {
                rest.Dispose();
            }
        }

        base.End();
    }

    protected override void Dispose(bool disposing)
    {
        _band.Dispose();
        base.Dispose(disposing);
    }

    private void Flush(PixelPlane<TPixel> plane)
    {
        operation.Run(new OperationInput<TPixel>(plane, plane, context, 0, cancellationToken));
        for (int y = 0; y < plane.Height; y++)
        {
            Next.Take(plane.Row(y));
        }

        _rows = 0;
    }
}

/// <summary>The end of a transcode's steps: rows collect in a band, handed to <paramref name="write"/> whenever it fills.</summary>
internal sealed class BandRows<TPixel>(int width, EgamiContext context, Action<Raster<TPixel>, int> write) : RowStep<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private const int Band = 32;

    private readonly Raster<TPixel> _band = Raster.Create<TPixel>(width, Band, context);
    private int _rows;

    public override void Take(ReadOnlySpan<TPixel> row)
    {
        row.CopyTo(_band.Row(_rows++));
        if (_rows == Band)
        {
            Flush();
        }
    }

    public override void End()
    {
        Flush();
    }

    /// <summary>Writes the rows collected so far.</summary>
    public void Flush()
    {
        if (_rows > 0)
        {
            write(_band, _rows);
            _rows = 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        _band.Dispose();
        base.Dispose(disposing);
    }
}
