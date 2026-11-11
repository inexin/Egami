using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

/// <summary>
/// Counts what bins would cost, in 1/32768 bit, moving the slice's states as coding would and noting the first old value of each
/// state it moves, so <see cref="CabacUndoLog.Restore"/> puts back only those (H.264 Encoder Speed Build Plan, task 5): rate
/// decisions count a candidate exactly without copying all the states in and out.
/// </summary>
internal struct CabacUndoSink(CabacUndoLog log) : ICabacSink
{
    /// <summary>The cost so far.</summary>
    public long Cost;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Encode(ref byte state, int bin)
    {
        log.Note(ref state);
        Cost += CabacCost.Update(ref state, bin);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Bypass(int bin) => Cost += CabacCost.Bit;

    // A terminating 0 costs next to nothing (its range is 2 of 510); a 1 ends the slice.
    public void Terminate(int bin) => Cost += bin == 0 ? 0 : 7 * CabacCost.Bit;
}

/// <summary>The states a <see cref="CabacUndoSink"/> moved, each with its value before the first move.</summary>
internal sealed class CabacUndoLog
{
    private readonly int[] _stamp;
    private readonly int[] _index;
    private readonly byte[] _old;
    private byte[] _states = [];
    private int _epoch, _count;

    public CabacUndoLog(int size) => (_stamp, _index, _old) = (new int[size], new int[size], new byte[size]);

    /// <summary>Starts noting moves of <paramref name="states"/>.</summary>
    public void Begin(byte[] states)
    {
        _states = states;
        _count = 0;
        if (++_epoch == int.MaxValue)
        {
            Array.Clear(_stamp);
            _epoch = 1;
        }
    }

    /// <summary>Notes the state about to move, unless it moved already.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Note(ref byte state)
    {
        int i = (int)Unsafe.ByteOffset(ref MemoryMarshal.GetArrayDataReference(_states), ref state);
        if (_stamp[i] != _epoch)
        {
            _stamp[i] = _epoch;
            _index[_count] = i;
            _old[_count++] = state;
        }
    }

    /// <summary>Puts every moved state back.</summary>
    public void Restore()
    {
        byte[] states = _states;
        for (int k = 0; k < _count; k++)
        {
            states[_index[k]] = _old[k];
        }
    }
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
