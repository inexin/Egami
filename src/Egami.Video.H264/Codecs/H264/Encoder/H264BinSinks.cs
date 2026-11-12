using System.Numerics;
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

    /// <summary>Gets the log the moves are noted in.</summary>
    public readonly CabacUndoLog Log => log;

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

/// <summary>
/// The states a <see cref="CabacUndoSink"/> moved, kept by chunks of 8 (H.264 Encoder Speed Build Plan, task 19): the first
/// move in a chunk saves the chunk's 8 states, one bit a chunk marks it saved, so a residual counter notes a block's context
/// ranges once and <see cref="Restore"/> writes back a few words.
/// </summary>
internal sealed class CabacUndoLog
{
    private readonly ulong[] _saved;
    private byte[] _states = [];
    private ulong _noted;

    /// <summary>Initialises a log for up to 512 states; the states it notes must be an array of a multiple of 8.</summary>
    public CabacUndoLog(int size)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 512);
        _saved = new ulong[64];
    }

    /// <summary>Starts noting moves of <paramref name="states"/>, whose length is a multiple of 8.</summary>
    public void Begin(byte[] states)
    {
        _states = states;
        _noted = 0;
    }

    /// <summary>Notes the state about to move, unless its chunk was saved already.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Note(ref byte state)
    {
        ref byte origin = ref MemoryMarshal.GetArrayDataReference(_states);
        int c = (int)Unsafe.ByteOffset(ref origin, ref state) >> 3;
        if ((_noted & (1UL << c)) == 0)
        {
            _noted |= 1UL << c;
            Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_saved), c) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref origin, c << 3));
        }
    }

    /// <summary>Notes the states <paramref name="first"/> to <paramref name="last"/> (inclusive) before they move.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void NoteRange(int first, int last)
    {
        int lo = first >> 3, hi = last >> 3;
        ulong fresh = (((2UL << hi) - 1) ^ ((1UL << lo) - 1)) & ~_noted;
        if (fresh == 0)
        {
            return;
        }

        _noted |= fresh;
        ref byte states = ref MemoryMarshal.GetArrayDataReference(_states);
        ref ulong saved = ref MemoryMarshal.GetArrayDataReference(_saved);
        do
        {
            int c = BitOperations.TrailingZeroCount(fresh);
            Unsafe.Add(ref saved, c) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref states, c << 3));
            fresh &= fresh - 1;
        }
        while (fresh != 0);
    }

    /// <summary>Puts every moved state back.</summary>
    public void Restore()
    {
        ulong noted = _noted;
        ref byte states = ref MemoryMarshal.GetArrayDataReference(_states);
        while (noted != 0)
        {
            int c = BitOperations.TrailingZeroCount(noted);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref states, c << 3), _saved[c]);
            noted &= noted - 1;
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
