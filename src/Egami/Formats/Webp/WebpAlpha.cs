using System.Buffers;

namespace Egami.Formats.Webp;

/// <summary>
/// Decodes the ALPH chunk of a lossy WebP (RFC 9649 section 2.7.1.2) a row at a time: a header byte with the compression (raw, or
/// a VP8L image stream whose green channel carries the alpha), the filter (none, horizontal, vertical or gradient, undone row by
/// row against the row above) and a preprocessing hint decoders may ignore, then the data.
/// </summary>
internal sealed class WebpAlpha : IDisposable
{
    private readonly byte[] _data;
    private readonly int _width;
    private readonly int _height;
    private readonly int _filter;
    private readonly Vp8lBitReader? _reader;
    private readonly Vp8lDecoder? _decoder;
    private readonly uint[]? _argb;
    private readonly byte[] _previous;
    private int _next;
    private bool _disposed;

    /// <summary>Starts decoding the ALPH payload <paramref name="data"/> for an image of the given size.</summary>
    /// <exception cref="CorruptDataException">The header byte is invalid.</exception>
    internal WebpAlpha(EgamiInput input, byte[] data, int width, int height, bool strict)
    {
        _data = data;
        _width = width;
        _height = height;
        int header = data[0];
        int compression = header & 3;
        _filter = (header >> 2) & 3;
        int preprocessing = (header >> 4) & 3;
        if (compression > 1 || preprocessing > 1 || (header >> 6) != 0)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, -1, $"The ALPH header byte 0x{header:X2} is invalid.");
        }

        _previous = ArrayPool<byte>.Shared.Rent(width);
        if (compression == 1)
        {
            _reader = new Vp8lBitReader(data, 1, data.Length - 1);
            _decoder = new Vp8lDecoder(input, _reader, strict, width, height);
            _argb = ArrayPool<uint>.Shared.Rent(width);
        }
        else if (data.Length - 1 < (long)width * height)
        {
            if (strict)
            {
                throw new CorruptDataException(WebpFormat.Info.Name, -1, "The raw ALPH data is shorter than the image.") { IsTruncation = true };
            }

            Warning = "The raw alpha data is shorter than the image; the missing rows are opaque.";
        }
    }

    /// <summary>Gets the warning a lenient decode recovered with, if any.</summary>
    internal string? Warning { get; private set; }

    /// <summary>Returns the bytes the decoder holds for an image of this size.</summary>
    internal static long WorkingBytes(int width, int height) => Vp8lDecoder.WorkingBytes(width, height) + (2L * width);

    /// <summary>Decodes the next row of alpha into <paramref name="alpha"/>.</summary>
    internal void DecodeRow(Span<byte> alpha)
    {
        int y = _next++;
        alpha = alpha[.._width];
        if (_decoder is not null)
        {
            Span<uint> argb = _argb.AsSpan(0, _width);
            _decoder.DecodeRow(argb);
            if (_decoder.Warning is string warning)
            {
                Warning = warning;
            }

            for (int x = 0; x < _width; x++)
            {
                alpha[x] = (byte)(argb[x] >> 8);
            }
        }
        else
        {
            long start = 1 + ((long)y * _width);
            if (start + _width <= _data.Length)
            {
                _data.AsSpan((int)start, _width).CopyTo(alpha);
            }
            else
            {
                // Lenient: missing raw rows stay opaque, unfiltered.
                alpha.Fill(255);
                _previous.AsSpan(0, _width).Fill(255);
                return;
            }
        }

        Unfilter(_filter, y == 0 ? default : _previous.AsSpan(0, _width), alpha);
        alpha.CopyTo(_previous);
    }

    /// <summary>Undoes an alpha filter on one row in place, against the unfiltered row above (empty for the first row).</summary>
    internal static void Unfilter(int filter, ReadOnlySpan<byte> above, Span<byte> row)
    {
        if (filter == 0)
        {
            return;
        }

        if (above.IsEmpty || filter == 1)
        {
            // Horizontal, and the first row of every filter: each pixel from its left neighbour, the first from the pixel above.
            byte pred = above.IsEmpty ? (byte)0 : above[0];
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = (byte)(pred + row[i]);
                pred = row[i];
            }

            return;
        }

        if (filter == 2)
        {
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = (byte)(above[i] + row[i]);
            }

            return;
        }

        // Gradient: left + above − above-left, clamped; the first pixel from the pixel above.
        int left = above[0];
        int topLeft = above[0];
        for (int i = 0; i < row.Length; i++)
        {
            int top = above[i];
            left = (byte)(row[i] + Math.Clamp(left + top - topLeft, 0, 255));
            topLeft = top;
            row[i] = (byte)left;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _decoder?.Dispose();
            _reader?.Dispose();
            ArrayPool<byte>.Shared.Return(_previous);
            if (_argb is not null)
            {
                ArrayPool<uint>.Shared.Return(_argb);
            }
        }
    }
}
