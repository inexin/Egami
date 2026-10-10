using System.Buffers;
using System.Runtime.CompilerServices;

namespace Egami.Formats;

/// <summary>
/// Byte-at-a-time reading for run-length and text decoders, without a call into <see cref="EgamiInput"/> per byte. It copies a
/// window of up to 64 KB from the input and consumes from the input only what was used, so after <see cref="Finish"/> the input
/// stands just past the last byte read.
/// </summary>
internal sealed class ByteReader(EgamiInput input)
{
    private const int WindowSize = 64 * 1024;

    private byte[] _window = ArrayPool<byte>.Shared.Rent(WindowSize);
    private int _position;
    private int _length;

    /// <summary>Gets the bytes taken from the window that the input has not consumed yet: where the next byte is, from the input's position.</summary>
    public int Pending => _position;

    /// <summary>Gets the offset of the next byte from the start of the image.</summary>
    public long Offset => input.Position + _position;

    /// <summary>Returns the next byte.</summary>
    /// <exception cref="CorruptDataException">The data has ended.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Next()
    {
        if (_position == _length)
        {
            Refill();
        }

        return _window[_position++];
    }

    /// <summary>Returns the next byte without consuming it, or -1 at the end of the data.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Peek()
    {
        if (_position == _length && !TryRefill())
        {
            return -1;
        }

        return _window[_position];
    }

    /// <summary>Returns the next <paramref name="count"/> bytes (at most 64 KB), which stay valid until the next call.</summary>
    /// <exception cref="CorruptDataException">The data ends first.</exception>
    public ReadOnlySpan<byte> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, WindowSize);
        if (_length - _position < count)
        {
            Refill(count);
        }

        ReadOnlySpan<byte> bytes = _window.AsSpan(_position, count);
        _position += count;
        return bytes;
    }

    /// <summary>
    /// Returns the bytes left in the window, refilled first when fewer than <paramref name="min"/> are left (at most 64 KB). The
    /// span is shorter than <paramref name="min"/> only at the end of the data. Consume from it with <see cref="Advance"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> Window(int min)
    {
        if (_length - _position < min)
        {
            TryRefill(min);
        }

        return _window.AsSpan(_position, _length - _position);
    }

    /// <summary>Consumes <paramref name="count"/> bytes of the span <see cref="Window"/> returned.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        _position += count;
    }

    /// <summary>Consumes from the input the bytes used, so the input continues just after them.</summary>
    public void Finish()
    {
        input.Skip(_position);
        _position = 0;
        _length = 0;
    }

    /// <summary>Returns the window to the pool.</summary>
    public void Release()
    {
        ArrayPool<byte>.Shared.Return(_window);
        _window = [];
    }

    private void Refill(int count = 1)
    {
        if (!TryRefill(count))
        {
            throw new CorruptDataException(input.FormatName, Offset, "The data ends unexpectedly.") { IsTruncation = true };
        }
    }

    private bool TryRefill(int count = 1)
    {
        input.Skip(_position);
        _position = 0;
        ReadOnlySpan<byte> peeked = input.Peek(WindowSize);
        peeked.CopyTo(_window);
        _length = peeked.Length;
        return _length >= count;
    }
}
