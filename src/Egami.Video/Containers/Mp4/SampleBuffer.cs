using System.Buffers;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// The bytes of a fragment's samples, in pooled chunks of 256 KB: it grows without copying, stays within a chunk of what it
/// holds, and reuses its chunks from one fragment to the next.
/// </summary>
internal sealed class SampleBuffer : IDisposable
{
    private const int ChunkSize = 256 << 10;

    private readonly List<byte[]> _chunks = [];

    public long Length { get; private set; }

    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int chunk = (int)(Length / ChunkSize), at = (int)(Length % ChunkSize);
            if (chunk == _chunks.Count)
            {
                _chunks.Add(ArrayPool<byte>.Shared.Rent(ChunkSize));
            }

            int n = Math.Min(ChunkSize - at, data.Length);
            data[..n].CopyTo(_chunks[chunk].AsSpan(at));
            data = data[n..];
            Length += n;
        }
    }

    /// <summary>Writes the first <paramref name="count"/> bytes to <paramref name="stream"/>.</summary>
    public void WriteTo(Stream stream, long count)
    {
        for (int chunk = 0; count > 0; chunk++)
        {
            int n = (int)Math.Min(ChunkSize, count);
            stream.Write(_chunks[chunk], 0, n);
            count -= n;
        }
    }

    /// <summary>Drops the first <paramref name="count"/> bytes, moving the rest to the start, and gives the chunks left empty back to the pool.</summary>
    public void Consume(long count)
    {
        long rest = Length - count;
        for (long done = 0; done < rest;)
        {
            long from = count + done;
            int fromChunk = (int)(from / ChunkSize), fromAt = (int)(from % ChunkSize);
            int toChunk = (int)(done / ChunkSize), toAt = (int)(done % ChunkSize);
            int n = (int)Math.Min(rest - done, Math.Min(ChunkSize - fromAt, ChunkSize - toAt));
            _chunks[fromChunk].AsSpan(fromAt, n).CopyTo(_chunks[toChunk].AsSpan(toAt));
            done += n;
        }

        Length = rest;
        int keep = (int)((rest + ChunkSize - 1) / ChunkSize);
        for (int i = keep; i < _chunks.Count; i++)
        {
            ArrayPool<byte>.Shared.Return(_chunks[i]);
        }

        _chunks.RemoveRange(keep, _chunks.Count - keep);
    }

    public void Dispose()
    {
        foreach (byte[] chunk in _chunks)
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        _chunks.Clear();
        Length = 0;
    }
}
