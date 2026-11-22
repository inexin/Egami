using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Heic.Hevc;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// A reference picture as the motion search sees it: its decoded picture buffer entry, and its luma at whole and half-sample
/// positions in four planes with <see cref="Pad"/> samples of repeated edge around them, made once when the picture becomes a
/// reference. A quarter-sample position is the average of the two nearest half-sample grid positions, as x264's search takes
/// them; the coding itself predicts with the decoder's exact 8-tap interpolation.
/// </summary>
internal sealed class HevcReference
{
    /// <summary>The samples of repeated edge around each plane: a 64x64 block may lie wholly outside the picture, and the filter reads 4 more.</summary>
    internal const int Pad = 80;

    private static readonly int[] HalfTaps = [-1, 4, -11, 40, 40, -11, 4, -1];

    internal HevcReference(HevcDpbPicture picture)
    {
        Picture = picture;
        HevcPicture p = picture.Picture;
        Width = p.Widths[0];
        Height = p.Heights[0];
        Stride = Width + (2 * Pad);
        int rows = Height + (2 * Pad);
        int max = (1 << p.Sps.BitDepthLuma) - 1;
        Planes = [new ushort[Stride * rows], new ushort[Stride * rows], new ushort[Stride * rows], new ushort[Stride * rows]];

        // The whole samples with their edges repeated.
        ushort[] full = Planes[0], source = p.Planes[0];
        for (int y = 0; y < rows; y++)
        {
            int sy = Math.Clamp(y - Pad, 0, Height - 1);
            Span<ushort> row = full.AsSpan(y * Stride, Stride);
            source.AsSpan(sy * Width, Width).CopyTo(row[Pad..]);
            row[..Pad].Fill(source[sy * Width]);
            row[(Pad + Width)..].Fill(source[(sy * Width) + Width - 1]);
        }

        // Half samples: H between columns, V between rows, HV the vertical filter of H (each rounded to a sample).
        Filter(full, Planes[1], 1, rows, max);
        Filter(full, Planes[2], Stride, rows, max);
        Filter(Planes[1], Planes[3], Stride, rows, max);
        if (p.Sps.BitDepthLuma == 8)
        {
            Planes8 = [Bytes(Planes[0]), Bytes(Planes[1]), Bytes(Planes[2]), Bytes(Planes[3])];
        }
    }

    /// <summary>Gets <see cref="Planes"/> as bytes when luma has 8 bits, for the whole and half-sample SAD, else null.</summary>
    internal byte[][]? Planes8 { get; }

    /// <summary>A plane of samples that fit a byte, as bytes.</summary>
    internal static byte[] Bytes(ushort[] plane)
    {
        byte[] bytes = GC.AllocateUninitializedArray<byte>(plane.Length);
        ref ushort p0 = ref MemoryMarshal.GetArrayDataReference(plane);
        ref byte b0 = ref MemoryMarshal.GetArrayDataReference(bytes);
        int i = 0;
        for (; i + 16 <= plane.Length; i += 16)
        {
            Vector128.Narrow(Vector128.LoadUnsafe(ref p0, (nuint)i), Vector128.LoadUnsafe(ref p0, (nuint)(i + 8))).StoreUnsafe(ref b0, (nuint)i);
        }

        for (; i < plane.Length; i++)
        {
            bytes[i] = (byte)plane[i];
        }

        return bytes;
    }

    /// <summary>Gets or sets whether the picture is a P or I picture, which P pictures predict from (a B picture is not).</summary>
    internal bool Anchor { get; init; }

    /// <summary>Gets the decoded picture buffer entry.</summary>
    internal HevcDpbPicture Picture { get; }

    /// <summary>Gets the luma width.</summary>
    internal int Width { get; }

    /// <summary>Gets the luma height.</summary>
    internal int Height { get; }

    /// <summary>Gets each plane's row length.</summary>
    internal int Stride { get; }

    /// <summary>Gets the planes at half-sample offsets (x, y) of (0, 0), (1/2, 0), (0, 1/2) and (1/2, 1/2), index x + 2y.</summary>
    internal ushort[][] Planes { get; }

    /// <summary>Gets the index in a plane of luma sample (x, y), which may lie up to <see cref="Pad"/> outside the picture.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int At(int x, int y) => ((y + Pad) * Stride) + x + Pad;

