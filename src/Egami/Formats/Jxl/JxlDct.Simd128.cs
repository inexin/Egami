using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Jxl;

/// <content>
/// The vector DCTs of JxlDct.Simd.cs in 128-bit vectors, for Arm64 and x86 without AVX2: a row of eight samples is a pair of
/// vectors of four (<see cref="Float8"/>), each 1D transform runs on the two halves in turn, and the operations are the same
/// in the same order, so the samples are the same bit for bit as the scalar and AVX paths.
/// </content>
internal static partial class JxlDct
{
    /// <summary>The DCT kernels on 128-bit vectors.</summary>
    internal static class Lanes4
    {
        /// <summary>Gets whether the kernels run with hardware vectors.</summary>
        internal static bool Supported => Vector128.IsHardwareAccelerated && (Sse2.IsSupported || AdvSimd.Arm64.IsSupported);

        /// <summary>Runs the inverse 8×8 DCT in place on the block at <paramref name="offset"/>, as <see cref="JxlDct.Idct8x8"/>.</summary>
        internal static void Idct8x8(Span<float> data, int offset, int stride)
        {
            if (offset + (7 * stride) + 8 > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            ref float origin = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
            Dct8x8(ref origin, stride, ref origin, stride, forward: false);
        }

        /// <summary>The 8×8 block at <paramref name="source"/> transformed into the one at <paramref name="origin"/>: rows, then columns.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Dct8x8(ref float source, int fromStride, ref float origin, int stride, bool forward)
        {
            Float8 r0 = Load(ref source, 0);
            Float8 r1 = Load(ref source, fromStride);
            Float8 r2 = Load(ref source, 2 * fromStride);
            Float8 r3 = Load(ref source, 3 * fromStride);
            Float8 r4 = Load(ref source, 4 * fromStride);
            Float8 r5 = Load(ref source, 5 * fromStride);
            Float8 r6 = Load(ref source, 6 * fromStride);
            Float8 r7 = Load(ref source, 7 * fromStride);

            // Rows: each vector pair one column x, its lanes the rows y.
            Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Lines(8, forward, ref r0.Low, ref r1.Low, ref r2.Low, ref r3.Low, ref r4.Low, ref r5.Low, ref r6.Low, ref r7.Low);
            Lines(8, forward, ref r0.High, ref r1.High, ref r2.High, ref r3.High, ref r4.High, ref r5.High, ref r6.High, ref r7.High);

            // Columns: each vector pair one row y, its lanes the columns x.
            Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Lines(8, forward, ref r0.Low, ref r1.Low, ref r2.Low, ref r3.Low, ref r4.Low, ref r5.Low, ref r6.Low, ref r7.Low);
            Lines(8, forward, ref r0.High, ref r1.High, ref r2.High, ref r3.High, ref r4.High, ref r5.High, ref r6.High, ref r7.High);
            Store(r0, ref origin, 0);
            Store(r1, ref origin, stride);
            Store(r2, ref origin, 2 * stride);
            Store(r3, ref origin, 3 * stride);
            Store(r4, ref origin, 4 * stride);
            Store(r5, ref origin, 5 * stride);
            Store(r6, ref origin, 6 * stride);
            Store(r7, ref origin, 7 * stride);
        }

        /// <summary>Runs Dct2D on a 4×4, 4×8 or 8×4 area, as <see cref="JxlDct.Dct2DSmall"/>.</summary>
        internal static bool Dct2DSmall(Span<float> data, int offset, int stride, int width, int height, bool forward)
        {
            if (width is not (4 or 8) || height is not (4 or 8) || (width == 8 && height == 8))
            {
                return false;
            }

            if (offset + ((height - 1) * stride) + width > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            ref float d = ref Unsafe.Add(ref MemoryMarshal.GetReference(data), offset);
            Float8 r0 = LoadRow(ref d, 0, width);
            Float8 r1 = LoadRow(ref d, stride, width);
            Float8 r2 = LoadRow(ref d, 2 * stride, width);
            Float8 r3 = LoadRow(ref d, 3 * stride, width);
            Float8 r4 = height == 8 ? LoadRow(ref d, 4 * stride, width) : default;
            Float8 r5 = height == 8 ? LoadRow(ref d, 5 * stride, width) : default;
            Float8 r6 = height == 8 ? LoadRow(ref d, 6 * stride, width) : default;
            Float8 r7 = height == 8 ? LoadRow(ref d, 7 * stride, width) : default;

            // Rows: each vector pair one column x, its lanes the rows y (the high halves rows 4 to 7, zero when there are four).
            Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Lines(width, forward, ref r0.Low, ref r1.Low, ref r2.Low, ref r3.Low, ref r4.Low, ref r5.Low, ref r6.Low, ref r7.Low);
            if (height == 8)
            {
                Lines(width, forward, ref r0.High, ref r1.High, ref r2.High, ref r3.High, ref r4.High, ref r5.High, ref r6.High, ref r7.High);
            }

            // Columns: each vector pair one row y, its lanes the columns x (the high halves columns 4 to 7).
            Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
            Lines(height, forward, ref r0.Low, ref r1.Low, ref r2.Low, ref r3.Low, ref r4.Low, ref r5.Low, ref r6.Low, ref r7.Low);
            if (width == 8)
            {
                Lines(height, forward, ref r0.High, ref r1.High, ref r2.High, ref r3.High, ref r4.High, ref r5.High, ref r6.High, ref r7.High);
            }

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

        /// <summary>Loads a row of 4 or 8 samples, zero above them.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Float8 LoadRow(ref float origin, int at, int width) =>
            width == 8 ? Load(ref origin, at) : new() { Low = Vector128.LoadUnsafe(ref origin, (nuint)at) };

        /// <summary>Stores a row of 4 or 8 samples.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StoreRow(Float8 v, ref float origin, int at, int width)
        {
            if (width == 8)
            {
                Store(v, ref origin, at);
            }
            else
            {
                v.Low.StoreUnsafe(ref origin, (nuint)at);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Float8 Load(ref float origin, int at) =>
            new() { Low = Vector128.LoadUnsafe(ref origin, (nuint)at), High = Vector128.LoadUnsafe(ref origin, (nuint)(at + 4)) };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Store(Float8 v, ref float origin, int at)
        {
            v.Low.StoreUnsafe(ref origin, (nuint)at);
            v.High.StoreUnsafe(ref origin, (nuint)(at + 4));
        }

        /// <summary>Transposes the 8×8 matrix whose rows are the eight vector pairs, as four 4×4 quarters.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Transpose(ref Float8 r0, ref Float8 r1, ref Float8 r2, ref Float8 r3, ref Float8 r4, ref Float8 r5, ref Float8 r6, ref Float8 r7)
        {
            Vector128<float> a0 = r0.Low, a1 = r1.Low, a2 = r2.Low, a3 = r3.Low;
            Vector128<float> b0 = r0.High, b1 = r1.High, b2 = r2.High, b3 = r3.High;
            Vector128<float> c0 = r4.Low, c1 = r5.Low, c2 = r6.Low, c3 = r7.Low;
            Vector128<float> d0 = r4.High, d1 = r5.High, d2 = r6.High, d3 = r7.High;
            Transpose4(ref a0, ref a1, ref a2, ref a3);
            Transpose4(ref b0, ref b1, ref b2, ref b3);
            Transpose4(ref c0, ref c1, ref c2, ref c3);
            Transpose4(ref d0, ref d1, ref d2, ref d3);
            r0 = new() { Low = a0, High = c0 };
            r1 = new() { Low = a1, High = c1 };
            r2 = new() { Low = a2, High = c2 };
            r3 = new() { Low = a3, High = c3 };
            r4 = new() { Low = b0, High = d0 };
            r5 = new() { Low = b1, High = d1 };
            r6 = new() { Low = b2, High = d2 };
            r7 = new() { Low = b3, High = d3 };
        }

        /// <summary>Transposes the 4×4 matrix whose rows are the four vectors.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Transpose4(ref Vector128<float> r0, ref Vector128<float> r1, ref Vector128<float> r2, ref Vector128<float> r3)
        {
            Vector128<float> t0 = ZipLow(r0, r1);
            Vector128<float> t1 = ZipHigh(r0, r1);
            Vector128<float> t2 = ZipLow(r2, r3);
            Vector128<float> t3 = ZipHigh(r2, r3);
            r0 = ZipLow(t0.AsDouble(), t2.AsDouble()).AsSingle();
            r1 = ZipHigh(t0.AsDouble(), t2.AsDouble()).AsSingle();
            r2 = ZipLow(t1.AsDouble(), t3.AsDouble()).AsSingle();
            r3 = ZipHigh(t1.AsDouble(), t3.AsDouble()).AsSingle();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<float> ZipLow(Vector128<float> a, Vector128<float> b) =>
            Sse.IsSupported ? Sse.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<float> ZipHigh(Vector128<float> a, Vector128<float> b) =>
            Sse.IsSupported ? Sse.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<double> ZipLow(Vector128<double> a, Vector128<double> b) =>
            Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<double> ZipHigh(Vector128<double> a, Vector128<double> b) =>
            Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

        /// <summary>
        /// <see cref="JxlDct.Dct2DVector(ReadOnlySpan{float}, int, Span{float}, int, int, int, bool)"/> in 128-bit vectors: false,
        /// with nothing done, unless both sides are multiples of 8.
        /// </summary>
        [SkipLocalsInit]
        internal static bool Dct2DVector(ReadOnlySpan<float> from, int fromStride, Span<float> to, int stride, int width, int height, bool forward)
        {
            if (width < 8 || height < 8 || width % 8 != 0 || height % 8 != 0)
            {
                return false;
            }

            if (width == 8 && height == 8)
            {
                if ((7 * fromStride) + 8 > from.Length || (7 * stride) + 8 > to.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(from));
                }

                Dct8x8(ref MemoryMarshal.GetReference(from), fromStride, ref MemoryMarshal.GetReference(to), stride, forward);
                return true;
            }

            float[]? rented = width * height > 4096 ? System.Buffers.ArrayPool<float>.Shared.Rent(width * height) : null;
            Span<float> transposed = rented is null ? stackalloc float[width * height] : rented;
            try
            {
                TransposeBlock(from, fromStride, transposed, height, height, width);
                Dct2DTransposed(transposed, to, stride, width, height, forward);
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

        /// <summary><see cref="JxlDct.InverseTransposed"/> in 128-bit vectors.</summary>
        internal static bool InverseTransposed(Span<float> columns, Span<float> to, int stride, int width, int height)
        {
            if (width < 8 || height < 8 || width % 8 != 0 || height % 8 != 0 || (width == 8 && height == 8))
            {
                return false;
            }

            if (columns.Length < width * height)
            {
                throw new ArgumentOutOfRangeException(nameof(columns));
            }

            Dct2DTransposed(columns, to, stride, width, height, forward: false);
            return true;
        }

        /// <summary>The rest of <see cref="Dct2DVector"/> once the block is transposed, four lanes in each half of a pair.</summary>
        [SkipLocalsInit]
        private static void Dct2DTransposed(Span<float> transposed, Span<float> to, int stride, int width, int height, bool forward)
        {
            int n = Math.Max(width, height);
            Span<Vector128<float>> low = stackalloc Vector128<float>[n];
            Span<Vector128<float>> high = stackalloc Vector128<float>[n];
            Span<Vector128<float>> scratch = stackalloc Vector128<float>[n];
            ref float t = ref MemoryMarshal.GetReference(transposed);
            if (transposed.Length < width * height || to.Length < ((height - 1) * stride) + width)
            {
                throw new ArgumentOutOfRangeException(nameof(to));
            }

            ref float d = ref MemoryMarshal.GetReference(to);
            for (int y = 0; y < height; y += 8)
            {
                for (int x = 0; x < width; x++)
                {
                    low[x] = Vector128.LoadUnsafe(ref t, (nuint)((x * height) + y));
                    high[x] = Vector128.LoadUnsafe(ref t, (nuint)((x * height) + y + 4));
                }

                DctVector(low[..width], scratch, forward);
                DctVector(high[..width], scratch, forward);
                for (int x = 0; x < width; x += 8)
                {
                    Float8 r0 = new() { Low = low[x], High = high[x] };
                    Float8 r1 = new() { Low = low[x + 1], High = high[x + 1] };
                    Float8 r2 = new() { Low = low[x + 2], High = high[x + 2] };
                    Float8 r3 = new() { Low = low[x + 3], High = high[x + 3] };
                    Float8 r4 = new() { Low = low[x + 4], High = high[x + 4] };
                    Float8 r5 = new() { Low = low[x + 5], High = high[x + 5] };
                    Float8 r6 = new() { Low = low[x + 6], High = high[x + 6] };
                    Float8 r7 = new() { Low = low[x + 7], High = high[x + 7] };
                    Transpose(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                    ref float target = ref Unsafe.Add(ref d, (y * stride) + x);
                    Store(r0, ref target, 0);
                    Store(r1, ref target, stride);
                    Store(r2, ref target, 2 * stride);
                    Store(r3, ref target, 3 * stride);
                    Store(r4, ref target, 4 * stride);
                    Store(r5, ref target, 5 * stride);
                    Store(r6, ref target, 6 * stride);
                    Store(r7, ref target, 7 * stride);
                }
            }

            // Columns, four at a time.
            for (int x = 0; x < width; x += 4)
            {
                for (int y = 0; y < height; y++)
                {
                    low[y] = Vector128.LoadUnsafe(ref d, (nuint)((y * stride) + x));
                }

                DctVector(low[..height], scratch, forward);
                for (int y = 0; y < height; y++)
                {
                    low[y].StoreUnsafe(ref d, (nuint)((y * stride) + x));
                }
            }
        }

        /// <summary><see cref="JxlDct.TransposeBlock"/> in 128-bit vectors.</summary>
        internal static void TransposeBlock(ReadOnlySpan<float> from, int fromStride, Span<float> to, int toStride, int rows, int columns)
        {
            ref float f = ref MemoryMarshal.GetReference(from);
            ref float o = ref MemoryMarshal.GetReference(to);
            if (from.Length < ((rows - 1) * fromStride) + columns || to.Length < ((columns - 1) * toStride) + rows)
            {
                throw new ArgumentOutOfRangeException(nameof(rows));
            }

            for (int y = 0; y < rows; y += 8)
            {
                for (int x = 0; x < columns; x += 8)
                {
                    ref float tile = ref Unsafe.Add(ref f, (y * fromStride) + x);
                    Float8 r0 = Load(ref tile, 0);
                    Float8 r1 = Load(ref tile, fromStride);
                    Float8 r2 = Load(ref tile, 2 * fromStride);
                    Float8 r3 = Load(ref tile, 3 * fromStride);
                    Float8 r4 = Load(ref tile, 4 * fromStride);
                    Float8 r5 = Load(ref tile, 5 * fromStride);
                    Float8 r6 = Load(ref tile, 6 * fromStride);
                    Float8 r7 = Load(ref tile, 7 * fromStride);
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

        /// <summary>Runs the 1D transform of length 4 (on the first four vectors) or 8, as <see cref="DctVector(Span{Vector128{float}}, Span{Vector128{float}}, bool)"/> does.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Lines(int n, bool forward, ref Vector128<float> i0, ref Vector128<float> i1, ref Vector128<float> i2, ref Vector128<float> i3, ref Vector128<float> i4, ref Vector128<float> i5, ref Vector128<float> i6, ref Vector128<float> i7)
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

        /// <summary>The inverse 1D DCT of length 8 (<see cref="Dct8"/> with forward false) on eight vectors at once.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Inverse8(ref Vector128<float> i0, ref Vector128<float> i1, ref Vector128<float> i2, ref Vector128<float> i3, ref Vector128<float> i4, ref Vector128<float> i5, ref Vector128<float> i6, ref Vector128<float> i7)
        {
            ReadOnlySpan<float> sec = SecHalf(8);
            Vector128<float> sqrt2 = Vector128.Create(Sqrt2);

            // a = even inputs, b = odd inputs folded, as Dct8.
            (Vector128<float> a0, Vector128<float> a1, Vector128<float> a2, Vector128<float> a3) = Inverse4(i0, i2, i4, i6);
            (Vector128<float> b0, Vector128<float> b1, Vector128<float> b2, Vector128<float> b3) = Inverse4(i1 * sqrt2, i3 + i1, i5 + i3, i7 + i5);
            Vector128<float> c0 = b0 * Vector128.Create(sec[0]);
            Vector128<float> c1 = b1 * Vector128.Create(sec[1]);
            Vector128<float> c2 = b2 * Vector128.Create(sec[2]);
            Vector128<float> c3 = b3 * Vector128.Create(sec[3]);
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
        private static (Vector128<float>, Vector128<float>, Vector128<float>, Vector128<float>) Inverse4(Vector128<float> i0, Vector128<float> i1, Vector128<float> i2, Vector128<float> i3)
        {
            Vector128<float> tmp0 = i1 * Vector128.Create(Sqrt2);
            Vector128<float> tmp1 = i1 + i3;
            Vector128<float> out0 = (tmp0 + tmp1) * Vector128.Create(0.5411961f);
            Vector128<float> out1 = (tmp0 - tmp1) * Vector128.Create(1.306563f);
            Vector128<float> sum02 = i0 + i2;
            Vector128<float> sub02 = i0 - i2;
            return (sum02 + out0, sub02 + out1, sub02 - out1, sum02 - out0);
        }

        /// <summary>The 1D DCT of <see cref="Dct(Span{float}, Span{float}, bool)"/> on vectors: each lane its own transform.</summary>
        private static void DctVector(Span<Vector128<float>> io, Span<Vector128<float>> scratch, bool forward)
        {
            if (scratch.Length < io.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(scratch));
            }

            DctVector(ref MemoryMarshal.GetReference(io), ref MemoryMarshal.GetReference(scratch), io.Length, forward);
        }

        /// <summary><see cref="DctVector(Span{Vector128{float}}, Span{Vector128{float}}, bool)"/> on <paramref name="n"/> vectors at <paramref name="io"/>, with <paramref name="n"/> of scratch.</summary>
        private static void DctVector(ref Vector128<float> io, ref Vector128<float> scratch, int n, bool forward)
        {
            switch (n)
            {
                case <= 1:
                    return;
                case 2:
                    {
                        Vector128<float> t0 = io + Unsafe.Add(ref io, 1);
                        Vector128<float> t1 = io - Unsafe.Add(ref io, 1);
                        Vector128<float> half2 = Vector128.Create(0.5f);
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
                case 16:
                    Dct16Vector(ref io, forward);
                    return;
            }

            int half = n / 2;
            ref float sec = ref MemoryMarshal.GetReference(SecHalf(n));
            ref Vector128<float> in0 = ref scratch;
            ref Vector128<float> in1 = ref Unsafe.Add(ref scratch, half);
            ref Vector128<float> out1 = ref Unsafe.Add(ref io, half);
            Vector128<float> halfV = Vector128.Create(0.5f);
            if (forward)
            {
                for (int i = 0; i < half; i++)
                {
                    Vector128<float> a = Unsafe.Add(ref io, i);
                    Vector128<float> b = Unsafe.Add(ref io, n - i - 1);
                    Unsafe.Add(ref in0, i) = (a + b) * halfV;
                    Unsafe.Add(ref in1, i) = (a - b) * halfV * Vector128.Create(Unsafe.Add(ref sec, i));
                }

                DctVector(ref in0, ref io, half, forward: true);
                DctVector(ref in1, ref out1, half, forward: true);
                in1 *= Vector128.Create(Sqrt2);
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

                in1 *= Vector128.Create(Sqrt2);
                DctVector(ref in0, ref io, half, forward: false);
                DctVector(ref in1, ref out1, half, forward: false);
                for (int i = 0; i < half; i++)
                {
                    Vector128<float> a = Unsafe.Add(ref in0, i);
                    Vector128<float> b = Unsafe.Add(ref in1, i) * Vector128.Create(Unsafe.Add(ref sec, i));
                    Unsafe.Add(ref io, i) = a + b;
                    Unsafe.Add(ref out1, half - i - 1) = a - b;
                }
            }
        }

        /// <summary>
        /// The 16-point case of <see cref="DctVector(ref Vector128{float}, ref Vector128{float}, int, bool)"/> in locals: the halves,
        /// their 8-point transforms and the merge, with the same operations in the same order.
        /// </summary>
        private static void Dct16Vector(ref Vector128<float> io, bool forward)
        {
            ref float sec = ref MemoryMarshal.GetReference(SecHalf(16));
            Vector128<float> sqrt2 = Vector128.Create(Sqrt2);
            if (forward)
            {
                Vector128<float> half = Vector128.Create(0.5f);
                Vector128<float> a0 = Pair(ref io, 0, half, out Vector128<float> b0, ref sec);
                Vector128<float> a1 = Pair(ref io, 1, half, out Vector128<float> b1, ref sec);
                Vector128<float> a2 = Pair(ref io, 2, half, out Vector128<float> b2, ref sec);
                Vector128<float> a3 = Pair(ref io, 3, half, out Vector128<float> b3, ref sec);
                Vector128<float> a4 = Pair(ref io, 4, half, out Vector128<float> b4, ref sec);
                Vector128<float> a5 = Pair(ref io, 5, half, out Vector128<float> b5, ref sec);
                Vector128<float> a6 = Pair(ref io, 6, half, out Vector128<float> b6, ref sec);
                Vector128<float> a7 = Pair(ref io, 7, half, out Vector128<float> b7, ref sec);
                Forward8(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
                Forward8(ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7);
                b0 *= sqrt2;
                io = a0;
                Unsafe.Add(ref io, 1) = b0 + b1;
                Unsafe.Add(ref io, 2) = a1;
                Unsafe.Add(ref io, 3) = b1 + b2;
                Unsafe.Add(ref io, 4) = a2;
                Unsafe.Add(ref io, 5) = b2 + b3;
                Unsafe.Add(ref io, 6) = a3;
                Unsafe.Add(ref io, 7) = b3 + b4;
                Unsafe.Add(ref io, 8) = a4;
                Unsafe.Add(ref io, 9) = b4 + b5;
                Unsafe.Add(ref io, 10) = a5;
                Unsafe.Add(ref io, 11) = b5 + b6;
                Unsafe.Add(ref io, 12) = a6;
                Unsafe.Add(ref io, 13) = b6 + b7;
                Unsafe.Add(ref io, 14) = a7;
                Unsafe.Add(ref io, 15) = b7;
            }
            else
            {
                Vector128<float> a0 = io, a1 = Unsafe.Add(ref io, 2), a2 = Unsafe.Add(ref io, 4), a3 = Unsafe.Add(ref io, 6);
                Vector128<float> a4 = Unsafe.Add(ref io, 8), a5 = Unsafe.Add(ref io, 10), a6 = Unsafe.Add(ref io, 12), a7 = Unsafe.Add(ref io, 14);
                Vector128<float> o0 = Unsafe.Add(ref io, 1), o1 = Unsafe.Add(ref io, 3), o2 = Unsafe.Add(ref io, 5), o3 = Unsafe.Add(ref io, 7);
                Vector128<float> o4 = Unsafe.Add(ref io, 9), o5 = Unsafe.Add(ref io, 11), o6 = Unsafe.Add(ref io, 13), o7 = Unsafe.Add(ref io, 15);

                // in1[half - i] += in1[half - i - 1], from the top down.
                Vector128<float> b7 = o7 + o6, b6 = o6 + o5, b5 = o5 + o4, b4 = o4 + o3, b3 = o3 + o2, b2 = o2 + o1, b1 = o1 + o0;
                Vector128<float> b0 = o0 * sqrt2;
                Inverse8(ref a0, ref a1, ref a2, ref a3, ref a4, ref a5, ref a6, ref a7);
                Inverse8(ref b0, ref b1, ref b2, ref b3, ref b4, ref b5, ref b6, ref b7);
                Merge(ref io, 0, a0, b0, ref sec);
                Merge(ref io, 1, a1, b1, ref sec);
                Merge(ref io, 2, a2, b2, ref sec);
                Merge(ref io, 3, a3, b3, ref sec);
                Merge(ref io, 4, a4, b4, ref sec);
                Merge(ref io, 5, a5, b5, ref sec);
                Merge(ref io, 6, a6, b6, ref sec);
                Merge(ref io, 7, a7, b7, ref sec);
            }
        }

        /// <summary>The forward split of input pair (i, 15 − i): the half sum, and the half difference times the secant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<float> Pair(ref Vector128<float> io, int i, Vector128<float> half, out Vector128<float> difference, ref float sec)
        {
            Vector128<float> a = Unsafe.Add(ref io, i);
            Vector128<float> b = Unsafe.Add(ref io, 15 - i);
            difference = (a - b) * half * Vector128.Create(Unsafe.Add(ref sec, i));
            return (a + b) * half;
        }

        /// <summary>The inverse merge of output pair (i, 15 − i).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Merge(ref Vector128<float> io, int i, Vector128<float> a, Vector128<float> b, ref float sec)
        {
            Vector128<float> r = b * Vector128.Create(Unsafe.Add(ref sec, i));
            Unsafe.Add(ref io, i) = a + r;
            Unsafe.Add(ref io, 15 - i) = a - r;
        }

        /// <summary>The forward 1D DCT of length 8 (<see cref="Dct8"/> with forward true) on eight vectors at once, in registers.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Forward8(ref Vector128<float> i0, ref Vector128<float> i1, ref Vector128<float> i2, ref Vector128<float> i3, ref Vector128<float> i4, ref Vector128<float> i5, ref Vector128<float> i6, ref Vector128<float> i7)
        {
            ReadOnlySpan<float> sec = SecHalf(8);
            Vector128<float> half = Vector128.Create(0.5f);
            (Vector128<float> a0, Vector128<float> a1, Vector128<float> a2, Vector128<float> a3) =
                Forward4((i0 + i7) * half, (i1 + i6) * half, (i2 + i5) * half, (i3 + i4) * half);
            (Vector128<float> b0, Vector128<float> b1, Vector128<float> b2, Vector128<float> b3) = Forward4(
                (i0 - i7) * Vector128.Create(sec[0]) * half,
                (i1 - i6) * Vector128.Create(sec[1]) * half,
                (i2 - i5) * Vector128.Create(sec[2]) * half,
                (i3 - i4) * Vector128.Create(sec[3]) * half);
            b0 *= Vector128.Create(Sqrt2);
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
        private static (Vector128<float>, Vector128<float>, Vector128<float>, Vector128<float>) Forward4(Vector128<float> i0, Vector128<float> i1, Vector128<float> i2, Vector128<float> i3)
        {
            Vector128<float> quarter = Vector128.Create(0.25f);
            Vector128<float> sum03 = i0 + i3;
            Vector128<float> sum12 = i1 + i2;
            Vector128<float> tmp0 = (i0 - i3) * Vector128.Create(0.5411961f);
            Vector128<float> tmp1 = (i1 - i2) * Vector128.Create(1.306563f);
            Vector128<float> out0 = (tmp0 + tmp1) * quarter;
            Vector128<float> out1 = (tmp0 - tmp1) * quarter;
            return ((sum03 + sum12) * quarter, (out0 * Vector128.Create(Sqrt2)) + out1, (sum03 - sum12) * quarter, out1);
        }
    }

    /// <summary>Eight float lanes as two 128-bit vectors: lanes 0 to 3, then 4 to 7.</summary>
    private struct Float8
    {
        public Vector128<float> Low;
        public Vector128<float> High;
    }
}
