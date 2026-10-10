using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Colors;

/// <summary>Where the color channels of an 8-bit pixel are: its size in bytes, and whether three channels are stored B, G, R.</summary>
internal readonly record struct ByteLayout(int Stride, bool Reversed);

/// <summary>
/// A conversion compiled for speed from the <see cref="ColorPipeline"/>: an identity, a matrix form (curves, one affine map,
/// curves) when both ends are matrix or gray encodings, or a grid sampled from the pipeline (33 points a side for RGB input,
/// 17 for CMYK, 4096 for gray) and interpolated. Values are normalized 0..1 for every channel.
/// </summary>
internal abstract class CompiledTransform
{
    /// <summary>The colors converted per pass: small enough for stack buffers, large enough for SIMD to pay.</summary>
    protected const int Chunk = 256;

    protected CompiledTransform(int inputs, int outputs)
    {
        (InputChannels, OutputChannels) = (inputs, outputs);
    }

    public int InputChannels { get; }

    public int OutputChannels { get; }

    /// <summary>Compiles the conversion; profiles that mean the same colors give an identity.</summary>
    /// <exception cref="UnsupportedFormatException">A profile has no tables for the direction it is used in.</exception>
    public static CompiledTransform Compile(ColorProfile from, ColorProfile to, RenderingIntent intent, bool blackPointCompensation)
    {
        if (from.IsEquivalentTo(to))
        {
            return new IdentityTransform(Channels(from));
        }

        ColorPipeline pipeline = ColorPipeline.Create(from, to, intent, blackPointCompensation);
        return pipeline.GetLinearForm() is LinearForm form ? new MatrixTransform(form, pipeline.InputChannels, pipeline.OutputChannels) : new GridTransform(pipeline);
    }

    /// <summary>Converts colors stored as interleaved floats: <see cref="InputChannels"/> per color in, <see cref="OutputChannels"/> out.</summary>
    public abstract void Apply(ReadOnlySpan<float> source, Span<float> destination, int count);

    /// <summary>Converts 8-bit colors; each color's channels are at the start of its <see cref="ByteLayout.Stride"/> bytes.</summary>
    public abstract void Apply(ReadOnlySpan<byte> source, ByteLayout sourceLayout, Span<byte> destination, ByteLayout destinationLayout, int count);

    internal static int Channels(ColorProfile profile) => profile.Model switch
    {
        ColorModel.Gray => 1,
        ColorModel.Cmyk => 4,
        _ => 3,
    };

    /// <summary>Rounds a normalized value to 8 bits, clamping, as every 8-bit write in Egami does.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static byte To8(float v) => v > 0f ? v < 1f ? (byte)((v * 255f) + 0.5f) : (byte)255 : (byte)0;

    // The byte offset of channel c within a pixel.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static int Offset(ByteLayout layout, int channels, int c) => layout.Reversed && channels == 3 ? 2 - c : c;
}

/// <summary>Profiles that mean the same colors: values are copied.</summary>
internal sealed class IdentityTransform(int channels) : CompiledTransform(channels, channels)
{
    public override void Apply(ReadOnlySpan<float> source, Span<float> destination, int count) => source[..(count * InputChannels)].CopyTo(destination);

    public override void Apply(ReadOnlySpan<byte> source, ByteLayout sourceLayout, Span<byte> destination, ByteLayout destinationLayout, int count)
    {
        int n = InputChannels;
        for (int i = 0; i < count; i++)
        {
            int s = i * sourceLayout.Stride, d = i * destinationLayout.Stride;
            for (int c = 0; c < n; c++)
            {
                destination[d + Offset(destinationLayout, n, c)] = source[s + Offset(sourceLayout, n, c)];
            }
        }
    }
}

/// <summary>
/// Curves, one affine map, curves. Rows are split into planes of up to <see cref="CompiledTransform.Chunk"/> values; curves
/// run as <see cref="TransferFunction"/> rows (8-bit input through 256-entry tables, 8-bit output through a 65536-entry
/// table indexed by the rounded linear value where the curve allows), and the map and the rounding run on Vector512, Vector256
/// or Vector128 with a scalar tail that does the same arithmetic in the same order, so every path gives the same bits.
/// </summary>
internal sealed class MatrixTransform : CompiledTransform
{
    private const int EncodeSize = 65536;
    private static readonly ConditionalWeakTable<TransferFunction, byte[]> EncodeTables = [];

