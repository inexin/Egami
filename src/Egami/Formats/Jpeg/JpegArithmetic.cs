using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Jpeg;

/// <summary>
/// The arithmetic decoder of T.81 annex D (the QM-coder) with the statistical models of annexes F, G and H: sequential and
/// progressive DCT scans and lossless differences. Each context is one byte, the index of its state in table D.3 in the low 7
/// bits and its MPS sense in the top bit; the statistics areas are reset at the start of each scan and at every restart.
/// </summary>
internal sealed class JpegArithmeticDecoder
{
    /// <summary>The DC statistics area: 49 bins for DCT scans (table F.4), 158 for lossless ones (table H.3).</summary>
    private const int DcBins = 158;

    /// <summary>The AC statistics area: 245 bins (table F.5), rounded up.</summary>
    private const int AcBins = 256;

    private readonly JpegBitReader _reader;
    private readonly JpegHeader _header;
    private readonly byte[] _dc = new byte[4 * DcBins];
    private readonly byte[] _ac = new byte[4 * AcBins];
    private readonly int[] _contexts = new int[4];
    private JpegScan? _scan;
    private byte _fixed = JpegQmTables.FixedState;
    private Coder _coder;
    private byte[] _lower;
    private byte[] _upper;
    private byte[] _kx;

    /// <summary>
    /// Creates a decoder reading <paramref name="reader"/>, with the header's conditioning tables as a DAC segment leaves them, or
    /// with copies of them as they stood for one scan (<paramref name="conditioning"/>), for scans decoded side by side.
    /// </summary>
    internal JpegArithmeticDecoder(JpegBitReader reader, JpegHeader header, (byte[] Lower, byte[] Upper, byte[] Kx)? conditioning = null)
    {
        _reader = reader;
        _header = header;
        (_lower, _upper, _kx) = conditioning ?? (header.ArithmeticLower, header.ArithmeticUpper, header.ArithmeticKx);
    }

    /// <summary>
    /// Gets whether the data held a magnitude too large for any coefficient (T.81 F.2.4.3), which only damaged data does; the
    /// caller stops decoding the scan, as libjpeg does.
    /// </summary>
    internal bool Failed { get; private set; }

    /// <summary>Starts the entropy-coded data of a scan: every statistics bin it uses is reset, and the decoder is initialized.</summary>
    internal void BeginScan(JpegScan scan)
    {
        _scan = scan;
        Failed = false;
        Restart();
    }

    /// <summary>
    /// Resets the statistics and the decoder at a restart marker (T.81 F.1.4.4.1.5, F.1.4.4.2.2, H.1.2.3.4). A progressive scan
    /// resets only the areas it codes with: DC in a first DC scan, AC in an AC scan.
    /// </summary>
    internal void Restart()
    {
        JpegScan scan = _scan!;
        bool progressive = _header.Progressive;
        for (int i = 0; i < scan.Components.Length; i++)
        {
            JpegScanComponent sc = scan.Components[i];
            if (!progressive || (scan.SpectralStart == 0 && scan.ApproxHigh == 0))
            {
                _dc.AsSpan(sc.DcTable * DcBins, DcBins).Clear();
                _contexts[i] = 0;
            }

            if (!_header.Lossless && (!progressive || scan.SpectralStart != 0))
            {
                _ac.AsSpan(sc.AcTable * AcBins, AcBins).Clear();
            }
        }

        // Initdec (T.81 D.2.7): the first two bytes go into C as the renormalization fetches them.
        _coder = new Coder { A = 0, C = 0, Ct = -16 };
    }

    /// <summary>Copies the decoder's whole state into <paramref name="copy"/>, to go back to if an incremental decode runs out of bytes.</summary>
    internal JpegArithmeticDecoder CopyTo(JpegArithmeticDecoder copy)
    {
        _dc.CopyTo(copy._dc, 0);
        _ac.CopyTo(copy._ac, 0);
        _contexts.CopyTo(copy._contexts, 0);
        copy._scan = _scan;
        copy._fixed = _fixed;
        copy._coder = _coder;
        copy._lower = _lower;
        copy._upper = _upper;
        copy._kx = _kx;
        copy.Failed = Failed;
        return copy;
    }

