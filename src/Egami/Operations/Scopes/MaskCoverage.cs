using System.Buffers;
using System.Runtime.Intrinsics;

using Egami.Pixels;

namespace Egami.Operations.Scopes;

/// <summary>
/// A <see cref="Mask"/> resolved for reading: the channel <see cref="MaskChannel.Auto"/> stands for, and the bounding box of
/// non-zero coverage in the image's coordinates, found once when the chain is planned. Rows read as 8-bit coverage (exact 0 to
/// 255) or as float coverage (0 to 1), for 16-bit and float targets that need the smoother gradient.
/// </summary>
internal sealed class MaskCoverage
{
    /// <summary>Stands for "every pixel": an inverted mask covers the whole image outside its raster.</summary>
    private static readonly Rect Everywhere = new(-(1 << 29), -(1 << 29), 1 << 30, 1 << 30);

    private readonly Raster _source;
    private readonly Point _offset;
    private readonly MaskChannel _channel;
    private readonly bool _inverted;

    /// <summary>Initializes a new instance of the <see cref="MaskCoverage"/> class.</summary>
    /// <param name="mask">The mask.</param>
    /// <param name="scan">Whether to find the box of non-zero coverage; without it, <see cref="Bounds"/> is everywhere.</param>
    internal MaskCoverage(Mask mask, bool scan = true)
    {
        _source = mask.Source ?? throw new ArgumentNullException(nameof(mask), "The mask has no raster.");
        _offset = mask.Offset;
        _inverted = mask.Inverted;
        _channel = mask.Channel != MaskChannel.Auto ? mask.Channel
            : _source.Layout.ChannelOrder == PixelChannelOrder.Alpha ? MaskChannel.Alpha
            : _source.Layout.ChannelOrder == PixelChannelOrder.Gray ? MaskChannel.Red
            : _source.Layout.HasAlpha ? MaskChannel.Alpha
            : MaskChannel.Luminance;
        Bounds = _inverted || !scan ? Everywhere : Scan();
    }

    private MaskCoverage(MaskCoverage other, Raster source)
    {
        _source = source;
        _offset = other._offset;
        _channel = other._channel;
        _inverted = other._inverted;
        Bounds = other.Bounds;
    }

    /// <summary>Gets the rectangle outside which coverage is 0, in the image's coordinates.</summary>
    internal Rect Bounds { get; }

    /// <summary>Gets the raster coverage is read from.</summary>
    internal Raster Source => _source;

    /// <summary>Returns the same coverage read from <paramref name="copy"/>, a snapshot of the source.</summary>
    internal MaskCoverage From(Raster copy) => new(this, copy);

    /// <summary>Writes the 8-bit coverage of image row <paramref name="y"/>, from column <paramref name="x"/>.</summary>
    internal void Row8(int y, int x, Span<byte> coverage)
    {
        byte outside = _inverted ? (byte)255 : (byte)0;
        coverage.Fill(outside);
        if (!Overlap(y, x, coverage.Length, out int sx, out int sy, out int from, out int count))
        {
            return;
        }

        // One-byte masks read their row as it is stored, and inversion is a bitwise not.
        ReadOnlySpan<byte> stored = _source switch
        {
            Raster<Gray8> gray when _channel is MaskChannel.Red or MaskChannel.Green or MaskChannel.Blue or MaskChannel.Luminance
                => System.Runtime.InteropServices.MemoryMarshal.AsBytes(gray.Row(sy).Slice(sx, count)),
            Raster<Alpha8> alpha when _channel == MaskChannel.Alpha => System.Runtime.InteropServices.MemoryMarshal.AsBytes(alpha.Row(sy).Slice(sx, count)),
            _ => default,
        };
        if (!stored.IsEmpty)
        {
            Span<byte> target = coverage.Slice(from, count);
            stored.CopyTo(target);
            if (_inverted)
            {
                Not(target);
            }

            return;
        }

        Rgba8[] buffer = ArrayPool<Rgba8>.Shared.Rent(count);
        try
        {
            Span<Rgba8> row = buffer.AsSpan(0, count);
            _source.ReadRow(sy, sx, row);
            Span<byte> part = coverage.Slice(from, count);
            for (int i = 0; i < count; i++)
            {
                Rgba8 p = row[i];
                byte v = _channel switch
                {
                    MaskChannel.Alpha => p.A,
                    MaskChannel.Red => p.R,
                    MaskChannel.Green => p.G,
                    MaskChannel.Blue => p.B,
                    _ => (byte)(((p.R * 13933) + (p.G * 46871) + (p.B * 4732) + 32768) >> 16),
                };
                part[i] = _inverted ? (byte)(255 - v) : v;
            }
        }
        finally
        {
            ArrayPool<Rgba8>.Shared.Return(buffer);
        }
    }

