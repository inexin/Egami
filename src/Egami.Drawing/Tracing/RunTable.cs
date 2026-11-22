using System.Buffers;

namespace Egami.Drawing.Tracing;

/// <summary>
/// The runs of a run-based connected-component labelling: each run's first pixel and its union-find parent, in arrays rented
/// from the shared pool (grown by doubling), so a warm labelling allocates nothing for them. Each set's root is its least run.
/// </summary>
internal sealed class RunTable : IDisposable
{
    private int[] _start = ArrayPool<int>.Shared.Rent(4096);
    private int[] _parent = ArrayPool<int>.Shared.Rent(4096);

    /// <summary>Gets the number of runs.</summary>
    internal int Count { get; private set; }

    /// <summary>Gets the first pixel (or column) of each run.</summary>
    internal int[] Start => _start;

    /// <summary>Adds a run starting at <paramref name="start"/>, a set of its own, and returns its index.</summary>
    internal int Add(int start)
    {
        if (Count == _start.Length)
        {
            _start = Grow(_start, Count);
            _parent = Grow(_parent, Count);
        }

        _start[Count] = start;
        _parent[Count] = Count;
        return Count++;
    }

    /// <summary>Joins the sets of runs <paramref name="a"/> and <paramref name="b"/>, the lesser root becoming the root.</summary>
    internal void Join(int a, int b)
    {
        int ra = Find(a), rb = Find(b);
        if (ra != rb)
        {
            _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }

    /// <summary>The root of run <paramref name="i"/>'s set, compressing the path to it.</summary>
    internal int Find(int i)
    {
        int[] parent = _parent;
        int root = i;
        while (parent[root] != root)
        {
            root = parent[root];
        }

        while (parent[i] != root)
        {
            int next = parent[i];
            parent[i] = root;
            i = next;
        }

        return root;
    }

    /// <summary>
    /// Numbers the sets in the order their first runs come and returns, in a rented array the caller gives back with
    /// <see cref="ArrayPool{T}.Return"/>, each run's set number; <paramref name="count"/> receives the number of sets.
    /// </summary>
    internal int[] Number(out int count)
    {
        int[] number = ArrayPool<int>.Shared.Rent(Math.Max(1, Count));
        count = 0;
        for (int r = 0; r < Count; r++)
        {
            int root = Find(r);
            number[r] = root == r ? count++ : number[root];
        }

        return number;
    }

    public void Dispose()
    {
        ArrayPool<int>.Shared.Return(_start);
        ArrayPool<int>.Shared.Return(_parent);
        _start = _parent = [];
    }

    private static int[] Grow(int[] array, int used)
    {
        int[] grown = ArrayPool<int>.Shared.Rent(array.Length * 2);
        array.AsSpan(0, used).CopyTo(grown);
        ArrayPool<int>.Shared.Return(array);
        return grown;
    }
}
