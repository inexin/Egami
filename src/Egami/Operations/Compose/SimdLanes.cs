using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Operations.Compose;

/// <summary>
/// Unsigned 32-bit lanes of one width, so one generic kernel runs at every width: <see cref="U1"/> is a single lane (the
/// scalar path, and the one used with intrinsics off), the others wrap Vector128, Vector256 and Vector512. Every operation is
/// exact, so all widths give the same bits.
/// </summary>
internal interface IULanes<TSelf>
    where TSelf : struct, IULanes<TSelf>
{
    static abstract int Count { get; }

    static abstract TSelf Load(ref uint p, nuint i);

    static abstract void Store(TSelf v, ref uint p, nuint i);

    /// <summary>Loads <see cref="Count"/> bytes, one per lane.</summary>
    static abstract TSelf LoadBytes(ref byte p, nuint i);

    static abstract TSelf Splat(uint v);

    static abstract TSelf operator +(TSelf a, TSelf b);

    static abstract TSelf operator -(TSelf a, TSelf b);

    static abstract TSelf operator *(TSelf a, TSelf b);

    static abstract TSelf operator &(TSelf a, TSelf b);

    static abstract TSelf operator |(TSelf a, TSelf b);

    static abstract TSelf operator >>(TSelf a, int shift);

    static abstract TSelf operator <<(TSelf a, int shift);

    static abstract TSelf Min(TSelf a, TSelf b);

    static abstract TSelf Max(TSelf a, TSelf b);

    /// <summary>All ones where a ≤ b.</summary>
    static abstract TSelf LessOrEqual(TSelf a, TSelf b);

    /// <summary>All ones where a = b.</summary>
    static abstract TSelf Equal(TSelf a, TSelf b);

    /// <summary>a where the mask is set, b elsewhere.</summary>
    static abstract TSelf Select(TSelf mask, TSelf a, TSelf b);

    /// <summary>Gets whether every lane is zero.</summary>
    static abstract bool AllZero(TSelf v);
}

/// <summary>Single-precision lanes paired with the unsigned lanes of the same count. Every operation is correctly rounded IEEE.</summary>
internal interface IFLanes<TSelf, TU>
    where TSelf : struct, IFLanes<TSelf, TU>
    where TU : struct, IULanes<TU>
{
    static abstract TSelf From(TU v);

    /// <summary>Rounds lanes in 0..2³¹ to the nearest integer, halves up.</summary>
    static abstract TU Round(TSelf v);

    static abstract TSelf Splat(float v);

    static abstract TSelf operator +(TSelf a, TSelf b);

    static abstract TSelf operator -(TSelf a, TSelf b);

    static abstract TSelf operator *(TSelf a, TSelf b);

    static abstract TSelf operator /(TSelf a, TSelf b);

    static abstract TSelf Min(TSelf a, TSelf b);

    static abstract TSelf Max(TSelf a, TSelf b);

    static abstract TSelf Sqrt(TSelf v);

    static abstract TSelf LessOrEqual(TSelf a, TSelf b);

    static abstract TSelf Less(TSelf a, TSelf b);

    static abstract TSelf Equal(TSelf a, TSelf b);

    static abstract TSelf Select(TSelf mask, TSelf a, TSelf b);
}

internal readonly struct U1(uint v) : IULanes<U1>
{
    internal readonly uint V = v;

    public static int Count => 1;

    public static U1 Load(ref uint p, nuint i) => new(Unsafe.Add(ref p, i));

    public static void Store(U1 v, ref uint p, nuint i) => Unsafe.Add(ref p, i) = v.V;

    public static U1 LoadBytes(ref byte p, nuint i) => new(Unsafe.Add(ref p, i));

    public static U1 Splat(uint v) => new(v);

    public static U1 operator +(U1 a, U1 b) => new(a.V + b.V);

    public static U1 operator -(U1 a, U1 b) => new(a.V - b.V);

    public static U1 operator *(U1 a, U1 b) => new(a.V * b.V);

    public static U1 operator &(U1 a, U1 b) => new(a.V & b.V);

    public static U1 operator |(U1 a, U1 b) => new(a.V | b.V);

    public static U1 operator >>(U1 a, int shift) => new(a.V >> shift);

    public static U1 operator <<(U1 a, int shift) => new(a.V << shift);

    public static U1 Min(U1 a, U1 b) => new(Math.Min(a.V, b.V));

    public static U1 Max(U1 a, U1 b) => new(Math.Max(a.V, b.V));

    public static U1 LessOrEqual(U1 a, U1 b) => new(a.V <= b.V ? uint.MaxValue : 0);

    public static U1 Equal(U1 a, U1 b) => new(a.V == b.V ? uint.MaxValue : 0);

    public static U1 Select(U1 mask, U1 a, U1 b) => new(mask.V != 0 ? a.V : b.V);

    public static bool AllZero(U1 v) => v.V == 0;
}

