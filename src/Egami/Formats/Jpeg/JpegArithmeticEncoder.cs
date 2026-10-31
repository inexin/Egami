using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Jpeg;

/// <summary>
/// The arithmetic encoder of T.81 annex D.1 (the QM-coder) with the statistical models of annexes F and G for sequential and
/// progressive DCT scans, at the default conditioning (L = 0, U = 1, Kx = 5) so the file needs no DAC segment. Contexts are the
/// bytes <see cref="JpegArithmeticDecoder"/> keeps, and they are reset at the start of each scan and at every restart.
/// </summary>
internal sealed class JpegArithmeticEncoder
{
    /// <summary>The DC statistics area of one table: 49 bins for DCT scans (table F.4), 158 for lossless ones (table H.3).</summary>
    private const int DcBins = 158;

    /// <summary>The AC statistics area of one table: 245 bins (table F.5), rounded up.</summary>
    private const int AcBins = 256;

    /// <summary>The default Kx (T.81 F.1.4.4.2.1): the last zigzag position whose magnitude categories use the low bins.</summary>
    private const int Kx = 5;

    private readonly JpegBitWriter _writer;
    private readonly byte[] _dc = new byte[4 * DcBins];
    private readonly byte[] _ac = new byte[4 * AcBins];
    private readonly int[] _contexts = new int[4];
    private Coder _coder;

    /// <summary>The byte waiting for a possible carry, or -1 before the first (T.81 D.1.6: B).</summary>
    private int _pending;

    /// <summary>The 0xFF bytes after <see cref="_pending"/>, held as a carry would turn them all into zeros (ST).</summary>
    private int _stacked;

    /// <summary>Zero bytes not written yet: dropped if nothing but zeros follows them before a marker (D.1.8, Discard_final_zeros).</summary>
    private int _zeros;

    internal JpegArithmeticEncoder(JpegBitWriter writer)
    {
        _writer = writer;
        Restart();
    }

    /// <summary>
    /// Resets every statistics bin and DC conditioning context and initializes the coder (Initenc, T.81 D.1.7): at the start of
    /// each scan and after each restart marker.
    /// </summary>
    internal void Restart()
    {
        Array.Clear(_dc);
        Array.Clear(_ac);
        Array.Clear(_contexts);
        _coder = new Coder { A = 0x10000, C = 0, Ct = 11 };
        _pending = -1;
        _stacked = 0;
        _zeros = 0;
    }

    /// <summary>
    /// Ends the entropy-coded segment (Flush, T.81 D.1.8): the code register is set to the value in its interval with the most
    /// trailing zero bits, its bytes go out, and trailing zero bytes are left for the decoder to supply. The caller writes the
    /// marker that follows, then calls <see cref="Restart"/> if more data comes.
    /// </summary>
    internal void Finish()
    {
        Coder q = _coder;
        int t = (q.C + q.A - 1) & ~0xFFFF;
        q.C = t < q.C ? t + 0x8000 : t;
        q.C <<= q.Ct;
        ByteOut(ref q);
        q.C <<= 8;
        ByteOut(ref q);
        if (_pending >= 0)
        {
            Emit(_pending);
        }

        for (; _stacked > 0; _stacked--)
        {
            Emit(0xFF);
        }

        _pending = -1;
        _zeros = 0;
        _coder = q;
    }