    /// <summary>
    /// Writes the w by h block at quarter-sample vector (mvx, mvy) from luma sample (x, y) into <paramref name="block"/> (rows
    /// <paramref name="blockStride"/> apart): whole and half positions from their plane, quarter positions averaged.
    /// </summary>
    internal void Block(int x, int y, int mvx, int mvy, int w, int h, ushort[] block, int blockStride)
    {
        // Half-sample grid coordinates of the two positions to average (the diagonal ones when both components are quarters):
        // equal for a whole or half position.
        int hx0 = ((4 * x) + mvx) >> 1, hy0 = ((4 * y) + mvy) >> 1;
        int hx1 = ((4 * x) + mvx + 1) >> 1, hy1 = ((4 * y) + mvy + 1) >> 1;
        ushort[] a = Planes[(hx0 & 1) + (2 * (hy0 & 1))], b = Planes[(hx1 & 1) + (2 * (hy1 & 1))];
        int atA = At(hx0 >> 1, hy0 >> 1), atB = At(hx1 >> 1, hy1 >> 1);
        if (atA == atB && a == b)
        {
            for (int r = 0; r < h; r++)
            {
                a.AsSpan(atA + (r * Stride), w).CopyTo(block.AsSpan(r * blockStride));
            }

            return;
        }

        for (int r = 0; r < h; r++)
        {
            ref ushort pa = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), atA + (r * Stride));
            ref ushort pb = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), atB + (r * Stride));
            ref ushort d = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(block), r * blockStride);
            int k = 0;
            for (; k + 16 <= w; k += 16)
            {
                Vector256<ushort> va = Vector256.LoadUnsafe(ref pa, (nuint)k), vb = Vector256.LoadUnsafe(ref pb, (nuint)k);
                ((va + vb + Vector256<ushort>.One) >> 1).StoreUnsafe(ref d, (nuint)k);
            }

            for (; k + 8 <= w; k += 8)
            {
                Vector128<ushort> va = Vector128.LoadUnsafe(ref pa, (nuint)k), vb = Vector128.LoadUnsafe(ref pb, (nuint)k);
                ((va + vb + Vector128<ushort>.One) >> 1).StoreUnsafe(ref d, (nuint)k);
            }

            for (; k < w; k++)
            {
                Unsafe.Add(ref d, k) = (ushort)((Unsafe.Add(ref pa, k) + Unsafe.Add(ref pb, k) + 1) >> 1);
            }
        }
    }

    /// <summary>The half-sample filter along one direction (step 1 across, stride down), each output between samples 0 and 1.</summary>
    private void Filter(ushort[] from, ushort[] to, int step, int rows, int max)
    {
        for (int y = 0; y < rows; y++)
        {
            int row = y * Stride;
            int first = step == 1 ? 3 : (y >= 3 ? 0 : -1), last = step == 1 ? Stride - 5 : (y + 4 < rows ? Stride - 1 : -1);
            if (first < 0 || last < 0)
            {
                // Rows too near the edge for the vertical taps copy the row next to them (they lie in the padding).
                from.AsSpan(row, Stride).CopyTo(to.AsSpan(row));
                continue;
            }

            from.AsSpan(row, first).CopyTo(to.AsSpan(row));
            from.AsSpan(row + last + 1, Stride - last - 1).CopyTo(to.AsSpan(row + last + 1));
            int x = first;
            if (Vector256.IsHardwareAccelerated)
            {
                // Eight outputs a step in 32-bit lanes: the taps' sum of a sample's magnitude stays far inside an int.
                ref ushort f0 = ref MemoryMarshal.GetArrayDataReference(from);
                ref ushort t0 = ref MemoryMarshal.GetArrayDataReference(to);
                Vector256<int> round = Vector256.Create(32), top = Vector256.Create(max);
                for (; x + 8 <= last + 1; x += 8)
                {
                    int at = row + x - (3 * step);
                    Vector256<int> sum = Vector256<int>.Zero;
                    for (int t = 0; t < 8; t++)
                    {
                        Vector256<int> v = Vector256.WidenLower(Vector128.LoadUnsafe(ref f0, (nuint)(at + (t * step))).ToVector256()).AsInt32();
                        sum += v * Vector256.Create(HalfTaps[t]);
                    }

                    Vector256<int> result = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(sum + round, 6), Vector256<int>.Zero), top);
                    Vector128<ushort> packed = Vector128.Narrow(result.GetLower().AsUInt32(), result.GetUpper().AsUInt32());
                    packed.StoreUnsafe(ref t0, (nuint)(row + x));
                }
            }

            for (; x <= last; x++)
            {
                int at = row + x - (3 * step), sum = 0;
                for (int t = 0; t < 8; t++)
                {
                    sum += HalfTaps[t] * from[at + (t * step)];
                }

                to[row + x] = (ushort)Math.Clamp((sum + 32) >> 6, 0, max);
            }
        }
    }
}
