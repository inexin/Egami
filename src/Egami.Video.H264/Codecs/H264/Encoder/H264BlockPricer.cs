using System.Diagnostics;
using System.Runtime.CompilerServices;

using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// An estimate of the CABAC rate of one residual block for RDOQ (H.264 Encoder Speed Build Plan, tasks 3 and 4), priced from the
/// slice's current context states without copying or updating them, and re-priced in part as RDOQ lowers levels from the last
/// one back:
/// <list type="bullet">
/// <item>each significance and last flag costs what it would in the slice's state. A 4x4 block codes each of those contexts at
/// most once, so its significance map is priced exactly; an 8x8 block shares contexts between positions, whose adaptation within
/// the block is left out;</item>
/// <item>each level's bins cost what they would in the slice's states, its unary prefix adapting as it is coded (one table step);
/// the adaptation from one level to the next is left out.</item>
/// </list>
/// So a level's cost depends only on its value and on one of eight coding states (how many levels coded before it were 1, up to
/// 3, while none was greater than 1; else how many were greater than 1, up to the last prefix context). RDOQ visits the levels
/// in coding order, so those before the tried one are final and those after it are still as quantized: the cost of the levels
/// after it from each state is worked out once per block (on demand, then remembered), so lowering a level re-prices that level
/// and looks the rest up. The significance map changes only when a level drops to 0, by the bins that adds or takes away. The
/// final bitstream is still coded exactly by the CABAC writer.
/// </summary>
internal sealed class H264BlockPricer
{
    private const int MaxOnes = 14;
    private const int States = 8;

    // The cost of the bins after the first of a unary prefix of 1 to MaxOnes (coeff_abs_level_minus1, capped), all in one
    // context from a state, by (state * MaxOnes) + prefix - 1.
    private static readonly int[] PrefixCost = new int[128 * MaxOnes];

    // By coding state: the context of the first coeff_abs_level_minus1 bin, that of the prefix (both from the block's base), and
    // the state after a level of 1 (first 8) or greater than 1 (last 8), for blocks with 4 and with 3 prefix contexts.
    private static readonly byte[] FirstInc = [1, 2, 3, 4, 0, 0, 0, 0];
    private static readonly byte[] PrefixInc = [5, 5, 5, 5, 6, 7, 8, 9];
    private static readonly byte[] Next4 = [1, 2, 3, 3, 4, 5, 6, 7, 4, 4, 4, 4, 5, 6, 7, 7];
    private static readonly byte[] Next3 = [1, 2, 3, 3, 4, 5, 6, 7, 4, 4, 4, 4, 5, 6, 6, 6];

    private readonly byte[] _abs = new byte[10];

    // The cost of the levels at rows r down to 0 coded from each state, by (r * States) + state (-1 until worked out).
    private readonly long[] _tail = new long[64 * States];
    private readonly byte[] _walk = new byte[64];
    private readonly long[] _walkCost = new long[64];

    private byte[] _ctx = [];
    private int[] _levels = [];
    private int[] _rows = [];
    private byte[] _next = Next4;
    private int _at, _max, _cat, _sigBase, _lastBase;
    private int _state, _row, _last, _tryLast;
    private long _prefix, _sig, _trySig;

    static H264BlockPricer()
    {
        for (int s = 0; s < 128; s++)
        {
            byte state = (byte)s;
            int cost = 0;
            for (int m = 1; m <= MaxOnes; m++)
            {
                // coeff_abs_level_minus1 = m: its first bin is coded in its own context, then m - 1 ones and a 0 below the cap.
                PrefixCost[(s * MaxOnes) + m - 1] = m < MaxOnes ? cost + CabacCost.Of(state, 0) : cost;
                cost += CabacCost.Update(ref state, 1);
            }
        }
    }

    /// <summary>
    /// Starts pricing the block of <paramref name="max"/> levels at <paramref name="at"/> (category <paramref name="cat"/>) from
    /// the states in <paramref name="ctx"/>, which are only read; <paramref name="rows"/> holds the scan positions of its
    /// <paramref name="count"/> non-zero levels, lowest first. Returns its rate in 1/32768 bit with one bit standing for the
    /// coded_block_flag, or 0 when it has no levels.
    /// </summary>
    public long Begin(byte[] ctx, int cat, int max, int[] levels, int at, int[] rows, int count)
    {
        (_ctx, _levels, _rows, _at, _max, _cat) = (ctx, levels, rows, at, max, cat);
        (_sigBase, _lastBase, int absBase) = H264MbSyntax.ResidualContexts(cat);
        _next = cat == 3 ? Next3 : Next4;
        ctx.AsSpan(absBase, 10).CopyTo(_abs);
        (_state, _prefix) = (0, 0);
        if (count == 0)
        {
            _last = -1;
            return 0;
        }

        _tail.AsSpan(0, count * States).Fill(-1);
        _row = count - 1;
        _last = rows[_row];
        _sig = SigCost(_last);
        return _sig + Tail(_row, 0) + CabacCost.Bit;
    }

