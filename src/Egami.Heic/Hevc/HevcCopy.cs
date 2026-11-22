using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Heic.Hevc;

/// <summary>
/// Copies of the short runs the CTU search saves and restores (a block's rows and its 4x4 block maps): the sizes that occur, 2 to
/// 128 bytes in powers of two, as fixed-size moves the JIT unrolls, without the call into a general memory copy; others as spans.
/// </summary>
internal static class HevcCopy
{
    /// <summary>Copies <paramref name="count"/> elements from <paramref name="from"/> at an index to <paramref name="to"/> at an index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Run<T>(T[] from, int fromAt, T[] to, int toAt, int count)
        where T : unmanaged
    {
        if ((uint)fromAt > (uint)from.Length || (uint)count > (uint)(from.Length - fromAt) || (uint)toAt > (uint)to.Length
            || (uint)count > (uint)(to.Length - toAt))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        ref byte source = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(from), fromAt));
        ref byte target = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(to), toAt));
        switch (count * Unsafe.SizeOf<T>())
        {
            case 0:
                break;
            case 1:
                target = source;
                break;
            case 2:
                Unsafe.CopyBlockUnaligned(ref target, ref source, 2);
                break;
            case 4:
                Unsafe.CopyBlockUnaligned(ref target, ref source, 4);
                break;
            case 8:
                Unsafe.CopyBlockUnaligned(ref target, ref source, 8);
                break;
            case 16:
                Unsafe.CopyBlockUnaligned(ref target, ref source, 16);
                break;
            case 32:
                Unsafe.CopyBlockUnaligned(ref target, ref source, 32);
                break;
            case 64:
                Unsafe.CopyBlockUnaligned(ref target, ref source, 64);
                break;
            case 128:
                Unsafe.CopyBlockUnaligned(ref target, ref source, 128);
                break;
            default:
                from.AsSpan(fromAt, count).CopyTo(to.AsSpan(toAt));
                break;
        }
    }
}
