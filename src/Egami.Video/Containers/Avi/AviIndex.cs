namespace Egami.Video.Containers.Avi;

/// <summary>
/// One stream's chunks in file order: where each chunk header sits, its size, its start time in stream ticks and whether
/// it is a keyframe. Built from idx1, from the OpenDML indexes, or by scanning the movi list once.
/// </summary>
internal sealed class AviIndex
{
    private long[] _offsets = new long[64];
    private int[] _sizes = new int[64];
    private long[] _ticks = new long[64];
    private bool[] _keys = new bool[64];

    public int Count { get; private set; }

    public long Offset(int i) => _offsets[i];

    public int Size(int i) => _sizes[i];

    public long Ticks(int i) => _ticks[i];

    public bool IsKey(int i) => _keys[i];

    public void Add(long offset, int size, long ticks, bool key)
    {
        if (Count == _offsets.Length)
        {
            int capacity = checked(Count * 2);
            Array.Resize(ref _offsets, capacity);
            Array.Resize(ref _sizes, capacity);
            Array.Resize(ref _ticks, capacity);
            Array.Resize(ref _keys, capacity);
        }

        _offsets[Count] = offset;
        _sizes[Count] = size;
        _ticks[Count] = ticks;
        _keys[Count] = key;
        Count++;
    }

    /// <summary>Returns the first entry at or after a file offset (entries are in file order), or <see cref="Count"/>.</summary>
    public int FirstAtOrAfter(long offset)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_offsets[mid] < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Returns the last non-empty keyframe whose start tick is at or before <paramref name="ticks"/>, else the first non-empty keyframe, else -1.</summary>
    public int KeyframeAtOrBefore(long ticks)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_ticks[mid] <= ticks)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int i = lo - 1; i >= 0; i--)
        {
            if (_keys[i] && _sizes[i] > 0)
            {
                return i;
            }
        }

        for (int i = 0; i < Count; i++)
        {
            if (_keys[i] && _sizes[i] > 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether the entries are in ascending file order, which seeking relies on.</summary>
    public bool IsSorted()
    {
        for (int i = 1; i < Count; i++)
        {
            if (_offsets[i] <= _offsets[i - 1])
            {
                return false;
            }
        }

        return true;
    }
}
