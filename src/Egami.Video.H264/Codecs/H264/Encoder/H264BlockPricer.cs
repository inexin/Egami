using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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

    // The cost of the bins after the first of the unary prefix of a level of 2 to 15 (coeff_abs_level_minus1 of 1 to MaxOnes,
    // capped), all in one context from a state, by (state * 16) + level; 0 for levels 0 and 1, which have none.
    private static readonly int[] PrefixCost = new int[128 * 16];

    // By coding state: the context of the first coeff_abs_level_minus1 bin, that of the prefix (both from the block's base), and
    // the state after a level of 1 (first 8) or greater than 1 (last 8), for blocks with 4 and with 3 prefix contexts.
    private static readonly byte[] FirstInc = [1, 2, 3, 4, 0, 0, 0, 0];
    private static readonly byte[] PrefixInc = [5, 5, 5, 5, 6, 7, 8, 9];
    private static readonly byte[] Next4 = [1, 2, 3, 3, 4, 5, 6, 7, 4, 4, 4, 4, 5, 6, 7, 7];
    private static readonly byte[] Next3 = [1, 2, 3, 3, 4, 5, 6, 7, 4, 4, 4, 4, 5, 6, 6, 6];

    private const int Categories = 6;
    private const int Positions = 64;

    private readonly byte[] _abs = new byte[10];

    // The bin costs of the significance map and the levels in the states the tables were made from, by category, made the first
    // time a block of that category is priced after Invalidate (the states do not change while a macroblock is decided):
    // the cost of significant_coeff_flag 0 summed over the positions before each one; what coding a position as a non-zero level
    // other than the last adds to that (flag 1 and last flag 0, less flag 0); the significance and last flags (both 1) of a last
    // level; the change from a level's last flag 0 to 1; and each level's coeff_abs_level_minus1 and sign by size (capped) and
    // coding state, by (size * States) + state, so the eight states of a size are one vector.
    private readonly long[] _zeroSum = new long[Categories * (Positions + 1)];
    private readonly int[] _coded = new int[Categories * Positions];
    private readonly int[] _ends = new int[Categories * Positions];
    private readonly int[] _lastFlip = new int[Categories * Positions];
    private readonly int[] _levelCosts = new int[Categories * States * 16];
    private byte[]? _tablesOf;
    private int _made;

    // The context states each category's tables were last made from (significance, last, level: 16, 16 and 10 bytes), and which
    // categories have tables at all.
    private const int StateBytes = 42;
    private readonly byte[] _states = new byte[Categories * StateBytes];
    private int _built;
    private int _sigAt, _levelAt;

    // The next coding state as lanes of the state before: after a level of 1, after a level greater than 1, for blocks with 4
    // and with 3 prefix contexts.
    private static readonly Vector256<int> NextOne4 = Vector256.Create(1, 2, 3, 3, 4, 5, 6, 7);
    private static readonly Vector256<int> NextGreater4 = Vector256.Create(4, 4, 4, 4, 5, 6, 7, 7);
    private static readonly Vector256<int> NextOne3 = Vector256.Create(1, 2, 3, 3, 4, 5, 6, 7);
    private static readonly Vector256<int> NextGreater3 = Vector256.Create(4, 4, 4, 4, 5, 6, 6, 6);

    // The cost of the levels at rows r down to 0 as quantized, coded from each state, by (r * States) + state. Worked out for
    // every row and state when a block begins: RDOQ only changes levels above the rows it looks up (a level fits in 2^22 units
    // and a block has at most 64, so the sums fit an int).
    private readonly int[] _tail = new int[64 * States];

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
                PrefixCost[(s * 16) + m + 1] = m < MaxOnes ? cost + CabacCost.Of(state, 0) : cost;
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
        (_sigAt, _levelAt) = (cat * Positions, cat * States * 16);
        if (!ReferenceEquals(ctx, _tablesOf))
        {
            (_tablesOf, _made, _built) = (ctx, 0, 0);
        }

        if ((_made & (1 << cat)) == 0)
        {
            // Speed round 4: the tables depend only on the category's significance, last and level contexts; after a macroblock
            // that coded no residual of this category (a skip, say) they are unchanged, and the tables still hold.
            Span<byte> was = _states.AsSpan(cat * StateBytes, StateBytes);
            int span = cat == 5 ? 15 : 16, lastSpan = cat == 5 ? 9 : 16;
            if ((_built & (1 << cat)) == 0 || !ctx.AsSpan(_sigBase, span).SequenceEqual(was[..span])
                || !ctx.AsSpan(_lastBase, lastSpan).SequenceEqual(was.Slice(16, lastSpan)) || !ctx.AsSpan(absBase, 10).SequenceEqual(was.Slice(32, 10)))
            {
                ctx.AsSpan(absBase, 10).CopyTo(_abs);
                MakeTables();
                ctx.AsSpan(_sigBase, span).CopyTo(was);
                ctx.AsSpan(_lastBase, lastSpan).CopyTo(was[16..]);
                ctx.AsSpan(absBase, 10).CopyTo(was[32..]);
                _built |= 1 << cat;
            }

            _made |= 1 << cat;
        }

        (_state, _prefix) = (0, 0);
        if (count == 0)
        {
            _last = -1;
            return 0;
        }

        _row = count - 1;
        _last = rows[_row];
        _sig = SigCost(count);
        Debug.Assert(_sig == SigCostOf(_last), "The tables price the significance map as the states do.");
        Tails(count);
        return _sig + _tail[_row * States] + CabacCost.Bit;
    }

    /// <summary>
    /// Forgets the tables made from the context states, which must be called before the states change (once a macroblock is
    /// coded into the slice).
    /// </summary>
    public void Invalidate() => _made = 0;

    // The tables of the block's category from the states, whose costs Begin and Price then look up instead of working out.
    private void MakeTables()
    {
        byte[] ctx = _ctx;
        int positions = _cat == 5 ? 64 : 16;
        long zeros = 0;
        _zeroSum[_sigAt + _cat] = 0;
        for (int i = 0; i < positions; i++)
        {
            (int sigCtx, int lastCtx) = Contexts(i);
            int s0 = CabacCost.Of(ctx[sigCtx], 0), s1 = CabacCost.Of(ctx[sigCtx], 1);
            int l0 = CabacCost.Of(ctx[lastCtx], 0), l1 = CabacCost.Of(ctx[lastCtx], 1);
            _coded[_sigAt + i] = s1 + l0 - s0;
            _ends[_sigAt + i] = s1 + l1;
            _lastFlip[_sigAt + i] = l1 - l0;
            zeros += s0;
            _zeroSum[_sigAt + _cat + i + 1] = zeros;
        }

        // A level's costs: the first coeff_abs_level_minus1 bin (0 for a level of 1, else 1 and the prefix's other bins) and the
        // sign, from each state.
        byte[] states = _abs;
        Span<int> ones = stackalloc int[States], greater = stackalloc int[States], prefixAt = stackalloc int[States];
        for (int state = 0; state < States; state++)
        {
            byte first = states[FirstInc[state]];
            ones[state] = CabacCost.Of(first, 0) + CabacCost.Bit;
            greater[state] = CabacCost.Of(first, 1) + CabacCost.Bit;
            prefixAt[state] = states[PrefixInc[state]] * 16;
        }

        int[] costs = _levelCosts;
        ones.CopyTo(costs.AsSpan(_levelAt + States, States));
        for (int size = 2; size < 16; size++)
        {
            for (int state = 0; state < States; state++)
            {
                costs[_levelAt + (size * States) + state] = greater[state] + PrefixCost[prefixAt[state] + size];
            }
        }
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
            _trySig -= _coded[_sigAt + k];
        }
        else
        {
            // The last level: the map ends at the next level down (the levels below it are still as quantized, so it is the
            // next row's), dropping the bins above it.
            if (_row == 0)
            {
                return 0;
            }

            int last = _rows[_row - 1];
            Debug.Assert(_levels[_at + last] != 0 && _levels.AsSpan(_at + last + 1, k - last - 1).IndexOfAnyExcept(0) < 0, "Rows hold the levels below.");
            long sig = _sig - (k <= _max - 2 ? _ends[_sigAt + k] : 0);
            int zeroAt = _sigAt + _cat;
            sig -= _zeroSum[zeroAt + k] - _zeroSum[zeroAt + last + 1];
            _trySig = sig + _lastFlip[_sigAt + last];
            _tryLast = last;
        }

        return _trySig + _prefix + cost + (_row > 0 ? _tail[((_row - 1) * States) + state] : 0) + CabacCost.Bit;
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

    // The levels as quantized at rows row down to 0, coded from each state (states 0 to 3 count the levels of 1 while none was
    // greater than 1, states 4 on the levels greater than 1, each up to where the contexts stop changing), for every row: each
    // row's costs from the eight states at once, plus the row below's from the state each leads to.
    private void Tails(int count)
    {
        int[] levels = _levels, rows = _rows, tail = _tail;
        if (Avx2.IsSupported)
        {
            bool three = _next == Next3;
            Vector256<int> one = three ? NextOne3 : NextOne4, greater = three ? NextGreater3 : NextGreater4;
            Vector256<int> below = Vector256<int>.Zero;
            int[] costs = _levelCosts;
            for (int row = 0; row < count; row++)
            {
                int abs = Math.Abs(levels[_at + rows[row]]);
                Vector256<int> own = Vector256.LoadUnsafe(ref costs[0], (nuint)(_levelAt + (Math.Min(abs, 15) * States)));
                if (abs > MaxOnes)
                {
                    own += Vector256.Create(Suffix(abs));
                }

                below = own + Avx2.PermuteVar8x32(below, abs > 1 ? greater : one);
                below.StoreUnsafe(ref tail[0], (nuint)(row * States));
            }

            return;
        }

        for (int row = 0; row < count; row++)
        {
            int level = levels[_at + rows[row]];
            for (int state = 0; state < States; state++)
            {
                tail[(row * States) + state] = LevelCost(level, state) + (row > 0 ? tail[((row - 1) * States) + Next(level, state)] : 0);
            }
        }
    }

    // coeff_abs_level_minus1 and the sign of one non-zero level from the tables, without branching on the level's size (which
    // RDOQ's levels make unpredictable) unless it needs a suffix.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LevelCost(int level, int state)
    {
        int abs = Math.Abs(level);
        int cost = _levelCosts[_levelAt + (Math.Min(abs, 15) * States) + state];
        return abs > MaxOnes ? cost + Suffix(abs) : cost;
    }

    // The Exp-Golomb suffix (k = 0) of a level above MaxOnes: k ones, a zero and k bits, all bypass.
    private static int Suffix(int abs)
    {
        int v = abs - 15, k = 0;
        while (v >= 1 << k)
        {
            v -= 1 << k;
            k++;
        }

        return ((2 * k) + 1) * CabacCost.Bit;
    }

    // significant_coeff_flag and last_significant_coeff_flag up to the last level, from the tables and the rows of the block's
    // count levels.
    private long SigCost(int count)
    {
        int[] rows = _rows;
        int last = rows[count - 1];
        long cost = _zeroSum[_sigAt + _cat + last] + (last < _max - 1 ? _ends[_sigAt + last] : 0);
        for (int r = 0; r < count - 1; r++)
        {
            cost += _coded[_sigAt + rows[r]];
        }

        return cost;
    }

    // SigCost worked out from the states, for the Debug check.
    private long SigCostOf(int last)
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
