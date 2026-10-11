using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Jpeg2000;

/// <summary>
/// Tier 1 of the JPEG 2000 decoder (T.800 Annex D): decodes a code-block's bit-planes with the significance propagation,
/// magnitude refinement and cleanup passes over the MQ decoder, in every code-block style. One instance per thread; it reuses its
/// buffers from block to block.
/// </summary>
/// <remarks>
/// <para>
/// Magnitudes are kept doubled, with a half step below the last bit-plane decoded, so a coefficient reconstructs at the middle of
/// its interval (r = 1/2, E.1.1.2) and a fully decoded reversible coefficient halves back exactly.
/// </para>
/// <para>
/// The passes keep the MQ registers in locals and decode inline, so each coefficient's flags (its own state and its
/// neighbours' significance) and magnitude sit side by side in one array, and the contexts, the zero coding table for the
/// block's orientation and the sign coding table share another: few enough live values that the loops stay in registers.
/// </para>
/// </remarks>
internal sealed class J2kTier1
{
    // Flag bits per coefficient: the significance of the eight neighbours, the sign of the four direct ones, then its own state.
    private const uint SigNW = 1 << 0;
    private const uint SigN = 1 << 1;
    private const uint SigNE = 1 << 2;
    private const uint SigW = 1 << 3;
    private const uint SigE = 1 << 4;
    private const uint SigSW = 1 << 5;
    private const uint SigS = 1 << 6;
    private const uint SigSE = 1 << 7;
    private const uint NegN = 1 << 8;
    private const uint NegW = 1 << 9;
    private const uint NegE = 1 << 10;
    private const uint NegS = 1 << 11;
    private const uint Sig = 1 << 12;
    private const uint Visit = 1 << 13;
    private const uint Refined = 1 << 14;
    private const uint Neg = 1 << 15;
    private const uint Neighbours = 0xFF;
    private const uint South = SigSW | SigS | SigSE | NegS;

    private const int RunContext = 17;
    private const int UniformContext = 18;

    // Where the tables lie in _tables: the 19 contexts' states, the zero coding contexts, the sign coding contexts.
    private const int ZeroBase = 32;
    private const int SignBase = ZeroBase + 256;
    private const int TablesLength = SignBase + 256;

    // The MQ probability table heads the byte buffer, so the passes reach it from the same base as the data.
    private const int StatesOffset = 1024;

    /// <summary>The zero coding context by orientation (LL, HL, LH, HH) and neighbour significance (Table D.1).</summary>
    private static readonly byte[] ZeroContexts = BuildZeroContexts();

    /// <summary>The sign coding context and XOR bit (bit 5) by direct neighbours' significance and signs (Table D.3).</summary>
    private static readonly byte[] SignContexts = BuildSignContexts();

    private readonly byte[] _tables = new byte[TablesLength];
    private Coefficient[] _coefficients = [];
    private byte[] _bytes = [];
    private int _width;
    private int _height;
    private int _stride;
    private bool _causal;
    private int _orientation = -1;

    // The MQ decoder's registers (T.800 C.3), which the passes copy into locals, and the end of the segment it decodes.
    private uint _a;
    private uint _c;
    private int _ct;
    private int _position;
    private int _end;

    // The raw (bypass) decoder's state.
    private int _rawPosition;
    private int _rawEnd;
    private int _rawByte;
    private int _rawBits;

    internal J2kTier1()
    {
        SignContexts.CopyTo(_tables, SignBase);
    }

    /// <summary>Gets or sets how many segments the decoders read past the end of, summed over blocks, for damage checks.</summary>
    internal int Overrun { get; set; }