internal readonly struct F1(float v) : IFLanes<F1, U1>
{
    private static readonly float True = BitConverter.Int32BitsToSingle(-1);

    internal readonly float V = v;

    public static F1 From(U1 v) => new((int)v.V);

    public static U1 Round(F1 v) => new((uint)(int)MathF.Floor(v.V + 0.5f));

    public static F1 Splat(float v) => new(v);

    public static F1 operator +(F1 a, F1 b) => new(a.V + b.V);

    public static F1 operator -(F1 a, F1 b) => new(a.V - b.V);

    public static F1 operator *(F1 a, F1 b) => new(a.V * b.V);

    public static F1 operator /(F1 a, F1 b) => new(a.V / b.V);

    public static F1 Min(F1 a, F1 b) => new(MathF.Min(a.V, b.V));

    public static F1 Max(F1 a, F1 b) => new(MathF.Max(a.V, b.V));

    public static F1 Sqrt(F1 v) => new(MathF.Sqrt(v.V));

    public static F1 LessOrEqual(F1 a, F1 b) => new(a.V <= b.V ? True : 0);

    public static F1 Less(F1 a, F1 b) => new(a.V < b.V ? True : 0);

    public static F1 Equal(F1 a, F1 b) => new(a.V == b.V ? True : 0);

    public static F1 Select(F1 mask, F1 a, F1 b) => new(BitConverter.SingleToInt32Bits(mask.V) != 0 ? a.V : b.V);
}

internal readonly struct U128(Vector128<uint> v) : IULanes<U128>
{
    internal readonly Vector128<uint> V = v;

    public static int Count => 4;

    public static U128 Load(ref uint p, nuint i) => new(Vector128.LoadUnsafe(ref p, i));

    public static void Store(U128 v, ref uint p, nuint i) => v.V.StoreUnsafe(ref p, i);

    public static U128 LoadBytes(ref byte p, nuint i)
    {
        Vector128<byte> bytes = Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, i))).AsByte();
        return new(Sse41.IsSupported ? Sse41.ConvertToVector128Int32(bytes).AsUInt32() : Vector128.WidenLower(Vector128.WidenLower(bytes)));
    }

    public static U128 Splat(uint v) => new(Vector128.Create(v));

    public static U128 operator +(U128 a, U128 b) => new(a.V + b.V);

    public static U128 operator -(U128 a, U128 b) => new(a.V - b.V);

    public static U128 operator *(U128 a, U128 b) => new(a.V * b.V);

    public static U128 operator &(U128 a, U128 b) => new(a.V & b.V);

    public static U128 operator |(U128 a, U128 b) => new(a.V | b.V);

    public static U128 operator >>(U128 a, int shift) => new(a.V >>> shift);

    public static U128 operator <<(U128 a, int shift) => new(a.V << shift);

    public static U128 Min(U128 a, U128 b) => new(Vector128.Min(a.V, b.V));

    public static U128 Max(U128 a, U128 b) => new(Vector128.Max(a.V, b.V));

    public static U128 LessOrEqual(U128 a, U128 b) => new(Vector128.LessThanOrEqual(a.V, b.V));

    public static U128 Equal(U128 a, U128 b) => new(Vector128.Equals(a.V, b.V));

    public static U128 Select(U128 mask, U128 a, U128 b) => new(Vector128.ConditionalSelect(mask.V, a.V, b.V));

    public static bool AllZero(U128 v) => v.V == Vector128<uint>.Zero;
}

