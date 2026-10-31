using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Writes the scans of a progressive file (T.81 annex G) from quantized blocks held for the whole image: libjpeg's
/// <c>jpeg_simple_progression</c> script of spectral selection and successive approximation, each scan coded twice, first only
/// counting symbols so it gets its own optimal Huffman tables, then for real; or once, arithmetic-coded with the models of annex
/// G.1.3. Restart markers follow the restart interval in MCUs, as in a sequential file. The blocks are put in zigzag order first, so each scan finds a block's coefficients in its band, and
/// those it codes, from bit masks.
/// </summary>
internal sealed class JpegProgressiveEncoder
{
    /// <summary>The most correction bits held for one end-of-band run before it is written out (libjpeg's MAX_CORR_BITS).</summary>
    private const int MaxCorrectionBits = 1000;

    private readonly Component[] _components;
    private readonly int _mcusX;
    private readonly int _mcusY;
    private readonly int _blocksPerMcu;
    private readonly int _restartInterval;
    private readonly Func<int, short[]> _rows;
    private readonly long[] _counts = new long[4 * 256];
    private readonly JpegHuffmanCodes[] _codes = new JpegHuffmanCodes[4];
    private readonly int[] _predictors;
    private readonly byte[] _corrections = new byte[MaxCorrectionBits + 64];
    private readonly byte[] _blockCorrections = new byte[64];
    private JpegBitWriter? _writer;
    private JpegArithmeticEncoder? _arithmetic;
    private int _table;
    private int _eobRun;
    private int _pendingCorrections;
    private int _restart;

    /// <summary>Initializes the encoder.</summary>
    /// <param name="components">The frame's components, in frame order.</param>
    /// <param name="mcusX">The MCUs across.</param>
    /// <param name="mcusY">The MCU rows.</param>
    /// <param name="restartInterval">The MCUs between restart markers, or 0.</param>
    /// <param name="rows">Returns MCU row <c>my</c>'s blocks: each MCU's blocks in turn, component by component, row by row.</param>
    internal JpegProgressiveEncoder(Component[] components, int mcusX, int mcusY, int restartInterval, Func<int, short[]> rows)
    {
        _components = components;
        _mcusX = mcusX;
        _mcusY = mcusY;
        _blocksPerMcu = components.Sum(c => c.H * c.V);
        _restartInterval = restartInterval;
        _rows = rows;
        _predictors = new int[components.Length];
    }

