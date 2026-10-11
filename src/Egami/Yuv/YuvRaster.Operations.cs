using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <content>Crop and resize on the planes.</content>
public abstract partial class YuvRaster
{
    /// <summary>
    /// Returns a new raster holding <paramref name="rect"/> of this one. The rectangle's corner must fall on a chroma sample: even
    /// x and y for 4:2:0, even x for 4:2:2. Chroma is copied exactly, with no filtering.
    /// </summary>
    /// <param name="rect">The area to keep, in luma pixels.</param>
    /// <returns>The new raster, with a copy of the metadata. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty or reaches outside the raster.</exception>
    /// <exception cref="ArgumentException">The rectangle's corner is not on a chroma sample.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="operations"/>
    /// </example>
    public YuvRaster Crop(Rect rect) => CropCore(rect);

    /// <summary>
    /// Returns a new raster with every plane resized by <paramref name="filter"/>, as a <see cref="Gray8"/> or <see cref="Gray16"/>
    /// resize would, keeping the chroma format, siting and color. Samples are clamped to the bit depth, since some filters
    /// overshoot. Scaling planes before converting costs half as much at 4:2:0 as scaling RGB after.
    /// </summary>
    /// <param name="width">The new luma width, at least 1.</param>
    /// <param name="height">The new luma height, at least 1.</param>
    /// <param name="filter">The resampling kernel. Defaults to <see cref="ResampleFilter.Bicubic"/>.</param>
    /// <returns>The new raster, with a copy of the metadata. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1.</exception>
    /// <exception cref="LimitExceededException">The new raster breaks the context's <see cref="SafetyLimits"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="operations"/>
    /// </example>
    public YuvRaster Resize(int width, int height, ResampleFilter filter = ResampleFilter.Bicubic) => ResizeCore(width, height, filter);

    private protected abstract YuvRaster CropCore(Rect rect);

    private protected abstract YuvRaster ResizeCore(int width, int height, ResampleFilter filter);
}

/// <content>Crop and resize on the planes.</content>
public sealed partial class YuvRaster<TSample>
{
    /// <summary>Returns a new raster holding <paramref name="rect"/> of this one. See <see cref="YuvRaster.Crop(Rect)"/>.</summary>
    /// <param name="rect">The area to keep, in luma pixels.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rectangle is empty or reaches outside the raster.</exception>
    /// <exception cref="ArgumentException">The rectangle's corner is not on a chroma sample.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public new YuvRaster<TSample> Crop(Rect rect)
    {
        ThrowIfDisposed();
        if (rect.Width < 1 || rect.Height < 1 || rect.X < 0 || rect.Y < 0 || rect.X > Width - rect.Width || rect.Y > Height - rect.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), rect, $"The rectangle must be inside the {Width}×{Height} raster.");
        }

        (int sx, int sy) = Subsampling(ChromaFormat);
        if (rect.X % sx != 0 || rect.Y % sy != 0)
        {
            throw new ArgumentException($"A {ChromaFormat} crop must start on a chroma sample: x a multiple of {sx} and y of {sy}.", nameof(rect));
        }

        YuvRaster<TSample> result = Allocate(rect.Width, rect.Height, ChromaFormat, BitDepth, Color, HasAlpha, Metadata.Copy(), Context, ClearMode.None);
        CopyArea(Y, result.Y, rect.X, rect.Y);
        CopyArea(A, result.A, rect.X, rect.Y);
        CopyArea(U, result.U, rect.X / sx, rect.Y / sy);
        CopyArea(V, result.V, rect.X / sx, rect.Y / sy);
        UpdateExif(result.Metadata, rect.Width, rect.Height);
        return result;

        static void CopyArea(YuvPlane<TSample>? from, YuvPlane<TSample>? to, int x, int y)
        {
            for (int row = 0; to is not null && row < to.Height; row++)
            {
                from!.Row(y + row).Slice(x, to.Width).CopyTo(to.Row(row));
            }
        }
    }

    /// <summary>Returns a new raster with every plane resized. See <see cref="YuvRaster.Resize(int, int, ResampleFilter)"/>.</summary>
    /// <param name="width">The new luma width, at least 1.</param>
    /// <param name="height">The new luma height, at least 1.</param>
    /// <param name="filter">The resampling kernel. Defaults to <see cref="ResampleFilter.Bicubic"/>.</param>
    /// <returns>The new raster. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is less than 1.</exception>
    /// <exception cref="LimitExceededException">The new raster breaks the context's <see cref="SafetyLimits"/>.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public new YuvRaster<TSample> Resize(int width, int height, ResampleFilter filter = ResampleFilter.Bicubic)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ThrowIfDisposed();
        YuvRaster<TSample> result = Allocate(width, height, ChromaFormat, BitDepth, Color, HasAlpha, Metadata.Copy(), Context, ClearMode.None);
        try
        {
            ResizePlane(Y, result.Y, filter);
            ResizePlane(U, result.U, filter);
            ResizePlane(V, result.V, filter);
            ResizePlane(A, result.A, filter);
            UpdateExif(result.Metadata, width, height);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    private protected override YuvRaster CropCore(Rect rect) => Crop(rect);

    /// <inheritdoc/>
    private protected override YuvRaster ResizeCore(int width, int height, ResampleFilter filter) => Resize(width, height, filter);

    /// <summary>Keeps EXIF's stored size in step with a new size, as raster operations do.</summary>
    private static void UpdateExif(RasterMetadata metadata, int width, int height)
    {
        Operations.Geometry.ExifUpkeep.Dimensions(metadata, new Size(width, height));
        Operations.Geometry.ExifUpkeep.DropThumbnail(metadata);
    }

    private static (int X, int Y) Subsampling(ChromaFormat format) => format switch
    {
        ChromaFormat.Yuv420 => (2, 2),
        ChromaFormat.Yuv422 => (2, 1),
        _ => (1, 1),
    };

    /// <summary>Resizes one plane in one channel with the gray resize's filters and weights, clamped to the bit depth.</summary>
    private void ResizePlane(YuvPlane<TSample>? from, YuvPlane<TSample>? to, ResampleFilter filter)
    {
        if (from is not null && to is not null)
        {
            YuvPlaneResize.Resize(from, to, filter, MaxSample, Context.MaxParallelism);
        }
    }
}
