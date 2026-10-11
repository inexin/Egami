using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Egami.Operations.Compose;

/// <summary>
/// Unsigned 16-bit lanes holding the channels of 8-bit pixels with alpha last, four lanes a pixel, so one generic integer kernel
/// runs at every width. Products of two channels fit a lane, and sums that wrap still give the right value when the true result
/// fits, which the blend formulas are written to ensure. Every operation is exact, so all widths give the same bits.
/// </summary>
internal interface IChannelLanes<TSelf>
    where TSelf : struct, IChannelLanes<TSelf>
{
    /// <summary>Gets the pixels a vector holds.</summary>
    static abstract int Pixels { get; }

    static abstract TSelf Load(ref uint p, nuint i);

    /// <summary>Stores lanes that are all 255 or less.</summary>
    static abstract void Store(TSelf v, ref uint p, nuint i);

    /// <summary>Loads one coverage byte per pixel into all four of its lanes.</summary>
    static abstract TSelf LoadCoverage(ref byte p, nuint i);

    static abstract TSelf Splat(ushort v);

    /// <summary>Gets lanes that are all ones in each pixel's alpha lane.</summary>
    static abstract TSelf AlphaLanes { get; }

    static abstract TSelf operator +(TSelf a, TSelf b);

    static abstract TSelf operator -(TSelf a, TSelf b);

    static abstract TSelf operator *(TSelf a, TSelf b);

    static abstract TSelf operator >>(TSelf a, int shift);

    static abstract TSelf operator <<(TSelf a, int shift);

    static abstract TSelf Min(TSelf a, TSelf b);

    static abstract TSelf Max(TSelf a, TSelf b);

    static abstract TSelf LessOrEqual(TSelf a, TSelf b);

    static abstract TSelf Select(TSelf mask, TSelf a, TSelf b);

    /// <summary>Copies each pixel's alpha lane into its other three lanes.</summary>
    static abstract TSelf BroadcastAlpha(TSelf v);
}

internal readonly struct C128(Vector128<ushort> v) : IChannelLanes<C128>
{
    private static readonly Vector128<byte> Spread = Vector128.Create((byte)0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0);

    internal readonly Vector128<ushort> V = v;

    public static int Pixels => 2;

    public static C128 AlphaLanes => new(Vector128.Create((ushort)0, 0, 0, 0xFFFF, 0, 0, 0, 0xFFFF));

    public static C128 Load(ref uint p, nuint i)
    {
        Vector128<byte> bytes = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref p, i)))).AsByte();
        return new(Vector128.WidenLower(bytes));
    }

    public static void Store(C128 v, ref uint p, nuint i)
    {
        Unsafe.WriteUnaligned(ref Unsafe.As<uint, byte>(ref Unsafe.Add(ref p, i)), Vector128.Narrow(v.V, v.V).AsUInt64().ToScalar());
    }

    public static C128 LoadCoverage(ref byte p, nuint i)
    {
        Vector128<byte> bytes = Vector128.CreateScalar(Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref p, i))).AsByte();
        return new(Vector128.WidenLower(Vector128.Shuffle(bytes, Spread)));
    }

    public static C128 Splat(ushort v) => new(Vector128.Create(v));

    public static C128 operator +(C128 a, C128 b) => new(a.V + b.V);

    public static C128 operator -(C128 a, C128 b) => new(a.V - b.V);

    public static C128 operator *(C128 a, C128 b) => new(a.V * b.V);

    public static C128 operator >>(C128 a, int shift) => new(a.V >>> shift);

    public static C128 operator <<(C128 a, int shift) => new(a.V << shift);

    public static C128 Min(C128 a, C128 b) => new(Vector128.Min(a.V, b.V));

    public static C128 Max(C128 a, C128 b) => new(Vector128.Max(a.V, b.V));

    public static C128 LessOrEqual(C128 a, C128 b) => new(Vector128.LessThanOrEqual(a.V, b.V));

    public static C128 Select(C128 mask, C128 a, C128 b) => new(Vector128.ConditionalSelect(mask.V, a.V, b.V));

    public static C128 BroadcastAlpha(C128 v)
    {
        Vector128<ulong> a = v.V.AsUInt64() >>> 48;
        return new((a | (a << 16) | (a << 32) | (a << 48)).AsUInt16());
    }
}

