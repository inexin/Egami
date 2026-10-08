using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Operations.Geometry;

/// <summary>Row reversal and tile transposition for the exact geometry operations.</summary>
internal static class GeometryKernels
{
    /// <summary>
    /// The side of a transpose tile, in pixels. Each tile reads 64 pixels from each of 64 rows; 32 measured 2.7 times slower on one
    /// thread, because every short read lands on a new page.
    /// </summary>
    internal const int Tile = 64;

    /// <summary>Reverses the pixels of a row in place, using the vectorized reverse for 1-, 2-, 4- and 8-byte pixels.</summary>
    internal static void ReverseRow<T>(Span<T> row)
        where T : unmanaged
    {
        switch (Unsafe.SizeOf<T>())
        {
            case 1:
                MemoryMarshal.Cast<T, byte>(row).Reverse();
                break;
            case 2:
                MemoryMarshal.Cast<T, ushort>(row).Reverse();
                break;
            case 4:
                MemoryMarshal.Cast<T, uint>(row).Reverse();
                break;
            case 8:
                MemoryMarshal.Cast<T, ulong>(row).Reverse();
                break;
            default:
                ReverseRowScalar(row);
                break;
        }
    }

    /// <summary>The reference for <see cref="ReverseRow{T}"/>.</summary>
    internal static void ReverseRowScalar<T>(Span<T> row)
        where T : unmanaged
    {
        for (int i = 0, j = row.Length - 1; i < j; i++, j--)
        {
            (row[i], row[j]) = (row[j], row[i]);
        }
    }

    /// <summary>
    /// Transposes a <see cref="Tile"/> × <see cref="Tile"/> tile: <c>destination[r * Tile + c] = source[c * Tile + r]</c>.
    /// Both spans hold at least <see cref="Tile"/>² pixels.
    /// </summary>
    internal static void Transpose<T>(ReadOnlySpan<T> source, Span<T> destination)
        where T : unmanaged
    {
        if (Unsafe.SizeOf<T>() == 4 && Sse2.IsSupported)
        {
            Transpose4x4Blocks(MemoryMarshal.Cast<T, uint>(source), MemoryMarshal.Cast<T, uint>(destination));
            return;
        }

        TransposeScalar(source, destination);
    }

    /// <summary>The reference for <see cref="Transpose{T}"/>.</summary>
    internal static void TransposeScalar<T>(ReadOnlySpan<T> source, Span<T> destination)
        where T : unmanaged
    {
        source = source[..(Tile * Tile)];
        destination = destination[..(Tile * Tile)];
        for (int r = 0; r < Tile; r++)
        {
            Span<T> row = destination.Slice(r * Tile, Tile);
            for (int c = 0; c < Tile; c++)
            {
                row[c] = source[(c * Tile) + r];
            }
        }
    }

    /// <summary>Transposes 4-byte pixels as a grid of 4 × 4 blocks, each with eight unpacks.</summary>
    private static void Transpose4x4Blocks(ReadOnlySpan<uint> source, Span<uint> destination)
    {
        if (source.Length < Tile * Tile || destination.Length < Tile * Tile)
        {
            throw new ArgumentException("A tile needs Tile × Tile pixels.");
        }

        ref uint src = ref MemoryMarshal.GetReference(source);
        ref uint dst = ref MemoryMarshal.GetReference(destination);
        for (int bc = 0; bc < Tile; bc += 4)
        {
            for (int br = 0; br < Tile; br += 4)
            {
                // Rows bc..bc+3 of the source, columns br..br+3.
                nuint s = (nuint)((bc * Tile) + br);
                Vector128<uint> a0 = Vector128.LoadUnsafe(ref src, s);
                Vector128<uint> a1 = Vector128.LoadUnsafe(ref src, s + Tile);
                Vector128<uint> a2 = Vector128.LoadUnsafe(ref src, s + (2 * Tile));
                Vector128<uint> a3 = Vector128.LoadUnsafe(ref src, s + (3 * Tile));

                Vector128<ulong> t0 = Sse2.UnpackLow(a0, a1).AsUInt64();
                Vector128<ulong> t1 = Sse2.UnpackLow(a2, a3).AsUInt64();
                Vector128<ulong> t2 = Sse2.UnpackHigh(a0, a1).AsUInt64();
                Vector128<ulong> t3 = Sse2.UnpackHigh(a2, a3).AsUInt64();

                nuint d = (nuint)((br * Tile) + bc);
                Sse2.UnpackLow(t0, t1).AsUInt32().StoreUnsafe(ref dst, d);
                Sse2.UnpackHigh(t0, t1).AsUInt32().StoreUnsafe(ref dst, d + Tile);
                Sse2.UnpackLow(t2, t3).AsUInt32().StoreUnsafe(ref dst, d + (2 * Tile));
                Sse2.UnpackHigh(t2, t3).AsUInt32().StoreUnsafe(ref dst, d + (3 * Tile));
            }
        }
    }
}
