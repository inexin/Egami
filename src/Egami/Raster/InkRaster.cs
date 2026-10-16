using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami;

/// <summary>How many bits each plane of an <see cref="InkRaster"/> stores per pixel.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="ink-raster"/>
/// </example>
public enum InkDepth
{
    /// <summary>8 bits: 0 no ink to 255 full ink.</summary>
    Unorm8,

    /// <summary>16 bits: 0 no ink to 65535 full ink.</summary>
    Unorm16,
}

/// <summary>
/// A printed page as plates: one plane per ink of an <see cref="InkSet"/> (a disabled spot has none) plus one alpha plane, each
/// 8 or 16 bits. A plane holds the ink as it prints over paper, 0 for none to the depth's maximum for full ink: drawing
/// translucently leaves less ink, so the planes are premultiplied by the alpha plane, and a plate is ready to print as it is.
/// A new raster is blank: no ink, alpha 0.
/// </summary>
/// <remarks>
/// Planar storage lets a plate come out as a <see cref="Raster{TPixel}"/> of <see cref="Gray8"/> or <see cref="Gray16"/> without
/// copying (<see cref="InkPlate.AsRaster{TPixel}"/>), and lets blend modes run one plane at a time. An A4 page at 300 dpi with
/// CMYK and two spots takes 61 MB at 8 bits. Not thread-safe, like <see cref="Raster"/>.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="ink-raster"/>
/// </example>
public sealed class InkRaster : IDisposable
{
    private readonly InkPlate[] _plates;
    private bool _disposed;

    /// <summary>Creates a blank ink raster.</summary>
    /// <param name="inks">The inks, fixed for the raster's life.</param>
    /// <param name="width">The width in pixels, at least 1.</param>
    /// <param name="height">The height in pixels, at least 1.</param>
    /// <param name="depth">The bits per plane.</param>
    /// <param name="context">The context to allocate from and whose <see cref="SafetyLimits"/> apply, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inks"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is less than 1, or <paramref name="depth"/> is not defined.</exception>
    /// <exception cref="LimitExceededException">The size breaks <see cref="SafetyLimits.MaxWidth"/>, <see cref="SafetyLimits.MaxHeight"/> or <see cref="SafetyLimits.MaxPixels"/>, or the planes together break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    public InkRaster(InkSet inks, int width, int height, InkDepth depth = InkDepth.Unorm8, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(inks);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (!Enum.IsDefined(depth))
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, null);
        }

        context ??= EgamiContext.Default;
        context.Limits.CheckFrameSize(width, height);
        List<Colorant> kept = [.. inks.Where((_, i) => !inks.IsDisabled(i))];
        long bytes = (long)width * height * (kept.Count + 1) * (depth == InkDepth.Unorm8 ? 1 : 2);
        if (bytes > context.Limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), context.Limits.MaxTotalBytes, bytes);
        }

        (Inks, Width, Height, Depth, Context) = (inks, width, height, depth, context);
        _plates = new InkPlate[kept.Count + 1];
        try
        {
            for (int i = 0; i < _plates.Length; i++)
            {
                _plates[i] = new InkPlate(this, i < kept.Count ? kept[i] : null, width, height, depth, context.Memory);
            }
        }
        catch
        {
            foreach (InkPlate? plate in _plates)
            {
                plate?.Release();
            }

            throw;
        }

        Plates = new ArraySegment<InkPlate>(_plates, 0, kept.Count);
    }

    /// <summary>Gets the inks.</summary>
    public InkSet Inks { get; }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the bits per plane.</summary>
    public InkDepth Depth { get; }

    /// <summary>Gets the context the raster allocated from.</summary>
    public EgamiContext Context { get; }

    /// <summary>Gets the plates, one per ink in the set's order, without disabled spots.</summary>
    public IReadOnlyList<InkPlate> Plates { get; }

    /// <summary>Gets the alpha plane: 0 where nothing was drawn to the maximum where the page is covered.</summary>
    public InkPlate Alpha => _plates[^1];

    /// <summary>Returns the plate of the ink named <paramref name="name"/> (ordinal), or <see langword="null"/> when the raster has none.</summary>
    /// <param name="name">The ink's name.</param>
    /// <returns>The plate, or <see langword="null"/>.</returns>
    public InkPlate? Plate(string name)
    {
        foreach (InkPlate plate in Plates)
        {
            if (string.Equals(plate.Name, name, StringComparison.Ordinal))
            {
                return plate;
            }
        }

        return null;
    }

    /// <summary>Returns the planes' memory to the context's memory source. Plate rasters taken from it stop working.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            foreach (InkPlate plate in _plates)
            {
                plate.Release();
            }
        }
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>
/// One plane of an <see cref="InkRaster"/>: an ink's plate, or the alpha plane. Rows are read and written in place;
/// <see cref="AsRaster{TPixel}"/> shows the plane as a gray raster without copying.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="ink-raster"/>
/// </example>
public sealed class InkPlate
{
    private readonly InkRaster _owner;
    private readonly PixelPlane<Gray8>? _plane8;
    private readonly PixelPlane<Gray16>? _plane16;

