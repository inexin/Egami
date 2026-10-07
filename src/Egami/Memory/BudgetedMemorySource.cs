using System.Buffers;

namespace Egami;

/// <summary>
/// Wraps a memory source for one load and throws <see cref="LimitExceededException"/> when the memory held at once
/// would exceed <see cref="SafetyLimits.MaxTotalBytes"/>. Disposed blocks no longer count.
/// </summary>
internal sealed class BudgetedMemorySource(IPixelMemorySource inner, long maxTotalBytes) : IPixelMemorySource
{
    private long _liveBytes;

    public int MaxBlockBytes => inner.MaxBlockBytes;

    /// <summary>Gets the bytes currently held through this source.</summary>
    public long LiveBytes => Interlocked.Read(ref _liveBytes);

    /// <summary>Throws if <paramref name="bytes"/> more would exceed the budget, without allocating.</summary>
    public void Check(long bytes)
    {
        long total = LiveBytes + bytes;
        if (total > maxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), maxTotalBytes, total);
        }
    }

    public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
    {
        long total = Interlocked.Add(ref _liveBytes, byteLength);
        if (total > maxTotalBytes)
        {
            Interlocked.Add(ref _liveBytes, -byteLength);
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), maxTotalBytes, total);
        }

        try
        {
            return new Block(this, inner.Allocate(byteLength, clear), byteLength);
        }
        catch
        {
            Interlocked.Add(ref _liveBytes, -byteLength);
            throw;
        }
    }

    private sealed class Block(BudgetedMemorySource owner, IMemoryOwner<byte> inner, int length) : IMemoryOwner<byte>
    {
        private int _disposed;

        public Memory<byte> Memory => inner.Memory;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                inner.Dispose();
                Interlocked.Add(ref owner._liveBytes, -length);
            }
        }
    }
}
