using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// Sample access for planes of either sample type: bytes for 8-bit pictures, which halves the memory the decoder moves, and
/// 16-bit samples above 8 bits. The code that uses these is generic over the type, and the JIT compiles each type's copy with
/// the other branch removed. Vector loads give 16-bit lanes whatever the type, so the filters compute alike for both.
/// </summary>
internal static class H264Samples
{
    /// <summary>Gets whether T is byte, the 8-bit sample type; else it is ushort.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Narrow<T>()
        where T : unmanaged => typeof(T) == typeof(byte);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Get<T>(T[] plane, int at)
        where T : unmanaged => Narrow<T>() ? Unsafe.As<byte[]>(plane)[at] : Unsafe.As<ushort[]>(plane)[at];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Set<T>(T[] plane, int at, int value)
        where T : unmanaged
    {
        if (Narrow<T>())
        {
            Unsafe.As<byte[]>(plane)[at] = (byte)value;
        }
        else
        {
            Unsafe.As<ushort[]>(plane)[at] = (ushort)value;
        }
    }

    /// <summary>Gets a sample value as the sample type.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T Of<T>(int value)
        where T : unmanaged
    {
        if (Narrow<T>())
        {
            byte b = (byte)value;
            return Unsafe.As<byte, T>(ref b);
        }

        ushort u = (ushort)value;
        return Unsafe.As<ushort, T>(ref u);
    }

    /// <summary>Gets a reference to sample <paramref name="at"/> of a plane, without a bounds check.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ref T At<T>(T[] plane, int at)
        where T : unmanaged => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), at);

    /// <summary>Loads eight samples into 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> Load8<T>(ref T at, nint offset = 0)
        where T : unmanaged
    {
        ref T from = ref Unsafe.Add(ref at, offset);
        return Narrow<T>()
            ? Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<T, byte>(ref from))).AsByte()).AsInt16()
            : Vector128.LoadUnsafe(ref Unsafe.As<T, ushort>(ref from)).AsInt16();
    }

    /// <summary>Loads sixteen samples into 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<short> Load16<T>(ref T at, nint offset = 0)
        where T : unmanaged
    {
        ref T from = ref Unsafe.Add(ref at, offset);
        if (Narrow<T>())
        {
            Vector128<byte> v = Vector128.LoadUnsafe(ref Unsafe.As<T, byte>(ref from));
            return Vector256.Create(Vector128.WidenLower(v), Vector128.WidenUpper(v)).AsInt16();
        }

        return Vector256.LoadUnsafe(ref Unsafe.As<T, ushort>(ref from)).AsInt16();
    }

    /// <summary>Stores eight 16-bit lanes as samples; the values are in the sample range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Store8<T>(ref T at, nint offset, Vector128<short> v)
        where T : unmanaged
    {
        ref T to = ref Unsafe.Add(ref at, offset);
        if (Narrow<T>())
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<T, byte>(ref to), Vector128.Narrow(v.AsUInt16(), v.AsUInt16()).AsUInt64().ToScalar());
        }
        else
        {
            v.AsUInt16().StoreUnsafe(ref Unsafe.As<T, ushort>(ref to));
        }
    }

    /// <summary>Stores the first four of eight 16-bit lanes as samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Store4<T>(ref T at, nint offset, Vector128<short> v)
        where T : unmanaged
    {
        ref T to = ref Unsafe.Add(ref at, offset);
        if (Narrow<T>())
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<T, byte>(ref to), Vector128.Narrow(v.AsUInt16(), v.AsUInt16()).AsUInt32().ToScalar());
        }
        else
        {
            Unsafe.WriteUnaligned(ref Unsafe.As<T, byte>(ref to), v.AsUInt64().ToScalar());
        }
    }

    /// <summary>Stores sixteen 16-bit lanes as samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Store16<T>(ref T at, nint offset, Vector256<short> v)
        where T : unmanaged
    {
        ref T to = ref Unsafe.Add(ref at, offset);
        if (Narrow<T>())
        {
            Vector128.Narrow(v.GetLower().AsUInt16(), v.GetUpper().AsUInt16()).StoreUnsafe(ref Unsafe.As<T, byte>(ref to));
        }
        else
        {
            v.AsUInt16().StoreUnsafe(ref Unsafe.As<T, ushort>(ref to));
        }
    }
}