    internal InkPlate(InkRaster owner, Colorant? colorant, int width, int height, InkDepth depth, IPixelMemorySource memory)
    {
        (_owner, Colorant, Width, Height, Depth) = (owner, colorant, width, height, depth);
        if (depth == InkDepth.Unorm8)
        {
            _plane8 = PixelPlane.Allocate<Gray8>(width, height, memory, ClearMode.Zero);
        }
        else
        {
            _plane16 = PixelPlane.Allocate<Gray16>(width, height, memory, ClearMode.Zero);
        }
    }

    /// <summary>Gets the ink, or <see langword="null"/> for the alpha plane.</summary>
    public Colorant? Colorant { get; }

    /// <summary>Gets the ink's name, or <c>Alpha</c> for the alpha plane.</summary>
    public string Name => Colorant?.Name ?? "Alpha";

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the bits per pixel.</summary>
    public InkDepth Depth { get; }

    /// <summary>Returns a row of an 8-bit plane, to read or write in place.</summary>
    /// <param name="y">The row, 0 to <see cref="Height"/> − 1.</param>
    /// <returns>The row's <see cref="Width"/> values.</returns>
    /// <exception cref="InvalidOperationException">The plane is 16-bit.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> is outside the plane.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Span<byte> Row8(int y)
    {
        _owner.ThrowIfDisposed();
        return MemoryMarshal.AsBytes((_plane8 ?? throw new InvalidOperationException("The plane is 16-bit; use Row16.")).Row(y));
    }

    /// <summary>Returns a row of a 16-bit plane, to read or write in place.</summary>
    /// <param name="y">The row, 0 to <see cref="Height"/> − 1.</param>
    /// <returns>The row's <see cref="Width"/> values.</returns>
    /// <exception cref="InvalidOperationException">The plane is 8-bit.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> is outside the plane.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Span<ushort> Row16(int y)
    {
        _owner.ThrowIfDisposed();
        return MemoryMarshal.Cast<Gray16, ushort>((_plane16 ?? throw new InvalidOperationException("The plane is 8-bit; use Row8.")).Row(y));
    }

    /// <summary>
    /// Returns the plane as a gray raster over the same memory, without copying: <see cref="Gray8"/> for an 8-bit plane,
    /// <see cref="Gray16"/> for a 16-bit one. Writing to it writes the plate. 0 is no ink and the maximum full ink, so saved as an
    /// image the plate shows ink as light. Dispose the raster when done; it frees nothing, and stops working when the ink raster
    /// is disposed. An operation that changes its size gives it memory of its own.
    /// </summary>
    /// <typeparam name="TPixel"><see cref="Gray8"/> or <see cref="Gray16"/>, matching <see cref="Depth"/>.</typeparam>
    /// <returns>The raster.</returns>
    /// <exception cref="InvalidOperationException"><typeparamref name="TPixel"/> does not match the plane's depth.</exception>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public Raster<TPixel> AsRaster<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        _owner.ThrowIfDisposed();
        object view = typeof(TPixel) == typeof(Gray8) && _plane8 is not null
            ? new Raster<Gray8>(new PixelPlane<Gray8>(_plane8, new Rect(0, 0, Width, Height)), new RasterMetadata(), _owner.Context)
            : typeof(TPixel) == typeof(Gray16) && _plane16 is not null
            ? new Raster<Gray16>(new PixelPlane<Gray16>(_plane16, new Rect(0, 0, Width, Height)), new RasterMetadata(), _owner.Context)
            : throw new InvalidOperationException($"A {(Depth == InkDepth.Unorm8 ? 8 : 16)}-bit plate is a Raster<{(Depth == InkDepth.Unorm8 ? "Gray8" : "Gray16")}>.");
        return (Raster<TPixel>)view;
    }

    /// <inheritdoc/>
    public override string ToString() => Name;

    internal void Release()
    {
        _plane8?.Dispose();
        _plane16?.Dispose();
    }
}
