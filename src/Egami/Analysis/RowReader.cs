using System.Buffers;

using Egami.Pixels;

namespace Egami.Analysis;

/// <summary>A straight RGBA color in double precision, channels 0 to 1.</summary>
internal struct RgbaD
{
    internal double R;
    internal double G;
    internal double B;
    internal double A;
}

/// <summary>
/// Reads rows of any raster as <see cref="RgbaD"/>: integer types through Rgba16, so an 8-bit value v is exactly v / 255 and a
/// 16-bit one v / 65535, and float types through RgbaF32.
/// </summary>
internal sealed class RowReader : IDisposable
{
    private readonly Raster _raster;
    private readonly int _frame;
    private readonly bool _integer;
    private readonly Rgba16[]? _words;
    private readonly RgbaF32[]? _floats;

    internal RowReader(Raster raster, int frame, int width)
    {
        _raster = raster;
        _frame = frame;
        _integer = raster.Layout.ChannelType is PixelChannelType.Unorm8 or PixelChannelType.Unorm16 or PixelChannelType.Packed;
        if (_integer)
        {
            _words = ArrayPool<Rgba16>.Shared.Rent(width);
        }
        else
        {
            _floats = ArrayPool<RgbaF32>.Shared.Rent(width);
        }
    }

    /// <summary>Reads <paramref name="row"/>.Length pixels of row <paramref name="y"/> from column <paramref name="x"/>.</summary>
    internal void Read(int y, int x, Span<RgbaD> row)
    {
        const double Scale = 1.0 / 65535;
        if (_integer)
        {
            Span<Rgba16> words = _words.AsSpan(0, row.Length);
            _raster.ReadFrameRow(_frame, y, x, words);
            for (int i = 0; i < row.Length; i++)
            {
                Rgba16 w = words[i];
                row[i] = new RgbaD { R = w.R * Scale, G = w.G * Scale, B = w.B * Scale, A = w.A * Scale };
            }
        }
        else
        {
            Span<RgbaF32> floats = _floats.AsSpan(0, row.Length);
            _raster.ReadFrameRow(_frame, y, x, floats);
            for (int i = 0; i < row.Length; i++)
            {
                RgbaF32 f = floats[i];
                row[i] = new RgbaD { R = f.R, G = f.G, B = f.B, A = f.A };
            }
        }
    }

    public void Dispose()
    {
        if (_words is not null)
        {
            ArrayPool<Rgba16>.Shared.Return(_words);
        }

        if (_floats is not null)
        {
            ArrayPool<RgbaF32>.Shared.Return(_floats);
        }
    }
}
