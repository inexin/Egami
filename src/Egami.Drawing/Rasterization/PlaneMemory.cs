using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Drawing.Rasterization;

/// <summary>Where planes keep their pixels, so a fill can tell whether the image it reads is the pixels it writes.</summary>
internal static class PlaneMemory
{
    /// <summary>
    /// Returns whether two planes share no bytes. Each plane's rows span a range of addresses; the ranges are taken relative to
    /// one of <paramref name="a"/>'s rows, with no garbage collection in between (which could move managed memory), and a
    /// collection during the walk gives <see langword="false"/>. Memory both planes share moves together, so it always overlaps.
    /// </summary>
    internal static bool Disjoint<TA, TB>(PixelPlane<TA> a, PixelPlane<TB> b)
        where TA : unmanaged, IPixelType<TA>
        where TB : unmanaged, IPixelType<TB>
    {
        if (a.Height == 0 || a.Width == 0 || b.Height == 0 || b.Width == 0)
        {
            return true;
        }

        int collections = GC.CollectionCount(0);
        ref byte anchor = ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(a.Row(0)));
        (nint aLow, nint aHigh) = Range(a, ref anchor);
        (nint bLow, nint bHigh) = Range(b, ref anchor);
        return GC.CollectionCount(0) == collections && (bHigh <= aLow || aHigh <= bLow);
    }

    private static (nint Low, nint High) Range<T>(PixelPlane<T> plane, ref byte anchor)
        where T : unmanaged, IPixelType<T>
    {
        nint low = nint.MaxValue, high = nint.MinValue, length = plane.Width * Unsafe.SizeOf<T>();
        for (int y = 0; y < plane.Height; y++)
        {
            nint start = Unsafe.ByteOffset(ref anchor, ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(plane.Row(y))));
            low = Math.Min(low, start);
            high = Math.Max(high, start + length);
        }

        return (low, high);
    }
}