    /// <summary>
    /// Codes one block of a sequential scan (T.81 F.1.4), stored in natural order: the DC difference from
    /// <paramref name="predictor"/>, which is updated, with statistics table <paramref name="dcTable"/> and the conditioning of scan
    /// component <paramref name="index"/>, then the AC coefficients with table <paramref name="acTable"/>.
    /// </summary>
    internal void EncodeBlock(ReadOnlySpan<short> block, int index, ref int predictor, int dcTable, int acTable)
    {
        block = block[..64];
        ref short coefficients = ref MemoryMarshal.GetReference(block);
        Coder q = _coder;
        EncodeDc(ref q, ref _dc[dcTable * DcBins], ref _contexts[index], coefficients - predictor);
        predictor = coefficients;

        ulong nonzero = JpegBlock.NonZeroZigZag(block) & ~1UL;
        int end = nonzero == 0 ? 0 : 63 - BitOperations.LeadingZeroCount(nonzero);
        ref byte stats = ref _ac[acTable * AcBins];
        ref byte zigzag = ref MemoryMarshal.GetReference(JpegTables.ZigZag);
        for (int k = 1; k <= end; k++)
        {
            int st = 3 * (k - 1);
            Encode(ref q, ref Unsafe.Add(ref stats, st), 0);
            int value;
            while ((value = Unsafe.Add(ref coefficients, Unsafe.Add(ref zigzag, k))) == 0)
            {
                Encode(ref q, ref Unsafe.Add(ref stats, st + 1), 0);
                st += 3;
                k++;
            }

            Encode(ref q, ref Unsafe.Add(ref stats, st + 1), 1);
            EncodeAc(ref q, ref stats, st, k, value);
        }

        if (end < 63)
        {
            Encode(ref q, ref Unsafe.Add(ref stats, 3 * end), 1);
        }

        _coder = q;
    }

    /// <summary>The first DC scan of a progressive file (T.81 G.1.3.1): the point-transformed DC as a difference, as sequential.</summary>
    internal void EncodeDcFirst(int value, int index, ref int predictor, int dcTable)
    {
        Coder q = _coder;
        EncodeDc(ref q, ref _dc[dcTable * DcBins], ref _contexts[index], value - predictor);
        predictor = value;
        _coder = q;
    }

    /// <summary>A DC refinement scan: one bit at the fixed estimate.</summary>
    internal void EncodeDcRefine(int bit)
    {
        Coder q = _coder;
        byte fixedState = JpegQmTables.FixedState;
        Encode(ref q, ref fixedState, bit);
        _coder = q;
    }

    /// <summary>
    /// The first scan of an AC band (T.81 G.1.3.2) of a block in zigzag order: the coefficients <paramref name="ss"/> to
    /// <paramref name="se"/>, point-transformed by <paramref name="al"/>, as a sequential scan codes them.
    /// </summary>
    internal void EncodeAcFirst(ReadOnlySpan<short> block, int acTable, int ss, int se, int al)
    {
        int end = ss - 1;
        for (int k = se; k >= ss; k--)
        {
            if (Math.Abs((int)block[k]) >> al != 0)
            {
                end = k;
                break;
            }
        }

        ref byte stats = ref _ac[acTable * AcBins];
        Coder q = _coder;
        for (int k = ss; k <= end; k++)
        {
            int st = 3 * (k - 1);
            Encode(ref q, ref Unsafe.Add(ref stats, st), 0);
            int value;
            while ((value = Transform(block[k], al)) == 0)
            {
                Encode(ref q, ref Unsafe.Add(ref stats, st + 1), 0);
                st += 3;
                k++;
            }

            Encode(ref q, ref Unsafe.Add(ref stats, st + 1), 1);
            EncodeAc(ref q, ref stats, st, k, value);
        }

        if (end < se)
        {
            Encode(ref q, ref Unsafe.Add(ref stats, 3 * end), 1);
        }

        _coder = q;
    }

