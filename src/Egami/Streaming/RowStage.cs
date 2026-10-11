using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Pixels;

namespace Egami.Streaming;

/// <summary>
/// Turns a source's rows into output rows: crops to the plan's region, averages Factor × Factor blocks, and converts to the
/// caller's pixel type. Holds one accumulator row when it reduces, nothing otherwise.
/// </summary>
internal sealed class RowStage(RowSource source, ReadPlan plan)
{
    private double[]? _sums;
    private RgbaF32[]? _line;
    private Rgba8[]? _bytes;
    private long[]? _fixed;
    private bool _fixedMode;
    private ushort[]? _columnBytes;
    private ushort[]? _columnPowers;
    private int _columnRows;

    /// <summary>
    /// Gets whether the source's rows are 8-bit straight channels, which convert to Rgba8 without loss: the stage then reads
    /// them as bytes and looks up their float values, instead of the slower conversion to floats, with the same sums.
    /// </summary>
    private readonly bool _eightBit = source.Layout is { ChannelType: PixelChannelType.Unorm8, Alpha: not AlphaKind.Premultiplied } layout
        && layout.ChannelOrder is PixelChannelOrder.Rgba or PixelChannelOrder.Bgra or PixelChannelOrder.Rgb or PixelChannelOrder.Bgr
            or PixelChannelOrder.Argb or PixelChannelOrder.Abgr or PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;

    /// <summary>The float each byte value converts to, as <see cref="PixelConvert"/> gives it to an RgbaF32 row, held as a double.</summary>
    private static readonly double[] Scaled = ScaledBytes();
    private int _accumulated = -1;

    /// <summary>Gets the next output row of the current frame.</summary>
    public int Row { get; private set; }

    /// <summary>Gets the output rows left in the current frame.</summary>
    public int Remaining => plan.Output.Height - Row;

    /// <summary>Gets the bytes the stage holds while reducing.</summary>
    public long WorkingBytes => plan.Factor == 1 ? 0 : (plan.Output.Width * 4L * sizeof(double)) + (plan.Region.Width * 16L);

    /// <summary>Starts the stage over for a new frame.</summary>
    public void Reset()
    {
        Row = 0;
        _accumulated = -1;
    }

    /// <summary>Moves past output rows without producing them, decoding only where the source must.</summary>
    public void Skip(int count)
    {
        Row += Math.Min(count, Remaining);
    }

    /// <summary>
    /// Gets or sets the conversion to <see cref="Formats.ReaderOptions.ConvertColorTo"/>, run on each row in the natural pixel
    /// type after cropping and reducing, as a load followed by ConvertColor does.
    /// </summary>
    public ColorRows? Color { get; set; }

    /// <summary>Writes the next output row into <paramref name="destination"/>, which is the output width.</summary>
    public void Read<TPixel>(Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (Color is ColorRows color)
        {
            color.Read(this, destination);
            return;
        }

        ReadStored(destination);
    }