    /// <summary>Goes back to a copy made with <see cref="CopyTo"/>.</summary>
    internal void Restore(JpegArithmeticDecoder saved)
    {
        saved.CopyTo(this);
    }

    /// <summary>Initializes the coder alone, with no scan, for tests of the coder (<see cref="DecodeBit"/>).</summary>
    internal void ResetCoder()
    {
        _coder = new Coder { A = 0, C = 0, Ct = -16 };
    }

    /// <summary>Decodes one binary decision with a context the caller keeps, for tests of the coder.</summary>
    internal int DecodeBit(ref byte context)
    {
        Coder q = _coder;
        int bit = Decode(ref q, ref context);
        _coder = q;
        return bit;
    }

    /// <summary>
    /// Decodes one block of a sequential scan (T.81 F.2.4) for scan component <paramref name="index"/>, adding its DC difference
    /// to <paramref name="predictor"/>. Coefficients go to their natural position when <paramref name="natural"/> is set, else to
    /// their zigzag one. Returns whether any AC coefficient is nonzero.
    /// </summary>
    internal bool DecodeBlock(int index, ref int predictor, Span<short> block, bool natural)
    {
        block = block[..64];
        block.Clear();
        JpegScanComponent sc = _scan!.Components[index];
        Coder q = _coder;
        predictor += DecodeDc(ref q, sc.DcTable, ref _contexts[index]);
        block[0] = (short)predictor;
        if (Failed)
        {
            _coder = q;
            return false;
        }

        ref byte order = ref MemoryMarshal.GetReference(JpegTables.ZigZag);
        ref byte stats = ref _ac[sc.AcTable * AcBins];
        ref short first = ref MemoryMarshal.GetReference(block);
        int kx = _kx[sc.AcTable];
        bool any = false;
        for (int k = 1; k <= 63; k++)
        {
            int st = 3 * (k - 1);
            if (Decode(ref q, ref Unsafe.Add(ref stats, st)) != 0)
            {
                break;
            }

            while (Decode(ref q, ref Unsafe.Add(ref stats, st + 1)) == 0)
            {
                st += 3;
                if (++k > 63)
                {
                    Failed = true;
                    _coder = q;
                    return any;
                }
            }

            int value = DecodeAc(ref q, ref stats, st, k, kx);
            if (Failed)
            {
                break;
            }

            Unsafe.Add(ref first, natural ? Unsafe.Add(ref order, k) : k) = (short)value;
            any = true;
        }

        _coder = q;
        return any;
    }

    /// <summary>The first DC scan of a progressive file (T.81 G.1.3.1): the DC difference, scaled up by the point transform.</summary>
    internal void DecodeDcFirst(int index, ref int predictor, Span<short> block, int al)
    {
        Coder q = _coder;
        predictor += DecodeDc(ref q, _scan!.Components[index].DcTable, ref _contexts[index]);
        _coder = q;
        block[0] = (short)(predictor << al);
    }

    /// <summary>A DC refinement scan: one more bit of the DC coefficient, at the fixed estimate.</summary>
    internal void DecodeDcRefine(Span<short> block, int al)
    {
        Coder q = _coder;
        if (Decode(ref q, ref _fixed) != 0)
        {
            block[0] |= (short)(1 << al);
        }

        _coder = q;
    }

    /// <summary>The first scan of an AC band (T.81 G.1.3.2), coefficients in zigzag order.</summary>
    internal void DecodeAcFirst(Span<short> block, int ss, int se, int al)
    {
        if (Failed)
        {
            return;
        }

        int table = _scan!.Components[0].AcTable;
        ref byte stats = ref _ac[table * AcBins];
        int kx = _kx[table];
        Coder q = _coder;
        for (int k = ss; k <= se; k++)
        {
            int st = 3 * (k - 1);
            if (Decode(ref q, ref Unsafe.Add(ref stats, st)) != 0)
            {
                break;
            }

            while (Decode(ref q, ref Unsafe.Add(ref stats, st + 1)) == 0)
            {
                st += 3;
                if (++k > se)
                {
                    Failed = true;
                    _coder = q;
                    return;
                }
            }

            int value = DecodeAc(ref q, ref stats, st, k, kx);
            if (Failed)
            {
                break;
            }

            block[k] = (short)(value << al);
        }

        _coder = q;
    }