    /// <summary>
    /// The rate with the level at scan position <paramref name="k"/> changed from <paramref name="old"/> to the value now in the
    /// levels, every level above it being final (<see cref="Advance"/>); 0 when the block has no levels left.
    /// </summary>
    public long Price(int k, int old)
    {
        Debug.Assert(_rows[_row] == k && old != 0, "RDOQ tries the quantized levels from the last one back.");
        int now = _levels[_at + k];
        _trySig = _sig;
        _tryLast = _last;
        long cost = 0;
        int state = _state;
        if (now != 0)
        {
            cost = LevelCost(now, state);
            state = Next(now, state);
        }
        else if (k < _last)
        {
            // A level below the last one: its significance bin turns 0 and its last flag goes.
            (int sigCtx, int lastCtx) = Contexts(k);
            byte[] ctx = _ctx;
            _trySig += CabacCost.Of(ctx[sigCtx], 0) - CabacCost.Of(ctx[sigCtx], 1) - CabacCost.Of(ctx[lastCtx], 0);
        }
        else
        {
            // The last level: the map ends at the next level down, dropping the bins above it.
            int last = k - 1;
            while (last >= 0 && _levels[_at + last] == 0)
            {
                last--;
            }

            if (last < 0)
            {
                return 0;
            }

            byte[] ctx = _ctx;
            long sig = _sig;
            if (k <= _max - 2)
            {
                (int sigCtx, int lastCtx) = Contexts(k);
                sig -= CabacCost.Of(ctx[sigCtx], 1) + CabacCost.Of(ctx[lastCtx], 1);
            }

            for (int i = last + 1; i < k; i++)
            {
                sig -= CabacCost.Of(ctx[Contexts(i).Sig], 0);
            }

            int lastAt = Contexts(last).Last;
            _trySig = sig + CabacCost.Of(ctx[lastAt], 1) - CabacCost.Of(ctx[lastAt], 0);
            _tryLast = last;
        }

        return _trySig + _prefix + cost + (_row > 0 ? Tail(_row - 1, state) : 0) + CabacCost.Bit;
    }

    /// <summary>Keeps the change of the last <see cref="Price"/> when RDOQ took it.</summary>
    public void Accept(bool take)
    {
        _sig = take ? _trySig : _sig;
        _last = take ? _tryLast : _last;
    }

    /// <summary>Moves the final level at scan position <paramref name="k"/> into the levels coded before the next one tried.</summary>
    public void Advance(int k)
    {
        Debug.Assert(_rows[_row] == k, "RDOQ tries the quantized levels from the last one back.");
        int level = _levels[_at + k];
        int coded = (level | -level) >> 31;
        _prefix += LevelCost(level, _state) & coded;
        _state = coded != 0 ? Next(level, _state) : _state;
        _row--;
    }

    // The coding state after a level, looked up rather than branched on.
    private int Next(int level, int state) => _next[(((1 - Math.Abs(level)) >>> 31) << 3) + state];

    private (int Sig, int Last) Contexts(int i) =>
        _cat == 5 ? (_sigBase + H264Tables.Significant8x8[i], _lastBase + H264Tables.Last8x8[i]) : (_sigBase + i, _lastBase + i);

    // The levels as quantized at rows row down to 0, coded from a state (states 0 to 3 count the levels of 1 while none was
    // greater than 1, states 4 on the levels greater than 1, each up to where the contexts stop changing); works out and
    // remembers what it has not yet.
    private long Tail(int row, int state)
    {
        long[] tail = _tail;
        int[] levels = _levels, rows = _rows;
        int depth = 0;
        while (row >= 0 && tail[(row * States) + state] < 0)
        {
            int level = levels[_at + rows[row]];
            _walk[depth] = (byte)state;
            _walkCost[depth++] = LevelCost(level, state);
            state = Next(level, state);
            row--;
        }

        long cost = row >= 0 ? tail[(row * States) + state] : 0;
        while (depth > 0)
        {
            depth--;
            row++;
            cost += _walkCost[depth];
            tail[(row * States) + _walk[depth]] = cost;
        }

        return cost;
    }

    // coeff_abs_level_minus1 and the sign of one non-zero level (7.3.5.3.3, 9.3.3.1.3) in the slice's states, without branching
    // on the level's size (which RDOQ's levels make unpredictable).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LevelCost(int level, int state)
    {
        byte[] states = _abs;
        int abs = Math.Abs(level);
        int greater = (1 - abs) >>> 31;
        int cost = CabacCost.Of(states[FirstInc[state]], greater) + CabacCost.Bit;
        int prefix = Math.Clamp(abs - 1, 1, MaxOnes);
        cost += PrefixCost[(states[PrefixInc[state]] * MaxOnes) + prefix - 1] & -greater;
        if (abs > MaxOnes)
        {
            // Exp-Golomb suffix, k = 0: k ones, a zero and k bits, all bypass.
            int v = abs - 15, k = 0;
            while (v >= 1 << k)
            {
                v -= 1 << k;
                k++;
            }

            cost += ((2 * k) + 1) * CabacCost.Bit;
        }

        return cost;
    }

    // significant_coeff_flag and last_significant_coeff_flag up to the last level, without branching on each level.
    private long SigCost(int last)
    {
        int[] levels = _levels;
        byte[] ctx = _ctx;
        long cost = 0;
        for (int i = 0; i < last; i++)
        {
            int level = levels[_at + i];
            int coded = (level | -level) >>> 31;
            (int sigCtx, int lastCtx) = Contexts(i);
            cost += CabacCost.Of(ctx[sigCtx], coded) + (CabacCost.Of(ctx[lastCtx], 0) & -coded);
        }

        if (last < _max - 1)
        {
            (int sigCtx, int lastCtx) = Contexts(last);
            cost += CabacCost.Of(ctx[sigCtx], 1) + CabacCost.Of(ctx[lastCtx], 1);
        }

        return cost;
    }
}