    /// <summary>Writes the next output row as stored: cropped, reduced and converted to the pixel type, with no color conversion.</summary>
    internal void ReadStored<TPixel>(Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rect region = plan.Region;
        int factor = plan.Factor;
        if (factor == 1)
        {
            source.ReadRow(region.Y + Row, region.X, destination);
            Row++;
            return;
        }

        int width = plan.Output.Width;
        double[] sums = _sums ??= new double[width * 4];
        RgbaF32[] line = _line ??= new RgbaF32[region.Width];
        if (_accumulated < 0)
        {
            Array.Clear(sums);
            _accumulated = 0;

            // Opaque byte rows add up exactly in integers, scaled by 2^32, while a block holds at most 2^20 pixels; the sums are
            // then the same doubles the float additions give.
            _fixedMode = _eightBit && factor <= 1024;
            if (_fixedMode)
            {
                Array.Clear(_fixed ??= new long[width * 3]);
                if (Linear)
                {
                    Array.Clear(_columnBytes ??= new ushort[region.Width * 4]);
                    Array.Clear(_columnPowers ??= new ushort[region.Width * 4]);
                    _columnRows = 0;
                }
            }
        }

        // The sums so far are kept if the source runs out of bytes, so an incremental decode carries on from the next row.
        int top = region.Y + (Row * factor);
        int rows = Math.Min(factor, region.Bottom - top);
        for (; _accumulated < rows; _accumulated++)
        {
            if (_eightBit)
            {
                Rgba8[] bytes = _bytes ??= new Rgba8[region.Width];
                source.ReadRow(top + _accumulated, region.X, bytes.AsSpan());
                if (_fixedMode && Opaque(bytes))
                {
                    if (!Linear)
                    {
                        AccumulateFixed(bytes, _fixed!, factor);
                    }
                    else if (AddColumns(bytes, _columnBytes!, _columnPowers!) && ++_columnRows == 256)
                    {
                        Fold(region.Width, factor);
                    }

                    continue;
                }

                if (_fixedMode)
                {
                    Unfix(sums, _accumulated, region.Width, factor);
                }

                Accumulate(bytes, sums, factor);
                continue;
            }

            source.ReadRow(top + _accumulated, region.X, line.AsSpan());
            Accumulate(line, sums, factor);
        }

        if (_fixedMode)
        {
            Unfix(sums, rows, region.Width, factor);
        }

        _accumulated = -1;

        if (Vector256.IsHardwareAccelerated)
        {
            // The same divisions as Average, four at once, then the same narrowing to float.
            ref double sum = ref MemoryMarshal.GetArrayDataReference(sums);
            int x = 0;
            if (typeof(TPixel) == typeof(Rgba8))
            {
                x = AveragesToBytes(sums, MemoryMarshal.AsBytes(destination), region.Width, factor, rows);
            }

            for (; x < width; x++)
            {
                Vector256<double> block = Vector256.LoadUnsafe(ref sum, (nuint)(x * 4));
                double alpha = block.GetElement(3);
                if (alpha <= 0)
                {
                    destination[x] = TPixel.FromScaled(Vector4.Zero);
                    continue;
                }

                int columns = Math.Min(factor, region.Width - (x * factor));
                Vector256<double> average = block / Vector256.Create(alpha, alpha, alpha, columns * rows);
                destination[x] = TPixel.FromScaled(Vector256.Narrow(average, average).GetLower().AsVector4());
            }
        }
        else
        {
            for (int x = 0; x < width; x++)
            {
                int columns = Math.Min(factor, region.Width - (x * factor));
                destination[x] = TPixel.FromScaled(Average(sums.AsSpan(x * 4, 4), columns * rows));
            }
        }

        Row++;
    }