    /// <summary>
    /// An AC refinement scan (T.81 G.1.3.3) of a block in zigzag order: no end-of-band decision up to the last coefficient
    /// already nonzero after the bits above <paramref name="ah"/>; each such coefficient gets its bit <paramref name="al"/>, and a
    /// zero one says whether it becomes ±1 at this bit, with its sign.
    /// </summary>
    internal void EncodeAcRefine(ReadOnlySpan<short> block, int acTable, int ss, int se, int ah, int al)
    {
        int end = ss - 1;
        int last = 0;
        for (int k = se; k >= ss; k--)
        {
            int magnitude = Math.Abs((int)block[k]);
            if (end < ss && magnitude >> al != 0)
            {
                end = k;
            }

            if (magnitude >> ah != 0)
            {
                last = k;
                break;
            }
        }

        ref byte stats = ref _ac[acTable * AcBins];
        Coder q = _coder;
        byte fixedState = JpegQmTables.FixedState;
        for (int k = ss; k <= end; k++)
        {
            int st = 3 * (k - 1);
            if (k > last)
            {
                Encode(ref q, ref Unsafe.Add(ref stats, st), 0);
            }

            while (true)
            {
                int coefficient = block[k];
                int magnitude = Math.Abs(coefficient);
                if (magnitude >> ah != 0)
                {
                    Encode(ref q, ref Unsafe.Add(ref stats, st + 2), (magnitude >> al) & 1);
                    break;
                }

                if (magnitude >> al != 0)
                {
                    Encode(ref q, ref Unsafe.Add(ref stats, st + 1), 1);
                    Encode(ref q, ref fixedState, coefficient < 0 ? 1 : 0);
                    break;
                }

                Encode(ref q, ref Unsafe.Add(ref stats, st + 1), 0);
                st += 3;
                k++;
            }
        }

        if (end < se)
        {
            Encode(ref q, ref Unsafe.Add(ref stats, 3 * end), 1);
        }

        _coder = q;
    }

    /// <summary>Codes one binary decision with a context the caller keeps, for tests of the coder alone.</summary>
    internal void EncodeBit(ref byte context, int bit)
    {
        Coder q = _coder;
        Encode(ref q, ref context, bit);
        _coder = q;
    }

    /// <summary>A coefficient after the point transform of T.81 G.1.2.2: its magnitude shifted down, its sign kept.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Transform(int coefficient, int al)
    {
        return coefficient < 0 ? -(-coefficient >> al) : coefficient >> al;
    }

    /// <summary>
    /// Codes a DC difference (T.81 F.1.4.1, figures F.4 to F.8): the zero decision in the component's conditioning context, the
    /// sign, the magnitude category and its bits; then sets the context for the next difference from this one's category.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EncodeDc(ref Coder q, ref byte stats, ref int context, int diff)
    {
        int s0 = context;
        if (diff == 0)
        {
            Encode(ref q, ref Unsafe.Add(ref stats, s0), 0);
            context = 0;
            return;
        }

        Encode(ref q, ref Unsafe.Add(ref stats, s0), 1);
        int sign = diff < 0 ? 1 : 0;
        Encode(ref q, ref Unsafe.Add(ref stats, s0 + 1), sign);
        int v = Math.Abs(diff) - 1;
        int st = s0 + 2 + sign;
        int m = 0;
        if (v == 0)
        {
            Encode(ref q, ref Unsafe.Add(ref stats, st), 0);
        }
        else
        {
            Encode(ref q, ref Unsafe.Add(ref stats, st), 1);
            m = 1;
            st = 20;
            while (m << 1 <= v)
            {
                Encode(ref q, ref Unsafe.Add(ref stats, st), 1);
                m <<= 1;
                st++;
            }

            Encode(ref q, ref Unsafe.Add(ref stats, st), 0);
        }

        // With L = 0 and U = 1 a magnitude of 1 or 2 is small and 3 or more large (F.1.4.4.1.2).
        context = (m > 1 ? 12 : 4) + (4 * sign);
        st += 14;
        while ((m >>= 1) != 0)
        {
            Encode(ref q, ref Unsafe.Add(ref stats, st), (v & m) != 0 ? 1 : 0);
        }
    }