    /// <summary>
    /// An AC refinement scan (T.81 G.1.3.3): the end-of-band decision is skipped up to the last coefficient already nonzero; each
    /// nonzero coefficient takes a correction bit, and a zero one may become ±1 at this bit position.
    /// </summary>
    internal void DecodeAcRefine(Span<short> block, int ss, int se, int al)
    {
        if (Failed)
        {
            return;
        }

        ref byte stats = ref _ac[_scan!.Components[0].AcTable * AcBins];
        ref short first = ref MemoryMarshal.GetReference(block[..64]);
        int p1 = 1 << al;
        int m1 = -1 << al;

        // The end of the band in the last scan: past it, each position starts with an end-of-band decision.
        ulong nonzero = JpegBlock.NonZero(block) & (se >= 63 ? ulong.MaxValue : (1UL << (se + 1)) - 1);
        int last = nonzero == 0 ? 0 : 63 - BitOperations.LeadingZeroCount(nonzero);
        Coder q = _coder;
        for (int k = ss; k <= se; k++)
        {
            int st = 3 * (k - 1);
            if (k > last && Decode(ref q, ref Unsafe.Add(ref stats, st)) != 0)
            {
                break;
            }

            while (true)
            {
                ref short coefficient = ref Unsafe.Add(ref first, k);
                if (coefficient != 0)
                {
                    if (Decode(ref q, ref Unsafe.Add(ref stats, st + 2)) != 0)
                    {
                        coefficient = (short)(coefficient + (coefficient < 0 ? m1 : p1));
                    }

                    break;
                }

                if (Decode(ref q, ref Unsafe.Add(ref stats, st + 1)) != 0)
                {
                    coefficient = (short)(Decode(ref q, ref _fixed) != 0 ? m1 : p1);
                    break;
                }

                st += 3;
                if (++k > se)
                {
                    Failed = true;
                    _coder = q;
                    return;
                }
            }
        }

        _coder = q;
    }

    /// <summary>
    /// Decodes one lossless difference (T.81 H.1.2.3) with conditioning table <paramref name="table"/>, given the differences
    /// coded for the sample to the left (<paramref name="left"/>) and above (<paramref name="above"/>).
    /// </summary>
    internal int DecodeDifference(int table, int left, int above)
    {
        int lower = _lower[table];
        int upper = _upper[table];
        int b = Category(above, lower, upper);
        int s0 = (20 * Category(left, lower, upper)) + (4 * b);
        Coder q = _coder;
        int value = DecodeMagnitude(ref q, ref _dc[table * DcBins], s0, b >= 3 ? 129 : 100, out _);
        _coder = q;
        return value;
    }

    /// <summary>Returns the conditioning category of a difference (T.81 F.1.4.4.1.2): 0 zero, 1 small +, 2 small -, 3 large +, 4 large -.</summary>
    private static int Category(int difference, int lower, int upper)
    {
        int magnitude = Math.Abs(difference);
        if (magnitude <= ((1 << lower) >> 1))
        {
            return 0;
        }

        return (magnitude <= 1 << upper ? 1 : 3) + (difference < 0 ? 1 : 0);
    }

    /// <summary>Decodes a DC difference (T.81 F.2.4.1) and updates the component's conditioning from it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeDc(ref Coder q, int table, ref int context)
    {
        if (Failed)
        {
            return 0;
        }

        int value = DecodeMagnitude(ref q, ref _dc[table * DcBins], context, 20, out int bound);
        if (value == 0)
        {
            context = 0;
            return 0;
        }

        // The category from the magnitude bound, as figure F.10 sets it out (equivalent to comparing the difference itself).
        int sign = value < 0 ? 4 : 0;
        context = bound < ((1 << _lower[table]) >> 1) ? 0
            : bound > ((1 << _upper[table]) >> 1) ? 12 + sign
            : 4 + sign;
        return value;
    }

