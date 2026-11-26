using System.Buffers.Binary;

namespace Egami.Metafiles.Writing;

/// <summary>
/// The records of a file, gathered in 64 KiB chunks until the header can be written before them: growing one array would copy
/// the whole file each time it doubles.
/// </summary>
internal sealed class ChunkBuffer
{
    private const int ChunkSize = 64 * 1024;

    private readonly List<byte[]> _full = [];
    private byte[] _chunk = new byte[ChunkSize];
    private int _used;

    /// <summary>Gets the number of bytes written.</summary>
    public long Length => ((long)_full.Count * ChunkSize) + _used;

    /// <summary>Writes two 32-bit fields, such as a record's type and size.</summary>
    public void Pair(uint first, uint second)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(head, first);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], second);
        Write(head);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        while (bytes.Length > 0)
        {
            if (_used == ChunkSize)
            {
                _full.Add(_chunk);
                _chunk = GC.AllocateUninitializedArray<byte>(ChunkSize);
                _used = 0;
            }

            int n = Math.Min(bytes.Length, ChunkSize - _used);
            bytes[..n].CopyTo(_chunk.AsSpan(_used));
            _used += n;
            bytes = bytes[n..];
        }
    }

    /// <summary>Pads with zeros to a multiple of four bytes.</summary>
    public void Align()
    {
        ReadOnlySpan<byte> zeros = [0, 0, 0];
        Write(zeros[..(int)((4 - (Length & 3)) & 3)]);
    }

    /// <summary>Writes every byte to a stream, in order.</summary>
    public void CopyTo(Stream stream)
    {
        foreach (byte[] chunk in _full)
        {
            stream.Write(chunk);
        }

        stream.Write(_chunk.AsSpan(0, _used));
    }
}
