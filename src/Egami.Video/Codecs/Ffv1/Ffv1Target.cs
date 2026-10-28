using Egami.Pixels;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// Writes decoded lines into a frame's planes: shifted up to the storage depth, and for RGB turned back from the reversible
/// color transform (RFC 9043 section 3.7.2) into G, B and R planes. Parts of a slice outside the frame are dropped.
/// </summary>
internal sealed class Ffv1Target
{
    private readonly YuvPlane<byte>?[] _bytes = new YuvPlane<byte>?[4];
    private readonly YuvPlane<ushort>?[] _words = new YuvPlane<ushort>?[4];
    private readonly int _shift;
    private readonly int _bits;
    private readonly int _max;
    private readonly bool _bgrException;

    public Ffv1Target(YuvRaster raster, Ffv1Parameters p)
    {
        Width = raster.Width;
        Height = raster.Height;
        _bits = p.BitsPerRawSample;
        _max = (1 << _bits) - 1;
        _shift = raster.BitDepth - _bits;

        // FFmpeg swapped G and B for 9 to 15 bit RGB without alpha, and RFC 9043 section 3.7.2.1 makes that the rule.
        _bgrException = p.ColorSpace == 1 && _bits is >= 9 and <= 15 && !p.ExtraPlane;
        if (raster is YuvRaster<byte> b)
        {
            (_bytes[0], _bytes[1], _bytes[2], _bytes[3]) = (b.Y, b.U, b.V, b.A);
        }
        else
        {
            var w = (YuvRaster<ushort>)raster;
            (_words[0], _words[1], _words[2], _words[3]) = (w.Y, w.U, w.V, w.A);
        }
    }

    /// <summary>Gets the frame's width in luma samples.</summary>
    public int Width { get; }

    /// <summary>Gets the frame's height in luma samples.</summary>
    public int Height { get; }

    /// <summary>Stores a line of plane <paramref name="plane"/> (0 Y, 1 Cb, 2 Cr, 3 alpha) at row <paramref name="y"/>, column <paramref name="x"/>.</summary>
    public void Store(int plane, int y, int x, ReadOnlySpan<int> samples)
    {
        if (_bytes[plane] is { } bytes)
        {
            if (y >= bytes.Height || x >= bytes.Width)
            {
                return;
            }

            Span<byte> row = bytes.Row(y)[x..];
            int n = Math.Min(row.Length, samples.Length);
            for (int i = 0; i < n; i++)
            {
                row[i] = (byte)samples[i];
            }
        }
        else if (_words[plane] is { } words)
        {
            if (y >= words.Height || x >= words.Width)
            {
                return;
            }

            Span<ushort> row = words.Row(y)[x..];
            int n = Math.Min(row.Length, samples.Length);
            int shift = _shift;
            for (int i = 0; i < n; i++)
            {
                row[i] = (ushort)(samples[i] << shift);
            }
        }
    }

    /// <summary>Turns a line of the reversible color transform back into G, B, R (and alpha) and stores it.</summary>
    public void StoreRgb(int y, int x, ReadOnlySpan<int> luma, ReadOnlySpan<int> cb, ReadOnlySpan<int> cr, ReadOnlySpan<int> alpha)
    {
        int offset = 1 << _bits;
        int max = _max;
        int n = luma.Length;
        Span<int> g = n <= 1024 ? stackalloc int[n] : new int[n];
        Span<int> b = n <= 1024 ? stackalloc int[n] : new int[n];
        Span<int> r = n <= 1024 ? stackalloc int[n] : new int[n];
        for (int i = 0; i < n; i++)
        {
            int u = cb[i] - offset;
            int v = cr[i] - offset;
            int first = luma[i] - ((u + v) >> 2);
            if (_bgrException)
            {
                b[i] = first & max;
                g[i] = (u + first) & max;
            }
            else
            {
                g[i] = first & max;
                b[i] = (u + first) & max;
            }

            r[i] = (v + first) & max;
        }

        Store(0, y, x, g);
        Store(1, y, x, b);
        Store(2, y, x, r);
        if (!alpha.IsEmpty)
        {
            Span<int> a = n <= 1024 ? stackalloc int[n] : new int[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = alpha[i] & max;
            }

            Store(3, y, x, a);
        }
    }
}
