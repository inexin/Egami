using System.Runtime.CompilerServices;

using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>Where CABAC bins go: the arithmetic coder, or a counter of their cost for rate decisions.</summary>
internal interface ICabacSink
{
    void Encode(ref byte state, int bin);

    void Bypass(int bin);

    void Terminate(int bin);
}

/// <summary>Where CAVLC bits go: the bit writer, or a counter.</summary>
internal interface IBitSink
{
    void U(int n, uint value);
}

/// <summary>Bins into the CABAC encoder.</summary>
internal readonly struct CabacWriteSink(CabacEncoder encoder) : ICabacSink
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encode(ref byte state, int bin) => encoder.Encode(ref state, bin);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Bypass(int bin) => encoder.Bypass(bin);

    public void Terminate(int bin) => encoder.Terminate(bin);
}

/// <summary>Counts what bins would cost, in 1/32768 bit (<see cref="CabacCost"/>), moving the states as coding would.</summary>
internal struct CabacCountSink : ICabacSink
{
    /// <summary>The cost so far.</summary>
    public long Cost;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encode(ref byte state, int bin) => Cost += CabacCost.Update(ref state, bin);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Bypass(int bin) => Cost += CabacCost.Bit;

    // A terminating 0 costs next to nothing (its range is 2 of 510); a 1 ends the slice.
    public void Terminate(int bin) => Cost += bin == 0 ? 0 : 7 * CabacCost.Bit;
}

/// <summary>Bits into an RBSP writer.</summary>
internal readonly struct BitWriteSink(NalBitWriter writer) : IBitSink
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void U(int n, uint value) => writer.U(n, value);
}

/// <summary>Counts bits.</summary>
internal struct BitCountSink : IBitSink
{
    /// <summary>The bits so far.</summary>
    public long Bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void U(int n, uint value) => Bits += n;
}

/// <summary>Exp-Golomb codes on any bit sink (9.1).</summary>
internal static class BitSinkCodes
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Ue<TSink>(ref TSink sink, uint value)
        where TSink : struct, IBitSink
    {
        uint code = value + 1;
        int length = 32 - System.Numerics.BitOperations.LeadingZeroCount(code);
        sink.U(length - 1, 0);
        sink.U(length, code);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Se<TSink>(ref TSink sink, int value)
        where TSink : struct, IBitSink => Ue(ref sink, value > 0 ? (uint)((2 * value) - 1) : (uint)(-2 * value));

    /// <summary>The length of ue(v).</summary>
    public static int UeBits(uint value) => (2 * (32 - System.Numerics.BitOperations.LeadingZeroCount(value + 1))) - 1;
}