    /// <summary>
    /// Writes the block averages as bytes two pixels at a time, with the same divisions, narrowing and rounding as
    /// <see cref="Rgba8.FromScaled"/>; stops at the first pair with a transparent block and returns the pixels written.
    /// </summary>
    private static int AveragesToBytes(double[] sums, Span<byte> destination, int regionWidth, int factor, int rows)
    {
        ref double sum = ref MemoryMarshal.GetArrayDataReference(sums);
        ref byte output = ref MemoryMarshal.GetReference(destination);
        int width = destination.Length / 4;
        Vector256<float> one = Vector256.Create(1f);
        Vector256<float> scale = Vector256.Create(255f);
        Vector256<float> half = Vector256.Create(0.5f);
        int x = 0;
        for (; x + 1 < width; x += 2)
        {
            Vector256<double> first = Vector256.LoadUnsafe(ref sum, (nuint)(x * 4));
            Vector256<double> second = Vector256.LoadUnsafe(ref sum, (nuint)((x + 1) * 4));
            double firstAlpha = first.GetElement(3);
            double secondAlpha = second.GetElement(3);
            if (!(firstAlpha > 0) || !(secondAlpha > 0))
            {
                break;
            }

            int firstColumns = Math.Min(factor, regionWidth - (x * factor));
            int secondColumns = Math.Min(factor, regionWidth - ((x + 1) * factor));
            Vector256<float> averages = Vector256.Narrow(
                first / Vector256.Create(firstAlpha, firstAlpha, firstAlpha, firstColumns * rows),
                second / Vector256.Create(secondAlpha, secondAlpha, secondAlpha, secondColumns * rows));

            // Scale.ToByte: clamp to 0..1 (every average here is a number), times 255, plus a half, truncated.
            Vector256<float> clamped = Vector256.Min(Vector256.Max(averages, Vector256<float>.Zero), one);
            Vector256<int> values = Vector256.ConvertToInt32((clamped * scale) + half);
            Vector256<ushort> shorts = Vector256.Narrow(values.AsUInt32(), values.AsUInt32());
            Vector256<byte> bytes = Vector256.Narrow(shorts, shorts);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref output, x * 4), bytes.AsUInt64().ToScalar());
        }

        return x;
    }

    /// <summary>Adds a row of bytes to the block sums, with each byte's float value: the same products as the float row gives.</summary>
    internal static void Accumulate(ReadOnlySpan<Rgba8> line, Span<double> sums, int factor)
    {
        ref double scaled = ref MemoryMarshal.GetArrayDataReference(Scaled);
        ref Rgba8 pixel = ref MemoryMarshal.GetReference(line);
        ref double sum = ref MemoryMarshal.GetReference(sums);
        int start = 0;
        int column = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            // Four blocks at a time, each with its own running sum, so the additions do not wait on one another; each sum still
            // takes its pixels in the same order.
            for (; start + (4 * factor) <= line.Length; start += 4 * factor, column += 4)
            {
                ref double block = ref Unsafe.Add(ref sum, column * 4);
                Vector256<double> t0 = Vector256.LoadUnsafe(ref block);
                Vector256<double> t1 = Vector256.LoadUnsafe(ref block, 4);
                Vector256<double> t2 = Vector256.LoadUnsafe(ref block, 8);
                Vector256<double> t3 = Vector256.LoadUnsafe(ref block, 12);
                for (int k = 0; k < factor; k++)
                {
                    t0 += Weighted(ref scaled, Unsafe.Add(ref pixel, start + k));
                    t1 += Weighted(ref scaled, Unsafe.Add(ref pixel, start + factor + k));
                    t2 += Weighted(ref scaled, Unsafe.Add(ref pixel, start + (2 * factor) + k));
                    t3 += Weighted(ref scaled, Unsafe.Add(ref pixel, start + (3 * factor) + k));
                }

                t0.StoreUnsafe(ref block);
                t1.StoreUnsafe(ref block, 4);
                t2.StoreUnsafe(ref block, 8);
                t3.StoreUnsafe(ref block, 12);
            }
        }

        for (; start < line.Length; start += factor, column++)
        {
            ref double block = ref Unsafe.Add(ref sum, column * 4);
            int end = Math.Min(start + factor, line.Length);
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<double> total = Vector256.LoadUnsafe(ref block);
                for (int x = start; x < end; x++)
                {
                    Rgba8 p = Unsafe.Add(ref pixel, x);
                    double alpha = Unsafe.Add(ref scaled, p.A);
                    Vector256<double> value = Vector256.Create(Unsafe.Add(ref scaled, p.R), Unsafe.Add(ref scaled, p.G), Unsafe.Add(ref scaled, p.B), alpha);
                    total += value * Vector256.Create(alpha, alpha, alpha, 1.0);
                }

                total.StoreUnsafe(ref block);
                continue;
            }

            double r = block, g = Unsafe.Add(ref block, 1), b = Unsafe.Add(ref block, 2), a = Unsafe.Add(ref block, 3);
            for (int x = start; x < end; x++)
            {
                Rgba8 p = Unsafe.Add(ref pixel, x);
                double alpha = Unsafe.Add(ref scaled, p.A);
                r += Unsafe.Add(ref scaled, p.R) * alpha;
                g += Unsafe.Add(ref scaled, p.G) * alpha;
                b += Unsafe.Add(ref scaled, p.B) * alpha;
                a += alpha;
            }

            block = r;
            Unsafe.Add(ref block, 1) = g;
            Unsafe.Add(ref block, 2) = b;
            Unsafe.Add(ref block, 3) = a;
        }
    }

    /// <summary>Returns whether every pixel of a byte row is opaque.</summary>
    private static bool Opaque(ReadOnlySpan<Rgba8> row)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<Rgba8, uint>(row);
        int x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<uint> mask = Vector256.Create(0xFF00_0000u);
            for (; x <= words.Length - Vector256<uint>.Count; x += Vector256<uint>.Count)
            {
                if ((Vector256.Create(words.Slice(x, Vector256<uint>.Count)) & mask) != mask)
                {
                    return false;
                }
            }
        }

        for (; x < words.Length; x++)
        {
            if ((words[x] & 0xFF00_0000u) != 0xFF00_0000u)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Adds an opaque byte row to the integer sums: each channel's float value times 2^32, which is a whole number.</summary>
    private static void AccumulateFixed(ReadOnlySpan<Rgba8> line, Span<long> sums, int factor)
    {
        ref long table = ref MemoryMarshal.GetArrayDataReference(Fixed);
        ref Rgba8 pixel = ref MemoryMarshal.GetReference(line);
        ref long sum = ref MemoryMarshal.GetReference(sums);
        for (int start = 0, column = 0; start < line.Length; start += factor, column++)
        {
            ref long block = ref Unsafe.Add(ref sum, column * 3);
            long r = block, g = Unsafe.Add(ref block, 1), b = Unsafe.Add(ref block, 2);
            int end = Math.Min(start + factor, line.Length);
            for (int x = start; x < end; x++)
            {
                Rgba8 p = Unsafe.Add(ref pixel, x);
                r += Unsafe.Add(ref table, p.R);
                g += Unsafe.Add(ref table, p.G);
                b += Unsafe.Add(ref table, p.B);
            }

            block = r;
            Unsafe.Add(ref block, 1) = g;
            Unsafe.Add(ref block, 2) = b;
        }
    }

    /// <summary>
    /// Moves the integer sums of the first <paramref name="rows"/> rows of a block into the double sums, exactly, so the block
    /// carries on with rows that have alpha.
    /// </summary>
    private void Unfix(Span<double> sums, int rows, int regionWidth, int factor)
    {
        Fold(regionWidth, factor);
        long[] fixedSums = _fixed!;
        for (int column = 0; column * factor < regionWidth; column++)
        {
            int columns = Math.Min(factor, regionWidth - (column * factor));
            sums[column * 4] = fixedSums[column * 3] * (1.0 / 4294967296.0);
            sums[(column * 4) + 1] = fixedSums[(column * 3) + 1] * (1.0 / 4294967296.0);
            sums[(column * 4) + 2] = fixedSums[(column * 3) + 2] * (1.0 / 4294967296.0);
            sums[(column * 4) + 3] = (double)rows * columns;
        }

        _fixedMode = false;
    }

    /// <summary>
    /// Adds an opaque byte row to the per-column sums of the bytes and of their highest powers of two, from which
    /// <see cref="Fold"/> makes the integer block sums; always returns true.
    /// </summary>
    private static bool AddColumns(ReadOnlySpan<Rgba8> line, Span<ushort> columnBytes, Span<ushort> columnPowers)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(line);
        ref byte b = ref MemoryMarshal.GetReference(bytes);
        ref ushort sum = ref MemoryMarshal.GetReference(columnBytes);
        ref ushort power = ref MemoryMarshal.GetReference(columnPowers);
        int i = 0;
        if (Avx2.IsSupported)
        {
            // The highest power of two of a byte is the larger of those of its high nibble (times 16) and its low nibble.
            Vector256<byte> high = Vector256.Create((byte)0, 16, 32, 32, 64, 64, 64, 64, 128, 128, 128, 128, 128, 128, 128, 128, 0, 16, 32, 32, 64, 64, 64, 64, 128, 128, 128, 128, 128, 128, 128, 128);
            Vector256<byte> low = Vector256.Create((byte)0, 1, 2, 2, 4, 4, 4, 4, 8, 8, 8, 8, 8, 8, 8, 8, 0, 1, 2, 2, 4, 4, 4, 4, 8, 8, 8, 8, 8, 8, 8, 8);
            Vector256<byte> nibble = Vector256.Create((byte)0x0F);
            for (; i <= bytes.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref b, (nuint)i);
                Vector256<byte> powers = Avx2.Max(
                    Avx2.Shuffle(high, Avx2.ShiftRightLogical(v.AsUInt16(), 4).AsByte() & nibble),
                    Avx2.Shuffle(low, v & nibble));
                (Vector256<ushort> v0, Vector256<ushort> v1) = Vector256.Widen(v);
                (Vector256<ushort> p0, Vector256<ushort> p1) = Vector256.Widen(powers);
                (Vector256.LoadUnsafe(ref sum, (nuint)i) + v0).StoreUnsafe(ref sum, (nuint)i);
                (Vector256.LoadUnsafe(ref sum, (nuint)(i + 16)) + v1).StoreUnsafe(ref sum, (nuint)(i + 16));
                (Vector256.LoadUnsafe(ref power, (nuint)i) + p0).StoreUnsafe(ref power, (nuint)i);
                (Vector256.LoadUnsafe(ref power, (nuint)(i + 16)) + p1).StoreUnsafe(ref power, (nuint)(i + 16));
            }
        }

        for (; i < bytes.Length; i++)
        {
            byte v = Unsafe.Add(ref b, i);
            Unsafe.Add(ref sum, i) += v;
            Unsafe.Add(ref power, i) += (ushort)(v == 0 ? 0 : 1 << (31 - BitOperations.LeadingZeroCount((uint)v)));
        }

        return true;
    }

    /// <summary>
    /// Moves the per-column sums into the integer block sums: a byte's float value times 2^32 is 0x01010100 times the byte
    /// plus twice its highest power of two, as <see cref="Linear"/> checked, so the block sums follow from the column sums.
    /// </summary>
    private void Fold(int regionWidth, int factor)
    {
        if (!Linear || _columnRows == 0)
        {
            return;
        }

        ushort[] bytes = _columnBytes!;
        ushort[] powers = _columnPowers!;
        long[] fixedSums = _fixed!;
        ref ushort byteSum = ref MemoryMarshal.GetArrayDataReference(bytes);
        ref ushort powerSum = ref MemoryMarshal.GetArrayDataReference(powers);
        for (int column = 0, start = 0; start < regionWidth; column++, start += factor)
        {
            int end = Math.Min(start + factor, regionWidth);
            if (Vector128.IsHardwareAccelerated)
            {
                // A block's column sums stay below 2^32: at most 1024 columns of at most 256 rows of 255.
                Vector128<uint> byteTotal = Vector128<uint>.Zero;
                Vector128<uint> powerTotal = Vector128<uint>.Zero;
                for (int x = start * 4; x < end * 4; x += 4)
                {
                    byteTotal += Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref byteSum, x)))).AsUInt16());
                    powerTotal += Vector128.WidenLower(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref powerSum, x)))).AsUInt16());
                }

                fixedSums[column * 3] += (0x01010100L * byteTotal.GetElement(0)) + (2L * powerTotal.GetElement(0));
                fixedSums[(column * 3) + 1] += (0x01010100L * byteTotal.GetElement(1)) + (2L * powerTotal.GetElement(1));
                fixedSums[(column * 3) + 2] += (0x01010100L * byteTotal.GetElement(2)) + (2L * powerTotal.GetElement(2));
                continue;
            }

            long r = 0, g = 0, b = 0, pr = 0, pg = 0, pb = 0;
            for (int x = start * 4; x < end * 4; x += 4)
            {
                r += bytes[x];
                g += bytes[x + 1];
                b += bytes[x + 2];
                pr += powers[x];
                pg += powers[x + 1];
                pb += powers[x + 2];
            }

            fixedSums[column * 3] += (0x01010100L * r) + (2 * pr);
            fixedSums[(column * 3) + 1] += (0x01010100L * g) + (2 * pg);
            fixedSums[(column * 3) + 2] += (0x01010100L * b) + (2 * pb);
        }

        Array.Clear(bytes);
        Array.Clear(powers);
        _columnRows = 0;
    }

    /// <summary>Returns a byte pixel's color weighted by its alpha, and its alpha, from the table of float values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> Weighted(ref double scaled, Rgba8 p)
    {
        double alpha = Unsafe.Add(ref scaled, p.A);
        return Vector256.Create(Unsafe.Add(ref scaled, p.R), Unsafe.Add(ref scaled, p.G), Unsafe.Add(ref scaled, p.B), alpha) * Vector256.Create(alpha, alpha, alpha, 1.0);
    }

    /// <summary>Each byte's float value times 2^32: whole numbers, since every such float is a multiple of 2^-32.</summary>
    private static readonly long[] Fixed = FixedBytes();

    /// <summary>Gets whether every byte's entry in <see cref="Fixed"/> is 0x01010100 times the byte plus twice its highest power of two.</summary>
    private static readonly bool Linear = Enumerable.Range(0, 256).All(v => Fixed[v] == (0x01010100L * v) + (v == 0 ? 0 : 2L << (31 - BitOperations.LeadingZeroCount((uint)v))));

    private static long[] FixedBytes()
    {
        long[] values = new long[256];
        for (int v = 0; v < 256; v++)
        {
            double scaled = Scaled[v] * 4294967296.0;
            values[v] = (long)scaled;
            if (values[v] != scaled)
            {
                throw new InvalidOperationException("A byte's float value is not a multiple of 2^-32.");
            }
        }

        return values;
    }

    private static double[] ScaledBytes()
    {
        Rgba8[] bytes = new Rgba8[256];
        for (int v = 0; v < 256; v++)
        {
            bytes[v] = new Rgba8((byte)v, (byte)v, (byte)v, (byte)v);
        }

        RgbaF32[] floats = new RgbaF32[256];
        PixelConvert.Convert<Rgba8, RgbaF32>(bytes, floats);
        return [.. floats.Select(f => (double)f.R)];
    }

    /// <summary>Adds a row to the block sums: color weighted by alpha, and alpha.</summary>
    internal static void Accumulate(ReadOnlySpan<RgbaF32> line, Span<double> sums, int factor)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            // The same products and the same order of additions per sum, four lanes at a time, so the result is identical.
            ref RgbaF32 pixel = ref MemoryMarshal.GetReference(line);
            ref double sum = ref MemoryMarshal.GetReference(sums);
            for (int start = 0, column = 0; start < line.Length; start += factor, column++)
            {
                ref double block = ref Unsafe.Add(ref sum, column * 4);
                Vector256<double> total = Vector256.LoadUnsafe(ref block);
                int end = Math.Min(start + factor, line.Length);
                for (int x = start; x < end; x++)
                {
                    Vector128<float> p = Vector128.LoadUnsafe(ref Unsafe.As<RgbaF32, float>(ref Unsafe.Add(ref pixel, x)));
                    Vector256<double> d = Vector256.Create(Vector128.WidenLower(p), Vector128.WidenUpper(p));
                    double a = d.GetElement(3);
                    total += d * Vector256.Create(a, a, a, 1.0);
                }

                total.StoreUnsafe(ref block);
            }

            return;
        }

        for (int x = 0; x < line.Length; x++)
        {
            RgbaF32 p = line[x];
            Span<double> sum = sums.Slice(x / factor * 4, 4);
            double a = p.A;
            sum[0] += p.R * a;
            sum[1] += p.G * a;
            sum[2] += p.B * a;
            sum[3] += a;
        }
    }

    /// <summary>Returns the average of a block from its sums: alpha-weighted color, and mean alpha over <paramref name="count"/> pixels.</summary>
    internal static Vector4 Average(ReadOnlySpan<double> sum, int count)
    {
        double alpha = sum[3];
        if (alpha <= 0)
        {
            return Vector4.Zero;
        }

        return new Vector4((float)(sum[0] / alpha), (float)(sum[1] / alpha), (float)(sum[2] / alpha), (float)(alpha / count));
    }
}