    /// <summary>Writes a header segment: the marker and the payload behind its length.</summary>
    internal delegate void SegmentWriter(JpegBitWriter writer, byte marker, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Returns the scans libjpeg's <c>jpeg_simple_progression</c> writes: for YCbCr its ten-scan script, for any other component
    /// count its general one (six scans for gray, eighteen for four components).
    /// </summary>
    internal static Scan[] Script(int components, bool ycbcr)
    {
        if (components == 3 && ycbcr)
        {
            return
            [
                new([0, 1, 2], 0, 0, 0, 1),
                new([0], 1, 5, 0, 2),
                new([2], 1, 63, 0, 1),
                new([1], 1, 63, 0, 1),
                new([0], 6, 63, 0, 2),
                new([0], 1, 63, 2, 1),
                new([0, 1, 2], 0, 0, 1, 0),
                new([2], 1, 63, 1, 0),
                new([1], 1, 63, 1, 0),
                new([0], 1, 63, 1, 0),
            ];
        }

        int[] all = [.. Enumerable.Range(0, components)];
        List<Scan> scans = [new(all, 0, 0, 0, 1)];
        AddBands(scans, components, 1, 5, 0, 2);
        AddBands(scans, components, 6, 63, 0, 2);
        AddBands(scans, components, 1, 63, 2, 1);
        scans.Add(new(all, 0, 0, 1, 0));
        AddBands(scans, components, 1, 63, 1, 0);
        return [.. scans];
    }

    /// <summary>
    /// Writes every scan: DHT for the tables it uses (unless <paramref name="arithmetic"/>), SOS and its entropy-coded data, then
    /// the next. The rows' blocks are left in zigzag order.
    /// </summary>
    internal void Write(JpegBitWriter writer, Scan[] script, SegmentWriter segment, bool arithmetic = false)
    {
        ToZigZag();
        _arithmetic = arithmetic ? new JpegArithmeticEncoder(writer) : null;
        foreach (Scan scan in script)
        {
            bool dc = scan.Ss == 0;
            if (!arithmetic && !(dc && scan.Ah > 0))
            {
                Array.Clear(_counts);
                _writer = null;
                Code(scan);
                foreach (int t in scan.Components.Select(c => _components[c].Table).Distinct().Order())
                {
                    _codes[t] = JpegHuffmanCodes.Optimal(_counts.AsSpan(t * 256, 256));
                    byte[] dht = new byte[17 + _codes[t].Symbols.Length];
                    dht[0] = (byte)(((dc ? 0 : 1) << 4) | t);
                    _codes[t].Counts.CopyTo(dht, 1);
                    _codes[t].Symbols.CopyTo(dht, 17);
                    segment(writer, JpegMarker.Dht, dht);
                }
            }

            // As libjpeg writes them: a DC scan names no AC table, and a DC refinement or AC scan names table 0 where it uses none.
            byte[] sos = new byte[1 + (2 * scan.Components.Length) + 3];
            sos[0] = (byte)scan.Components.Length;
            for (int i = 0; i < scan.Components.Length; i++)
            {
                Component c = _components[scan.Components[i]];
                int td = dc && scan.Ah == 0 ? c.Table : 0;
                int ta = dc ? 0 : c.Table;
                sos[1 + (2 * i)] = (byte)c.Id;
                sos[2 + (2 * i)] = (byte)((td << 4) | ta);
            }

            sos[^3] = (byte)scan.Ss;
            sos[^2] = (byte)scan.Se;
            sos[^1] = (byte)((scan.Ah << 4) | scan.Al);
            segment(writer, JpegMarker.Sos, sos);
            _writer = writer;
            _arithmetic?.Restart();
            Code(scan);
            _arithmetic?.Finish();
            writer.Align();
        }
    }

    /// <summary>Reorders every block of every MCU row from natural to zigzag order, in place.</summary>
    private void ToZigZag()
    {
        Span<short> natural = stackalloc short[64];
        ReadOnlySpan<byte> zigzag = JpegTables.ZigZag;
        int blocks = _mcusX * _blocksPerMcu;
        for (int my = 0; my < _mcusY; my++)
        {
            short[] row = _rows(my);
            for (int b = 0; b < blocks; b++)
            {
                Span<short> block = row.AsSpan(b * 64, 64);
                block.CopyTo(natural);
                for (int k = 0; k < 64; k++)
                {
                    block[k] = natural[zigzag[k]];
                }
            }
        }
    }

    /// <summary>Returns a bit for each coefficient whose magnitude is at least <paramref name="threshold"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong AtLeast(ReadOnlySpan<short> block, int threshold)
    {
        ref short first = ref MemoryMarshal.GetReference(block);
        Vector256<short> below = Vector256.Create((short)(threshold - 1));
        Vector256<short> a = Vector256.GreaterThan(Vector256.Abs(Vector256.LoadUnsafe(ref first)), below);
        Vector256<short> b = Vector256.GreaterThan(Vector256.Abs(Vector256.LoadUnsafe(ref first, 16)), below);
        Vector256<short> c = Vector256.GreaterThan(Vector256.Abs(Vector256.LoadUnsafe(ref first, 32)), below);
        Vector256<short> d = Vector256.GreaterThan(Vector256.Abs(Vector256.LoadUnsafe(ref first, 48)), below);
        ulong low = Vector256.Narrow(a, b).ExtractMostSignificantBits();
        ulong high = Vector256.Narrow(c, d).ExtractMostSignificantBits();
        return low | (high << 32);
    }

    /// <summary>Returns the bits <paramref name="ss"/> to <paramref name="se"/>.</summary>
    internal static ulong Band(int ss, int se) => (se == 63 ? ulong.MaxValue : (1UL << (se + 1)) - 1) & ~((1UL << ss) - 1);

    private static void AddBands(List<Scan> scans, int components, int ss, int se, int ah, int al)
    {
        for (int c = 0; c < components; c++)
        {
            scans.Add(new([c], ss, se, ah, al));
        }
    }

    /// <summary>Codes (or, without a writer, counts) one scan, MCU by MCU, with restart markers where they are due.</summary>
    private void Code(Scan scan)
    {
        Array.Clear(_predictors);
        _eobRun = 0;
        _pendingCorrections = 0;
        _restart = 0;
        int mcu = 0;
        if (scan.Components.Length > 1)
        {
            for (int my = 0; my < _mcusY; my++)
            {
                short[] row = _rows(my);
                for (int mx = 0; mx < _mcusX; mx++, mcu++)
                {
                    Restart(mcu);
                    foreach (int ci in scan.Components)
                    {
                        Component c = _components[ci];
                        for (int n = 0; n < c.H * c.V; n++)
                        {
                            CodeBlock(scan, ci, row.AsSpan(((mx * _blocksPerMcu) + c.Offset + n) * 64, 64));
                        }
                    }
                }
            }
        }
        else
        {
            // A scan of one component codes only its blocks inside the image, each its own MCU (T.81 A.2.2).
            int ci = scan.Components[0];
            Component c = _components[ci];
            for (int by = 0; by < c.BlocksHigh; by++)
            {
                short[] row = _rows(by / c.V);
                int inRow = c.Offset + ((by % c.V) * c.H);
                for (int bx = 0; bx < c.BlocksWide; bx++, mcu++)
                {
                    Restart(mcu);
                    int b = ((bx / c.H) * _blocksPerMcu) + inRow + (bx % c.H);
                    CodeBlock(scan, ci, row.AsSpan(b * 64, 64));
                }
            }
        }

        EmitEobRun();
    }

    private void Restart(int mcu)
    {
        if (_restartInterval == 0 || mcu == 0 || mcu % _restartInterval != 0)
        {
            return;
        }

        EmitEobRun();
        if (_writer is not null)
        {
            _arithmetic?.Finish();
            _writer.Align();
            _writer.Marker((byte)(JpegMarker.Rst0 + _restart));
        }

        _restart = (_restart + 1) & 7;
        Array.Clear(_predictors);
        _arithmetic?.Restart();
    }

    private void CodeBlock(Scan scan, int component, ReadOnlySpan<short> block)
    {
        _table = _components[component].Table;
        if (_arithmetic is not null)
        {
            CodeArithmetic(scan, component, block);
            return;
        }

        if (scan.Ss == 0)
        {
            if (scan.Ah == 0)
            {
                // DC first: the point-transformed DC, as a difference from the last block's (G.1.2.1).
                int value = block[0] >> scan.Al;
                int diff = value - _predictors[component];
                _predictors[component] = value;
                int size = Size(diff);
                Symbol(size);
                Bits(diff - (diff < 0 ? 1 : 0), size);
            }
            else
            {
                // DC refinement: one bit, the next bit down (G.1.2.1).
                Bits(block[0] >> scan.Al, 1);
            }
        }
        else if (scan.Ah == 0)
        {
            AcFirst(scan, block);
        }
        else
        {
            AcRefine(scan, block);
        }
    }

    /// <summary>Codes one block of a scan with the arithmetic coder (T.81 G.1.3): the DC and AC models of each kind of scan.</summary>
    private void CodeArithmetic(Scan scan, int component, ReadOnlySpan<short> block)
    {
        JpegArithmeticEncoder coder = _arithmetic!;
        if (scan.Ss == 0 && scan.Ah == 0)
        {
            coder.EncodeDcFirst(block[0] >> scan.Al, component, ref _predictors[component], _table);
        }
        else if (scan.Ss == 0)
        {
            coder.EncodeDcRefine((block[0] >> scan.Al) & 1);
        }
        else if (scan.Ah == 0)
        {
            coder.EncodeAcFirst(block, _table, scan.Ss, scan.Se, scan.Al);
        }
        else
        {
            coder.EncodeAcRefine(block, _table, scan.Ss, scan.Se, scan.Ah, scan.Al);
        }
    }

    /// <summary>An AC first scan's block: zero runs, sizes and values of the point-transformed band, with runs of empty bands (G.1.2.2).</summary>
    private void AcFirst(Scan scan, ReadOnlySpan<short> block)
    {
        ulong coded = AtLeast(block, 1 << scan.Al) & Band(scan.Ss, scan.Se);
        int last = scan.Ss - 1;
        while (coded != 0)
        {
            int k = BitOperations.TrailingZeroCount(coded);
            int run = k - last - 1;
            EmitEobRun();
            while (run > 15)
            {
                Symbol(0xF0);
                run -= 16;
            }

            int coefficient = block[k];
            int magnitude = Math.Abs(coefficient) >> scan.Al;
            int size = Size(magnitude);
            Symbol((run << 4) | size);
            Bits(coefficient < 0 ? ~magnitude : magnitude, size);
            last = k;
            coded &= coded - 1;
        }

        if (last < scan.Se)
        {
            _eobRun++;
            if (_eobRun == 0x7FFF)
            {
                EmitEobRun();
            }
        }
    }

    /// <summary>
    /// An AC refinement scan's block (G.1.2.3): newly nonzero coefficients are coded with their sign, and every coefficient that
    /// was already nonzero gets its next bit as a correction bit, sent after the next code; the last newly nonzero one decides
    /// whether trailing runs of 16 zeros fold into the end of band.
    /// </summary>
    private void AcRefine(Scan scan, ReadOnlySpan<short> block)
    {
        ulong band = Band(scan.Ss, scan.Se);
        ulong coded = AtLeast(block, 1 << scan.Al) & band;
        ulong fresh = coded & ~AtLeast(block, 2 << scan.Al);
        int end = fresh == 0 ? 0 : 63 - BitOperations.LeadingZeroCount(fresh);
        int run = 0;
        int corrections = 0;
        int last = scan.Ss - 1;
        while (coded != 0)
        {
            int k = BitOperations.TrailingZeroCount(coded);
            coded &= coded - 1;
            run += k - last - 1;
            last = k;
            while (run > 15 && k <= end)
            {
                EmitEobRun();
                Symbol(0xF0);
                run -= 16;
                EmitCorrections(_blockCorrections, corrections);
                corrections = 0;
            }

            if ((fresh & (1UL << k)) == 0)
            {
                _blockCorrections[corrections++] = (byte)((Math.Abs(block[k]) >> scan.Al) & 1);
                continue;
            }

            EmitEobRun();
            Symbol((run << 4) | 1);
            Bits(block[k] < 0 ? 0 : 1, 1);
            EmitCorrections(_blockCorrections, corrections);
            corrections = 0;
            run = 0;
        }

        run += scan.Se - last;
        if (run > 0 || corrections > 0)
        {
            _eobRun++;
            _blockCorrections.AsSpan(0, corrections).CopyTo(_corrections.AsSpan(_pendingCorrections));
            _pendingCorrections += corrections;
            if (_eobRun == 0x7FFF || _pendingCorrections > MaxCorrectionBits - 64 + 1)
            {
                EmitEobRun();
            }
        }
    }

    /// <summary>Writes the pending run of empty bands (EOBn, G.1.2.2) and the correction bits held for it.</summary>
    private void EmitEobRun()
    {
        if (_eobRun == 0)
        {
            return;
        }

        int bits = BitOperations.Log2((uint)_eobRun);
        Symbol(bits << 4);
        Bits(_eobRun, bits);
        _eobRun = 0;
        EmitCorrections(_corrections, _pendingCorrections);
        _pendingCorrections = 0;
    }

    private void EmitCorrections(byte[] corrections, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Bits(corrections[i], 1);
        }
    }

    private void Symbol(int symbol)
    {
        if (_writer is null)
        {
            _counts[(_table * 256) + symbol]++;
        }
        else
        {
            JpegHuffmanCodes codes = _codes[_table];
            _writer.Put(codes.Code(symbol), codes.Length(symbol));
        }
    }

    private void Bits(int value, int count)
    {
        if (_writer is not null && count > 0)
        {
            _writer.Put(value, count);
        }
    }

    private static int Size(int value) => 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(value));

    /// <summary>A frame component: its id, sampling, table slot, blocks inside the image and first block within each MCU.</summary>
    internal readonly record struct Component(int Id, int H, int V, int Table, int BlocksWide, int BlocksHigh, int Offset);

    /// <summary>One scan: its components (frame indexes), spectral band and successive approximation bits.</summary>
    internal sealed record Scan(int[] Components, int Ss, int Se, int Ah, int Al);
}
