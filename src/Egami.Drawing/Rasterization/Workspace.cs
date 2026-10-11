using System.Buffers;
using System.Runtime.InteropServices;

namespace Egami.Drawing.Rasterization;

/// <summary>
/// A growable block of unmanaged values from the context's memory source, so the rasterizer's cells and the clip masks count in
/// the context's memory and are reused from call to call.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
internal sealed class GrowableBuffer<T>(IPixelMemorySource source) : IDisposable
    where T : unmanaged
{
    private IMemoryOwner<byte>? _owner;
    private int _capacity;

    /// <summary>Gets the buffer's capacity in elements.</summary>
    internal int Capacity => _capacity;

    /// <summary>Returns at least <paramref name="count"/> elements, growing (by doubling) when needed. Growing keeps the first <paramref name="keep"/> elements.</summary>
    internal Span<T> Get(int count, int keep = 0)
    {
        if (count > _capacity)
        {
            Grow(count, keep);
        }

        return MemoryMarshal.Cast<byte, T>(_owner!.Memory.Span)[..count];
    }

    /// <summary>Returns the whole buffer, at its capacity.</summary>
    internal Span<T> All => _owner is null ? [] : MemoryMarshal.Cast<byte, T>(_owner.Memory.Span)[.._capacity];

    public void Dispose()
    {
        _owner?.Dispose();
        _owner = null;
        _capacity = 0;
    }

    private void Grow(int count, int keep)
    {
        long wanted = Math.Max(count, Math.Min(2L * _capacity, int.MaxValue / 2 / System.Runtime.CompilerServices.Unsafe.SizeOf<T>()));
        wanted = Math.Max(wanted, 64);
        int bytes = checked((int)(wanted * System.Runtime.CompilerServices.Unsafe.SizeOf<T>()));
        IMemoryOwner<byte> owner = source.Allocate(bytes, ClearMode.None);
        if (_owner is not null && keep > 0)
        {
            MemoryMarshal.Cast<byte, T>(_owner.Memory.Span)[..keep].CopyTo(MemoryMarshal.Cast<byte, T>(owner.Memory.Span));
        }

        _owner?.Dispose();
        _owner = owner;
        _capacity = (int)wanted;
    }
}

/// <summary>One band's scratch: its cells, row heads, coverage row and crossings. One per thread at a time; reused across calls.</summary>
internal sealed class Workspace(IPixelMemorySource source) : IDisposable
{
    internal GrowableBuffer<Cell> Cells { get; } = new(source);

    internal GrowableBuffer<int> RowHeads { get; } = new(source);

    internal GrowableBuffer<byte> Coverage { get; } = new(source);

    internal GrowableBuffer<Cell> RowCells { get; } = new(source);

    internal GrowableBuffer<Crossing> Crossings { get; } = new(source);

    public void Dispose()
    {
        Cells.Dispose();
        RowHeads.Dispose();
        Coverage.Dispose();
        RowCells.Dispose();
        Crossings.Dispose();
    }
}

/// <summary>A pixel's accumulated signed area and cover, in 24.8 fixed point, linked to the next cell of its row.</summary>
internal struct Cell
{
    internal int X;
    internal int Cover;
    internal int Area;
    internal int Next;
}

/// <summary>Where an edge crosses a row's sampling line, and which way it winds: for drawing without antialiasing.</summary>
internal struct Crossing
{
    internal float X;
    internal int Winding;
}

/// <summary>Hands out workspaces, one per band running at once, and keeps them for the next call.</summary>
internal sealed class WorkspacePool(IPixelMemorySource source) : IDisposable
{
    private readonly Stack<Workspace> _free = new();
    private bool _disposed;

    internal Workspace Rent()
    {
        lock (_free)
        {
            return _free.Count > 0 ? _free.Pop() : new Workspace(source);
        }
    }

    internal void Return(Workspace workspace)
    {
        lock (_free)
        {
            if (_disposed)
            {
                workspace.Dispose();
                return;
            }

            _free.Push(workspace);
        }
    }

    public void Dispose()
    {
        lock (_free)
        {
            _disposed = true;
            while (_free.Count > 0)
            {
                _free.Pop().Dispose();
            }
        }
    }
}
