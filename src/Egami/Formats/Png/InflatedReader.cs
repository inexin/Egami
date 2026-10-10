using System.Buffers;

namespace Egami.Formats.Png;

/// <summary>
/// Reads decompressed image data in large blocks and hands it out a scanline at a time. Each call into the runtime's zlib
/// has a fixed cost, so reading whole rows straight from the decompressor was measured at about a quarter of the decode time
/// for a 4000×3000 photo.
/// </summary>
/// <remarks>
/// When the compressed data is damaged, the decompressor throws for a whole block, so in lenient mode up to one block of rows
/// before the damage can be lost, compared with reading row by row.
/// </remarks>
internal sealed class InflatedReader(Stream inflater, int scanlineBytes, IdatStream? source = null) : IDisposable
{
    private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(64 * 1024, scanlineBytes));
    private int _start;
    private int _end;
    private bool _ended;

    /// <summary>Gets the problem that stopped decompression, or <see langword="null"/>.</summary>
    public string? Problem { get; private set; }

    /// <summary>Fills <paramref name="destination"/> as far as the data allows.</summary>
    /// <returns>The number of bytes copied; less than requested only at the end of the data or after a problem.</returns>
    public int Read(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            if (_start == _end && !Fill())
            {
                break;
            }

            int n = Math.Min(destination.Length - total, _end - _start);
            _buffer.AsSpan(_start, n).CopyTo(destination[total..]);
            _start += n;
            total += n;
        }

        return total;
    }

    /// <summary>Returns whether the decompressed data ends here, with no extra bytes and no damage after it.</summary>
    public bool AtEnd()
    {
        return _start == _end && !Fill() && Problem is null;
    }

    /// <summary>Gets whether the last fill found nothing because the compressed data has not all arrived yet.</summary>
    public bool Waiting => source is { Dry: true } && !_ended;

    /// <summary>
    /// Inflates until <paramref name="count"/> bytes are ready without consuming any, so a scanline is read whole or not at all.
    /// Returns whether they are; if not, <see cref="Waiting"/> says whether more data may still bring them.
    /// </summary>
    public bool Ensure(int count)
    {
        while (_end - _start < count && !_ended)
        {
            if (_start > 0)
            {
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                _end -= _start;
                _start = 0;
            }

            int read;
            try
            {
                read = inflater.Read(_buffer.AsSpan(_end));
            }
            catch (InvalidDataException e)
            {
                Problem = $"The compressed image data is damaged ({e.Message}).";
                _ended = true;
                break;
            }

            if (read == 0)
            {
                _ended = source is not { Dry: true };
                break;
            }

            _end += read;
        }

        return _end - _start >= count;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
    }

    private bool Fill()
    {
        if (_ended)
        {
            return false;
        }

        _start = 0;
        _end = 0;
        try
        {
            _end = inflater.Read(_buffer);
        }
        catch (InvalidDataException e)
        {
            Problem = $"The compressed image data is damaged ({e.Message}).";
        }

        // An incremental decode that has run dry is not at the end: more compressed bytes may come.
        _ended = _end == 0 && source is not { Dry: true };
        return _end > 0;
    }
}
