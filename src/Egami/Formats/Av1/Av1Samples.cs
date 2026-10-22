using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Av1;

/// <summary>
/// Reads and writes the samples of a plane whose type is a type parameter: <see cref="byte"/> for 8-bit frames, <see cref="ushort"/>
/// for deeper ones. Each method compiles to a plain load or store once the JIT specializes it.
/// </summary>
internal static class Av1Samples
{
    /// <summary>Gets a sample's value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Get<T>(T sample)
        where T : unmanaged => typeof(T) == typeof(byte) ? Unsafe.As<T, byte>(ref sample) : Unsafe.As<T, ushort>(ref sample);

    /// <summary>Makes a sample of a value already in the sample range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static T From<T>(int value)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            byte b = (byte)value;
            return Unsafe.As<byte, T>(ref b);
        }

        ushort u = (ushort)value;
        return Unsafe.As<ushort, T>(ref u);
    }

    /// <summary>Gets whether <typeparamref name="T"/> is the 8-bit sample type.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsByte<T>()
        where T : unmanaged => typeof(T) == typeof(byte);

    /// <summary>Loads four samples at <paramref name="at"/> into the low 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> Load4<T>(ref T p, nint at)
        where T : unmanaged
    {
        ref byte b = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref p, at));
        return typeof(T) == typeof(byte)
            ? Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<uint>(ref b)).AsByte()).AsInt16()
            : Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref b)).AsInt16();
    }

    /// <summary>Loads eight samples at <paramref name="at"/> as 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<short> Load8<T>(ref T p, nint at)
        where T : unmanaged
    {
        ref byte b = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref p, at));
        return typeof(T) == typeof(byte)
            ? Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref b)).AsByte()).AsInt16()
            : Vector128.LoadUnsafe(ref Unsafe.As<byte, short>(ref b));
    }

    /// <summary>Loads sixteen samples at <paramref name="at"/> as 16-bit lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<short> Load16<T>(ref T p, nint at)
        where T : unmanaged
    {
        ref byte b = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref p, at));
        return typeof(T) == typeof(byte)
            ? Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref b))
            : Vector256.LoadUnsafe(ref Unsafe.As<byte, short>(ref b));
    }

    /// <summary>Stores eight 16-bit lanes, already in the sample range, as samples at <paramref name="at"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Store8<T>(ref T p, nint at, Vector128<ushort> value)
        where T : unmanaged
    {
        ref byte b = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref p, at));
        if (typeof(T) == typeof(byte))
        {
            Unsafe.WriteUnaligned(ref b, Vector128.Narrow(value, value).AsUInt64().ToScalar());
        }
        else
        {
            value.StoreUnsafe(ref Unsafe.As<byte, ushort>(ref b));
        }
    }

    /// <summary>Widens planes of either sample type to 16 bits; 16-bit planes are returned as they are.</summary>
    internal static ushort[][] Widen(Array[] planes)
    {
        if (planes is ushort[][] words)
        {
            return words;
        }

        ushort[][] wide = new ushort[planes.Length][];
        for (int plane = 0; plane < planes.Length; plane++)
        {
            byte[] bytes = (byte[])planes[plane];
            ushort[] to = new ushort[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                to[i] = bytes[i];
            }

            wide[plane] = to;
        }

        return wide;
    }
}
