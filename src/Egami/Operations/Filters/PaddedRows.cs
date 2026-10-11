using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Loads rows of a plane as working floats (four per pixel, premultiplied unless opaque), padded on both sides by an edge mode,
/// for the neighbourhood filters. Rows past the top and bottom are mapped by the same mode; transparent edges read zeros.
/// </summary>
internal readonly struct PaddedRows<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly PixelPlane<TPixel> _source;
    private readonly RowConverter<TPixel> _converter;
    private readonly EdgeMode _edge;
    private readonly int _pad;

    internal PaddedRows(PixelPlane<TPixel> source, RowConverter<TPixel> converter, EdgeMode edge, int pad)
    {
        _source = source;
        _converter = converter;
        _edge = edge;
        _pad = pad;
    }

    /// <summary>Gets the floats in a padded row.</summary>
    internal int Length => (_source.Width + (2 * _pad)) * 4;

    /// <summary>Loads virtual row <paramref name="v"/> into <paramref name="line"/>, which holds <see cref="Length"/> floats.</summary>
    /// <returns><see langword="true"/> if every pixel loaded is opaque.</returns>
    internal bool Load(int v, Span<float> line)
    {
        int width = _source.Width;
        Span<float> middle = line.Slice(_pad * 4, width * 4);
        int row = ConvolutionOperation.MapIndex(v, _source.Height, _edge);
        bool opaque;
        if (row < 0)
        {
            middle.Clear();
            opaque = false;
        }
        else
        {
            opaque = _converter.Load(_source.Row(row), middle);
        }

        for (int i = 1; i <= _pad; i++)
        {
            Pad(line, _pad - i, ConvolutionOperation.MapIndex(-i, width, _edge));
            Pad(line, _pad + width - 1 + i, ConvolutionOperation.MapIndex(width - 1 + i, width, _edge));
        }

        return opaque && (_pad == 0 || _edge != EdgeMode.Transparent);
    }

    private void Pad(Span<float> line, int at, int from)
    {
        Span<float> target = line.Slice(at * 4, 4);
        if (from < 0)
        {
            target.Clear();
        }
        else
        {
            line.Slice((_pad + from) * 4, 4).CopyTo(target);
        }
    }
}