    /// <summary>
    /// Decodes a DC or lossless difference: the zero decision at <paramref name="s0"/>, the sign, then the magnitude category from
    /// <paramref name="x1"/> and the magnitude bits (figures F.19, F.23 and F.24). <paramref name="bound"/> is the category's bound.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeMagnitude(ref Coder q, ref byte stats, int s0, int x1, out int bound)
    {
        bound = 0;
        if (Decode(ref q, ref Unsafe.Add(ref stats, s0)) == 0)
        {
            return 0;
        }

        int sign = Decode(ref q, ref Unsafe.Add(ref stats, s0 + 1));
        int st = s0 + 2 + sign;
        int m = Decode(ref q, ref Unsafe.Add(ref stats, st));
        if (m != 0)
        {
            st = x1;
            while (Decode(ref q, ref Unsafe.Add(ref stats, st)) != 0)
            {
                if ((m <<= 1) == 0x8000)
                {
                    Failed = true;
                    return 0;
                }

                st++;
            }
        }

        bound = m;
        int value = m;
        st += 14;
        while ((m >>= 1) != 0)
        {
            if (Decode(ref q, ref Unsafe.Add(ref stats, st)) != 0)
            {
                value |= m;
            }
        }

        value++;
        return sign != 0 ? -value : value;
    }

    /// <summary>Decodes a nonzero AC coefficient whose zero-run ended at <paramref name="st"/> (figures F.21 to F.24).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeAc(ref Coder q, ref byte stats, int st, int k, int kx)
    {
        int sign = Decode(ref q, ref _fixed);
        st += 2;
        int m = Decode(ref q, ref Unsafe.Add(ref stats, st));
        if (m != 0 && Decode(ref q, ref Unsafe.Add(ref stats, st)) != 0)
        {
            m <<= 1;
            st = k <= kx ? 189 : 217;
            while (Decode(ref q, ref Unsafe.Add(ref stats, st)) != 0)
            {
                if ((m <<= 1) == 0x8000)
                {
                    Failed = true;
                    return 0;
                }

                st++;
            }
        }

        int value = m;
        st += 14;
        while ((m >>= 1) != 0)
        {
            if (Decode(ref q, ref Unsafe.Add(ref stats, st)) != 0)
            {
                value |= m;
            }
        }

        value++;
        return sign != 0 ? -value : value;
    }

    /// <summary>
    /// Decodes one binary decision with context <paramref name="context"/> (T.81 D.2.4 to D.2.6), renormalizing first as libjpeg
    /// does so the bytes are fetched on demand: A is kept at least X'8000', and C holds <see cref="Coder.Ct"/> bits beyond the 16
    /// compared. The registers are a local of the caller's, so they stay in machine registers through a block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Decode(ref Coder q, ref byte context)
    {
        while (q.A < 0x8000)
        {
            // Renormalization (T.81 D.2.6): double A, bringing a byte into C every 8 doublings.
            if (--q.Ct < 0)
            {
                q.C = (q.C << 8) | _reader.ArithmeticByte();
                if ((q.Ct += 8) < 0 && ++q.Ct == 0)
                {
                    // Both bytes of the initialization have arrived.
                    q.A = 0x8000;
                }
            }

            q.A <<= 1;
        }

        int sv = context;
        int entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(JpegQmTables.States), sv & 0x7F);
        int qe = entry >>> 16;
        int interval = q.A - qe;
        q.A = interval;
        interval <<= q.Ct;
        if (q.C >= interval)
        {
            // The LPS sub-interval, or an MPS where the intervals were exchanged (D.2.4, conditional exchange).
            q.C -= interval;
            if (q.A < qe)
            {
                q.A = qe;
                context = (byte)((sv & 0x80) ^ ((entry >> 8) & 0xFF));
            }
            else
            {
                q.A = qe;
                context = (byte)((sv & 0x80) ^ (entry & 0xFF));
                sv ^= 0x80;
            }
        }
        else if (q.A < 0x8000)
        {
            if (q.A < qe)
            {
                context = (byte)((sv & 0x80) ^ (entry & 0xFF));
                sv ^= 0x80;
            }
            else
            {
                context = (byte)((sv & 0x80) ^ ((entry >> 8) & 0xFF));
            }
        }

        return sv >> 7;
    }

    /// <summary>The coder's registers: the interval A, the code C and the count of extra bits in C.</summary>
    private struct Coder
    {
        internal int A;
        internal int C;
        internal int Ct;
    }
}
