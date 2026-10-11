using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Operations.Color;
using Egami.Pixels;

namespace Egami.Operations.Tone;

/// <summary>
/// A filter compiled for one pixel type, run row by row. Per-channel chains on 8- and 16-bit types run as exact lookup tables, one
/// per channel, built in double precision (256 or 65,536 entries; gray types need red, green and blue to share a curve). Chains of
/// color matrices alone run exactly as <see cref="ColorMatrixOperation"/> runs them. Anything else runs on straight float RGBA:
/// 8-bit types read their leading per-channel stages from exact float tables, and every other type converts through
/// <see cref="RgbaF32"/>.
/// </summary>
internal sealed class FilterProgram<TPixel> : IDisposable
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly FilterStage[] _stages;
    private Kind _kind;
    private readonly FilterContext _context = new();

    /// <summary>The RGBA channel of each sample in a pixel; -1 for a gray sample.</summary>
    private readonly int[]? _map;

    /// <summary>The byte tables, 256 entries per sample in memory order; the 16-bit tables, one per sample, null where it passes through.</summary>
    private readonly byte[]? _byteTables;
    private readonly ushort[]?[]? _wordTables;

    /// <summary>For the byte float path: 256 floats per RGBA channel from the leading per-channel stages, and the stages after them.</summary>
    private readonly float[]? _prefix;
    private readonly FilterStage[] _rest;

    /// <summary>For the byte threshold path: the stage, and each channel's intensity term per byte value.</summary>
    private ThresholdStage? _threshold;
    private double[]? _luma;

    /// <summary>For the byte linear-light path: the matrix, and each channel's linear value per byte.</summary>
    private readonly float[]? _linear;
    private readonly float[]? _decode;

    /// <summary>For the byte 3D-table path: the stage, and each byte's grid offset and fraction per axis.</summary>
    private readonly LutStage? _lutStage;
    private readonly int[]? _cells;
    private readonly float[]? _fractions;

    /// <summary>The steps of the encoding table: fine enough that its rounding is within a tenth of a level of the exact curve's.</summary>
    private const int EncodeSteps = 16383;

    /// <summary>The 8-bit sRGB encoding of linear values in steps of 1/16,383, small enough to stay in cache.</summary>
    private static readonly Lazy<byte[]> Encode8 = new(() =>
    {
        byte[] table = new byte[EncodeSteps + 1];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (byte)(int)((Srgb.FromLinear(i / (double)EncodeSteps) * 255) + 0.5);
        }

        return table;
    });

    /// <summary>The same encoding as 32-bit values, for gathers.</summary>
    private static readonly Lazy<int[]> Encode8Wide = new(() => [.. Encode8.Value.Select(v => (int)v)]);

    /// <summary>For the matrix path: the stages as columns in memory order, and the leading per-channel matrices as float tables.</summary>
    private readonly Vector4[][]? _columns;
    private readonly byte[]? _matrixTables;
    private readonly float[]? _matrixPrefix;
    private readonly int _matrixSkip;

    internal FilterProgram(FilterStage[] stages)
    {
        _stages = stages;
        _rest = stages;
        _map = SampleMap();
        int sampleBytes = TPixel.Layout.ChannelType == PixelChannelType.Unorm8 ? 1 : 2;
        bool tabled = _map is not null && TPixel.Layout.Alpha != AlphaKind.Premultiplied && Unsafe.SizeOf<TPixel>() == _map.Length * sampleBytes
            && TPixel.Layout.ChannelType is PixelChannelType.Unorm8 or PixelChannelType.Unorm16;
        if (stages.Length == 0)
        {
            _kind = Kind.Copy;
            return;
        }

        if (tabled && stages.All(s => s is MatrixStage) && sampleBytes == 1 && _map!.All(c => c >= 0) && (_map!.Length == 4 || _map.Length == 3))
        {
            _kind = Kind.Matrix;
            (_columns, _matrixTables, _matrixPrefix, _matrixSkip) = MatrixPlan(stages, _map!);
            return;
        }

        if (tabled && FilterStage.AllPerChannel(stages) && TablesFit(stages))
        {
            if (sampleBytes == 1)
            {
                _kind = Kind.ByteTables;
                _byteTables = BuildByteTables(stages, _map!);
            }
            else
            {
                _kind = Kind.WordTables;
                _wordTables = BuildWordTables(stages, _map!);
            }

            return;
        }

        if (tabled && sampleBytes == 1 && _map!.All(c => c >= 0) && (_map!.Length == 4 || _map!.Length == 3))
        {
            // The leading per-channel stages replace the byte-to-float conversion.
            int prefix = 0;
            while (prefix < stages.Length && stages[prefix].IsPerChannel)
            {
                prefix++;
            }

            _kind = Kind.ByteFloat;
            _prefix = ArrayPool<float>.Shared.Rent(4 * 256);
            FilterStage[] leading = stages[..prefix];
            for (int c = 0; c < 4; c++)
            {
                for (int v = 0; v < 256; v++)
                {
                    _prefix[(c * 256) + v] = Quantized(leading, c, v, 255) / 255f;
                }
            }

            _rest = stages[prefix..];
            if (_rest is [LinearMatrixStage linear])
            {
                // A matrix in linear light after per-channel stages: decode from a table of the 256 inputs, multiply, and encode
                // through a 16,384-entry table.
                _kind = Kind.ByteLinear;
                _linear = linear.Matrix.ToArray();
                _decode = ArrayPool<float>.Shared.Rent(3 * 256);
                for (int c = 0; c < 3; c++)
                {
                    for (int v = 0; v < 256; v++)
                    {
                        _decode[(c * 256) + v] = Srgb.LinearFrom8[Quantized(leading, c, v, 255)];
                    }
                }
            }

            if (_rest is [LutStage { Lut.Shaper: null } lutStage])
            {
                // A 3D table after per-channel stages: each byte's grid cell and fraction per axis come from tables.
                _kind = Kind.ByteLut;
                _lutStage = lutStage;
                Lut3D lut = lutStage.Lut;
                int n = lut.Size;
                _cells = ArrayPool<int>.Shared.Rent(3 * 256);
                _fractions = ArrayPool<float>.Shared.Rent(3 * 256);
                ReadOnlySpan<int> strides = [3, n * 3, n * n * 3];
                ReadOnlySpan<float> min = [lut.DomainMin.X, lut.DomainMin.Y, lut.DomainMin.Z], scale = [lut.Scale.X, lut.Scale.Y, lut.Scale.Z];
                bool nearest = lutStage.Interpolation == LutInterpolation.Nearest;
                for (int c = 0; c < 3; c++)
                {
                    for (int v = 0; v < 256; v++)
                    {
                        float x = Quantized(leading, c, v, 255) / 255f;
                        float p = Math.Clamp((x - min[c]) * scale[c], 0, n - 1);
                        int cell = nearest ? (int)(p + 0.5f) : Math.Min((int)p, n - 2);
                        _cells[(c * 256) + v] = cell * strides[c];
                        _fractions[(c * 256) + v] = nearest ? 0 : p - cell;
                    }
                }
            }

            if (_rest is [ThresholdStage { Mode: ThresholdMode.Luminance } threshold])
            {
                SetUpThreshold(threshold, leading);
            }

            return;
        }

        if (tabled && sampleBytes == 1 && _map![0] < 0 && FilterStage.AllPerChannel(stages.AsSpan(0, stages.Length - 1)) && stages[^1] is ThresholdStage { Mode: ThresholdMode.Luminance } grayThreshold)
        {
            // A gray luminance threshold: the gray sample is red, green and blue.
            FilterStage[] leading = stages[..^1];
            _prefix = ArrayPool<float>.Shared.Rent(4 * 256);
            for (int c = 0; c < 4; c++)
            {
                for (int v = 0; v < 256; v++)
                {
                    _prefix[(c * 256) + v] = Quantized(leading, c, v, 255) / 255f;
                }
            }

            _rest = [grayThreshold];
            SetUpThreshold(grayThreshold, leading);
            return;
        }

        _kind = typeof(TPixel) == typeof(RgbaF32) ? Kind.Float : Kind.Convert;
    }

    /// <summary>The byte threshold path's tables: each channel's intensity term per byte value, after the leading stages.</summary>
    private void SetUpThreshold(ThresholdStage threshold, FilterStage[] leading)
    {
        // Intensity from three tables of 16-bit-scaled weights, summed in ImageMagick's order, so the comparison is the stage's own.
        _kind = Kind.ByteThreshold;
        _threshold = threshold;
        _luma = ArrayPool<double>.Shared.Rent(3 * 256);
        ReadOnlySpan<double> weights = [0.212656, 0.715158, 0.072186];
        for (int c = 0; c < 3; c++)
        {
            for (int v = 0; v < 256; v++)
            {
                _luma[(c * 256) + v] = weights[c] * ThresholdStage.Sample(Quantized(leading, c, v, 255) / 255.0);
            }
        }
    }

    private enum Kind
    {
        Copy,
        Matrix,
        ByteTables,
        WordTables,
        ByteFloat,
        ByteThreshold,
        ByteLinear,
        ByteLut,
        Float,
        Convert,
    }

    /// <summary>Runs one row; <paramref name="source"/> and <paramref name="destination"/> may be the same.</summary>
    internal void RunRow(ReadOnlySpan<TPixel> source, Span<TPixel> destination)
    {
        switch (_kind)
        {
            case Kind.Copy:
                if (!Unsafe.AreSame(ref MemoryMarshal.GetReference(source), ref MemoryMarshal.GetReference(destination)))
                {
                    source.CopyTo(destination);
                }

                break;
            case Kind.Matrix:
                RunMatrix(MemoryMarshal.AsBytes(source), MemoryMarshal.AsBytes(destination));
                break;
            case Kind.ByteTables:
                RunTables(MemoryMarshal.AsBytes(source), MemoryMarshal.AsBytes(destination), _byteTables!, _map!.Length);
                break;
            case Kind.WordTables:
                RunTables(MemoryMarshal.Cast<TPixel, ushort>(source), MemoryMarshal.Cast<TPixel, ushort>(destination), _wordTables!);
                break;
            case Kind.ByteFloat:
                RunByteFloat(MemoryMarshal.AsBytes(source), MemoryMarshal.AsBytes(destination));
                break;
            case Kind.ByteThreshold:
                RunByteThreshold(MemoryMarshal.AsBytes(source), MemoryMarshal.AsBytes(destination));
                break;
            case Kind.ByteLinear:
                RunByteLinear(MemoryMarshal.AsBytes(source), MemoryMarshal.AsBytes(destination));
                break;
            case Kind.ByteLut:
                RunByteLut(MemoryMarshal.AsBytes(source), MemoryMarshal.AsBytes(destination));
                break;
            case Kind.Float:
                {
                    Span<Vector4> d = MemoryMarshal.Cast<TPixel, Vector4>(destination);
                    MemoryMarshal.Cast<TPixel, Vector4>(source).CopyTo(d);
                    FilterStage.Run(_stages, d, _context);
                    break;
                }

            default:
                RunConverting(source, destination);
                break;
        }
    }

    public void Dispose()
    {
        if (_prefix is not null)
        {
            ArrayPool<float>.Shared.Return(_prefix);
        }

        if (_luma is not null)
        {
            ArrayPool<double>.Shared.Return(_luma);
        }

        if (_decode is not null)
        {
            ArrayPool<float>.Shared.Return(_decode);
        }

        if (_cells is not null)
        {
            ArrayPool<int>.Shared.Return(_cells);
            ArrayPool<float>.Shared.Return(_fractions!);
        }

        if (_wordTables is not null)
        {
            foreach (ushort[] table in _wordTables.OfType<ushort[]>().Distinct())
            {
                ArrayPool<ushort>.Shared.Return(table);
            }
        }

        _context.Dispose();
    }

    private static void RunTables(ReadOnlySpan<byte> source, Span<byte> destination, byte[] tables, int channels)
    {
        ColorKernels.ApplyTables(source, destination, tables, channels);
    }

    private static void RunTables(ReadOnlySpan<ushort> source, Span<ushort> destination, ushort[]?[] tables)
    {
        int channels = tables.Length;
        bool inPlace = Unsafe.AreSame(ref MemoryMarshal.GetReference(source), ref MemoryMarshal.GetReference(destination));
        for (int c = 0; c < channels; c++)
        {
            ushort[]? table = tables[c];
            if (table is null)
            {
                if (!inPlace)
                {
                    for (int i = c; i < source.Length; i += channels)
                    {
                        destination[i] = source[i];
                    }
                }

                continue;
            }

            ref ushort t = ref MemoryMarshal.GetArrayDataReference(table);
            for (int i = c; i < source.Length; i += channels)
            {
                destination[i] = Unsafe.Add(ref t, source[i]);
            }
        }
    }

    private void RunByteFloat(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int channels = _map!.Length;
        int width = source.Length / channels;
        int r = Array.IndexOf(_map, 0), g = Array.IndexOf(_map, 1), b = Array.IndexOf(_map, 2), a = Array.IndexOf(_map, 3);
        Vector4[] buffer = ArrayPool<Vector4>.Shared.Rent(width);
        try
        {
            Span<Vector4> row = buffer.AsSpan(0, width);
            float[] t = _prefix!;
            for (int x = 0, at = 0; x < width; x++, at += channels)
            {
                row[x] = new Vector4(t[source[at + r]], t[256 + source[at + g]], t[512 + source[at + b]], a >= 0 ? t[768 + source[at + a]] : 1);
            }

            foreach (FilterStage stage in _rest)
            {
                stage.Apply(row, _context);
                Quantize(row);
            }

            for (int x = 0, at = 0; x < width; x++, at += channels)
            {
                Vector128<int> p = Vector128.ConvertToInt32((row[x] * 255f).AsVector128() + Vector128.Create(0.5f));
                destination[at + r] = (byte)p.GetElement(0);
                destination[at + g] = (byte)p.GetElement(1);
                destination[at + b] = (byte)p.GetElement(2);
                if (a >= 0)
                {
                    destination[at + a] = (byte)p.GetElement(3);
                }
            }
        }
        finally
        {
            ArrayPool<Vector4>.Shared.Return(buffer);
        }
    }

    /// <summary>Rounds clamped values to the nearest of 256 levels, as an 8-bit step between stages would store them.</summary>
    private static void Quantize(Span<Vector4> row)
    {
        Vector128<float> scale = Vector128.Create(255f), half = Vector128.Create(0.5f), inverse = Vector128.Create(1f / 255f);
        foreach (ref Vector4 p in row)
        {
            p = (Vector128.ConvertToSingle(Vector128.ConvertToInt32((p.AsVector128() * scale) + half)) * inverse).AsVector4();
        }
    }

    private void RunByteThreshold(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int channels = _map!.Length;
        int r = Array.IndexOf(_map, 0), g = Array.IndexOf(_map, 1), b = Array.IndexOf(_map, 2), a = Array.IndexOf(_map, 3);
        double[] luma = _luma!;
        float[] prefix = _prefix!;
        double level = _threshold!.Level;
        Rgba8 low = _threshold.LowColor, high = _threshold.HighColor;
        Span<byte> alphas = stackalloc byte[256];
        Alphas(prefix, alphas);
        if (_map[0] < 0)
        {
            // Gray: one sample is red, green and blue, and the two colors are written as the gray they convert to.
            r = g = b = 0;
            Span<Rgba8> colors = [low, high];
            Span<TPixel> converted = stackalloc TPixel[2];
            PixelConvert.Convert<Rgba8, TPixel>(colors, converted);
            byte lowGray = MemoryMarshal.AsBytes(converted)[0], highGray = MemoryMarshal.AsBytes(converted)[channels];
            low = new Rgba8(lowGray, lowGray, lowGray, 255);
            high = new Rgba8(highGray, highGray, highGray, 255);
        }
        if (_map[0] < 0)
        {
            // Gray: the decision depends on one byte, so each byte's output is a table.
            Span<byte> outputs = stackalloc byte[256];
            for (int v = 0; v < 256; v++)
            {
                outputs[v] = ThresholdStage.Above(luma[v] + luma[256 + v] + luma[512 + v], level) ? high.R : low.R;
            }

            for (int at = 0; at + channels <= source.Length; at += channels)
            {
                destination[at] = outputs[source[at]];
                if (a >= 0)
                {
                    destination[at + a] = alphas[source[at + a]];
                }
            }

            return;
        }

        int start = 0;
        if (channels == 4 && Avx2.IsSupported && source.Length >= 16)
        {
            start = ThresholdAvx2(source, destination, luma, level, r, g, b, a, low, high, alphas);
        }

        for (int at = start; at + channels <= source.Length; at += channels)
        {
            double intensity = luma[source[at + r]] + luma[256 + source[at + g]] + luma[512 + source[at + b]];
            Rgba8 c = ThresholdStage.Above(intensity, level) ? high : low;
            byte alpha = a >= 0 ? alphas[source[at + a]] : (byte)0;
            destination[at + r] = c.R;
            destination[at + g] = c.G;
            destination[at + b] = c.B;
            if (a >= 0)
            {
                destination[at + a] = alpha;
            }
        }
    }

    /// <summary>
    /// Four 4-byte pixels at a time: the three intensity terms gathered as doubles and summed in the scalar order, compared with the
    /// level, and the chosen colors blended in with each pixel's alpha. Returns the byte offset it stopped at.
    /// </summary>
    private static int ThresholdAvx2(ReadOnlySpan<byte> source, Span<byte> destination, double[] luma, double level, int r, int g, int b, int a, Rgba8 low, Rgba8 high, ReadOnlySpan<byte> alphas)
    {
        // The two colors as 32-bit pixels in memory order, alpha byte cleared; alpha comes from the table.
        uint lowBits = Pack(low), highBits = Pack(high);
        Vector128<uint> lows = Vector128.Create(lowBits), highs = Vector128.Create(highBits), alphaMask = Vector128.Create(0xFFu << (a * 8));
        bool keepsAlpha = IsIdentity(alphas);
        Vector256<double> levels = Vector256.Create(level);
        ref double t = ref MemoryMarshal.GetArrayDataReference(luma);
        ReadOnlySpan<uint> src = MemoryMarshal.Cast<byte, uint>(source);
        Span<uint> dst = MemoryMarshal.Cast<byte, uint>(destination);
        Vector128<int> mask = Vector128.Create(0xFF);
        int i = 0;
        unsafe
        {
            fixed (double* table = &t)
            {
                for (; i + 4 <= src.Length; i += 4)
                {
                    Vector128<int> p = Vector128.Create(src[i], src[i + 1], src[i + 2], src[i + 3]).AsInt32();
                    Vector128<int> ir = (p >>> (r * 8)) & mask, ig = ((p >>> (g * 8)) & mask) + Vector128.Create(256), ib = ((p >>> (b * 8)) & mask) + Vector128.Create(512);
                    Vector256<double> sum = Avx2.GatherVector256(table, ir, 8) + Avx2.GatherVector256(table, ig, 8) + Avx2.GatherVector256(table, ib, 8);
                    Vector256<double> above = Avx.CompareGreaterThan(sum, levels);
                    if (keepsAlpha)
                    {
                        // Each 64-bit comparison lane narrowed to 32 bits picks the color; alpha comes straight from the source.
                        Vector128<uint> wide = Avx2.PermuteVar8x32(above.AsInt32(), Vector256.Create(0, 2, 4, 6, 0, 2, 4, 6)).GetLower().AsUInt32();
                        Vector128<uint> pixels = Vector128.ConditionalSelect(wide, highs, lows) | (p.AsUInt32() & alphaMask);
                        pixels.StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)i);
                        continue;
                    }

                    int bits = Avx.MoveMask(above);
                    for (int k = 0; k < 4; k++)
                    {
                        uint alpha = alphas[(int)((src[i + k] >> (a * 8)) & 0xFF)];
                        dst[i + k] = (((bits >> k) & 1) != 0 ? highBits : lowBits) | (alpha << (a * 8));
                    }
                }
            }
        }

        return i * 4;

        uint Pack(Rgba8 c) => ((uint)c.R << (r * 8)) | ((uint)c.G << (g * 8)) | ((uint)c.B << (b * 8));
    }

    private void RunByteLinear(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int channels = _map!.Length;
        int r = Array.IndexOf(_map, 0), g = Array.IndexOf(_map, 1), b = Array.IndexOf(_map, 2), a = Array.IndexOf(_map, 3);
        float[] d = _decode!, m = _linear!, prefix = _prefix!;
        byte[] encode = Encode8.Value;
        float m0 = m[0], m1 = m[1], m2 = m[2], m3 = m[3], m4 = m[4], m5 = m[5], m6 = m[6], m7 = m[7], m8 = m[8];
        Span<byte> alphas = stackalloc byte[256];
        Alphas(prefix, alphas);
        int start = 0;
        if (channels == 4 && Avx2.IsSupported && source.Length >= 32)
        {
            start = LinearAvx2(source, destination, d, m, encode, r, g, b, a, alphas);
        }

        for (int at = start; at + channels <= source.Length; at += channels)
        {
            float lr = d[source[at + r]], lg = d[256 + source[at + g]], lb = d[512 + source[at + b]];
            byte alpha = a >= 0 ? alphas[source[at + a]] : (byte)0;
            destination[at + r] = encode[Index((m0 * lr) + (m1 * lg) + (m2 * lb))];
            destination[at + g] = encode[Index((m3 * lr) + (m4 * lg) + (m5 * lb))];
            destination[at + b] = encode[Index((m6 * lr) + (m7 * lg) + (m8 * lb))];
            if (a >= 0)
            {
                destination[at + a] = alpha;
            }
        }

        static int Index(float linear) => (int)((Math.Clamp(linear, 0f, 1f) * EncodeSteps) + 0.5f);
    }

    /// <summary>
    /// Eight 4-byte pixels at a time: linear values gathered, the matrix applied with the scalar order of operations, and the encoding
    /// gathered from the encoding table. Returns the byte offset it stopped at.
    /// </summary>
    private static int LinearAvx2(ReadOnlySpan<byte> source, Span<byte> destination, float[] decode, float[] m, byte[] encode, int r, int g, int b, int a, ReadOnlySpan<byte> alphas)
    {
        ReadOnlySpan<uint> src = MemoryMarshal.Cast<byte, uint>(source);
        Span<uint> dst = MemoryMarshal.Cast<byte, uint>(destination);
        Vector256<int> mask = Vector256.Create(0xFF), green = Vector256.Create(256), blue = Vector256.Create(512);
        Vector256<float> zero = Vector256<float>.Zero, one = Vector256.Create(1f), steps = Vector256.Create((float)EncodeSteps), half = Vector256.Create(0.5f);
        Vector256<float> m0 = Vector256.Create(m[0]), m1 = Vector256.Create(m[1]), m2 = Vector256.Create(m[2]), m3 = Vector256.Create(m[3]), m4 = Vector256.Create(m[4]);
        Vector256<float> m5 = Vector256.Create(m[5]), m6 = Vector256.Create(m[6]), m7 = Vector256.Create(m[7]), m8 = Vector256.Create(m[8]);
        Span<int> ro = stackalloc int[8], go = stackalloc int[8], bo = stackalloc int[8];
        bool keepsAlpha = IsIdentity(alphas);
        Vector256<int> alphaMask = Vector256.Create(0xFF << (a * 8));
        int[] wide = Encode8Wide.Value;
        int i = 0;
        unsafe
        {
            fixed (float* table = decode)
            {
                fixed (uint* pixels = src)
                {
                    for (; i + 8 <= src.Length; i += 8)
                    {
                        Vector256<int> p = Vector256.Load(pixels + i).AsInt32();
                        Vector256<float> lr = Avx2.GatherVector256(table, (p >>> (r * 8)) & mask, 4);
                        Vector256<float> lg = Avx2.GatherVector256(table, ((p >>> (g * 8)) & mask) + green, 4);
                        Vector256<float> lb = Avx2.GatherVector256(table, ((p >>> (b * 8)) & mask) + blue, 4);
                        Vector256<int> ir = Index((m0 * lr) + (m1 * lg) + (m2 * lb));
                        Vector256<int> ig = Index((m3 * lr) + (m4 * lg) + (m5 * lb));
                        Vector256<int> ib = Index((m6 * lr) + (m7 * lg) + (m8 * lb));
                        if (keepsAlpha)
                        {
                            fixed (int* encodes = wide)
                            {
                                Vector256<int> packed = (Avx2.GatherVector256(encodes, ir, 4) << (r * 8)) | (Avx2.GatherVector256(encodes, ig, 4) << (g * 8))
                                    | (Avx2.GatherVector256(encodes, ib, 4) << (b * 8)) | (p & alphaMask);
                                packed.AsUInt32().StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)i);
                            }

                            continue;
                        }

                        ir.CopyTo(ro);
                        ig.CopyTo(go);
                        ib.CopyTo(bo);
                        for (int k = 0; k < 8; k++)
                        {
                            uint pixel = ((uint)encode[ro[k]] << (r * 8)) | ((uint)encode[go[k]] << (g * 8)) | ((uint)encode[bo[k]] << (b * 8));
                            dst[i + k] = pixel | ((uint)alphas[(int)((src[i + k] >> (a * 8)) & 0xFF)] << (a * 8));
                        }
                    }
                }
            }
        }

        return i * 4;

        Vector256<int> Index(Vector256<float> linear)
        {
            return Vector256.ConvertToInt32((Vector256.Min(Vector256.Max(linear, zero), one) * steps) + half);
        }
    }

    /// <summary>Gets whether the alpha table keeps every value.</summary>
    private static bool IsIdentity(ReadOnlySpan<byte> alphas)
    {
        for (int v = 0; v < 256; v++)
        {
            if (alphas[v] != v)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The alpha byte each stored alpha becomes through the leading stages.</summary>
    private static void Alphas(float[] prefix, Span<byte> alphas)
    {
        for (int v = 0; v < 256; v++)
        {
            alphas[v] = (byte)(int)((prefix[768 + v] * 255f) + 0.5f);
        }
    }

    private void RunByteLut(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int channels = _map!.Length;
        int r = Array.IndexOf(_map, 0), g = Array.IndexOf(_map, 1), b = Array.IndexOf(_map, 2), a = Array.IndexOf(_map, 3);
        int[] cells = _cells!;
        float[] fractions = _fractions!, prefix = _prefix!;
        int n = _lutStage!.Lut.Size, dr = 3, dg = n * 3, db = n * n * 3;
        LutInterpolation mode = _lutStage.Interpolation;
        ref float t = ref MemoryMarshal.GetArrayDataReference(_lutStage.Lut.Grid);
        Span<byte> alphas = stackalloc byte[256];
        Alphas(prefix, alphas);
        for (int at = 0; at + channels <= source.Length; at += channels)
        {
            int sr = source[at + r], sg = 256 + source[at + g], sb = 512 + source[at + b];
            int i = cells[sr] + cells[sg] + cells[sb];
            float fr = fractions[sr], fg = fractions[sg], fb = fractions[sb];
            Vector3 c;
            if (mode == LutInterpolation.Nearest)
            {
                c = At(ref t, i);
            }
            else if (mode == LutInterpolation.Trilinear)
            {
                Vector3 x00 = Vector3.Lerp(At(ref t, i), At(ref t, i + dr), fr), x10 = Vector3.Lerp(At(ref t, i + dg), At(ref t, i + dr + dg), fr);
                Vector3 x01 = Vector3.Lerp(At(ref t, i + db), At(ref t, i + dr + db), fr), x11 = Vector3.Lerp(At(ref t, i + dg + db), At(ref t, i + dr + dg + db), fr);
                c = Vector3.Lerp(Vector3.Lerp(x00, x10, fg), Vector3.Lerp(x01, x11, fg), fb);
            }
            else
            {
                c = Tetrahedral(ref t, i, fr, fg, fb, dr, dg, db);
            }

            c = (Vector3.Clamp(c, Vector3.Zero, Vector3.One) * 255f) + new Vector3(0.5f);
            byte alpha = a >= 0 ? alphas[source[at + a]] : (byte)0;
            destination[at + r] = (byte)(int)c.X;
            destination[at + g] = (byte)(int)c.Y;
            destination[at + b] = (byte)(int)c.Z;
            if (a >= 0)
            {
                destination[at + a] = alpha;
            }
        }
    }

    /// <summary>The same tetrahedra as the table's own reading, on a grid offset already found.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Tetrahedral(ref float t, int i, float fr, float fg, float fb, int dr, int dg, int db)
    {
        Vector3 c000 = At(ref t, i), c111 = At(ref t, i + dr + dg + db);
        if (fr >= fg)
        {
            if (fg >= fb)
            {
                Vector3 c100 = At(ref t, i + dr), c110 = At(ref t, i + dr + dg);
                return c000 + (fr * (c100 - c000)) + (fg * (c110 - c100)) + (fb * (c111 - c110));
            }

            if (fr >= fb)
            {
                Vector3 c100 = At(ref t, i + dr), c101 = At(ref t, i + dr + db);
                return c000 + (fr * (c100 - c000)) + (fb * (c101 - c100)) + (fg * (c111 - c101));
            }

            Vector3 c001 = At(ref t, i + db), c101b = At(ref t, i + dr + db);
            return c000 + (fb * (c001 - c000)) + (fr * (c101b - c001)) + (fg * (c111 - c101b));
        }

        if (fb >= fg)
        {
            Vector3 c001 = At(ref t, i + db), c011 = At(ref t, i + dg + db);
            return c000 + (fb * (c001 - c000)) + (fg * (c011 - c001)) + (fr * (c111 - c011));
        }

        if (fb >= fr)
        {
            Vector3 c010 = At(ref t, i + dg), c011 = At(ref t, i + dg + db);
            return c000 + (fg * (c010 - c000)) + (fb * (c011 - c010)) + (fr * (c111 - c011));
        }

        Vector3 c010b = At(ref t, i + dg), c110b = At(ref t, i + dr + dg);
        return c000 + (fg * (c010b - c000)) + (fr * (c110b - c010b)) + (fb * (c111 - c110b));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 At(ref float t, int i) => new(Unsafe.Add(ref t, i), Unsafe.Add(ref t, i + 1), Unsafe.Add(ref t, i + 2));

    private void RunConverting(ReadOnlySpan<TPixel> source, Span<TPixel> destination)
    {
        RgbaF32[] buffer = ArrayPool<RgbaF32>.Shared.Rent(source.Length);
        try
        {
            Span<RgbaF32> row = buffer.AsSpan(0, source.Length);
            PixelConvert.Convert(source, row);
            FilterStage.Run(_stages, MemoryMarshal.Cast<RgbaF32, Vector4>(row), _context);
            PixelConvert.Convert<RgbaF32, TPixel>(row, destination);
        }
        finally
        {
            ArrayPool<RgbaF32>.Shared.Return(buffer);
        }
    }

    private void RunMatrix(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (_matrixTables is not null)
        {
            ColorKernels.ApplyTables(source, destination, _matrixTables, _map!.Length);
        }
        else if (_map!.Length == 4)
        {
            ColorKernels.Apply4(source, destination, _columns.AsSpan(_matrixSkip), _matrixPrefix);
        }
        else
        {
            ColorKernels.Apply3Scalar(source, destination, _columns);
        }
    }

    /// <summary>The same plan <see cref="ColorMatrixOperation"/> makes for byte types, so a chain of matrices gives its exact pixels.</summary>
    private static (Vector4[][] Columns, byte[]? Tables, float[]? Prefix, int Skip) MatrixPlan(FilterStage[] stages, int[] map)
    {
        int channels = map.Length;
        int[] lanes = channels == 4 ? map : map[0] == 0 ? [0, 1, 2, 3] : [2, 1, 0, 3];
        Vector4[][] columns = [.. stages.Select(s => ColorKernels.MemoryOrderColumns(((MatrixStage)s).Matrix, lanes))];
        int prefix = -1;
        bool allPerChannel = true;
        for (int i = 0; i < stages.Length; i++)
        {
            if (!stages[i].IsPerChannel)
            {
                allPerChannel = false;
                prefix = prefix < 0 ? i : prefix;
            }
        }

        if (allPerChannel)
        {
            return (columns, ColorKernels.BuildTables(columns, channels), null, 0);
        }

        return prefix > 0 && channels == 4 ? (columns, null, ColorKernels.BuildFloatTables(columns.AsSpan(0, prefix)), prefix) : (columns, null, null, 0);
    }

    /// <summary>Builds one 256-entry table per sample, joined, from per-channel stages; identity where a channel passes through.</summary>
    private static byte[] BuildByteTables(FilterStage[] stages, int[] map)
    {
        byte[] tables = new byte[map.Length * 256];
        for (int s = 0; s < map.Length; s++)
        {
            int channel = map[s] < 0 ? 0 : map[s];
            for (int v = 0; v < 256; v++)
            {
                tables[(s * 256) + v] = (byte)Quantized(stages, channel, v, 255);
            }
        }

        return tables;
    }

    /// <summary>Builds one 65,536-entry table per sample from per-channel stages, shared between samples of one channel; null where it passes through.</summary>
    private static ushort[]?[] BuildWordTables(FilterStage[] stages, int[] map)
    {
        ushort[]?[] tables = new ushort[map.Length][];
        ushort[]?[] byChannel = new ushort[4][];
        bool[] done = new bool[4];
        for (int s = 0; s < map.Length; s++)
        {
            int channel = map[s] < 0 ? 0 : map[s];
            if (!done[channel])
            {
                done[channel] = true;
                bool identity = true;
                ushort[] table = ArrayPool<ushort>.Shared.Rent(65536);
                for (int v = 0; v < 65536; v++)
                {
                    table[v] = (ushort)Quantized(stages, channel, v, 65535);
                    identity &= table[v] == v;
                }

                if (identity)
                {
                    ArrayPool<ushort>.Shared.Return(table);
                }

                byChannel[channel] = identity ? null : table;
            }

            tables[s] = byChannel[channel];
        }

        return tables;
    }

    /// <summary>
    /// Maps one stored value through per-channel stages, rounding to the type's levels after each as running the steps one by one
    /// would, so a fused chain gives exactly the step-by-step pixels.
    /// </summary>
    private static int Quantized(FilterStage[] stages, int channel, int value, int max)
    {
        foreach (FilterStage stage in stages)
        {
            value = (int)((stage.MapChannel(channel, value / (double)max) * max) + 0.5);
        }

        return value;
    }

    /// <summary>Gets whether a gray type's red, green and blue maps agree, so its one sample can take one table.</summary>
    private bool TablesFit(FilterStage[] stages)
    {
        if (!_map!.Contains(-1))
        {
            return true;
        }

        for (int v = 0; v <= 256; v++)
        {
            double x = v / 256.0;
            double r = FilterStage.MapChannel(stages, 0, x);
            if (r != FilterStage.MapChannel(stages, 1, x) || r != FilterStage.MapChannel(stages, 2, x))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets which RGBA channel each sample of the pixel holds; null for layouts that are not plain channels.</summary>
    private static int[]? SampleMap()
    {
        return TPixel.Layout.ChannelOrder switch
        {
            PixelChannelOrder.Rgba => [0, 1, 2, 3],
            PixelChannelOrder.Bgra => [2, 1, 0, 3],
            PixelChannelOrder.Rgb => [0, 1, 2],
            PixelChannelOrder.Bgr => [2, 1, 0],
            PixelChannelOrder.Argb => [3, 0, 1, 2],
            PixelChannelOrder.Abgr => [3, 2, 1, 0],
            PixelChannelOrder.Gray => [-1],
            PixelChannelOrder.GrayAlpha => [-1, 3],
            _ => null,
        };
    }
}
