using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Compression;

/// <summary>
/// XXH64, the 64-bit xxHash (Yann Collet's xxHash specification, version 0.1.1), which Zstandard's content checksum is the low
/// 32 bits of.
/// </summary>
internal static class XxHash64
{
    private const ulong Prime1 = 0x9E3779B185EBCA87;
    private const ulong Prime2 = 0xC2B2AE3D27D4EB4F;
    private const ulong Prime3 = 0x165667B19E3779F9;
    private const ulong Prime4 = 0x85EBCA77C2B2AE63;
    private const ulong Prime5 = 0x27D4EB2F165667C5;

    /// <summary>Hashes <paramref name="data"/> in one go.</summary>
    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        int at = 0;
        ulong hash;
        if (data.Length >= 32)
        {
            ulong v1 = seed + Prime1 + Prime2;
            ulong v2 = seed + Prime2;
            ulong v3 = seed;
            ulong v4 = seed - Prime1;
            int stop = data.Length - 32;
            ref byte start = ref MemoryMarshal.GetReference(data);
            while (at <= stop)
            {
                ref byte stripe = ref Unsafe.Add(ref start, at);
                v1 = Round(v1, Lane(ref stripe));
                v2 = Round(v2, Lane(ref Unsafe.Add(ref stripe, 8)));
                v3 = Round(v3, Lane(ref Unsafe.Add(ref stripe, 16)));
                v4 = Round(v4, Lane(ref Unsafe.Add(ref stripe, 24)));
                at += 32;
            }

            hash = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12)
                + BitOperations.RotateLeft(v4, 18);
            hash = Merge(hash, v1);
            hash = Merge(hash, v2);
            hash = Merge(hash, v3);
            hash = Merge(hash, v4);
        }
        else
        {
            hash = seed + Prime5;
        }

        hash += (ulong)data.Length;
        for (; at + 8 <= data.Length; at += 8)
        {
            hash ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data[at..]));
            hash = (BitOperations.RotateLeft(hash, 27) * Prime1) + Prime4;
        }

        if (at + 4 <= data.Length)
        {
            hash ^= BinaryPrimitives.ReadUInt32LittleEndian(data[at..]) * Prime1;
            hash = (BitOperations.RotateLeft(hash, 23) * Prime2) + Prime3;
            at += 4;
        }

        for (; at < data.Length; at++)
        {
            hash ^= data[at] * Prime5;
            hash = BitOperations.RotateLeft(hash, 11) * Prime1;
        }

        hash ^= hash >> 33;
        hash *= Prime2;
        hash ^= hash >> 29;
        hash *= Prime3;
        hash ^= hash >> 32;
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Lane(ref byte at)
    {
        ulong lane = Unsafe.ReadUnaligned<ulong>(ref at);
        return BitConverter.IsLittleEndian ? lane : BinaryPrimitives.ReverseEndianness(lane);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Round(ulong accumulator, ulong lane) => BitOperations.RotateLeft(accumulator + (lane * Prime2), 31) * Prime1;

    private static ulong Merge(ulong hash, ulong accumulator) => ((hash ^ Round(0, accumulator)) * Prime1) + Prime4;
}
