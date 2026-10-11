using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Jpeg2000;

/// <summary>
/// Tier 1 of the JPEG 2000 decoder (T.800 Annex D): decodes a code-block's bit-planes with the significance propagation,
/// magnitude refinement and cleanup passes over the MQ decoder, in every code-block style. One instance per thread; it reuses its
/// buffers from block to block.
/// </summary>
/// <remarks>
/// Magnitudes are kept doubled, with a half step below the last bit-plane decoded, so a coefficient reconstructs at the middle of
/// its interval (r = 1/2, E.1.1.2) and a fully decoded reversible coefficient halves back exactly.
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

    /// <summary>The zero coding context by orientation (LL, HL, LH, HH) and neighbour significance (Table D.1).</summary>
    private static readonly byte[] ZeroContexts = BuildZeroContexts();

    /// <summary>The sign coding context and XOR bit (bit 5) by direct neighbours' significance and signs (Table D.3).</summary>
    private static readonly byte[] SignContexts = BuildSignContexts();

    private readonly MqDecoder _mq = new();
    private readonly byte[] _contexts = new byte[19];
    private uint[] _flags = [];
    private int[] _magnitudes = [];
    private byte[] _bytes = [];
    private int _width;
    private int _height;
    private int _stride;
    private bool _causal;

    // The raw (bypass) decoder's state.
    private int _rawPosition;
    private int _rawEnd;
    private int _rawByte;
    private int _rawBits;

    /// <summary>Gets or sets how many bytes the decoders read past their segments' ends, summed over blocks, for damage checks.</summary>
    internal int Overrun { get; set; }

    /// <summary>
    /// Decodes a code-block and writes its coefficients, dequantized, into <paramref name="integers"/> or <paramref name="floats"/> from
    /// <paramref name="offset"/> with rows <paramref name="stride"/> apart: integers for the reversible path, else floats scaled
    /// by the band's step.
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
        Prepare(width, height, (tc.Style.BlockStyle & 8) != 0);
        Gather(block);
        Run(block, band.Orientation, tc.Style.BlockStyle, planes, passes);
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

    private void Prepare(int width, int height, bool causal)
    {
        _width = width;
        _height = height;
        _stride = width + 2;
        _causal = causal;
        int flags = _stride * (height + 2);
        if (_flags.Length < flags)
        {
            _flags = new uint[flags];
        }
        else
        {
            _flags.AsSpan(0, flags).Clear();
        }

        int samples = width * height;
        if (_magnitudes.Length < samples)
        {
            _magnitudes = new int[samples];
        }
        else
        {
            _magnitudes.AsSpan(0, samples).Clear();
        }
    }

    /// <summary>Copies the block's chunks into one buffer, segment after segment.</summary>
    private void Gather(J2kCodeBlock block)
    {
        if (_bytes.Length < block.Bytes + 2)
        {
            _bytes = new byte[Math.Max(block.Bytes + 2, _bytes.Length * 2)];
        }

        int at = 0;
        foreach (ReadOnlyMemory<byte> chunk in block.Chunks)
        {
            chunk.Span.CopyTo(_bytes.AsSpan(at));
            at += chunk.Length;
        }
    }

    private void ResetContexts()
    {
        Array.Clear(_contexts);
        _contexts[0] = 4 << 1;
        _contexts[RunContext] = 3 << 1;
        _contexts[UniformContext] = 46 << 1;
    }

    /// <summary>Runs the coding passes, starting each segment's decoder (D.4.1) and resetting contexts as the style says.</summary>
    private void Run(J2kCodeBlock block, int orientation, int style, int planes, int passes)
    {
        bool bypass = (style & 1) != 0;
        bool reset = (style & 2) != 0;
        bool segmentation = (style & 0x20) != 0;
        ResetContexts();
        int plane = planes - 1;
        int type = 2;
        int pass = 0;
        int segmentStart = 0;
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
                        _mq.Start(_bytes, segmentStart, segment.Length);
                    }
                }

                switch (type)
                {
                    case 0:
                        if (raw)
                        {
                            SignificanceRaw(plane);
                        }
                        else
                        {
                            Significance(plane, orientation);
                        }

                        break;
                    case 1:
                        if (raw)
                        {
                            RefinementRaw(plane);
                        }
                        else
                        {
                            Refinement(plane);
                        }

                        break;
                    default:
                        Cleanup(plane, orientation, segmentation);
                        break;
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

            if (!raw)
            {
                Overrun += _mq.Overrun;
            }

            segmentStart += segment.Length;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Masked(uint f, int y) => _causal && (y & 3) == 3 ? f & ~South : f;

    /// <summary>Marks a coefficient significant and tells its neighbours.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Significant(ref uint flags, int stride, bool negative)
    {
        flags |= Sig | (negative ? Neg : 0);
        Unsafe.Subtract(ref flags, stride + 1) |= SigSE;
        Unsafe.Subtract(ref flags, stride) |= SigS | (negative ? NegS : 0);
        Unsafe.Subtract(ref flags, stride - 1) |= SigSW;
        Unsafe.Subtract(ref flags, 1) |= SigE | (negative ? NegE : 0);
        Unsafe.Add(ref flags, 1) |= SigW | (negative ? NegW : 0);
        Unsafe.Add(ref flags, stride - 1) |= SigNE;
        Unsafe.Add(ref flags, stride) |= SigN | (negative ? NegN : 0);
        Unsafe.Add(ref flags, stride + 1) |= SigNW;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SignIndex(uint f) => (int)(((f >> 1) & 1) | ((f >> 2) & 2) | ((f >> 2) & 4) | ((f >> 3) & 8) | ((f >> 4) & 0xF0));

    /// <summary>Decodes a newly significant coefficient's sign with the MQ decoder (D.3.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool DecodeSign(ref MqRegisters r, uint f)
    {
        int sc = SignContexts[SignIndex(f)];
        return (_mq.Decode(ref r, ref _contexts[sc & 0x1F]) ^ (sc >> 5)) != 0;
    }

    /// <summary>The significance propagation pass (D.3.1).</summary>
    private void Significance(int plane, int orientation)
    {
        int stride = _stride;
        int value = 3 << plane;
        ref uint flags0 = ref MemoryMarshal.GetArrayDataReference(_flags);
        ref int mag0 = ref MemoryMarshal.GetArrayDataReference(_magnitudes);
        ReadOnlySpan<byte> zc = ZeroContexts.AsSpan(orientation << 8, 256);
        MqRegisters r = _mq.Registers;
        for (int y0 = 0; y0 < _height; y0 += 4)
        {
            int y1 = Math.Min(y0 + 4, _height);
            for (int x = 0; x < _width; x++)
            {
                for (int y = y0; y < y1; y++)
                {
                    ref uint flags = ref Unsafe.Add(ref flags0, ((y + 1) * stride) + x + 1);
                    uint f = Masked(flags, y);
                    if ((f & (Sig | Visit)) != 0 || (f & Neighbours) == 0)
                    {
                        continue;
                    }

                    if (_mq.Decode(ref r, ref _contexts[zc[(int)(f & Neighbours)]]) != 0)
                    {
                        bool negative = DecodeSign(ref r, f);
                        Unsafe.Add(ref mag0, (y * _width) + x) = value;
                        Significant(ref flags, stride, negative);
                    }

                    flags |= Visit;
                }
            }
        }

        _mq.Registers = r;
    }

    /// <summary>The magnitude refinement pass (D.3.3).</summary>
    private void Refinement(int plane)
    {
        int stride = _stride;
        int half = 1 << plane;
        ref uint flags0 = ref MemoryMarshal.GetArrayDataReference(_flags);
        ref int mag0 = ref MemoryMarshal.GetArrayDataReference(_magnitudes);
        MqRegisters r = _mq.Registers;
        for (int y0 = 0; y0 < _height; y0 += 4)
        {
            int y1 = Math.Min(y0 + 4, _height);
            for (int x = 0; x < _width; x++)
            {
                for (int y = y0; y < y1; y++)
                {
                    ref uint flags = ref Unsafe.Add(ref flags0, ((y + 1) * stride) + x + 1);
                    uint f = flags;
                    if ((f & (Sig | Visit)) != Sig)
                    {
                        continue;
                    }

                    int context = (f & Refined) != 0 ? 16 : (Masked(f, y) & Neighbours) != 0 ? 15 : 14;
                    int bit = _mq.Decode(ref r, ref _contexts[context]);
                    ref int m = ref Unsafe.Add(ref mag0, (y * _width) + x);
                    m += bit != 0 ? half : -half;
                    flags = f | Refined;
                }
            }
        }

        _mq.Registers = r;
    }

    /// <summary>The cleanup pass (D.3.4), with run-length decoding of insignificant columns, then the segmentation symbol.</summary>
    private void Cleanup(int plane, int orientation, bool segmentation)
    {
        int stride = _stride;
        int value = 3 << plane;
        ref uint flags0 = ref MemoryMarshal.GetArrayDataReference(_flags);
        ref int mag0 = ref MemoryMarshal.GetArrayDataReference(_magnitudes);
        ReadOnlySpan<byte> zc = ZeroContexts.AsSpan(orientation << 8, 256);
        MqRegisters r = _mq.Registers;
        for (int y0 = 0; y0 < _height; y0 += 4)
        {
            int y1 = Math.Min(y0 + 4, _height);
            for (int x = 0; x < _width; x++)
            {
                ref uint top = ref Unsafe.Add(ref flags0, ((y0 + 1) * stride) + x + 1);
                int y = y0;
                if (y1 - y0 == 4
                    && ((top | Unsafe.Add(ref top, stride) | Unsafe.Add(ref top, 2 * stride)) & (Sig | Visit | Neighbours)) == 0
                    && (Masked(Unsafe.Add(ref top, 3 * stride), 3) & (Sig | Visit | Neighbours)) == 0)
                {
                    // Run-length mode: one decision says whether any of the four becomes significant.
                    if (_mq.Decode(ref r, ref _contexts[RunContext]) == 0)
                    {
                        continue;
                    }

                    int run = _mq.Decode(ref r, ref _contexts[UniformContext]) << 1;
                    run |= _mq.Decode(ref r, ref _contexts[UniformContext]);
                    y = y0 + run;
                    ref uint flags = ref Unsafe.Add(ref top, run * stride);
                    bool negative = DecodeSign(ref r, Masked(flags, y));
                    Unsafe.Add(ref mag0, (y * _width) + x) = value;
                    Significant(ref flags, stride, negative);
                    y++;
                }

                for (; y < y1; y++)
                {
                    ref uint flags = ref Unsafe.Add(ref flags0, ((y + 1) * stride) + x + 1);
                    uint f = Masked(flags, y);
                    if ((f & (Sig | Visit)) == 0 && _mq.Decode(ref r, ref _contexts[zc[(int)(f & Neighbours)]]) != 0)
                    {
                        bool negative = DecodeSign(ref r, f);
                        Unsafe.Add(ref mag0, (y * _width) + x) = value;
                        Significant(ref flags, stride, negative);
                    }
                }

                for (y = y0; y < y1; y++)
                {
                    Unsafe.Add(ref top, (y - y0) * stride) &= ~Visit;
                }
            }
        }

        if (segmentation)
        {
            for (int i = 0; i < 4; i++)
            {
                _mq.Decode(ref r, ref _contexts[UniformContext]);
            }
        }

        _mq.Registers = r;
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
                Overrun++;
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
                    ref uint flags = ref _flags[((y + 1) * stride) + x + 1];
                    uint f = Masked(flags, y);
                    if ((f & (Sig | Visit)) != 0 || (f & Neighbours) == 0)
                    {
                        continue;
                    }

                    if (RawBit() != 0)
                    {
                        bool negative = RawBit() != 0;
                        _magnitudes[(y * _width) + x] = value;
                        Significant(ref flags, stride, negative);
                    }

                    flags |= Visit;
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
                    ref uint flags = ref _flags[((y + 1) * stride) + x + 1];
                    if ((flags & (Sig | Visit)) != Sig)
                    {
                        continue;
                    }

                    _magnitudes[(y * _width) + x] += RawBit() != 0 ? half : -half;
                    flags |= Refined;
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
        int flagStride = _stride;
        for (int y = 0; y < _height; y++)
        {
            ReadOnlySpan<int> magnitudes = _magnitudes.AsSpan(y * _width, _width);
            ReadOnlySpan<uint> flags = _flags.AsSpan(((y + 1) * flagStride) + 1, _width);
            if (reversible)
            {
                Span<int> row = integers.Slice(offset + (y * stride), _width);
                for (int x = 0; x < row.Length; x++)
                {
                    int m = magnitudes[x];
                    if (m >= threshold)
                    {
                        m >>= roi;
                    }

                    m >>= 1;
                    row[x] = (flags[x] & Neg) != 0 ? -m : m;
                }
            }
            else
            {
                Span<float> row = floats.Slice(offset + (y * stride), _width);
                for (int x = 0; x < row.Length; x++)
                {
                    int m = magnitudes[x];
                    if (m >= threshold)
                    {
                        m >>= roi;
                    }

                    float v = m * scale;
                    row[x] = (flags[x] & Neg) != 0 ? -v : v;
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
