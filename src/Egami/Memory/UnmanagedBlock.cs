using System.Buffers;
using System.Runtime.InteropServices;

namespace Egami;

/// <summary>A block of unmanaged memory aligned to 64 bytes.</summary>
/// <remarks>
/// The memory is owned by an <see cref="AlignedMemoryHandle"/>, whose finalizer frees it if the block is never disposed,
/// so a forgotten plane leaks only until the next garbage collection.
/// </remarks>
internal sealed unsafe class UnmanagedBlock : MemoryManager<byte>
{
    private readonly int _length;
    private readonly AlignedMemoryHandle _handle;

    internal UnmanagedBlock(int length, ClearMode clear)
    {
        _length = length;
        _handle = AlignedMemoryHandle.Allocate(length);
        if (clear == ClearMode.Zero)
        {
            NativeMemory.Clear((void*)_handle.DangerousGetHandle(), (nuint)length);
        }
    }

    /// <summary>Gets the number of unmanaged bytes currently allocated by all blocks. Used by tests to find leaks.</summary>
    internal static long LiveBytes => AlignedMemoryHandle.LiveBytes;

    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return new Span<byte>((void*)_handle.DangerousGetHandle(), _length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, _length);
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return new MemoryHandle((byte*)_handle.DangerousGetHandle() + elementIndex, default, this);
    }

    public override void Unpin()
    {
        // Unmanaged memory never moves.
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handle.Dispose();
        }
    }
}

/// <summary>Owns one 64-byte-aligned unmanaged allocation and frees it on dispose or, failing that, on finalization.</summary>
internal sealed unsafe class AlignedMemoryHandle : SafeHandle
{
    private const int Alignment = 64;

    private static long s_liveBytes;

    private readonly long _length;

    private AlignedMemoryHandle(int length)
        : base(0, ownsHandle: true)
    {
        _length = length;
    }

    public override bool IsInvalid => handle == 0;

    internal static long LiveBytes => Interlocked.Read(ref s_liveBytes);

    internal static AlignedMemoryHandle Allocate(int length)
    {
        AlignedMemoryHandle result = new(length);
        result.SetHandle((nint)NativeMemory.AlignedAlloc((nuint)Math.Max(length, 1), Alignment));
        GC.AddMemoryPressure(Math.Max(length, 1));
        Interlocked.Add(ref s_liveBytes, length);
        return result;
    }

    protected override bool ReleaseHandle()
    {
        NativeMemory.AlignedFree((void*)handle);
        GC.RemoveMemoryPressure(Math.Max(_length, 1));
        Interlocked.Add(ref s_liveBytes, -_length);
        return true;
    }
}
