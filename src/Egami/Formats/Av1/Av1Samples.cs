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
    /// <summary>
    /// The bytes past the end of an 8-bit plane, and of the sample buffers inter prediction reads, that vector loads may read
    /// without using.
    /// </summary>
    internal const int Padding = 32;

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

    /// <summary>
    /// Packs eight 32-bit lanes, each 0 to 65535, into 16-bit lanes: one saturating pack where SSE4.1 has it, which keeps such
    /// values exactly, rather than the masks a truncating narrow costs.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<ushort> Pack(Vector256<int> v) =>
        Sse41.IsSupported ? Sse41.PackUnsignedSaturate(v.GetLower(), v.GetUpper()) : Vector128.Narrow(v.GetLower().AsUInt32(), v.GetUpper().AsUInt32());

    /// <summary>Packs four 32-bit lanes, each 0 to 65535, into the low four 16-bit lanes, as <see cref="Pack(Vector256{int})"/> does.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<ushort> Pack(Vector128<int> v) =>
        Sse41.IsSupported ? Sse41.PackUnsignedSaturate(v, v) : Vector128.Narrow(v.AsUInt32(), v.AsUInt32());

    /// <summary>Packs 16-bit lanes, each 0 to 255, into bytes (twice over), with one saturating pack where SSE2 has it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector128<byte> PackBytes(Vector128<ushort> v) =>
        Sse2.IsSupported ? Sse2.PackUnsignedSaturate(v.AsInt16(), v.AsInt16()) : Vector128.Narrow(v, v);

    /// <summary>Stores the low four 16-bit lanes, already in the sample range, as samples at <paramref name="at"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Store4<T>(ref T p, nint at, Vector128<ushort> value)
        where T : unmanaged
    {
        ref byte b = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref p, at));
        if (typeof(T) == typeof(byte))
        {
            Unsafe.WriteUnaligned(ref b, PackBytes(value).AsUInt32().ToScalar());
        }
        else
        {
            Unsafe.WriteUnaligned(ref b, value.AsUInt64().ToScalar());
        }
    }

    /// <summary>Stores eight 16-bit lanes, already in the sample range, as samples at <paramref name="at"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Store8<T>(ref T p, nint at, Vector128<ushort> value)
        where T : unmanaged
    {
        ref byte b = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref p, at));
        if (typeof(T) == typeof(byte))
        {
            Unsafe.WriteUnaligned(ref b, PackBytes(value).AsUInt64().ToScalar());
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
            int i = 0;
            if (Avx2.IsSupported)
            {
                ref byte from = ref bytes[0];
                ref ushort into = ref to[0];
                for (; i + 16 <= bytes.Length; i += 16)
                {
                    Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref from, (nuint)i)).AsUInt16().StoreUnsafe(ref into, (nuint)i);
                }
            }

            for (; i < bytes.Length; i++)
            {
                to[i] = bytes[i];
            }

            wide[plane] = to;
        }

        return wide;
    }
}