internal readonly struct F128(Vector128<float> v) : IFLanes<F128, U128>
{
    internal readonly Vector128<float> V = v;

    public static F128 From(U128 v) => new(Vector128.ConvertToSingle(v.V.AsInt32()));

    public static U128 Round(F128 v) => new(Vector128.ConvertToInt32(Vector128.Floor(v.V + Vector128.Create(0.5f))).AsUInt32());

    public static F128 Splat(float v) => new(Vector128.Create(v));

    public static F128 operator +(F128 a, F128 b) => new(a.V + b.V);

    public static F128 operator -(F128 a, F128 b) => new(a.V - b.V);

    public static F128 operator *(F128 a, F128 b) => new(a.V * b.V);

    public static F128 operator /(F128 a, F128 b) => new(a.V / b.V);

    public static F128 Min(F128 a, F128 b) => new(Vector128.Min(a.V, b.V));

    public static F128 Max(F128 a, F128 b) => new(Vector128.Max(a.V, b.V));

    public static F128 Sqrt(F128 v) => new(Vector128.Sqrt(v.V));

    public static F128 LessOrEqual(F128 a, F128 b) => new(Vector128.LessThanOrEqual(a.V, b.V));

    public static F128 Less(F128 a, F128 b) => new(Vector128.LessThan(a.V, b.V));

    public static F128 Equal(F128 a, F128 b) => new(Vector128.Equals(a.V, b.V));

    public static F128 Select(F128 mask, F128 a, F128 b) => new(Vector128.ConditionalSelect(mask.V, a.V, b.V));
}

internal readonly struct U256(Vector256<uint> v) : IULanes<U256>
{
    internal readonly Vector256<uint> V = v;

    public static int Count => 8;

    public static U256 Load(ref uint p, nuint i) => new(Vector256.LoadUnsafe(ref p, i));

    public static void Store(U256 v, ref uint p, nuint i) => v.V.StoreUnsafe(ref p, i);

    public static U256 LoadBytes(ref byte p, nuint i)
    {
        Vector128<byte> bytes = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i))).AsByte();
        return new(Avx2.IsSupported ? Avx2.ConvertToVector256Int32(bytes).AsUInt32() : Vector256.WidenLower(Vector128.WidenLower(bytes).ToVector256Unsafe()));
    }

    public static U256 Splat(uint v) => new(Vector256.Create(v));

    public static U256 operator +(U256 a, U256 b) => new(a.V + b.V);

    public static U256 operator -(U256 a, U256 b) => new(a.V - b.V);

    public static U256 operator *(U256 a, U256 b) => new(a.V * b.V);

    public static U256 operator &(U256 a, U256 b) => new(a.V & b.V);

    public static U256 operator |(U256 a, U256 b) => new(a.V | b.V);

    public static U256 operator >>(U256 a, int shift) => new(a.V >>> shift);

    public static U256 operator <<(U256 a, int shift) => new(a.V << shift);

    public static U256 Min(U256 a, U256 b) => new(Vector256.Min(a.V, b.V));

    public static U256 Max(U256 a, U256 b) => new(Vector256.Max(a.V, b.V));

    public static U256 LessOrEqual(U256 a, U256 b) => new(Vector256.LessThanOrEqual(a.V, b.V));

    public static U256 Equal(U256 a, U256 b) => new(Vector256.Equals(a.V, b.V));

    public static U256 Select(U256 mask, U256 a, U256 b) => new(Vector256.ConditionalSelect(mask.V, a.V, b.V));

    public static bool AllZero(U256 v) => v.V == Vector256<uint>.Zero;
}

internal readonly struct F256(Vector256<float> v) : IFLanes<F256, U256>
{
    internal readonly Vector256<float> V = v;

    public static F256 From(U256 v) => new(Vector256.ConvertToSingle(v.V.AsInt32()));

    public static U256 Round(F256 v) => new(Vector256.ConvertToInt32(Vector256.Floor(v.V + Vector256.Create(0.5f))).AsUInt32());

    public static F256 Splat(float v) => new(Vector256.Create(v));

    public static F256 operator +(F256 a, F256 b) => new(a.V + b.V);

    public static F256 operator -(F256 a, F256 b) => new(a.V - b.V);

    public static F256 operator *(F256 a, F256 b) => new(a.V * b.V);

    public static F256 operator /(F256 a, F256 b) => new(a.V / b.V);

    public static F256 Min(F256 a, F256 b) => new(Vector256.Min(a.V, b.V));

    public static F256 Max(F256 a, F256 b) => new(Vector256.Max(a.V, b.V));

    public static F256 Sqrt(F256 v) => new(Vector256.Sqrt(v.V));

    public static F256 LessOrEqual(F256 a, F256 b) => new(Vector256.LessThanOrEqual(a.V, b.V));

    public static F256 Less(F256 a, F256 b) => new(Vector256.LessThan(a.V, b.V));

    public static F256 Equal(F256 a, F256 b) => new(Vector256.Equals(a.V, b.V));

    public static F256 Select(F256 mask, F256 a, F256 b) => new(Vector256.ConditionalSelect(mask.V, a.V, b.V));
}

