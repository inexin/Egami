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

    /// <summary>The state of the fixed 0.5 estimate used for signs and refinement bits: Qe X'5A1D', never adapted.</summary>
    private const byte FixedState = 113;

    /// <summary>
    /// Table D.3, one entry per state: Qe_Value in bits 16 to 31, Next_Index_MPS in bits 8 to 15, Switch_MPS in bit 7 and
    /// Next_Index_LPS in bits 0 to 6. Entry 113 is the fixed estimate, which leads back to itself.
    /// </summary>
    private static readonly int[] States =
    [
        State(0x5A1D, 1, 1, 1), State(0x2586, 14, 2, 0), State(0x1114, 16, 3, 0), State(0x080B, 18, 4, 0),
        State(0x03D8, 20, 5, 0), State(0x01DA, 23, 6, 0), State(0x00E5, 25, 7, 0), State(0x006F, 28, 8, 0),
        State(0x0036, 30, 9, 0), State(0x001A, 33, 10, 0), State(0x000D, 35, 11, 0), State(0x0006, 9, 12, 0),
        State(0x0003, 10, 13, 0), State(0x0001, 12, 13, 0), State(0x5A7F, 15, 15, 1), State(0x3F25, 36, 16, 0),
        State(0x2CF2, 38, 17, 0), State(0x207C, 39, 18, 0), State(0x17B9, 40, 19, 0), State(0x1182, 42, 20, 0),
        State(0x0CEF, 43, 21, 0), State(0x09A1, 45, 22, 0), State(0x072F, 46, 23, 0), State(0x055C, 48, 24, 0),
        State(0x0406, 49, 25, 0), State(0x0303, 51, 26, 0), State(0x0240, 52, 27, 0), State(0x01B1, 54, 28, 0),
        State(0x0144, 56, 29, 0), State(0x00F5, 57, 30, 0), State(0x00B7, 59, 31, 0), State(0x008A, 60, 32, 0),
        State(0x0068, 62, 33, 0), State(0x004E, 63, 34, 0), State(0x003B, 32, 35, 0), State(0x002C, 33, 9, 0),
        State(0x5AE1, 37, 37, 1), State(0x484C, 64, 38, 0), State(0x3A0D, 65, 39, 0), State(0x2EF1, 67, 40, 0),
        State(0x261F, 68, 41, 0), State(0x1F33, 69, 42, 0), State(0x19A8, 70, 43, 0), State(0x1518, 72, 44, 0),
        State(0x1177, 73, 45, 0), State(0x0E74, 74, 46, 0), State(0x0BFB, 75, 47, 0), State(0x09F8, 77, 48, 0),
        State(0x0861, 78, 49, 0), State(0x0706, 79, 50, 0), State(0x05CD, 48, 51, 0), State(0x04DE, 50, 52, 0),
        State(0x040F, 50, 53, 0), State(0x0363, 51, 54, 0), State(0x02D4, 52, 55, 0), State(0x025C, 53, 56, 0),
        State(0x01F8, 54, 57, 0), State(0x01A4, 55, 58, 0), State(0x0160, 56, 59, 0), State(0x0125, 57, 60, 0),
        State(0x00F6, 58, 61, 0), State(0x00CB, 59, 62, 0), State(0x00AB, 61, 63, 0), State(0x008F, 61, 32, 0),
        State(0x5B12, 65, 65, 1), State(0x4D04, 80, 66, 0), State(0x412C, 81, 67, 0), State(0x37D8, 82, 68, 0),
        State(0x2FE8, 83, 69, 0), State(0x293C, 84, 70, 0), State(0x2379, 86, 71, 0), State(0x1EDF, 87, 72, 0),
        State(0x1AA9, 87, 73, 0), State(0x174E, 72, 74, 0), State(0x1424, 72, 75, 0), State(0x119C, 74, 76, 0),
        State(0x0F6B, 74, 77, 0), State(0x0D51, 75, 78, 0), State(0x0BB6, 77, 79, 0), State(0x0A40, 77, 48, 0),
        State(0x5832, 80, 81, 1), State(0x4D1C, 88, 82, 0), State(0x438E, 89, 83, 0), State(0x3BDD, 90, 84, 0),
        State(0x34EE, 91, 85, 0), State(0x2EAE, 92, 86, 0), State(0x299A, 93, 87, 0), State(0x2516, 86, 71, 0),
        State(0x5570, 88, 89, 1), State(0x4CA9, 95, 90, 0), State(0x44D9, 96, 91, 0), State(0x3E22, 97, 92, 0),
        State(0x3824, 99, 93, 0), State(0x32B4, 99, 94, 0), State(0x2E17, 93, 86, 0), State(0x56A8, 95, 96, 1),
        State(0x4F46, 101, 97, 0), State(0x47E5, 102, 98, 0), State(0x41CF, 103, 99, 0), State(0x3C3D, 104, 100, 0),
        State(0x375E, 99, 93, 0), State(0x5231, 105, 102, 0), State(0x4C0F, 106, 103, 0), State(0x4639, 107, 104, 0),
        State(0x415E, 103, 99, 0), State(0x5627, 105, 106, 1), State(0x50E7, 108, 107, 0), State(0x4B85, 109, 103, 0),
        State(0x5597, 110, 109, 0), State(0x504F, 111, 107, 0), State(0x5A10, 110, 111, 1), State(0x5522, 112, 109, 0),
        State(0x59EB, 112, 111, 1), State(0x5A1D, 113, 113, 0),
    ];

    private readonly JpegBitReader _reader;
    private readonly JpegHeader _header;
    private readonly byte[] _dc = new byte[4 * DcBins];
    private readonly byte[] _ac = new byte[4 * AcBins];
    private readonly int[] _contexts = new int[4];
    private JpegScan? _scan;
    private byte _fixed = FixedState;
    private int _c;
    private int _a;
    private int _ct;

    internal JpegArithmeticDecoder(JpegBitReader reader, JpegHeader header)
    {
        _reader = reader;
        _header = header;
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

        // Initdec (T.81 D.2.7): the first two bytes go into C as the renormalization below fetches them.
        _c = 0;
        _a = 0;
        _ct = -16;
    }

    /// <summary>Copies the decoder's whole state into <paramref name="copy"/>, to go back to if an incremental decode runs out of bytes.</summary>
    internal JpegArithmeticDecoder CopyTo(JpegArithmeticDecoder copy)
    {
        _dc.CopyTo(copy._dc, 0);
        _ac.CopyTo(copy._ac, 0);
        _contexts.CopyTo(copy._contexts, 0);
        copy._scan = _scan;
        copy._fixed = _fixed;
        copy._c = _c;
        copy._a = _a;
        copy._ct = _ct;
        copy.Failed = Failed;
        return copy;
    }

    /// <summary>Goes back to a copy made with <see cref="CopyTo"/>.</summary>
    internal void Restore(JpegArithmeticDecoder saved)
    {
        saved.CopyTo(this);
    }

    /// <summary>
    /// Decodes one block of a sequential scan (T.81 F.2.4) for scan component <paramref name="index"/>, adding its DC difference
    /// to <paramref name="predictor"/>. Coefficients go to their natural position when <paramref name="natural"/> is set, else to
    /// their zigzag one. Returns whether any AC coefficient is nonzero.
    /// </summary>
    internal bool DecodeBlock(int index, ref int predictor, Span<short> block, bool natural)
    {
        block.Clear();
        JpegScanComponent sc = _scan!.Components[index];
        predictor += DecodeDc(sc.DcTable, ref _contexts[index]);
        block[0] = (short)predictor;
        if (Failed)
        {
            return false;
        }

        ReadOnlySpan<byte> order = JpegTables.ZigZag;
        bool any = false;
        Span<byte> stats = _ac.AsSpan(sc.AcTable * AcBins, AcBins);
        int kx = _header.ArithmeticKx[sc.AcTable];
        for (int k = 1; k <= 63; k++)
        {
            int st = 3 * (k - 1);
            if (Decode(ref stats[st]) != 0)
            {
                break;
            }

            while (Decode(ref stats[st + 1]) == 0)
            {
                st += 3;
                if (++k > 63)
                {
                    Failed = true;
                    return any;
                }
            }

            int value = DecodeAc(stats, st, k, kx);
            if (Failed)
            {
                return any;
            }

            block[natural ? order[k] : k] = (short)value;
            any = true;
        }

        return any;
    }

    /// <summary>The first DC scan of a progressive file (T.81 G.1.3.1): the DC difference, scaled up by the point transform.</summary>
    internal void DecodeDcFirst(int index, ref int predictor, Span<short> block, int al)
    {
        predictor += DecodeDc(_scan!.Components[index].DcTable, ref _contexts[index]);
        block[0] = (short)(predictor << al);
    }

    /// <summary>A DC refinement scan: one more bit of the DC coefficient, at the fixed estimate.</summary>
    internal void DecodeDcRefine(Span<short> block, int al)
    {
        if (Decode(ref _fixed) != 0)
        {
            block[0] |= (short)(1 << al);
        }
    }

    /// <summary>The first scan of an AC band (T.81 G.1.3.2), coefficients in zigzag order.</summary>
    internal void DecodeAcFirst(Span<short> block, int ss, int se, int al)
    {
        if (Failed)
        {
            return;
        }

        int table = _scan!.Components[0].AcTable;
        Span<byte> stats = _ac.AsSpan(table * AcBins, AcBins);
        int kx = _header.ArithmeticKx[table];
        for (int k = ss; k <= se; k++)
        {
            int st = 3 * (k - 1);
            if (Decode(ref stats[st]) != 0)
            {
                break;
            }

            while (Decode(ref stats[st + 1]) == 0)
            {
                st += 3;
                if (++k > se)
                {
                    Failed = true;
                    return;
                }
            }

            int value = DecodeAc(stats, st, k, kx);
            if (Failed)
            {
                return;
            }

            block[k] = (short)(value << al);
        }
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

        Span<byte> stats = _ac.AsSpan(_scan!.Components[0].AcTable * AcBins, AcBins);
        int p1 = 1 << al;
        int m1 = -1 << al;
        int last = se;
        while (last > 0 && block[last] == 0)
        {
            last--;
        }

        for (int k = ss; k <= se; k++)
        {
            int st = 3 * (k - 1);
            if (k > last && Decode(ref stats[st]) != 0)
            {
                break;
            }

            while (true)
            {
                ref short coefficient = ref block[k];
                if (coefficient != 0)
                {
                    if (Decode(ref stats[st + 2]) != 0)
                    {
                        coefficient = (short)(coefficient + (coefficient < 0 ? m1 : p1));
                    }

                    break;
                }

                if (Decode(ref stats[st + 1]) != 0)
                {
                    coefficient = (short)(Decode(ref _fixed) != 0 ? m1 : p1);
                    break;
                }

                st += 3;
                if (++k > se)
                {
                    Failed = true;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Decodes one lossless difference (T.81 H.1.2.3) with conditioning table <paramref name="table"/>, given the differences
    /// coded for the sample to the left (<paramref name="left"/>) and above (<paramref name="above"/>).
    /// </summary>
    internal int DecodeDifference(int table, int left, int above)
    {
        int lower = _header.ArithmeticLower[table];
        int upper = _header.ArithmeticUpper[table];
        int b = Category(above, lower, upper);
        int s0 = (20 * Category(left, lower, upper)) + (4 * b);
        return DecodeMagnitude(_dc.AsSpan(table * DcBins, DcBins), s0, b >= 3 ? 129 : 100, out _);
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
    private int DecodeDc(int table, ref int context)
    {
        if (Failed)
        {
            return 0;
        }

        int value = DecodeMagnitude(_dc.AsSpan(table * DcBins, DcBins), context, 20, out int bound);
        if (value == 0)
        {
            context = 0;
            return 0;
        }

        // The category from the magnitude bound, as figure F.10 sets it out (equivalent to comparing the difference itself).
        int sign = value < 0 ? 4 : 0;
        context = bound < ((1 << _header.ArithmeticLower[table]) >> 1) ? 0
            : bound > ((1 << _header.ArithmeticUpper[table]) >> 1) ? 12 + sign
            : 4 + sign;
        return value;
    }

    /// <summary>
    /// Decodes a DC or lossless difference: the zero decision at <paramref name="s0"/>, the sign, then the magnitude category from
    /// <paramref name="x1"/> and the magnitude bits (figures F.19, F.23 and F.24). <paramref name="bound"/> is the category's bound.
    /// </summary>
    private int DecodeMagnitude(Span<byte> stats, int s0, int x1, out int bound)
    {
        bound = 0;
        if (Decode(ref stats[s0]) == 0)
        {
            return 0;
        }

        int sign = Decode(ref stats[s0 + 1]);
        int st = s0 + 2 + sign;
        int m = Decode(ref stats[st]);
        if (m != 0)
        {
            st = x1;
            while (Decode(ref stats[st]) != 0)
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
            if (Decode(ref stats[st]) != 0)
            {
                value |= m;
            }
        }

        value++;
        return sign != 0 ? -value : value;
    }

    /// <summary>Decodes a nonzero AC coefficient whose zero-run ended at <paramref name="st"/> (figures F.21 to F.24).</summary>
    private int DecodeAc(Span<byte> stats, int st, int k, int kx)
    {
        int sign = Decode(ref _fixed);
        st += 2;
        int m = Decode(ref stats[st]);
        if (m != 0 && Decode(ref stats[st]) != 0)
        {
            m <<= 1;
            st = k <= kx ? 189 : 217;
            while (Decode(ref stats[st]) != 0)
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
            if (Decode(ref stats[st]) != 0)
            {
                value |= m;
            }
        }

        value++;
        return sign != 0 ? -value : value;
    }

    /// <summary>
    /// Decodes one binary decision with context <paramref name="context"/> (T.81 D.2.4 to D.2.6), renormalizing first as libjpeg
    /// does so the bytes are fetched on demand: A is kept at least X'8000', and C holds <c>_ct</c> bits beyond the 16 compared.
    /// </summary>
    private int Decode(ref byte context)
    {
        while (_a < 0x8000)
        {
            if (--_ct < 0)
            {
                _c = (_c << 8) | _reader.ArithmeticByte();
                if ((_ct += 8) < 0 && ++_ct == 0)
                {
                    // Both bytes of the initialization have arrived.
                    _a = 0x8000;
                }
            }

            _a <<= 1;
        }

        int sv = context;
        int entry = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(States), sv & 0x7F);
        int qe = entry >>> 16;
        int lps = entry & 0xFF;
        int mps = (entry >> 8) & 0xFF;
        int interval = _a - qe;
        _a = interval;
        interval <<= _ct;
        if (_c >= interval)
        {
            // The LPS sub-interval, or an MPS where the intervals were exchanged (D.2.4, conditional exchange).
            _c -= interval;
            if (_a < qe)
            {
                _a = qe;
                context = (byte)((sv & 0x80) ^ mps);
            }
            else
            {
                _a = qe;
                context = (byte)((sv & 0x80) ^ lps);
                sv ^= 0x80;
            }
        }
        else if (_a < 0x8000)
        {
            if (_a < qe)
            {
                context = (byte)((sv & 0x80) ^ lps);
                sv ^= 0x80;
            }
            else
            {
                context = (byte)((sv & 0x80) ^ mps);
            }
        }

        return sv >> 7;
    }

    private static int State(int qe, int nextLps, int nextMps, int switchMps)
    {
        return (qe << 16) | (nextMps << 8) | (switchMps << 7) | nextLps;
    }
}
