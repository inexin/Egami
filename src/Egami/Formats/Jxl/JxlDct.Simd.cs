using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Jxl;

/// <content>
/// The 8×8 inverse DCT with AVX: the eight rows as eight vectors, transposed so each butterfly of the 1D transform runs on all
/// eight rows at once, then transposed back for the columns. The operations are the scalar ones in the same order, so the
/// samples are the same bit for bit.
/// </content>
internal static partial class JxlDct
{
    /// <summary>Gets whether <see cref="Idct8x8"/> runs with vectors.</summary>
    internal static bool Idct8x8Vectorized => Avx.IsSupported;

    /// <summary>Runs the inverse 8×8 DCT in place on the block at <paramref name="offset"/>: rows, then columns, as Dct2D does.</summary>
    internal static void Idct8x8(Span<float> data, int offset, int stride)
    {
        ref float origin = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
        if (offset + (7 * stride) + 8 > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        Vector256<float> r0 = Load(ref origin, 0);
        Vector256<float> r1 = Load(ref origin, stride);
        Vector256<float> r2 = Load(ref origin, 2 * stride);
        Vector256<float> r3 = Load(ref origin, 3 * stride);
        Vector256<float> r4 = Load(ref origin, 4 * stride);
        Vector256<float> r5 = Load(ref origin, 5 * stride);
        Vector256<float> r6 = Load(ref origin, 6 * stride);
        Vector256<float> r7 = Load(ref origin, 7 * stride);

        // Rows: each vector one column x, its lanes the rows y.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Inverse8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        // Columns: each vector one row y, its lanes the columns x.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Inverse8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Store(r0, ref origin, 0);
        Store(r1, ref origin, stride);
        Store(r2, ref origin, 2 * stride);
        Store(r3, ref origin, 3 * stride);
        Store(r4, ref origin, 4 * stride);
        Store(r5, ref origin, 5 * stride);
        Store(r6, ref origin, 6 * stride);
        Store(r7, ref origin, 7 * stride);
    }

    /// <summary>
    /// Runs the forward or inverse 8×8 DCT in place as <see cref="Dct2DVector"/> does (rows, then columns, with the same
    /// operations), the block kept in eight registers: the lossy encoder runs it for every block it tries.
    /// </summary>
    [SkipLocalsInit]
    private static void Dct8x8(Span<float> data, int offset, int stride, bool forward)
    {
        ref float origin = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
        if (offset + (7 * stride) + 8 > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        Span<Vector256<float>> r = stackalloc Vector256<float>[8];
        for (int i = 0; i < 8; i++)
        {
            r[i] = Load(ref origin, i * stride);
        }

        Transpose(ref r[0], ref r[1], ref r[2], ref r[3], ref r[4], ref r[5], ref r[6], ref r[7]);
        Dct8Vector(r, forward);
        Transpose(ref r[0], ref r[1], ref r[2], ref r[3], ref r[4], ref r[5], ref r[6], ref r[7]);
        Dct8Vector(r, forward);
        for (int i = 0; i < 8; i++)
        {
            Store(r[i], ref origin, i * stride);
        }
    }

    /// <summary>
    /// Runs Dct2D on a 4×4, 4×8 or 8×4 area with vectors, rows then columns as the scalar path, each 1D transform run on all
    /// rows (or columns) at once by <see cref="DctVector"/>, which keeps the scalar operations: the samples are the same.
    /// </summary>
    [SkipLocalsInit]
    private static bool Dct2DSmall(Span<float> data, int offset, int stride, int width, int height, bool forward)
    {
        if (!Avx.IsSupported || width is not (4 or 8) || height is not (4 or 8) || (width == 8 && height == 8))
        {
            return false;
        }

        if (offset + ((height - 1) * stride) + width > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        Span<float> t = stackalloc float[64];
        Span<Vector256<float>> v = stackalloc Vector256<float>[8];
        Span<Vector256<float>> scratch = stackalloc Vector256<float>[8];
        t.Clear();
        ref float d = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
        ref float tr = ref MemoryMarshal.GetReference(t);

        // Rows: each vector one column x, its lanes the rows y.
        for (int y = 0; y < height; y++)
        {
            ref float row = ref Unsafe.Add(ref d, y * stride);
            for (int x = 0; x < width; x++)
            {
                Unsafe.Add(ref tr, (x * 8) + y) = Unsafe.Add(ref row, x);
            }
        }

        for (int x = 0; x < width; x++)
        {
            v[x] = Vector256.LoadUnsafe(ref tr, (nuint)(x * 8));
        }

        DctVector(v[..width], scratch, forward);

        // Columns: each vector one row y, its lanes the columns x.
        for (int x = 0; x < width; x++)
        {
            v[x].StoreUnsafe(ref tr, (nuint)(x * 8));
        }

        Span<float> r = stackalloc float[64];
        r.Clear();
        ref float rr = ref MemoryMarshal.GetReference(r);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Unsafe.Add(ref rr, (y * 8) + x) = Unsafe.Add(ref tr, (x * 8) + y);
            }
        }

        for (int y = 0; y < height; y++)
        {
            v[y] = Vector256.LoadUnsafe(ref rr, (nuint)(y * 8));
        }

        DctVector(v[..height], scratch, forward);
        for (int y = 0; y < height; y++)
        {
            v[y].StoreUnsafe(ref rr, (nuint)(y * 8));
            ref float row = ref Unsafe.Add(ref d, y * stride);
            for (int x = 0; x < width; x++)
            {
                Unsafe.Add(ref row, x) = Unsafe.Add(ref rr, (y * 8) + x);
            }
        }

        return true;
    }

    private static Vector256<float> Load(ref float origin, int at) => Vector256.LoadUnsafe(ref origin, (nuint)at);

    private static void Store(Vector256<float> v, ref float origin, int at) => v.StoreUnsafe(ref origin, (nuint)at);

    /// <summary>The inverse 1D DCT of length 8 (<see cref="Dct8"/> with forward false) on eight vectors at once.</summary>
    private static void Inverse8(ref Vector256<float> i0, ref Vector256<float> i1, ref Vector256<float> i2, ref Vector256<float> i3, ref Vector256<float> i4, ref Vector256<float> i5, ref Vector256<float> i6, ref Vector256<float> i7)
    {
        ReadOnlySpan<float> sec = SecHalf(8);
        Vector256<float> sqrt2 = Vector256.Create(Sqrt2);

        // a = even inputs, b = odd inputs folded, as Dct8.
        (Vector256<float> a0, Vector256<float> a1, Vector256<float> a2, Vector256<float> a3) = Inverse4(i0, i2, i4, i6);
        (Vector256<float> b0, Vector256<float> b1, Vector256<float> b2, Vector256<float> b3) = Inverse4(i1 * sqrt2, i3 + i1, i5 + i3, i7 + i5);
        Vector256<float> c0 = b0 * Vector256.Create(sec[0]);
        Vector256<float> c1 = b1 * Vector256.Create(sec[1]);
        Vector256<float> c2 = b2 * Vector256.Create(sec[2]);
        Vector256<float> c3 = b3 * Vector256.Create(sec[3]);
        i0 = a0 + c0;
        i7 = a0 - c0;
        i1 = a1 + c1;
        i6 = a1 - c1;
        i2 = a2 + c2;
        i5 = a2 - c2;
        i3 = a3 + c3;
        i4 = a3 - c3;
    }

    /// <summary>The inverse 1D DCT of length 4 (<see cref="Dct4Small"/> with forward false) on four vectors.</summary>
    private static (Vector256<float>, Vector256<float>, Vector256<float>, Vector256<float>) Inverse4(Vector256<float> i0, Vector256<float> i1, Vector256<float> i2, Vector256<float> i3)
    {
        Vector256<float> tmp0 = i1 * Vector256.Create(Sqrt2);
        Vector256<float> tmp1 = i1 + i3;
        Vector256<float> out0 = (tmp0 + tmp1) * Vector256.Create(0.5411961f);
        Vector256<float> out1 = (tmp0 - tmp1) * Vector256.Create(1.306563f);
        Vector256<float> sum02 = i0 + i2;
        Vector256<float> sub02 = i0 - i2;
        return (sum02 + out0, sub02 + out1, sub02 - out1, sum02 - out0);
    }

    /// <summary>Transposes the 8×8 matrix whose rows are the eight vectors.</summary>
    private static void Transpose(ref Vector256<float> r0, ref Vector256<float> r1, ref Vector256<float> r2, ref Vector256<float> r3, ref Vector256<float> r4, ref Vector256<float> r5, ref Vector256<float> r6, ref Vector256<float> r7)
    {
        Vector256<float> t0 = Avx.UnpackLow(r0, r1);
        Vector256<float> t1 = Avx.UnpackHigh(r0, r1);
        Vector256<float> t2 = Avx.UnpackLow(r2, r3);
        Vector256<float> t3 = Avx.UnpackHigh(r2, r3);
        Vector256<float> t4 = Avx.UnpackLow(r4, r5);
        Vector256<float> t5 = Avx.UnpackHigh(r4, r5);
        Vector256<float> t6 = Avx.UnpackLow(r6, r7);
        Vector256<float> t7 = Avx.UnpackHigh(r6, r7);
        Vector256<float> s0 = Avx.Shuffle(t0, t2, 0x44);
        Vector256<float> s1 = Avx.Shuffle(t0, t2, 0xEE);
        Vector256<float> s2 = Avx.Shuffle(t1, t3, 0x44);
        Vector256<float> s3 = Avx.Shuffle(t1, t3, 0xEE);
        Vector256<float> s4 = Avx.Shuffle(t4, t6, 0x44);
        Vector256<float> s5 = Avx.Shuffle(t4, t6, 0xEE);
        Vector256<float> s6 = Avx.Shuffle(t5, t7, 0x44);
        Vector256<float> s7 = Avx.Shuffle(t5, t7, 0xEE);
        r0 = Avx.Permute2x128(s0, s4, 0x20);
        r1 = Avx.Permute2x128(s1, s5, 0x20);
        r2 = Avx.Permute2x128(s2, s6, 0x20);
        r3 = Avx.Permute2x128(s3, s7, 0x20);
        r4 = Avx.Permute2x128(s0, s4, 0x31);
        r5 = Avx.Permute2x128(s1, s5, 0x31);
        r6 = Avx.Permute2x128(s2, s6, 0x31);
        r7 = Avx.Permute2x128(s3, s7, 0x31);
    }

    /// <summary>
    /// Runs Dct2D with vectors when both sides are multiples of 8: the rows through a transposed copy and the columns in place,
    /// eight at a time, each with the scalar 1D transform's operations in the same order.
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    private static bool Dct2DVector(Span<float> data, int offset, int stride, int width, int height, bool forward)
    {
        if (!Avx.IsSupported || width < 8 || height < 8 || width % 8 != 0 || height % 8 != 0)
        {
            return false;
        }

        if (width == 8 && height == 8)
        {
            Dct8x8(data, offset, stride, forward);
            return true;
        }

        // Up to 64×64 the transposed copy lives on the stack; larger ones (the decoder's 128 and 256) come from the pool.
        float[]? rented = width * height > 4096 ? System.Buffers.ArrayPool<float>.Shared.Rent(width * height) : null;
        Span<float> transposed = rented is null ? stackalloc float[width * height] : rented;
        Span<Vector256<float>> lines = stackalloc Vector256<float>[Math.Max(width, height)];
        Span<Vector256<float>> scratch = stackalloc Vector256<float>[Math.Max(width, height)];
        try
        {
            // Rows: the block transposed (width rows of height samples), each column of that transformed, then transposed back.
            TransposeBlock(data[offset..], stride, transposed, height, height, width);
            ref float t = ref MemoryMarshal.GetReference(transposed);
            for (int y = 0; y < height; y += 8)
            {
                for (int x = 0; x < width; x++)
                {
                    lines[x] = Vector256.LoadUnsafe(ref t, (nuint)((x * height) + y));
                }

                DctVector(lines[..width], scratch, forward);
                for (int x = 0; x < width; x++)
                {
                    lines[x].StoreUnsafe(ref t, (nuint)((x * height) + y));
                }
            }

            TransposeBlock(transposed, height, data[offset..], stride, width, height);

            // Columns, eight at a time.
            ref float d = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
            for (int x = 0; x < width; x += 8)
            {
                for (int y = 0; y < height; y++)
                {
                    lines[y] = Vector256.LoadUnsafe(ref d, (nuint)((y * stride) + x));
                }

                DctVector(lines[..height], scratch, forward);
                for (int y = 0; y < height; y++)
                {
                    lines[y].StoreUnsafe(ref d, (nuint)((y * stride) + x));
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<float>.Shared.Return(rented);
            }
        }

        return true;
    }

    /// <summary>Writes the <paramref name="rows"/>×<paramref name="columns"/> block at <paramref name="from"/> transposed to <paramref name="to"/>, by 8×8 tiles.</summary>
    internal static void TransposeBlock(ReadOnlySpan<float> from, int fromStride, Span<float> to, int toStride, int rows, int columns)
    {
        ref float f = ref MemoryMarshal.GetReference(from);
        ref float o = ref MemoryMarshal.GetReference(to);
        if (from.Length < ((rows - 1) * fromStride) + columns || to.Length < ((columns - 1) * toStride) + rows)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), $"from {from.Length} stride {fromStride} to {to.Length} stride {toStride} rows {rows} columns {columns}");
        }