internal readonly struct U512(Vector512<uint> v) : IULanes<U512>
{
    internal readonly Vector512<uint> V = v;

    public static int Count => 16;

    public static U512 Load(ref uint p, nuint i) => new(Vector512.LoadUnsafe(ref p, i));

    public static void Store(U512 v, ref uint p, nuint i) => v.V.StoreUnsafe(ref p, i);

    public static U512 LoadBytes(ref byte p, nuint i)
    {
        Vector128<byte> bytes = Vector128.LoadUnsafe(ref p, i);
        return new(Avx512F.IsSupported
            ? Avx512F.ConvertToVector512Int32(bytes).AsUInt32()
            : Vector512.WidenLower(Vector256.WidenLower(bytes.ToVector256Unsafe()).ToVector512Unsafe()));
    }

    public static U512 Splat(uint v) => new(Vector512.Create(v));

    public static U512 operator +(U512 a, U512 b) => new(a.V + b.V);

    public static U512 operator -(U512 a, U512 b) => new(a.V - b.V);

    public static U512 operator *(U512 a, U512 b) => new(a.V * b.V);

    public static U512 operator &(U512 a, U512 b) => new(a.V & b.V);

    public static U512 operator |(U512 a, U512 b) => new(a.V | b.V);

    public static U512 operator >>(U512 a, int shift) => new(a.V >>> shift);

    public static U512 operator <<(U512 a, int shift) => new(a.V << shift);

    public static U512 Min(U512 a, U512 b) => new(Vector512.Min(a.V, b.V));

    public static U512 Max(U512 a, U512 b) => new(Vector512.Max(a.V, b.V));

    public static U512 LessOrEqual(U512 a, U512 b) => new(Vector512.LessThanOrEqual(a.V, b.V));

    public static U512 Equal(U512 a, U512 b) => new(Vector512.Equals(a.V, b.V));

    public static U512 Select(U512 mask, U512 a, U512 b) => new(Vector512.ConditionalSelect(mask.V, a.V, b.V));

    public static bool AllZero(U512 v) => v.V == Vector512<uint>.Zero;
}

internal readonly struct F512(Vector512<float> v) : IFLanes<F512, U512>
{
    internal readonly Vector512<float> V = v;

    public static F512 From(U512 v) => new(Vector512.ConvertToSingle(v.V.AsInt32()));

    public static U512 Round(F512 v) => new(Vector512.ConvertToInt32(Vector512.Floor(v.V + Vector512.Create(0.5f))).AsUInt32());

    public static F512 Splat(float v) => new(Vector512.Create(v));

    public static F512 operator +(F512 a, F512 b) => new(a.V + b.V);

    public static F512 operator -(F512 a, F512 b) => new(a.V - b.V);

    public static F512 operator *(F512 a, F512 b) => new(a.V * b.V);

    public static F512 operator /(F512 a, F512 b) => new(a.V / b.V);

    public static F512 Min(F512 a, F512 b) => new(Vector512.Min(a.V, b.V));

    public static F512 Max(F512 a, F512 b) => new(Vector512.Max(a.V, b.V));

    public static F512 Sqrt(F512 v) => new(Vector512.Sqrt(v.V));

    public static F512 LessOrEqual(F512 a, F512 b) => new(Vector512.LessThanOrEqual(a.V, b.V));

    public static F512 Less(F512 a, F512 b) => new(Vector512.LessThan(a.V, b.V));

    public static F512 Equal(F512 a, F512 b) => new(Vector512.Equals(a.V, b.V));

    public static F512 Select(F512 mask, F512 a, F512 b) => new(Vector512.ConditionalSelect(mask.V, a.V, b.V));
}
