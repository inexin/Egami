using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Heic.Hevc;

/// <summary>
/// Fills and marks the short rows of the per-4x4-block maps (at most 16 entries: a 64-sample CTB) with one or two stores each,
/// which overlap for lengths between the store sizes; a span fill of a few entries costs a call and its set-up.
/// </summary>
internal static class HevcFill
{
    /// <summary>Sets <paramref name="count"/> bytes (1 to 16) to <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Bytes(ref byte d, int count, byte value)
    {
        ulong v = value * 0x0101010101010101UL;
        if (count >= 8)
        {
            Unsafe.WriteUnaligned(ref d, v);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, count - 8), v);
        }
        else if (count >= 4)
        {
            Unsafe.WriteUnaligned(ref d, (uint)v);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, count - 4), (uint)v);
        }
        else if (count >= 2)
        {
            Unsafe.WriteUnaligned(ref d, (ushort)v);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, count - 2), (ushort)v);
        }
        else
        {
            d = value;
        }
    }

    /// <summary>Sets bits in <paramref name="count"/> bytes (1 to 16).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Or(ref byte d, int count, byte value)
    {
        ulong v = value * 0x0101010101010101UL;
        if (count >= 8)
        {
            Unsafe.WriteUnaligned(ref d, Unsafe.ReadUnaligned<ulong>(ref d) | v);
            ref byte last = ref Unsafe.Add(ref d, count - 8);
            Unsafe.WriteUnaligned(ref last, Unsafe.ReadUnaligned<ulong>(ref last) | v);
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.Add(ref d, i) |= value;
            }
        }
    }

    /// <summary>Sets <paramref name="count"/> ints (1 to 16) to <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Ints(ref int d, int count, int value)
    {
        if (count >= 8)
        {
            Vector256<int> v = Vector256.Create(value);
            v.StoreUnsafe(ref d);
            v.StoreUnsafe(ref d, (nuint)(count - 8));
        }
        else if (count >= 4)
        {
            Vector128<int> v = Vector128.Create(value);
            v.StoreUnsafe(ref d);
            v.StoreUnsafe(ref d, (nuint)(count - 4));
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                Unsafe.Add(ref d, i) = value;
            }
        }
    }

    /// <summary>The first entry of a map's row from block (x4, y4).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T At<T>(T[] map, int index) => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(map), index);
}
