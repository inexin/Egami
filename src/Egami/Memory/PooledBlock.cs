using System.Buffers;

namespace Egami;

/// <summary>A block backed by an array rented from <see cref="ArrayPool{T}.Shared"/>.</summary>
internal sealed class PooledBlock : IMemoryOwner<byte>
{
    private readonly int _length;
    private readonly PixelMemoryUsage.Counted? _counted;
    private byte[]? _array;

    internal PooledBlock(int length, ClearMode clear, PixelMemoryUsage? usage = null)
    {
        _length = length;
        _array = ArrayPool<byte>.Shared.Rent(length);
        _counted = usage?.Allocated(length);
        if (clear == ClearMode.Zero)
        {
            _array.AsSpan(0, length).Clear();
        }
    }

    public Memory<byte> Memory
    {
        get
        {
            byte[]? array = _array;
            ObjectDisposedException.ThrowIf(array is null, this);
            return array.AsMemory(0, _length);
        }
    }

    public void Dispose()
    {
        byte[]? array = Interlocked.Exchange(ref _array, null);
        if (array is not null)
        {
            ArrayPool<byte>.Shared.Return(array);
            _counted?.Released(_length);
        }
    }
}
