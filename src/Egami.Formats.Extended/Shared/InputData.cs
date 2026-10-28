using System.Buffers;

namespace Egami.Formats.Extended;

/// <summary>Reads what is left of the input whole, for formats that need data from the end of the file or out of order.</summary>
internal static class InputData
{
    /// <summary>
    /// Reads the rest of the input, at most <paramref name="max"/> bytes, checking each step against the load's memory budget.
    /// Returns the bytes in an array at least as long as the returned length.
    /// </summary>
    public static byte[] ReadToEnd(EgamiInput input, long max, out int length)
    {
        byte[] data = new byte[(int)Math.Min(64 * 1024, Math.Max(max, 0))];
        length = 0;
        while (length < max)
        {
            if (length == data.Length)
            {
                int grown = (int)Math.Min(Math.Min((long)data.Length * 2, max), Array.MaxLength);
                if (grown <= data.Length)
                {
                    break;
                }

                input.CheckMemory(grown);
                Array.Resize(ref data, grown);
            }

            int read = input.Read(data.AsSpan(length));
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        return data;
    }
}

/// <summary>Collects a writer's bytes and hands them to the stream in large blocks.</summary>
internal sealed class OutputBuffer : IDisposable
{
    private readonly Stream _output;
    private byte[] _buffer;
    private int _length;

    public OutputBuffer(Stream output, int capacity = 64 * 1024)
    {
        _output = output;
        _buffer = ArrayPool<byte>.Shared.Rent(capacity);
    }

    /// <summary>Returns room for at least <paramref name="size"/> bytes; <see cref="Advance"/> says how many were used.</summary>
    public Span<byte> GetSpan(int size)
    {
        if (_buffer.Length - _length < size)
        {
            Flush();
            if (_buffer.Length < size)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = ArrayPool<byte>.Shared.Rent(size);
            }
        }

        return _buffer.AsSpan(_length);
    }

    public void Advance(int count)
    {
        _length += count;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(GetSpan(bytes.Length));
        _length += bytes.Length;
    }

    public void WriteByte(byte value)
    {
        GetSpan(1)[0] = value;
        _length++;
    }

    public void Flush()
    {
        if (_length > 0)
        {
            _output.Write(_buffer, 0, _length);
            _length = 0;
        }
    }

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }
}
