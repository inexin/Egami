using System.Buffers;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Quantization;

/// <summary>
/// Quantize and Remap: each frame gets a palette (its own, or the fixed one) and every pixel becomes its palette color, in the
/// frame's own pixel type. Alpha takes part in matching when the palette has translucent colors; with an opaque palette each
/// pixel keeps its own alpha.
/// </summary>
internal sealed class QuantizeOperation(PaletteOptions options) : IRasterOperation
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width, height = input.Source.Height;
        using IndexedFrame frame = IndexedFrame.Map(width, height, (y, row) => PixelConvert.Convert<TPixel, Rgba8>(input.Source.Row(y), row), options, input.CancellationToken);
        Rgba8[] colors = frame.Colors;
        Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(width);
        try
        {
            for (int y = 0; y < height; y++)
            {
                Span<Rgba8> pixels = row.AsSpan(0, width);
                if (!frame.WithAlpha)
                {
                    PixelConvert.Convert<TPixel, Rgba8>(input.Source.Row(y), pixels);
                }

                ReadOnlySpan<byte> indices = frame.Row(y);
                for (int x = 0; x < width; x++)
                {
                    Rgba8 c = colors[indices[x]];
                    pixels[x] = frame.WithAlpha ? c : new Rgba8(c.R, c.G, c.B, pixels[x].A);
                }

                PixelConvert.Convert<Rgba8, TPixel>(pixels, input.Destination.Row(y));
            }
        }
        finally
        {
            ArrayPool<Rgba8>.Shared.Return(row);
        }
    }
}

/// <summary>A frame mapped to palette indices: the palette, whether alpha took part, and pooled indices.</summary>
internal sealed class IndexedFrame : IDisposable
{
    private byte[]? _indices;

    private IndexedFrame(Rgba8[] colors, int transparentIndex, bool withAlpha, byte[] indices, int width)
    {
        Colors = colors;
        TransparentIndex = transparentIndex;
        WithAlpha = withAlpha;
        _indices = indices;
        Width = width;
    }

    internal Rgba8[] Colors { get; }

    internal int TransparentIndex { get; }

    internal bool WithAlpha { get; }

    internal int Width { get; }

    internal byte[] Indices => _indices ?? throw new ObjectDisposedException(nameof(IndexedFrame));

    internal ReadOnlySpan<byte> Row(int y) => Indices.AsSpan(y * Width, Width);

    /// <summary>Builds the palette the options ask for and maps the frame <paramref name="source"/> gives to it.</summary>
    internal static IndexedFrame Map(int width, int height, RowSource source, PaletteOptions options, CancellationToken cancellationToken)
    {
        Rgba8[] buffer = ArrayPool<Rgba8>.Shared.Rent(width);
        byte[] indices = ArrayPool<byte>.Shared.Rent(checked(width * height));
        BuiltPalette? palette = null;
        try
        {
            palette = PaletteBuilder.Build(height, y =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                source(y, buffer.AsSpan(0, width));
                return buffer.AsMemory(0, width);
            }, options, binaryAlpha: false);
            bool withAlpha = options.FixedPalette?.WithAlpha ?? palette.Colors.Any(c => c.A != 255);
            using PaletteMapper mapper = new(palette.Colors, palette.TransparentIndex, withAlpha, 1);
            FrameMapper.Map(width, height, source, palette, mapper, options, indices.AsSpan(0, width * height));
            return new IndexedFrame(palette.Colors, palette.TransparentIndex, withAlpha, indices, width);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(indices);
            throw;
        }
        finally
        {
            palette?.Release();
            ArrayPool<Rgba8>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (_indices is byte[] indices)
        {
            _indices = null;
            ArrayPool<byte>.Shared.Return(indices);
        }
    }
}

/// <summary>
/// An image as palette indices: one byte per pixel and the palette they index, for formats and APIs that want indexed color,
/// such as a PDF indexed image. The indices are pooled; dispose the image to return them.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Palettes.cs" region="indexed"/>
/// </example>
public sealed class IndexedImage : IDisposable
{
    private readonly IndexedFrame _frame;

    internal IndexedImage(IndexedFrame frame, int width, int height)
    {
        _frame = frame;
        Width = width;
        Height = height;
        Palette = Palette.FromColors(frame.Colors, frame.TransparentIndex >= 0 ? frame.TransparentIndex : null);
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the palette the indices refer to.</summary>
    public Palette Palette { get; }

    /// <summary>Gets every index, row after row, <see cref="Width"/> per row.</summary>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public ReadOnlySpan<byte> Indices => _frame.Indices.AsSpan(0, Width * Height);

    /// <summary>Returns the indices of one row.</summary>
    /// <param name="y">The row.</param>
    /// <returns>The row's <see cref="Width"/> indices.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="y"/> is outside the image.</exception>
    /// <exception cref="ObjectDisposedException">The image has been disposed.</exception>
    public ReadOnlySpan<byte> Row(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return _frame.Row(y);
    }

    /// <summary>Returns the pooled indices.</summary>
    public void Dispose() => _frame.Dispose();
}