    private readonly TransferFunction[] _in;
    private readonly TransferFunction[] _out;
    private readonly float[] _m;          // row-major 3×3, then three offsets
    private readonly float[][] _decode8;  // per input channel: the linear value of each byte
    private readonly byte[]?[] _encode8;  // per output channel: the byte for linear i / 65535; null to compute

    public MatrixTransform(LinearForm form, int inputs, int outputs)
        : base(inputs, outputs)
    {
        (_in, _out) = (form.InCurves, form.OutCurves);
        Matrix3d m = form.Matrix;
        _m = [(float)m[0, 0], (float)m[0, 1], (float)m[0, 2], (float)m[1, 0], (float)m[1, 1], (float)m[1, 2], (float)m[2, 0], (float)m[2, 1], (float)m[2, 2],
              (float)form.Offset.X, (float)form.Offset.Y, (float)form.Offset.Z];
        _decode8 = [.. _in.Select(c => Enumerable.Range(0, 256).Select(i => (float)c.ToLinear(i / 255.0)).ToArray())];
        _encode8 = [.. _out.Select(EncodeTable)];
    }

    public override void Apply(ReadOnlySpan<float> source, Span<float> destination, int count)
    {
        Span<float> planes = stackalloc float[Chunk * 6];
        for (int start = 0; start < count; start += Chunk)
        {
            int n = Math.Min(Chunk, count - start);
            Span<float> x = planes.Slice(0, n), y = planes.Slice(Chunk, n), z = planes.Slice(Chunk * 2, n);
            Span<float> r = planes.Slice(Chunk * 3, n), g = planes.Slice(Chunk * 4, n), b = planes.Slice(Chunk * 5, n);
            ReadOnlySpan<float> src = source[(start * InputChannels)..];
            Span<float> dst = destination[(start * OutputChannels)..];
            if (InputChannels == 1)
            {
                src[..n].CopyTo(x);
                _in[0].ToLinear(x, x);
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    (x[i], y[i], z[i]) = (src[i * 3], src[(i * 3) + 1], src[(i * 3) + 2]);
                }

                _in[0].ToLinear(x, x);
                _in[1].ToLinear(y, y);
                _in[2].ToLinear(z, z);
            }

            Affine(x, y, z, r, g, b);
            _out[0].FromLinear(r, r);
            if (OutputChannels == 1)
            {
                r.CopyTo(dst);
                continue;
            }

            _out[1].FromLinear(g, g);
            _out[2].FromLinear(b, b);
            for (int i = 0; i < n; i++)
            {
                (dst[i * 3], dst[(i * 3) + 1], dst[(i * 3) + 2]) = (r[i], g[i], b[i]);
            }
        }
    }

    public override void Apply(ReadOnlySpan<byte> source, ByteLayout sourceLayout, Span<byte> destination, ByteLayout destinationLayout, int count)
    {
        Span<float> planes = stackalloc float[Chunk * 6];
        Span<int> indices = stackalloc int[Chunk];
        int ins = InputChannels, outs = OutputChannels;
        int s0 = Offset(sourceLayout, ins, 0), s1 = Offset(sourceLayout, ins, 1), s2 = Offset(sourceLayout, ins, 2);
        int sourceStride = sourceLayout.Stride, destinationStride = destinationLayout.Stride;
        float[] d0 = _decode8[0], d1 = _decode8[ins == 3 ? 1 : 0], d2 = _decode8[ins == 3 ? 2 : 0];
        for (int start = 0; start < count; start += Chunk)
        {
            int n = Math.Min(Chunk, count - start);
            Span<float> x = planes.Slice(0, n), y = planes.Slice(Chunk, n), z = planes.Slice(Chunk * 2, n);
            Span<float> r = planes.Slice(Chunk * 3, n), g = planes.Slice(Chunk * 4, n), b = planes.Slice(Chunk * 5, n);
            ReadOnlySpan<byte> src = source[(start * sourceStride)..];
            if (ins == 3)
            {
                for (int i = 0, p = 0; i < n; i++, p += sourceStride)
                {
                    x[i] = d0[src[p + s0]];
                    y[i] = d1[src[p + s1]];
                    z[i] = d2[src[p + s2]];
                }
            }
            else
            {
                for (int i = 0, p = 0; i < n; i++, p += sourceStride)
                {
                    x[i] = d0[src[p + s0]];
                }
            }

            Affine(x, y, z, r, g, b);
            Span<byte> dst = destination[(start * destinationStride)..];
            for (int c = 0; c < outs; c++)
            {
                Span<float> plane = c == 0 ? r : c == 1 ? g : b;
                int offset = Offset(destinationLayout, outs, c);
                if (_encode8[c] is byte[] table)
                {
                    Quantize(plane, indices);
                    for (int i = 0, p = offset; i < n; i++, p += destinationStride)
                    {
                        dst[p] = table[indices[i]];
                    }
                }
                else
                {
                    _out[c].FromLinear(plane, plane);
                    for (int i = 0, p = offset; i < n; i++, p += destinationStride)
                    {
                        dst[p] = To8(plane[i]);
                    }
                }
            }
        }
    }

    // A 65536-entry table from linear to 8 bits works where the curve takes linear 0..1 onto encoded 0..1, as SDR curves do;
    // the shared curves' tables are built once.
    private static byte[]? EncodeTable(TransferFunction curve)
    {
        if (curve.IsHdr || Math.Abs(curve.ToLinear(1) - 1) > 1e-6)
        {
            return null;
        }

        return EncodeTables.GetValue(curve, c =>
        {
            byte[] table = new byte[EncodeSize];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = (byte)Math.Round(Math.Clamp(c.FromLinear(i / (double)(EncodeSize - 1)), 0, 1) * 255);
            }

            return table;
        });
    }

    // indices = round(clamp(v, 0, 1) × 65535), with NaN as 0, the same on every path.
    private static void Quantize(ReadOnlySpan<float> values, Span<int> indices)
    {
        int n = values.Length, i = 0;
        const float Scale = EncodeSize - 1;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= n - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                Vector256<float> v = Vector256.Create(values[i..]);
                v = Vector256.ConditionalSelect(Vector256.GreaterThan(v, Vector256<float>.Zero), v, Vector256<float>.Zero);
                v = Vector256.Min(v, Vector256<float>.One);
                Vector256.ConvertToInt32((v * Scale) + Vector256.Create(0.5f)).CopyTo(indices[i..]);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= n - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                Vector128<float> v = Vector128.Create(values[i..]);
                v = Vector128.ConditionalSelect(Vector128.GreaterThan(v, Vector128<float>.Zero), v, Vector128<float>.Zero);
                v = Vector128.Min(v, Vector128<float>.One);
                Vector128.ConvertToInt32((v * Scale) + Vector128.Create(0.5f)).CopyTo(indices[i..]);
            }
        }

        for (; i < n; i++)
        {
            float v = values[i] > 0f ? values[i] : 0f;
            v = v < 1f ? v : 1f;
            indices[i] = (int)((v * Scale) + 0.5f);
        }
    }

    // r, g, b = M × (x, y, z) + offset, with y and z unused for one input and only r written for one output.
    private void Affine(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z, Span<float> r, Span<float> g, Span<float> b)
    {
        float[] m = _m;
        int n = x.Length, i = 0;
        bool gray = InputChannels == 1, rows3 = OutputChannels == 3;
        if (Vector512.IsHardwareAccelerated)
        {
            for (; i <= n - Vector512<float>.Count; i += Vector512<float>.Count)
            {
                Vector512<float> vx = Vector512.Create(x[i..]);
                Vector512<float> vy = gray ? default : Vector512.Create(y[i..]), vz = gray ? default : Vector512.Create(z[i..]);
                Row(m, 0, vx, vy, vz, gray).CopyTo(r[i..]);
                if (rows3)
                {
                    Row(m, 1, vx, vy, vz, gray).CopyTo(g[i..]);
                    Row(m, 2, vx, vy, vz, gray).CopyTo(b[i..]);
                }
            }
        }

        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= n - Vector256<float>.Count; i += Vector256<float>.Count)
            {
                Vector256<float> vx = Vector256.Create(x[i..]);
                Vector256<float> vy = gray ? default : Vector256.Create(y[i..]), vz = gray ? default : Vector256.Create(z[i..]);
                Row(m, 0, vx, vy, vz, gray).CopyTo(r[i..]);
                if (rows3)
                {
                    Row(m, 1, vx, vy, vz, gray).CopyTo(g[i..]);
                    Row(m, 2, vx, vy, vz, gray).CopyTo(b[i..]);
                }
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= n - Vector128<float>.Count; i += Vector128<float>.Count)
            {
                Vector128<float> vx = Vector128.Create(x[i..]);
                Vector128<float> vy = gray ? default : Vector128.Create(y[i..]), vz = gray ? default : Vector128.Create(z[i..]);
                Row(m, 0, vx, vy, vz, gray).CopyTo(r[i..]);
                if (rows3)
                {
                    Row(m, 1, vx, vy, vz, gray).CopyTo(g[i..]);
                    Row(m, 2, vx, vy, vz, gray).CopyTo(b[i..]);
                }
            }
        }

        for (; i < n; i++)
        {
            float vx = x[i], vy = gray ? 0 : y[i], vz = gray ? 0 : z[i];
            r[i] = Row(m, 0, vx, vy, vz, gray);
            if (rows3)
            {
                g[i] = Row(m, 1, vx, vy, vz, gray);
                b[i] = Row(m, 2, vx, vy, vz, gray);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Row(float[] m, int row, float x, float y, float z, bool gray) =>
        gray ? (m[row * 3] * x) + m[9 + row] : (m[row * 3] * x) + (m[(row * 3) + 1] * y) + (m[(row * 3) + 2] * z) + m[9 + row];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<float> Row(float[] m, int row, Vector512<float> x, Vector512<float> y, Vector512<float> z, bool gray) =>
        gray ? (Vector512.Create(m[row * 3]) * x) + Vector512.Create(m[9 + row])
             : (Vector512.Create(m[row * 3]) * x) + (Vector512.Create(m[(row * 3) + 1]) * y) + (Vector512.Create(m[(row * 3) + 2]) * z) + Vector512.Create(m[9 + row]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Row(float[] m, int row, Vector256<float> x, Vector256<float> y, Vector256<float> z, bool gray) =>
        gray ? (Vector256.Create(m[row * 3]) * x) + Vector256.Create(m[9 + row])
             : (Vector256.Create(m[row * 3]) * x) + (Vector256.Create(m[(row * 3) + 1]) * y) + (Vector256.Create(m[(row * 3) + 2]) * z) + Vector256.Create(m[9 + row]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Row(float[] m, int row, Vector128<float> x, Vector128<float> y, Vector128<float> z, bool gray) =>
        gray ? (Vector128.Create(m[row * 3]) * x) + Vector128.Create(m[9 + row])
             : (Vector128.Create(m[row * 3]) * x) + (Vector128.Create(m[(row * 3) + 1]) * y) + (Vector128.Create(m[(row * 3) + 2]) * z) + Vector128.Create(m[9 + row]);
}

/// <summary>
/// The pipeline sampled on a device grid and interpolated: linear for gray input, tetrahedral for RGB, and for CMYK tetrahedral
/// over cyan, magenta and yellow with black linear, the scheme Little CMS and skcms use. Inputs are clamped to 0..1.
/// </summary>
internal sealed class GridTransform : CompiledTransform
{
    private readonly int _points;
    private readonly float[] _grid;
    private readonly int[] _strides;     // per input: the grid offset of one step
    private readonly int[][] _offset8;   // per input and byte value: the grid offset of its cell
    private readonly float[] _fraction8; // per byte value: the position inside the cell

    public GridTransform(ColorPipeline pipeline)
        : base(pipeline.InputChannels, pipeline.OutputChannels)
    {
        _points = InputChannels switch
        {
            1 => 4096,
            3 => 33,
            _ => 17,
        };
        _grid = Sample(pipeline, InputChannels, OutputChannels, _points);
        _strides = new int[InputChannels];
        for (int c = InputChannels - 1, stride = OutputChannels; c >= 0; c--, stride *= _points)
        {
            _strides[c] = stride;
        }

        _offset8 = new int[InputChannels][];
        _fraction8 = new float[256];
        for (int c = 0; c < InputChannels; c++)
        {
            _offset8[c] = new int[256];
            for (int v = 0; v < 256; v++)
            {
                (int cell, _fraction8[v]) = Cell(v / 255f, _points);
                _offset8[c][v] = cell * _strides[c];
            }
        }
    }

    public override void Apply(ReadOnlySpan<float> source, Span<float> destination, int count)
    {
        int ins = InputChannels, outs = OutputChannels;
        Span<float> f = stackalloc float[4];
        for (int i = 0; i < count; i++)
        {
            int origin = 0;
            for (int c = 0; c < ins; c++)
            {
                (int cell, f[c]) = Cell(source[(i * ins) + c], _points);
                origin += cell * _strides[c];
            }

            Interpolate(origin, f, destination.Slice(i * outs, outs));
        }
    }

    public override void Apply(ReadOnlySpan<byte> source, ByteLayout sourceLayout, Span<byte> destination, ByteLayout destinationLayout, int count)
    {
        int ins = InputChannels, outs = OutputChannels;
        Span<float> f = stackalloc float[4];
        Span<float> result = stackalloc float[outs];
        Span<int> so = stackalloc int[ins];
        Span<int> dOffsets = stackalloc int[outs];
        for (int c = 0; c < ins; c++)
        {
            so[c] = Offset(sourceLayout, ins, c);
        }

        for (int c = 0; c < outs; c++)
        {
            dOffsets[c] = Offset(destinationLayout, outs, c);
        }

        for (int i = 0, s = 0, d = 0; i < count; i++, s += sourceLayout.Stride, d += destinationLayout.Stride)
        {
            int origin = 0;
            for (int c = 0; c < ins; c++)
            {
                byte v = source[s + so[c]];
                origin += _offset8[c][v];
                f[c] = _fraction8[v];
            }

            Interpolate(origin, f, result);
            for (int c = 0; c < outs; c++)
            {
                destination[d + dOffsets[c]] = To8(result[c]);
            }
        }
    }

    private static float[] Sample(ColorPipeline pipeline, int inputs, int outputs, int points)
    {
        int nodes = 1;
        for (int i = 0; i < inputs; i++)
        {
            nodes *= points;
        }

        float[] grid = new float[nodes * outputs];
        double[] input = new double[inputs], output = new double[outputs];
        for (int node = 0; node < nodes; node++)
        {
            // The first input varies slowest.
            for (int c = inputs - 1, rest = node; c >= 0; c--, rest /= points)
            {
                input[c] = rest % points / (double)(points - 1);
            }

            pipeline.Evaluate(input, output);
            for (int o = 0; o < outputs; o++)
            {
                grid[(node * outputs) + o] = (float)output[o];
            }
        }

        return grid;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int Cell, float Fraction) Cell(float v, int points)
    {
        float p = (v > 0f ? v < 1f ? v : 1f : 0f) * (points - 1);
        int i = Math.Min((int)p, points - 2);
        return (i, p - i);
    }

    private void Interpolate(int origin, ReadOnlySpan<float> f, Span<float> output)
    {
        int outs = OutputChannels;
        ref float t = ref MemoryMarshal.GetArrayDataReference(_grid);
        switch (InputChannels)
        {
            case 1:
                {
                    for (int o = 0; o < outs; o++)
                    {
                        float a = Unsafe.Add(ref t, origin + o);
                        output[o] = a + ((Unsafe.Add(ref t, origin + outs + o) - a) * f[0]);
                    }

                    break;
                }

            case 3:
                Tetrahedral(ref Unsafe.Add(ref t, origin), _strides[0], _strides[1], _strides[2], outs, f[0], f[1], f[2], output);
                break;
            default:
                {
                    // Tetrahedral over cyan, magenta and yellow at this black and the next, then linear in black.
                    Span<float> high = stackalloc float[outs];
                    Tetrahedral(ref Unsafe.Add(ref t, origin), _strides[0], _strides[1], _strides[2], outs, f[0], f[1], f[2], output);
                    Tetrahedral(ref Unsafe.Add(ref t, origin + _strides[3]), _strides[0], _strides[1], _strides[2], outs, f[0], f[1], f[2], high);
                    for (int o = 0; o < outs; o++)
                    {
                        output[o] += (high[o] - output[o]) * f[3];
                    }

                    break;
                }
        }
    }

    // The six tetrahedra of a cube, chosen by the order of the fractions; the cube's grid values are read relative to its corner.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Tetrahedral(ref float c000, int sx, int sy, int sz, int outs, float rx, float ry, float rz, Span<float> output)
    {
        int a, b, c;
        float fa, fb, fc;
        if (rx >= ry && ry >= rz)
        {
            (a, b, c, fa, fb, fc) = (sx, sx + sy, sx + sy + sz, rx, ry, rz);
        }
        else if (rx >= rz && rz >= ry)
        {
            (a, b, c, fa, fb, fc) = (sx, sx + sz, sx + sy + sz, rx, rz, ry);
        }
        else if (rz >= rx && rx >= ry)
        {
            (a, b, c, fa, fb, fc) = (sz, sx + sz, sx + sy + sz, rz, rx, ry);
        }
        else if (ry >= rx && rx >= rz)
        {
            (a, b, c, fa, fb, fc) = (sy, sx + sy, sx + sy + sz, ry, rx, rz);
        }
        else if (ry >= rz && rz >= rx)
        {
            (a, b, c, fa, fb, fc) = (sy, sy + sz, sx + sy + sz, ry, rz, rx);
        }
        else
        {
            (a, b, c, fa, fb, fc) = (sz, sy + sz, sx + sy + sz, rz, ry, rx);
        }

        // The path corner, a, b, c through the cube: v = v0 + (va − v0)·fa + (vb − va)·fb + (vc − vb)·fc.
        for (int o = 0; o < outs; o++)
        {
            float v0 = Unsafe.Add(ref c000, o), va = Unsafe.Add(ref c000, a + o), vb = Unsafe.Add(ref c000, b + o), vc = Unsafe.Add(ref c000, c + o);
            output[o] = v0 + ((va - v0) * fa) + ((vb - va) * fb) + ((vc - vb) * fc);
        }
    }
}

/// <summary>
/// Compiled transforms, kept per <see cref="EgamiContext"/> and keyed by what both profiles mean and the options, so batch
/// jobs compile each conversion once. Holds at most <see cref="Capacity"/> transforms, dropping the oldest.
/// </summary>
internal static class TransformCache
{
    internal const int Capacity = 32;

    private static readonly ConditionalWeakTable<EgamiContext, Store> Stores = [];

    public static CompiledTransform Get(EgamiContext context, ColorProfile from, ColorProfile to, RenderingIntent intent, bool blackPointCompensation)
    {
        Store store = Stores.GetValue(context, _ => new Store());
        Key key = new(ProfileKey.Of(from), ProfileKey.Of(to), intent, blackPointCompensation);
        lock (store)
        {
            if (store.Transforms.TryGetValue(key, out CompiledTransform? cached))
            {
                return cached;
            }
        }

        CompiledTransform compiled = CompiledTransform.Compile(from, to, intent, blackPointCompensation);
        lock (store)
        {
            if (store.Transforms.TryAdd(key, compiled))
            {
                store.Order.Enqueue(key);
                while (store.Order.Count > Capacity)
                {
                    store.Transforms.Remove(store.Order.Dequeue());
                }
            }

            return store.Transforms[key];
        }
    }

    /// <summary>The number of transforms cached for a context, for tests.</summary>
    internal static int Count(EgamiContext context) => Stores.TryGetValue(context, out Store? store) ? store.Transforms.Count : 0;

    private sealed class Store
    {
        public Dictionary<Key, CompiledTransform> Transforms { get; } = [];

        public Queue<Key> Order { get; } = new();
    }

    private readonly record struct Key(ProfileKey From, ProfileKey To, RenderingIntent Intent, bool BlackPointCompensation);

    /// <summary>What a profile means: its ICC bytes when it came from a profile, else its model, primaries, white and curve.</summary>
    private sealed class ProfileKey : IEquatable<ProfileKey>
    {
        private readonly byte[]? _icc;
        private readonly (ColorModel, ColorPrimaries?, Chromaticity, TransferFunction?) _encoding;
        private readonly int _hash;

        private ProfileKey(ColorProfile profile)
        {
            if (profile.Icc is IccData icc)
            {
                _icc = icc.ToBytes().ToArray();
                HashCode hash = default;
                hash.AddBytes(_icc);
                _hash = hash.ToHashCode();
            }
            else
            {
                _encoding = (profile.Model, profile.Primaries, profile.White, profile.Transfer);
                _hash = _encoding.GetHashCode();
            }
        }

        public static ProfileKey Of(ColorProfile profile) => new(profile);

        public bool Equals(ProfileKey? other) => other is not null && _hash == other._hash
            && (_icc is null ? other._icc is null && _encoding.Equals(other._encoding) : other._icc is not null && _icc.AsSpan().SequenceEqual(other._icc));

        public override bool Equals(object? obj) => Equals(obj as ProfileKey);

        public override int GetHashCode() => _hash;
    }
}
