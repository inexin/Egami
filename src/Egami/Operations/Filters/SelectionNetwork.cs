using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Operations.Filters;

/// <summary>
/// A comparator network that leaves the median of n values in the middle slot: Batcher's odd-even merge sort for n inputs, with
/// every comparator that cannot affect the middle slot removed. Run on vectors, each lane holds its own n values, so one pass
/// finds 32 byte medians at once.
/// </summary>
internal sealed class SelectionNetwork
{
    private static readonly SelectionNetwork?[] Cache = new SelectionNetwork?[64];

    private readonly (int Low, int High)[] _pairs;

    private SelectionNetwork(int count, (int Low, int High)[] pairs)
    {
        Count = count;
        _pairs = pairs;
    }

    /// <summary>Gets the number of inputs.</summary>
    internal int Count { get; }

    /// <summary>Gets the comparators: after each, the lower slot holds the smaller value.</summary>
    internal ReadOnlySpan<(int Low, int High)> Pairs => _pairs;

    /// <summary>Returns the network for an odd number of inputs up to 63.</summary>
    internal static SelectionNetwork Median(int count)
    {
        return Cache[count] ??= Build(count);
    }

    private static SelectionNetwork Build(int count)
    {
        // Batcher's odd-even merge sort on the next power of two; comparators touching slots past the inputs are dropped, which
        // is the same as padding with +∞.
        int size = 1;
        while (size < count)
        {
            size *= 2;
        }

        List<(int, int)> all = [];
        for (int p = 1; p < size; p *= 2)
        {
            for (int k = p; k >= 1; k /= 2)
            {
                for (int j = k % p; j + k < size; j += 2 * k)
                {
                    for (int i = 0; i < Math.Min(k, size - j - k); i++)
                    {
                        int a = i + j, b = i + j + k;
                        if (a / (2 * p) == b / (2 * p) && b < count)
                        {
                            all.Add((a, b));
                        }
                    }
                }
            }
        }

        // Backwards from the middle slot: a comparator matters only if one of its outputs is needed later.
        bool[] needed = new bool[count];
        needed[count / 2] = true;
        List<(int, int)> kept = [];
        for (int c = all.Count - 1; c >= 0; c--)
        {
            (int a, int b) = all[c];
            if (needed[a] || needed[b])
            {
                needed[a] = needed[b] = true;
                kept.Add((a, b));
            }
        }

        kept.Reverse();
        return new SelectionNetwork(count, [.. kept]);
    }

    /// <summary>Runs the network on <paramref name="values"/>; afterwards the middle slot holds each lane's median.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Run(Span<Vector256<byte>> values)
    {
        ref Vector256<byte> v = ref MemoryMarshal.GetReference(values);
        foreach ((int low, int high) in _pairs)
        {
            ref Vector256<byte> a = ref Unsafe.Add(ref v, low);
            ref Vector256<byte> b = ref Unsafe.Add(ref v, high);
            Vector256<byte> min = Vector256.Min(a, b);
            b = Vector256.Max(a, b);
            a = min;
        }
    }

    /// <summary>Runs the network on scalars, for the ends of rows.</summary>
    internal void Run(Span<byte> values)
    {
        foreach ((int low, int high) in _pairs)
        {
            byte a = values[low], b = values[high];
            values[low] = Math.Min(a, b);
            values[high] = Math.Max(a, b);
        }
    }
}