    /// <summary>
    /// Decodes a code-block and writes its coefficients, dequantized, into <paramref name="integers"/> or <paramref name="floats"/>
    /// from <paramref name="offset"/> with rows <paramref name="stride"/> apart: integers for the reversible path, else floats
    /// scaled by the band's step.
    /// </summary>
    internal void Decode(J2kCodeBlock block, J2kBand band, J2kTileComponent tc, Span<int> integers, Span<float> floats, int offset, int stride)
    {
        int width = block.Width;
        int height = block.Height;
        int planes = band.MagnitudePlanes + tc.RoiShift - block.ZeroPlanes;
        int passes = 0;
        foreach (J2kSegment segment in block.Segments)
        {
            passes += segment.KeptPasses;
        }

        bool reversible = tc.Style.Reversible;
        if (planes <= 0 || passes == 0 || width <= 0 || height <= 0)
        {
            Clear(integers, floats, offset, stride, width, height, reversible);
            return;
        }

        if (planes > 30)
        {
            throw new InvalidDataException($"A code-block has {planes} bit-planes, more than 30.");
        }

        passes = Math.Min(passes, (3 * planes) - 2);
        Prepare(width, height, (tc.Style.BlockStyle & 8) != 0, band.Orientation);
        Gather(block);
        Run(block, tc.Style.BlockStyle, planes, passes);
        Write(band, tc, integers, floats, offset, stride, reversible);
    }

    private static void Clear(Span<int> integers, Span<float> floats, int offset, int stride, int width, int height, bool reversible)
    {
        for (int y = 0; y < height; y++)
        {
            if (reversible)
            {
                integers.Slice(offset + (y * stride), width).Clear();
            }
            else
            {
                floats.Slice(offset + (y * stride), width).Clear();
            }
        }
    }

    private void Prepare(int width, int height, bool causal, int orientation)
    {
        _width = width;
        _height = height;
        _stride = width + 2;
        _causal = causal;
        int size = _stride * (height + 2);
        if (_coefficients.Length < size)
        {
            _coefficients = new Coefficient[size];
        }
        else
        {
            _coefficients.AsSpan(0, size).Clear();
        }

        if (orientation != _orientation)
        {
            ZeroContexts.AsSpan(orientation << 8, 256).CopyTo(_tables.AsSpan(ZeroBase));
            _orientation = orientation;
        }
    }

    /// <summary>
    /// Copies the block's chunks into one buffer, segment after segment, each followed by 0xFF 0xFF: a marker, which the
    /// decoders read as an endless run of 1-bits (C.3.4), so they never test for the end.
    /// </summary>
    private void Gather(J2kCodeBlock block)
    {
        int size = StatesOffset + block.Bytes + (2 * block.Segments.Count) + 2;
        if (_bytes.Length < size)
        {
            _bytes = new byte[Math.Max(size, _bytes.Length * 2)];
            MemoryMarshal.AsBytes(MqDecoder.StateTable.AsSpan()).CopyTo(_bytes);
        }

        int chunk = 0;
        int offset = 0;
        int at = StatesOffset;
        foreach (J2kSegment segment in block.Segments)
        {
            int remaining = segment.Length;
            while (remaining > 0)
            {
                ReadOnlySpan<byte> bytes = block.Chunks[chunk].Span[offset..];
                int take = Math.Min(remaining, bytes.Length);
                bytes[..take].CopyTo(_bytes.AsSpan(at));
                at += take;
                remaining -= take;
                offset += take;
                if (offset == block.Chunks[chunk].Length)
                {
                    chunk++;
                    offset = 0;
                }
            }

            _bytes[at++] = 0xFF;
            _bytes[at++] = 0xFF;
        }

        _bytes[at] = 0xFF;
        _bytes[at + 1] = 0xFF;
    }

    private void ResetContexts()
    {
        _tables.AsSpan(0, 19).Clear();
        _tables[0] = 4 << 1;
        _tables[RunContext] = 3 << 1;
        _tables[UniformContext] = 46 << 1;
    }

