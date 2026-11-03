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
    /// Runs the forward or inverse 8×8 DCT in place as <see cref="Dct2DVector(Span{float}, int, int, int, int, bool)"/> does (rows, then columns, with the same
    /// operations), the block kept in eight registers: the lossy encoder runs it for every block it tries.
    /// </summary>
    private static void Dct8x8(Span<float> data, int offset, int stride, bool forward) =>
        Dct8x8(data[offset..], stride, data[offset..], stride, forward);

    /// <summary><see cref="Dct8x8(Span{float}, int, int, bool)"/> from the block at <paramref name="from"/> into the one at <paramref name="to"/>.</summary>
    private static void Dct8x8(ReadOnlySpan<float> from, int fromStride, Span<float> to, int stride, bool forward)
    {
        if ((7 * fromStride) + 8 > from.Length || (7 * stride) + 8 > to.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(from));
        }

        ref float source = ref MemoryMarshal.GetReference(from);
        ref float origin = ref MemoryMarshal.GetReference(to);
        Vector256<float> r0 = Load(ref source, 0);
        Vector256<float> r1 = Load(ref source, fromStride);
        Vector256<float> r2 = Load(ref source, 2 * fromStride);
        Vector256<float> r3 = Load(ref source, 3 * fromStride);
        Vector256<float> r4 = Load(ref source, 4 * fromStride);
        Vector256<float> r5 = Load(ref source, 5 * fromStride);
        Vector256<float> r6 = Load(ref source, 6 * fromStride);
        Vector256<float> r7 = Load(ref source, 7 * fromStride);
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        if (forward)
        {
            Forward8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Forward8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        }
        else
        {
            Inverse8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Inverse8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        }

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
    /// Runs Dct2D on a 4×4, 4×8 or 8×4 area with vectors, rows then columns as the scalar path, each 1D transform run on all
    /// rows (or columns) at once by <see cref="DctVector(Span{Vector256{float}}, Span{Vector256{float}}, bool)"/>, which keeps the scalar operations: the samples are the same. The
    /// area is held as an 8×8 block of registers, zero outside it, and turned with <see cref="Transpose"/>.
    /// </summary>
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

        ref float d = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
        Vector256<float> r0 = LoadRow(ref d, 0, width);
        Vector256<float> r1 = LoadRow(ref d, stride, width);
        Vector256<float> r2 = LoadRow(ref d, 2 * stride, width);
        Vector256<float> r3 = LoadRow(ref d, 3 * stride, width);
        Vector256<float> r4 = height == 8 ? LoadRow(ref d, 4 * stride, width) : default;
        Vector256<float> r5 = height == 8 ? LoadRow(ref d, 5 * stride, width) : default;
        Vector256<float> r6 = height == 8 ? LoadRow(ref d, 6 * stride, width) : default;
        Vector256<float> r7 = height == 8 ? LoadRow(ref d, 7 * stride, width) : default;

        // Rows: each vector one column x, its lanes the rows y.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Lines(width, forward, ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

        // Columns: each vector one row y, its lanes the columns x.
        Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        Lines(height, forward, ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
        StoreRow(r0, ref d, 0, width);
        StoreRow(r1, ref d, stride, width);
        StoreRow(r2, ref d, 2 * stride, width);
        StoreRow(r3, ref d, 3 * stride, width);
        if (height == 8)
        {
            StoreRow(r4, ref d, 4 * stride, width);
            StoreRow(r5, ref d, 5 * stride, width);
            StoreRow(r6, ref d, 6 * stride, width);
            StoreRow(r7, ref d, 7 * stride, width);
        }

        return true;
    }

    /// <summary>Runs the 1D transform of length 4 (on the first four vectors) or 8, as <see cref="DctVector(Span{Vector256{float}}, Span{Vector256{float}}, bool)"/> does.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Lines(int n, bool forward, ref Vector256<float> i0, ref Vector256<float> i1, ref Vector256<float> i2, ref Vector256<float> i3, ref Vector256<float> i4, ref Vector256<float> i5, ref Vector256<float> i6, ref Vector256<float> i7)
    {
        if (n == 8)
        {
            if (forward)
            {
                Forward8(ref i0, ref i1, ref i2, ref i3, ref i4, ref i5, ref i6, ref i7);
            }
            else
            {
                Inverse8(ref i0, ref i1, ref i2, ref i3, ref i4, ref i5, ref i6, ref i7);
            }
        }
        else
        {
            (i0, i1, i2, i3) = forward ? Forward4(i0, i1, i2, i3) : Inverse4(i0, i1, i2, i3);
        }
    }

    /// <summary>Loads a row of 4 or 8 samples, zero above them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> LoadRow(ref float origin, int at, int width) =>
        width == 8 ? Load(ref origin, at) : Vector128.LoadUnsafe(ref origin, (nuint)at).ToVector256();

    /// <summary>Stores a row of 4 or 8 samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreRow(Vector256<float> v, ref float origin, int at, int width)
    {
        if (width == 8)
        {
            Store(v, ref origin, at);
        }
        else
        {
            v.GetLower().StoreUnsafe(ref origin, (nuint)at);
        }
    }

    private static Vector256<float> Load(ref float origin, int at) => Vector256.LoadUnsafe(ref origin, (nuint)at);

    private static void Store(Vector256<float> v, ref float origin, int at) => v.StoreUnsafe(ref origin, (nuint)at);

    /// <summary>The inverse 1D DCT of length 8 (<see cref="Dct8"/> with forward false) on eight vectors at once.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    private static bool Dct2DVector(Span<float> data, int offset, int stride, int width, int height, bool forward) =>
        Dct2DVector(data[offset..], stride, data[offset..], stride, width, height, forward);

    /// <summary>
    /// Runs the forward 2D DCT of the <paramref name="width"/>×<paramref name="height"/> area at <paramref name="from"/> into
    /// <paramref name="to"/> (as a copy there, then <see cref="Dct2D"/>, would), when both sides are multiples of 8 and AVX is
    /// there; returns false otherwise. The encoder transforms its planes this way without copying each block out first.
    /// </summary>
    internal static bool ForwardFrom(ReadOnlySpan<float> from, int fromStride, Span<float> to, int stride, int width, int height) =>
        Dct2DVector(from, fromStride, to, stride, width, height, forward: true);

    [System.Runtime.CompilerServices.SkipLocalsInit]
    private static bool Dct2DVector(ReadOnlySpan<float> from, int fromStride, Span<float> to, int stride, int width, int height, bool forward)
    {
        if (!Avx.IsSupported || width < 8 || height < 8 || width % 8 != 0 || height % 8 != 0)
        {
            return false;
        }

        if (width == 8 && height == 8)
        {
            Dct8x8(from, fromStride, to, stride, forward);
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
            TransposeBlock(from, fromStride, transposed, height, height, width);
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

            TransposeBlock(transposed, height, to, stride, width, height);

            // Columns, eight at a time.
            ref float d = ref MemoryMarshal.GetReference(to);
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
        if (scratch.Length < io.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(scratch));
        }

        DctVector(ref MemoryMarshal.GetReference(io), ref MemoryMarshal.GetReference(scratch), io.Length, forward);
    }

    /// <summary><see cref="DctVector(Span{Vector256{float}}, Span{Vector256{float}}, bool)"/> on <paramref name="n"/> vectors at <paramref name="io"/>, with <paramref name="n"/> of scratch.</summary>
    private static void DctVector(ref Vector256<float> io, ref Vector256<float> scratch, int n, bool forward)
    {
        switch (n)
        {
            case <= 1:
                return;
            case 2:
                {
                    Vector256<float> t0 = io + Unsafe.Add(ref io, 1);
                    Vector256<float> t1 = io - Unsafe.Add(ref io, 1);
                    Vector256<float> half2 = Vector256.Create(0.5f);
                    io = forward ? t0 * half2 : t0;
                    Unsafe.Add(ref io, 1) = forward ? t1 * half2 : t1;
                    return;
                }

            case 4:
                (io, Unsafe.Add(ref io, 1), Unsafe.Add(ref io, 2), Unsafe.Add(ref io, 3)) = forward
                    ? Forward4(io, Unsafe.Add(ref io, 1), Unsafe.Add(ref io, 2), Unsafe.Add(ref io, 3))
                    : Inverse4(io, Unsafe.Add(ref io, 1), Unsafe.Add(ref io, 2), Unsafe.Add(ref io, 3));
                return;
            case 8:
                if (forward)
                {
                    Forward8(ref io, ref Unsafe.Add(ref io, 1), ref Unsafe.Add(ref io, 2), ref Unsafe.Add(ref io, 3), ref Unsafe.Add(ref io, 4), ref Unsafe.Add(ref io, 5), ref Unsafe.Add(ref io, 6), ref Unsafe.Add(ref io, 7));
                }
                else
                {
                    Inverse8(ref io, ref Unsafe.Add(ref io, 1), ref Unsafe.Add(ref io, 2), ref Unsafe.Add(ref io, 3), ref Unsafe.Add(ref io, 4), ref Unsafe.Add(ref io, 5), ref Unsafe.Add(ref io, 6), ref Unsafe.Add(ref io, 7));
                }

                return;
        }

        int half = n / 2;
        ref float sec = ref MemoryMarshal.GetReference(SecHalf(n));
        ref Vector256<float> in0 = ref scratch;
        ref Vector256<float> in1 = ref Unsafe.Add(ref scratch, half);
        ref Vector256<float> out1 = ref Unsafe.Add(ref io, half);
        Vector256<float> halfV = Vector256.Create(0.5f);
        if (forward)
        {
            for (int i = 0; i < half; i++)
            {
                Vector256<float> a = Unsafe.Add(ref io, i);
                Vector256<float> b = Unsafe.Add(ref io, n - i - 1);
                Unsafe.Add(ref in0, i) = (a + b) * halfV;
                Unsafe.Add(ref in1, i) = (a - b) * halfV * Vector256.Create(Unsafe.Add(ref sec, i));
            }

            DctVector(ref in0, ref io, half, forward: true);
            DctVector(ref in1, ref out1, half, forward: true);
            in1 *= Vector256.Create(Sqrt2);
            for (int i = 0; i < half - 1; i++)
            {
                Unsafe.Add(ref in1, i) += Unsafe.Add(ref in1, i + 1);
            }

            for (int i = 0; i < half; i++)
            {
                Unsafe.Add(ref io, 2 * i) = Unsafe.Add(ref in0, i);
                Unsafe.Add(ref io, (2 * i) + 1) = Unsafe.Add(ref in1, i);
            }
        }
        else
        {
            for (int i = 0; i < half; i++)
            {
                Unsafe.Add(ref in0, i) = Unsafe.Add(ref io, 2 * i);
                Unsafe.Add(ref in1, i) = Unsafe.Add(ref io, (2 * i) + 1);
            }

            for (int i = 1; i < half; i++)
            {
                Unsafe.Add(ref in1, half - i) += Unsafe.Add(ref in1, half - i - 1);
            }

            in1 *= Vector256.Create(Sqrt2);
            DctVector(ref in0, ref io, half, forward: false);
            DctVector(ref in1, ref out1, half, forward: false);
            for (int i = 0; i < half; i++)
            {
                Vector256<float> a = Unsafe.Add(ref in0, i);
                Vector256<float> b = Unsafe.Add(ref in1, i) * Vector256.Create(Unsafe.Add(ref sec, i));
                Unsafe.Add(ref io, i) = a + b;
                Unsafe.Add(ref out1, half - i - 1) = a - b;
            }
        }
    }

    /// <summary>The forward 1D DCT of length 8 (<see cref="Dct8"/> with forward true) on eight vectors at once, in registers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Forward8(ref Vector256<float> i0, ref Vector256<float> i1, ref Vector256<float> i2, ref Vector256<float> i3, ref Vector256<float> i4, ref Vector256<float> i5, ref Vector256<float> i6, ref Vector256<float> i7)
    {
        ReadOnlySpan<float> sec = SecHalf(8);
        Vector256<float> half = Vector256.Create(0.5f);
        (Vector256<float> a0, Vector256<float> a1, Vector256<float> a2, Vector256<float> a3) =
            Forward4((i0 + i7) * half, (i1 + i6) * half, (i2 + i5) * half, (i3 + i4) * half);
        (Vector256<float> b0, Vector256<float> b1, Vector256<float> b2, Vector256<float> b3) = Forward4(
            (i0 - i7) * Vector256.Create(sec[0]) * half,
            (i1 - i6) * Vector256.Create(sec[1]) * half,
            (i2 - i5) * Vector256.Create(sec[2]) * half,
            (i3 - i4) * Vector256.Create(sec[3]) * half);
        b0 *= Vector256.Create(Sqrt2);
        i0 = a0;
        i2 = a1;
        i4 = a2;
        i6 = a3;
        i1 = b0 + b1;
        i3 = b1 + b2;
        i5 = b2 + b3;
        i7 = b3;
    }

    /// <summary>The forward 1D DCT of length 4 (<see cref="Dct4Small"/> with forward true) on four vectors.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector256<float>, Vector256<float>, Vector256<float>, Vector256<float>) Forward4(Vector256<float> i0, Vector256<float> i1, Vector256<float> i2, Vector256<float> i3)
    {
        Vector256<float> quarter = Vector256.Create(0.25f);
        Vector256<float> sum03 = i0 + i3;
        Vector256<float> sum12 = i1 + i2;
        Vector256<float> tmp0 = (i0 - i3) * Vector256.Create(0.5411961f);
        Vector256<float> tmp1 = (i1 - i2) * Vector256.Create(1.306563f);
        Vector256<float> out0 = (tmp0 + tmp1) * quarter;
        Vector256<float> out1 = (tmp0 - tmp1) * quarter;
        return ((sum03 + sum12) * quarter, (out0 * Vector256.Create(Sqrt2)) + out1, (sum03 - sum12) * quarter, out1);
    }
}