    /// <summary>
    /// Codes a nonzero AC coefficient whose zero-run decision ended at <paramref name="st"/> (T.81 F.1.4.4.2, figures F.9 to F.11):
    /// the sign at the fixed estimate, then the magnitude category and bits, the categories past 2 in the bins Kx selects.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EncodeAc(ref Coder q, ref byte stats, int st, int k, int value)
    {
        byte fixedState = JpegQmTables.FixedState;
        Encode(ref q, ref fixedState, value < 0 ? 1 : 0);
        int v = Math.Abs(value) - 1;
        st += 2;
        int m = 0;
        if (v == 0)
        {
            Encode(ref q, ref Unsafe.Add(ref stats, st), 0);
        }
        else
        {
            Encode(ref q, ref Unsafe.Add(ref stats, st), 1);
            m = 1;
            if (v == 1)
            {
                Encode(ref q, ref Unsafe.Add(ref stats, st), 0);
            }
            else
            {
                Encode(ref q, ref Unsafe.Add(ref stats, st), 1);
                m = 2;
                st = k <= Kx ? 189 : 217;
                while (m << 1 <= v)
                {
                    Encode(ref q, ref Unsafe.Add(ref stats, st), 1);
                    m <<= 1;
                    st++;
                }

                Encode(ref q, ref Unsafe.Add(ref stats, st), 0);
            }
        }

        st += 14;
        while ((m >>= 1) != 0)
        {
            Encode(ref q, ref Unsafe.Add(ref stats, st), (v & m) != 0 ? 1 : 0);
        }
    }

    /// <summary>
    /// Codes one binary decision (Code_0 and Code_1 through Code_MPS and Code_LPS, T.81 D.1.2 to D.1.4): the MPS takes the lower
    /// sub-interval A - Qe and the LPS the upper Qe, exchanged when A - Qe is the smaller; the state moves on as table D.3 says when
    /// an LPS is coded or the interval needs renormalizing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Encode(ref Coder q, ref byte context, int bit)
    {
        int sv = context;
        int entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(JpegQmTables.States), sv & 0x7F);
        int qe = entry >>> 16;
        q.A -= qe;
        if (bit == sv >> 7)
        {
            if (q.A >= 0x8000)
            {
                return;
            }

            if (q.A < qe)
            {
                q.C += q.A;
                q.A = qe;
            }

            context = (byte)((sv & 0x80) ^ ((entry >> 8) & 0xFF));
        }
        else
        {
            if (q.A >= qe)
            {
                q.C += q.A;
                q.A = qe;
            }

            context = (byte)((sv & 0x80) ^ (entry & 0xFF));
        }

        // Renorm_e (D.1.5): double A and C until A is at least X'8000', a byte going out every 8 doublings.
        do
        {
            q.A <<= 1;
            q.C <<= 1;
            if (--q.Ct == 0)
            {
                ByteOut(ref q);
                q.Ct = 8;
            }
        }
        while (q.A < 0x8000);
    }

    /// <summary>
    /// Moves the top byte of C out (Byte_out, T.81 D.1.6): a carry into it adds one to the byte held back and turns the 0xFF bytes
    /// stacked after that into zeros; a 0xFF byte is stacked, since a later carry would change it; any other byte releases the
    /// held one and the stack, and is held in turn.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ByteOut(ref Coder q)
    {
        int t = q.C >> 19;
        if (t > 0xFF)
        {
            // The first byte of a segment never takes a carry: the value is below the interval it started with.
            Debug.Assert(_pending >= 0 && _pending < 0xFF, "A carry needs a byte below 0xFF to go into.");
            Emit(_pending + 1);
            for (; _stacked > 0; _stacked--)
            {
                Emit(0);
            }

            _pending = t & 0xFF;
        }
        else if (t == 0xFF)
        {
            _stacked++;
        }
        else
        {
            if (_pending >= 0)
            {
                Emit(_pending);
            }

            for (; _stacked > 0; _stacked--)
            {
                Emit(0xFF);
            }

            _pending = t;
        }

        q.C &= 0x7FFFF;
    }

    /// <summary>Writes a byte of the code, holding back zero bytes until something other than a zero or marker follows them.</summary>
    private void Emit(int value)
    {
        if (value == 0)
        {
            _zeros++;
            return;
        }

        for (; _zeros > 0; _zeros--)
        {
            _writer.Byte(0);
        }

        _writer.Byte(value);
    }

    /// <summary>The coder's registers (A, C and CT of T.81 D.1), copied to a local for a block so they stay in machine registers.</summary>
    private struct Coder
    {
        internal int A;
        internal int C;
        internal int Ct;
    }
}