    /// <summary>Writes the float coverage of image row <paramref name="y"/>, from column <paramref name="x"/>.</summary>
    internal void RowFloat(int y, int x, Span<float> coverage)
    {
        coverage.Fill(_inverted ? 1f : 0f);
        if (!Overlap(y, x, coverage.Length, out int sx, out int sy, out int from, out int count))
        {
            return;
        }

        RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(count);
        try
        {
            Span<RgbaF32> row = buffer.AsSpan(0, count);
            _source.ReadRow(sy, sx, row);
            Span<float> part = coverage.Slice(from, count);
            for (int i = 0; i < count; i++)
            {
                RgbaF32 p = row[i];
                float v = _channel switch
                {
                    MaskChannel.Alpha => p.A,
                    MaskChannel.Red => p.R,
                    MaskChannel.Green => p.G,
                    MaskChannel.Blue => p.B,
                    _ => (0.2126f * p.R) + (0.7152f * p.G) + (0.0722f * p.B),
                };
                v = Math.Clamp(v, 0f, 1f);
                part[i] = _inverted ? 1f - v : v;
            }
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(buffer);
        }
    }

    /// <summary>Replaces each byte with 255 minus it.</summary>
    internal static void Not(Span<byte> values)
    {
        int i = 0;
        if (System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= values.Length; i += 32)
            {
                Span<byte> chunk = values.Slice(i, 32);
                (~System.Runtime.Intrinsics.Vector256.Create((ReadOnlySpan<byte>)chunk)).CopyTo(chunk);
            }
        }

        for (; i < values.Length; i++)
        {
            values[i] = (byte)~values[i];
        }
    }

    /// <summary>Finds where row <paramref name="y"/>, columns <paramref name="x"/> onward, meets the mask raster.</summary>
    private bool Overlap(int y, int x, int length, out int sx, out int sy, out int from, out int count)
    {
        sy = y - _offset.Y;
        int left = Math.Max(x, _offset.X), right = Math.Min(x + length, _offset.X + _source.Width);
        sx = left - _offset.X;
        from = left - x;
        count = right - left;
        return (uint)sy < (uint)_source.Height && count > 0;
    }

    /// <summary>Scans the mask once for the box of non-zero coverage, row by row with a vectorized search; 8-bit masks in bytes.</summary>
    private Rect Scan()
    {
        return _source.Layout.ChannelType == PixelChannelType.Unorm8 ? Scan<byte>() : Scan<float>();
    }

    private Rect Scan<T>()
        where T : unmanaged, IEquatable<T>
    {
        int top = int.MaxValue, bottom = int.MinValue, left = int.MaxValue, right = int.MinValue;
        T[] row = ArrayPool<T>.Shared.Rent(_source.Width);
        try
        {
            Span<T> coverage = row.AsSpan(0, _source.Width);
            for (int y = 0; y < _source.Height; y++)
            {
                if (typeof(T) == typeof(byte))
                {
                    Row8(y + _offset.Y, _offset.X, System.Runtime.InteropServices.MemoryMarshal.Cast<T, byte>(coverage));
                }
                else
                {
                    RowFloat(y + _offset.Y, _offset.X, System.Runtime.InteropServices.MemoryMarshal.Cast<T, float>(coverage));
                }

                int first = coverage.IndexOfAnyExcept(default(T));
                if (first < 0)
                {
                    continue;
                }

                int last = coverage.LastIndexOfAnyExcept(default(T));
                top = Math.Min(top, y);
                bottom = y;
                left = Math.Min(left, first);
                right = Math.Max(right, last);
            }
        }
        finally
        {
            ArrayPool<T>.Shared.Return(row);
        }

        return top == int.MaxValue ? default : new Rect(_offset.X + left, _offset.Y + top, right - left + 1, bottom - top + 1);
    }
}