internal readonly struct C256(Vector256<ushort> v) : IChannelLanes<C256>
{
    private static readonly Vector128<byte> Spread = Vector128.Create((byte)0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3);

    internal readonly Vector256<ushort> V = v;

    public static int Pixels => 4;

    public static C256 AlphaLanes => new(Vector256.Create((ushort)0, 0, 0, 0xFFFF, 0, 0, 0, 0xFFFF, 0, 0, 0, 0xFFFF, 0, 0, 0, 0xFFFF));

    public static C256 Load(ref uint p, nuint i) => new(Vector256.WidenLower(Vector128.LoadUnsafe(ref p, i).AsByte().ToVector256Unsafe()));

    public static void Store(C256 v, ref uint p, nuint i)
    {
        Vector256<byte> narrow = Vector256.Narrow(v.V, v.V);
        narrow.GetLower().AsUInt32().StoreUnsafe(ref p, i);
    }

    public static C256 LoadCoverage(ref byte p, nuint i)
    {
        Vector128<byte> bytes = Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref p, i))).AsByte();
        return new(Vector256.WidenLower(Vector128.Shuffle(bytes, Spread).ToVector256Unsafe()));
    }

    public static C256 Splat(ushort v) => new(Vector256.Create(v));

    public static C256 operator +(C256 a, C256 b) => new(a.V + b.V);

    public static C256 operator -(C256 a, C256 b) => new(a.V - b.V);

    public static C256 operator *(C256 a, C256 b) => new(a.V * b.V);

    public static C256 operator >>(C256 a, int shift) => new(a.V >>> shift);

    public static C256 operator <<(C256 a, int shift) => new(a.V << shift);

    public static C256 Min(C256 a, C256 b) => new(Vector256.Min(a.V, b.V));

    public static C256 Max(C256 a, C256 b) => new(Vector256.Max(a.V, b.V));

    public static C256 LessOrEqual(C256 a, C256 b) => new(Vector256.LessThanOrEqual(a.V, b.V));

    public static C256 Select(C256 mask, C256 a, C256 b) => new(Vector256.ConditionalSelect(mask.V, a.V, b.V));

    public static C256 BroadcastAlpha(C256 v)
    {
        Vector256<ulong> a = v.V.AsUInt64() >>> 48;
        return new((a | (a << 16) | (a << 32) | (a << 48)).AsUInt16());
    }
}

internal readonly struct C512(Vector512<ushort> v) : IChannelLanes<C512>
{
    private static readonly Vector128<byte> Spread = Vector128.Create((byte)0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3);

    internal readonly Vector512<ushort> V = v;

    public static int Pixels => 8;

    public static C512 AlphaLanes => new(Vector512.Create(C256.AlphaLanes.V, C256.AlphaLanes.V));

    public static C512 Load(ref uint p, nuint i) => new(Vector512.WidenLower(Vector256.LoadUnsafe(ref p, i).AsByte().ToVector512Unsafe()));

    public static void Store(C512 v, ref uint p, nuint i)
    {
        Vector512<byte> narrow = Vector512.Narrow(v.V, v.V);
        narrow.GetLower().AsUInt32().StoreUnsafe(ref p, i);
    }

    public static C512 LoadCoverage(ref byte p, nuint i)
    {
        Vector128<byte> bytes = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, i))).AsByte();
        Vector128<byte> low = Vector128.Shuffle(bytes, Spread);
        Vector128<byte> high = Vector128.Shuffle(bytes, Spread + Vector128.Create((byte)4));
        return new(Vector512.WidenLower(Vector256.Create(low, high).ToVector512Unsafe()));
    }

    public static C512 Splat(ushort v) => new(Vector512.Create(v));

    public static C512 operator +(C512 a, C512 b) => new(a.V + b.V);

    public static C512 operator -(C512 a, C512 b) => new(a.V - b.V);

    public static C512 operator *(C512 a, C512 b) => new(a.V * b.V);

    public static C512 operator >>(C512 a, int shift) => new(a.V >>> shift);

    public static C512 operator <<(C512 a, int shift) => new(a.V << shift);

    public static C512 Min(C512 a, C512 b) => new(Vector512.Min(a.V, b.V));

    public static C512 Max(C512 a, C512 b) => new(Vector512.Max(a.V, b.V));

    public static C512 LessOrEqual(C512 a, C512 b) => new(Vector512.LessThanOrEqual(a.V, b.V));

    public static C512 Select(C512 mask, C512 a, C512 b) => new(Vector512.ConditionalSelect(mask.V, a.V, b.V));

    public static C512 BroadcastAlpha(C512 v)
    {
        Vector512<ulong> a = v.V.AsUInt64() >>> 48;
        return new((a | (a << 16) | (a << 32) | (a << 48)).AsUInt16());
    }
}
