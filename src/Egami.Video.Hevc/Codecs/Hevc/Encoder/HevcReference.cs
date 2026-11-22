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

            for (int x = 0; x < Stride; x++)
            {
                if (x < first || x > last)
                {
                    to[row + x] = from[row + x];
                    continue;
                }

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