        for (int y = 0; y < rows; y += 8)
        {
            for (int x = 0; x < columns; x += 8)
            {
                ref float tile = ref Unsafe.Add(ref f, (y * fromStride) + x);
                Vector256<float> r0 = Load(ref tile, 0);
                Vector256<float> r1 = Load(ref tile, fromStride);
                Vector256<float> r2 = Load(ref tile, 2 * fromStride);
                Vector256<float> r3 = Load(ref tile, 3 * fromStride);
                Vector256<float> r4 = Load(ref tile, 4 * fromStride);
                Vector256<float> r5 = Load(ref tile, 5 * fromStride);
                Vector256<float> r6 = Load(ref tile, 6 * fromStride);
                Vector256<float> r7 = Load(ref tile, 7 * fromStride);
                Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                ref float target = ref Unsafe.Add(ref o, (x * toStride) + y);
                Store(r0, ref target, 0);
                Store(r1, ref target, toStride);
                Store(r2, ref target, 2 * toStride);
                Store(r3, ref target, 3 * toStride);
                Store(r4, ref target, 4 * toStride);
                Store(r5, ref target, 5 * toStride);
                Store(r6, ref target, 6 * toStride);
                Store(r7, ref target, 7 * toStride);
            }
        }
    }

    /// <summary>The 1D DCT of <see cref="Dct(Span{float}, Span{float}, bool)"/> on vectors: each lane its own transform.</summary>
    private static void DctVector(Span<Vector256<float>> io, Span<Vector256<float>> scratch, bool forward)
    {
        int n = io.Length;
        Vector256<float> two = Vector256.Create(2f);
        switch (n)
        {
            case <= 1:
                return;
            case 2:
                {
                    Vector256<float> t0 = io[0] + io[1];
                    Vector256<float> t1 = io[0] - io[1];
                    io[0] = forward ? t0 / two : t0;
                    io[1] = forward ? t1 / two : t1;
                    return;
                }

            case 4:
                Dct4Vector(io, forward);
                return;
            case 8:
                Dct8Vector(io, forward);
                return;
        }

        int half = n / 2;
        ReadOnlySpan<float> sec = SecHalf(n);
        Span<Vector256<float>> in0 = scratch[..half];
        Span<Vector256<float>> in1 = scratch[half..n];
        Span<Vector256<float>> out0 = io[..half];
        Span<Vector256<float>> out1 = io[half..];
        if (forward)
        {
            for (int i = 0; i < half; i++)
            {
                in0[i] = (io[i] + io[n - i - 1]) / two;
                in1[i] = (io[i] - io[n - i - 1]) / two;
            }

            for (int i = 0; i < half; i++)
            {
                in1[i] *= Vector256.Create(sec[i]);
            }

            DctVector(in0, out0, forward: true);
            DctVector(in1, out1, forward: true);
            in1[0] *= Vector256.Create(Sqrt2);
            for (int i = 0; i < half - 1; i++)
            {
                in1[i] += in1[i + 1];
            }

            for (int i = 0; i < half; i++)
            {
                io[2 * i] = in0[i];
                io[(2 * i) + 1] = in1[i];
            }
        }
        else
        {
            for (int i = 0; i < half; i++)
            {
                in0[i] = io[2 * i];
                in1[i] = io[(2 * i) + 1];
            }

            for (int i = 1; i < half; i++)
            {
                in1[half - i] += in1[half - i - 1];
            }

            in1[0] *= Vector256.Create(Sqrt2);
            DctVector(in0, out0, forward: false);
            DctVector(in1, out1, forward: false);
            for (int i = 0; i < half; i++)
            {
                in1[i] *= Vector256.Create(sec[i]);
            }

            for (int i = 0; i < half; i++)
            {
                out0[i] = scratch[i] + scratch[i + half];
                out1[half - i - 1] = scratch[i] - scratch[i + half];
            }
        }
    }

    /// <summary><see cref="Dct4Small"/> on vectors.</summary>
    private static void Dct4Vector(Span<Vector256<float>> io, bool forward)
    {
        Vector256<float> sec0 = Vector256.Create(0.5411961f);
        Vector256<float> sec1 = Vector256.Create(1.306563f);
        Vector256<float> sqrt2 = Vector256.Create(Sqrt2);
        Vector256<float> four = Vector256.Create(4f);
        Vector256<float> i0 = io[0];
        Vector256<float> i1 = io[1];
        Vector256<float> i2 = io[2];
        Vector256<float> i3 = io[3];
        if (forward)
        {
            Vector256<float> sum03 = i0 + i3;
            Vector256<float> sum12 = i1 + i2;
            Vector256<float> tmp0 = (i0 - i3) * sec0;
            Vector256<float> tmp1 = (i1 - i2) * sec1;
            Vector256<float> out0 = (tmp0 + tmp1) / four;
            Vector256<float> out1 = (tmp0 - tmp1) / four;
            io[0] = (sum03 + sum12) / four;
            io[1] = (out0 * sqrt2) + out1;
            io[2] = (sum03 - sum12) / four;
            io[3] = out1;
        }
        else
        {
            (io[0], io[1], io[2], io[3]) = Inverse4(i0, i1, i2, i3);
        }
    }

    /// <summary><see cref="Dct8"/> on vectors.</summary>
    private static void Dct8Vector(Span<Vector256<float>> io, bool forward)
    {
        if (!forward)
        {
            Inverse8(ref io[0], ref io[1], ref io[2], ref io[3], ref io[4], ref io[5], ref io[6], ref io[7]);
            return;
        }

        ReadOnlySpan<float> sec = SecHalf(8);
        Vector256<float> two = Vector256.Create(2f);
        Span<Vector256<float>> a = stackalloc Vector256<float>[4];
        Span<Vector256<float>> b = stackalloc Vector256<float>[4];
        for (int i = 0; i < 4; i++)
        {
            a[i] = (io[i] + io[7 - i]) / two;
            b[i] = (io[i] - io[7 - i]) * Vector256.Create(sec[i]) / two;
        }

        Dct4Vector(a, forward: true);
        Dct4Vector(b, forward: true);
        for (int i = 0; i < 4; i++)
        {
            io[i * 2] = a[i];
        }

        b[0] *= Vector256.Create(Sqrt2);
        for (int i = 0; i < 3; i++)
        {
            io[(i * 2) + 1] = b[i] + b[i + 1];
        }

        io[7] = b[3];
    }
}