/// <summary>Converts rows to <see cref="Formats.ReaderOptions.ConvertColorTo"/>; made for the source's natural pixel type.</summary>
internal abstract class ColorRows
{
    /// <summary>Reads the stage's next row in the natural type and converts it into <paramref name="destination"/>.</summary>
    public abstract void Read<TPixel>(RowStage stage, Span<TPixel> destination)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <summary>Makes the conversion for a source whose natural type is the visited one.</summary>
    internal sealed class Factory(Colors.ColorProfile source, Colors.HdrMetadata? hdr, Colors.ColorProfile target, EgamiContext context) : INaturalTypeVisitor<ColorRows>
    {
        public ColorRows Visit<TNatural>()
            where TNatural : unmanaged, IPixelType<TNatural>
        {
            return new ColorRows<TNatural>(source, hdr, target, context);
        }
    }
}

/// <summary>
/// The conversion for natural type <typeparamref name="TNatural"/>: the same transform Raster.ConvertColor builds, from the
/// source's profile to the target at the destination's color model, kept per destination type.
/// </summary>
internal sealed class ColorRows<TNatural>(Colors.ColorProfile source, Colors.HdrMetadata? hdr, Colors.ColorProfile target, EgamiContext context) : ColorRows
    where TNatural : unmanaged, IPixelType<TNatural>
{
    private TNatural[] _row = [];
    private Type? _type;
    private Colors.ColorTransform? _transform;

    public override void Read<TPixel>(RowStage stage, Span<TPixel> destination)
    {
        if (_row.Length < destination.Length)
        {
            _row = new TNatural[destination.Length];
        }

        Span<TNatural> natural = _row.AsSpan(0, destination.Length);
        stage.ReadStored(natural);
        if (_type != typeof(TPixel))
        {
            Colors.ColorProfile from = Operations.Color.ConvertColorOperation.ForPixels(source, Colors.ColorTransform.ModelOf<TNatural>() ?? Colors.ColorModel.Rgb);
            Colors.ColorProfile to = Operations.Color.ConvertColorOperation.ForPixels(target, Colors.ColorTransform.ModelOf<TPixel>() ?? Colors.ColorModel.Rgb);
            _transform = Colors.ColorTransform.CreateFor(from, to, Colors.ColorTransformOptions.Default, hdr, context);
            _type = typeof(TPixel);
        }

        _transform!.Apply<TNatural, TPixel>(natural, destination);
    }
}