    /// <summary>Runs the coding passes, starting each segment's decoder (D.4.1) and resetting contexts as the style says.</summary>
    private void Run(J2kCodeBlock block, int style, int planes, int passes)
    {
        bool bypass = (style & 1) != 0;
        bool reset = (style & 2) != 0;
        bool segmentation = (style & 0x20) != 0;
        ResetContexts();
        int plane = planes - 1;
        int type = 2;
        int pass = 0;
        int segmentStart = StatesOffset;
        bool raw = false;
        foreach (J2kSegment segment in block.Segments)
        {
            if (pass >= passes)
            {
                break;
            }

            int segmentPasses = Math.Min(segment.KeptPasses, passes - pass);
            for (int i = 0; i < segmentPasses; i++, pass++)
            {
                if (i == 0)
                {
                    raw = bypass && pass >= 10 && type != 2;
                    if (raw)
                    {
                        StartRaw(segmentStart, segment.Length);
                    }
                    else
                    {
                        StartMq(segmentStart, segment.Length);
                    }
                }

                if (raw)
                {
                    if (type == 0)
                    {
                        SignificanceRaw(plane);
                    }
                    else
                    {
                        RefinementRaw(plane);
                    }
                }
                else if (_causal)
                {
                    Pass<CausalMode>(type, plane, segmentation);
                }
                else
                {
                    Pass<NormalMode>(type, plane, segmentation);
                }

                if (reset && !raw)
                {
                    ResetContexts();
                }

                if (++type == 3)
                {
                    type = 0;
                    plane--;
                }
            }

            if (!raw && _position > _end)
            {
                Overrun++;
            }

            segmentStart += segment.Length + 2;
        }
    }

    private void Pass<TMode>(int type, int plane, bool segmentation)
        where TMode : struct, ICausal
    {
        switch (type)
        {
            case 0:
                Significance<TMode>(plane);
                break;
            case 1:
                Refinement<TMode>(plane);
                break;
            default:
                Cleanup<TMode>(plane, segmentation);
                break;
        }
    }

    /// <summary>A coefficient: its flags and its doubled magnitude.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Coefficient
    {
        internal uint Flags;
        internal int Magnitude;
    }

    /// <summary>Says at compile time whether contexts ignore the stripe below (vertically causal mode, D.7).</summary>
    private interface ICausal
    {
        static abstract bool On { get; }
    }

    private struct CausalMode : ICausal
    {
        public static bool On => true;
    }

    private struct NormalMode : ICausal
    {
        public static bool On => false;
    }

    /// <summary>Drops the south neighbours of a stripe's last row in vertically causal mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Masked<TMode>(uint f, int row)
        where TMode : struct, ICausal
    {
        return TMode.On && row == 3 ? f & ~South : f;
    }

    /// <summary>Marks a coefficient significant and tells its neighbours.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Significant(ref Coefficient p, int stride, bool negative)
    {
        p.Flags |= Sig | (negative ? Neg : 0);
        Unsafe.Subtract(ref p, stride + 1).Flags |= SigSE;
        Unsafe.Subtract(ref p, stride).Flags |= SigS | (negative ? NegS : 0);
        Unsafe.Subtract(ref p, stride - 1).Flags |= SigSW;
        Unsafe.Subtract(ref p, 1).Flags |= SigE | (negative ? NegE : 0);
        Unsafe.Add(ref p, 1).Flags |= SigW | (negative ? NegW : 0);
        Unsafe.Add(ref p, stride - 1).Flags |= SigNE;
        Unsafe.Add(ref p, stride).Flags |= SigN | (negative ? NegN : 0);
        Unsafe.Add(ref p, stride + 1).Flags |= SigNW;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignIndex(uint f) => (int)(((f >> 1) & 1) | ((f >> 2) & 2) | ((f >> 2) & 4) | ((f >> 3) & 8) | ((f >> 4) & 0xF0));

    /// <summary>Starts the MQ decoder on a segment (INITDEC, C.3.5).</summary>
    private void StartMq(int start, int length)
    {
        _end = start + length;
        _position = start;
        _c = (uint)_bytes[start] << 16;
        _ct = 0;
        ByteIn(ref _c, ref _ct, ref _position, ref MemoryMarshal.GetArrayDataReference(_bytes));
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    /// <summary>
    /// Decodes one decision (DECODE, C.3.2) with the registers in a pass's locals, renormalizing (RENORMD) with at most two
    /// byte reads, which the segment's 0xFF 0xFF padding keeps in bounds.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mq(ref uint a, ref uint c, ref int ct, ref int position, ref byte data, ref byte state)
    {
        uint s = state;
        uint entry = Unsafe.Add(ref Unsafe.As<byte, uint>(ref data), (nint)s);
        uint qe = entry & 0xFFFF;
        a -= qe;
        int d;
        if ((c >> 16) < qe)
        {
            if (a < qe)
            {
                d = (int)(s & 1);
                state = (byte)(entry >> 16);
            }
            else
            {
                d = (int)(s & 1) ^ 1;
                state = (byte)(entry >> 24);
            }

            a = qe;
        }
        else
        {
            c -= qe << 16;
            if ((a & 0x8000) != 0)
            {
                return (int)(s & 1);
            }

            if (a < qe)
            {
                d = (int)(s & 1) ^ 1;
                state = (byte)(entry >> 24);
            }
            else
            {
                d = (int)(s & 1);
                state = (byte)(entry >> 16);
            }
        }

        int shift = BitOperations.LeadingZeroCount(a) - 16;
        a <<= shift;
        if (shift <= ct)
        {
            c <<= shift;
            ct -= shift;
            return d;
        }

        c <<= ct;
        shift -= ct;
        ByteIn(ref c, ref ct, ref position, ref data);
        if (shift > ct)
        {
            c <<= ct;
            shift -= ct;
            ByteIn(ref c, ref ct, ref position, ref data);
        }

        c <<= shift;
        ct -= shift;
        return d;
    }

    /// <summary>Reads the next byte into C (BYTEIN, C.3.4): 7 bits after 0xFF, and 1-bits at a marker.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ByteIn(ref uint c, ref int ct, ref int position, ref byte data)
    {
        uint b1 = Unsafe.Add(ref data, position + 1);
        if (Unsafe.Add(ref data, position) == 0xFF)
        {
            if (b1 > 0x8F)
            {
                c += 0xFF00;
                ct = 8;
            }
            else
            {
                position++;
                c += b1 << 9;
                ct = 7;
            }
        }
        else
        {
            position++;
            c += b1 << 8;
            ct = 8;
        }
    }

    /// <summary>The significance propagation pass (D.3.1), skipping stripe columns with nothing to code.</summary>
    private void Significance<TMode>(int plane)
        where TMode : struct, ICausal
    {
        int stride = _stride;
        int width = _width;
        int height = _height;
        int value = 3 << plane;
        ref Coefficient first = ref MemoryMarshal.GetArrayDataReference(_coefficients);
        ref byte tables = ref MemoryMarshal.GetArrayDataReference(_tables);
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_bytes);
        uint a = _a;
        uint c = _c;
        int ct = _ct;
        int position = _position;
        for (int y0 = 0; y0 < height; y0 += 4)
        {
            ref Coefficient row = ref Unsafe.Add(ref first, ((y0 + 1) * stride) + 1);
            int rows = height - y0;
            if (rows >= 4)
            {
                for (int x = 0; x < width; x++)
                {
                    ref Coefficient p = ref Unsafe.Add(ref row, x);
                    uint f0 = p.Flags;
                    uint f1 = Unsafe.Add(ref p, stride).Flags;
                    uint f2 = Unsafe.Add(ref p, 2 * stride).Flags;
                    uint f3 = Unsafe.Add(ref p, 3 * stride).Flags;
                    if (((f0 | f1 | f2 | f3) & Neighbours) == 0 || (f0 & f1 & f2 & f3 & Sig) != 0)
                    {
                        // Nothing near anything significant, or everything significant already: nothing to code.
                        continue;
                    }

                    SignificanceOne<TMode>(ref p, 0, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    SignificanceOne<TMode>(ref Unsafe.Add(ref p, stride), 1, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    SignificanceOne<TMode>(ref Unsafe.Add(ref p, 2 * stride), 2, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    SignificanceOne<TMode>(ref Unsafe.Add(ref p, 3 * stride), 3, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    for (int k = 0; k < rows; k++)
                    {
                        SignificanceOne<TMode>(ref Unsafe.Add(ref row, x + (k * stride)), k, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    }
                }
            }
        }

        _a = a;
        _c = c;
        _ct = ct;
        _position = position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SignificanceOne<TMode>(ref Coefficient p, int row, int stride, int value, ref byte tables, ref uint a, ref uint c, ref int ct, ref int position, ref byte data)
        where TMode : struct, ICausal
    {
        uint f = Masked<TMode>(p.Flags, row);
        if ((f & (Sig | Visit)) != 0 || (f & Neighbours) == 0)
        {
            return;
        }

        if (Mq(ref a, ref c, ref ct, ref position, ref data, ref Unsafe.Add(ref tables, Unsafe.Add(ref tables, ZeroBase + (int)(f & Neighbours)))) != 0)
        {
            int sc = Unsafe.Add(ref tables, SignBase + SignIndex(f));
            bool negative = (Mq(ref a, ref c, ref ct, ref position, ref data, ref Unsafe.Add(ref tables, sc & 0x1F)) ^ (sc >> 5)) != 0;
            p.Magnitude = value;
            Significant(ref p, stride, negative);
        }

        p.Flags |= Visit;
    }

    /// <summary>The magnitude refinement pass (D.3.3), skipping stripe columns with nothing significant.</summary>
    private void Refinement<TMode>(int plane)
        where TMode : struct, ICausal
    {
        int stride = _stride;
        int width = _width;
        int height = _height;
        int half = 1 << plane;
        ref Coefficient first = ref MemoryMarshal.GetArrayDataReference(_coefficients);
        ref byte tables = ref MemoryMarshal.GetArrayDataReference(_tables);
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_bytes);
        uint a = _a;
        uint c = _c;
        int ct = _ct;
        int position = _position;
        for (int y0 = 0; y0 < height; y0 += 4)
        {
            ref Coefficient row = ref Unsafe.Add(ref first, ((y0 + 1) * stride) + 1);
            int rows = height - y0;
            if (rows >= 4)
            {
                for (int x = 0; x < width; x++)
                {
                    ref Coefficient p = ref Unsafe.Add(ref row, x);
                    if (((p.Flags | Unsafe.Add(ref p, stride).Flags | Unsafe.Add(ref p, 2 * stride).Flags | Unsafe.Add(ref p, 3 * stride).Flags) & Sig) == 0)
                    {
                        continue;
                    }

                    RefinementOne<TMode>(ref p, 0, half, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    RefinementOne<TMode>(ref Unsafe.Add(ref p, stride), 1, half, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    RefinementOne<TMode>(ref Unsafe.Add(ref p, 2 * stride), 2, half, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    RefinementOne<TMode>(ref Unsafe.Add(ref p, 3 * stride), 3, half, ref tables, ref a, ref c, ref ct, ref position, ref data);
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    for (int k = 0; k < rows; k++)
                    {
                        RefinementOne<TMode>(ref Unsafe.Add(ref row, x + (k * stride)), k, half, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    }
                }
            }
        }

        _a = a;
        _c = c;
        _ct = ct;
        _position = position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RefinementOne<TMode>(ref Coefficient p, int row, int half, ref byte tables, ref uint a, ref uint c, ref int ct, ref int position, ref byte data)
        where TMode : struct, ICausal
    {
        uint f = p.Flags;
        if ((f & (Sig | Visit)) != Sig)
        {
            return;
        }

        int context = (f & Refined) != 0 ? 16 : (Masked<TMode>(f, row) & Neighbours) != 0 ? 15 : 14;
        int bit = Mq(ref a, ref c, ref ct, ref position, ref data, ref Unsafe.Add(ref tables, context));
        p.Magnitude += bit != 0 ? half : -half;
        p.Flags = f | Refined;
    }

    /// <summary>The cleanup pass (D.3.4), with run-length decoding of insignificant columns, then the segmentation symbol.</summary>
    private void Cleanup<TMode>(int plane, bool segmentation)
        where TMode : struct, ICausal
    {
        int stride = _stride;
        int width = _width;
        int height = _height;
        int value = 3 << plane;
        ref Coefficient first = ref MemoryMarshal.GetArrayDataReference(_coefficients);
        ref byte tables = ref MemoryMarshal.GetArrayDataReference(_tables);
        ref byte data = ref MemoryMarshal.GetArrayDataReference(_bytes);
        uint a = _a;
        uint c = _c;
        int ct = _ct;
        int position = _position;
        for (int y0 = 0; y0 < height; y0 += 4)
        {
            ref Coefficient row = ref Unsafe.Add(ref first, ((y0 + 1) * stride) + 1);
            int rows = height - y0;
            if (rows >= 4)
            {
                for (int x = 0; x < width; x++)
                {
                    ref Coefficient p = ref Unsafe.Add(ref row, x);
                    uint f0 = p.Flags;
                    uint f1 = Unsafe.Add(ref p, stride).Flags;
                    uint f2 = Unsafe.Add(ref p, 2 * stride).Flags;
                    uint f3 = Unsafe.Add(ref p, 3 * stride).Flags;
                    if ((f0 & f1 & f2 & f3 & Sig) != 0)
                    {
                        // All significant: nothing to code, only the visits to forget.
                        if (((f0 | f1 | f2 | f3) & Visit) != 0)
                        {
                            p.Flags = f0 & ~Visit;
                            Unsafe.Add(ref p, stride).Flags = f1 & ~Visit;
                            Unsafe.Add(ref p, 2 * stride).Flags = f2 & ~Visit;
                            Unsafe.Add(ref p, 3 * stride).Flags = f3 & ~Visit;
                        }

                        continue;
                    }

                    if (((f0 | f1 | f2 | Masked<TMode>(f3, 3)) & (Sig | Visit | Neighbours)) == 0)
                    {
                        // Run-length mode: one decision says whether any of the four becomes significant.
                        if (Mq(ref a, ref c, ref ct, ref position, ref data, ref Unsafe.Add(ref tables, RunContext)) == 0)
                        {
                            continue;
                        }

                        int run = Mq(ref a, ref c, ref ct, ref position, ref data, ref Unsafe.Add(ref tables, UniformContext)) << 1;
                        run |= Mq(ref a, ref c, ref ct, ref position, ref data, ref Unsafe.Add(ref tables, UniformContext));
                        ref Coefficient q = ref Unsafe.Add(ref p, run * stride);
                        int sc = Unsafe.Add(ref tables, SignBase + SignIndex(Masked<TMode>(q.Flags, run)));
                        bool negative = (Mq(ref a, ref c, ref ct, ref position, ref data, ref Unsafe.Add(ref tables, sc & 0x1F)) ^ (sc >> 5)) != 0;
                        q.Magnitude = value;
                        Significant(ref q, stride, negative);
                        for (int k = run + 1; k < 4; k++)
                        {
                            CleanupOne<TMode>(ref Unsafe.Add(ref p, k * stride), k, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                        }

                        continue;
                    }

                    CleanupOne<TMode>(ref p, 0, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    CleanupOne<TMode>(ref Unsafe.Add(ref p, stride), 1, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    CleanupOne<TMode>(ref Unsafe.Add(ref p, 2 * stride), 2, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    CleanupOne<TMode>(ref Unsafe.Add(ref p, 3 * stride), 3, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    for (int k = 0; k < rows; k++)
                    {
                        CleanupOne<TMode>(ref Unsafe.Add(ref row, x + (k * stride)), k, stride, value, ref tables, ref a, ref c, ref ct, ref position, ref data);
                    }
                }
            }
        }

        if (segmentation)
        {
            for (int i = 0; i < 4; i++)
            {
                Mq(ref a, ref c, ref ct, ref position, ref data, ref Unsafe.Add(ref tables, UniformContext));
            }
        }

        _a = a;
        _c = c;
        _ct = ct;
        _position = position;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CleanupOne<TMode>(ref Coefficient p, int row, int stride, int value, ref byte tables, ref uint a, ref uint c, ref int ct, ref int position, ref byte data)
        where TMode : struct, ICausal
    {
        uint f = Masked<TMode>(p.Flags, row);
        if ((f & (Sig | Visit)) != 0)
        {
            p.Flags &= ~Visit;
            return;
        }

        if (Mq(ref a, ref c, ref ct, ref position, ref data, ref Unsafe.Add(ref tables, Unsafe.Add(ref tables, ZeroBase + (int)(f & Neighbours)))) != 0)
        {
            int sc = Unsafe.Add(ref tables, SignBase + SignIndex(f));
            bool negative = (Mq(ref a, ref c, ref ct, ref position, ref data, ref Unsafe.Add(ref tables, sc & 0x1F)) ^ (sc >> 5)) != 0;
            p.Magnitude = value;
            Significant(ref p, stride, negative);
        }
    }

    private void StartRaw(int start, int length)
    {
        _rawPosition = start;
        _rawEnd = start + length;
        _rawByte = 0;
        _rawBits = 0;
    }

    /// <summary>Reads one raw bit (D.6): most significant first, skipping the stuffed bit after 0xFF, 1-bits past the end.</summary>
    private int RawBit()
    {
        if (_rawBits == 0)
        {
            bool afterFF = _rawByte == 0xFF;
            if (_rawPosition < _rawEnd)
            {
                _rawByte = _bytes[_rawPosition];
            }
            else
            {
                _rawByte = 0xFF;
                if (_rawPosition == _rawEnd)
                {
                    Overrun++;
                }
            }

            _rawPosition++;
            _rawBits = afterFF ? 7 : 8;
        }

        _rawBits--;
        return (_rawByte >> _rawBits) & 1;
    }

    /// <summary>The significance propagation pass in raw (bypass) mode.</summary>
    private void SignificanceRaw(int plane)
    {
        int stride = _stride;
        int value = 3 << plane;
        for (int y0 = 0; y0 < _height; y0 += 4)
        {
            int y1 = Math.Min(y0 + 4, _height);
            for (int x = 0; x < _width; x++)
            {
                for (int y = y0; y < y1; y++)
                {
                    ref Coefficient p = ref _coefficients[((y + 1) * stride) + x + 1];
                    uint f = _causal && (y & 3) == 3 ? p.Flags & ~South : p.Flags;
                    if ((f & (Sig | Visit)) != 0 || (f & Neighbours) == 0)
                    {
                        continue;
                    }

                    if (RawBit() != 0)
                    {
                        bool negative = RawBit() != 0;
                        p.Magnitude = value;
                        Significant(ref p, stride, negative);
                    }

                    p.Flags |= Visit;
                }
            }
        }
    }

    /// <summary>The magnitude refinement pass in raw (bypass) mode.</summary>
    private void RefinementRaw(int plane)
    {
        int stride = _stride;
        int half = 1 << plane;
        for (int y0 = 0; y0 < _height; y0 += 4)
        {
            int y1 = Math.Min(y0 + 4, _height);
            for (int x = 0; x < _width; x++)
            {
                for (int y = y0; y < y1; y++)
                {
                    ref Coefficient p = ref _coefficients[((y + 1) * stride) + x + 1];
                    if ((p.Flags & (Sig | Visit)) != Sig)
                    {
                        continue;
                    }

                    p.Magnitude += RawBit() != 0 ? half : -half;
                    p.Flags |= Refined;
                }
            }
        }
    }

    /// <summary>Writes the coefficients, signed, shifted down out of a region of interest and dequantized.</summary>
    private void Write(J2kBand band, J2kTileComponent tc, Span<int> integers, Span<float> floats, int offset, int stride, bool reversible)
    {
        int roi = tc.RoiShift;
        int threshold = roi > 0 ? 1 << Math.Min(roi + 1, 30) : int.MaxValue;
        float scale = band.Step * 0.5f;
        for (int y = 0; y < _height; y++)
        {
            ReadOnlySpan<Coefficient> source = _coefficients.AsSpan(((y + 1) * _stride) + 1, _width);
            if (reversible)
            {
                Span<int> row = integers.Slice(offset + (y * stride), _width);
                for (int x = 0; x < row.Length; x++)
                {
                    int m = source[x].Magnitude;
                    if (m >= threshold)
                    {
                        m >>= roi;
                    }

                    m >>= 1;
                    row[x] = (source[x].Flags & Neg) != 0 ? -m : m;
                }
            }
            else
            {
                Span<float> row = floats.Slice(offset + (y * stride), _width);
                for (int x = 0; x < row.Length; x++)
                {
                    int m = source[x].Magnitude;
                    if (m >= threshold)
                    {
                        m >>= roi;
                    }

                    float v = m * scale;
                    row[x] = (source[x].Flags & Neg) != 0 ? -v : v;
                }
            }
        }
    }

    private static byte[] BuildZeroContexts()
    {
        byte[] table = new byte[4 * 256];
        for (int orientation = 0; orientation < 4; orientation++)
        {
            for (int f = 0; f < 256; f++)
            {
                int h = ((f >> 3) & 1) + ((f >> 4) & 1);
                int v = ((f >> 1) & 1) + ((f >> 6) & 1);
                int d = (f & 1) + ((f >> 2) & 1) + ((f >> 5) & 1) + ((f >> 7) & 1);
                if (orientation == 1)
                {
                    (h, v) = (v, h);
                }

                int context;
                if (orientation == 3)
                {
                    int hv = h + v;
                    context = d >= 3 ? 8
                        : d == 2 ? (hv >= 1 ? 7 : 6)
                        : d == 1 ? (hv >= 2 ? 5 : hv == 1 ? 4 : 3)
                        : hv >= 2 ? 2 : hv;
                }
                else
                {
                    context = h == 2 ? 8
                        : h == 1 ? (v >= 1 ? 7 : d >= 1 ? 6 : 5)
                        : v == 2 ? 4
                        : v == 1 ? 3
                        : d >= 2 ? 2 : d;
                }

                table[(orientation << 8) | f] = (byte)context;
            }
        }

        return table;
    }

    private static byte[] BuildSignContexts()
    {
        byte[] table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            // Bits 0 to 3: N, W, E, S significant; bits 4 to 7: N, W, E, S negative.
            int n = (i & 1) == 0 ? 0 : (i & 0x10) != 0 ? -1 : 1;
            int w = (i & 2) == 0 ? 0 : (i & 0x20) != 0 ? -1 : 1;
            int e = (i & 4) == 0 ? 0 : (i & 0x40) != 0 ? -1 : 1;
            int s = (i & 8) == 0 ? 0 : (i & 0x80) != 0 ? -1 : 1;
            int h = Math.Clamp(w + e, -1, 1);
            int v = Math.Clamp(n + s, -1, 1);
            (int context, int xor) = (h, v) switch
            {
                (1, 1) => (13, 0),
                (1, 0) => (12, 0),
                (1, -1) => (11, 0),
                (0, 1) => (10, 0),
                (0, 0) => (9, 0),
                (0, -1) => (10, 1),
                (-1, 1) => (11, 1),
                (-1, 0) => (12, 1),
                _ => (13, 1),
            };
            table[i] = (byte)(context | (xor << 5));
        }

        return table;
    }
}
