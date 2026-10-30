using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Heic.Hevc;

/// <summary>
/// Intra sample prediction (H.265 8.4.4.2): the reference samples with substitution, their filtering (and strong intra smoothing),
/// and the planar, DC and 33 angular modes, written straight into the picture's plane.
/// </summary>
internal sealed class HevcIntra
{
    /// <summary>intraPredAngle (Table 8-5) for modes 2 to 34.</summary>
    private static readonly int[] Angles = [32, 26, 21, 17, 13, 9, 5, 2, 0, -2, -5, -9, -13, -17, -21, -26, -32, -26, -21, -17, -13, -9, -5, -2, 0, 2, 5, 9, 13, 17, 21, 26, 32];

    /// <summary>invAngle (Table 8-6) for modes 11 to 25.</summary>
    private static readonly int[] InverseAngles = [-4096, -1638, -910, -630, -482, -390, -315, -256, -315, -390, -482, -630, -910, -1638, -4096];

    // p[-1][2N-1] .. p[-1][-1] .. p[2N-1][-1] in one array: left column bottom-up, the corner, then the top row.
    private readonly int[] _ref = new int[(4 * 64) + 1];
    private readonly int[] _filtered = new int[(4 * 64) + 1];
    private readonly int[] _line = new int[(3 * 64) + 1];
    private readonly ushort[] _block = new ushort[32 * 32];

    /// <summary>
    /// Predicts a square block of component <paramref name="c"/> at (x0, y0) in component samples, 1 &lt;&lt; log2 wide, with
    /// intra mode <paramref name="mode"/> (already mapped for 4:2:2 chroma).
    /// </summary>
    internal void Predict(HevcPicture picture, int c, int x0, int y0, int log2, int mode, bool disableBoundaryFilter)
    {
        HevcSps sps = picture.Sps;
        int n = 1 << log2;
        int bitDepth = c == 0 ? sps.BitDepthLuma : sps.BitDepthChroma;
        ushort[] plane = picture.Planes[c];
        int stride = picture.Widths[c];
        int sx = c == 0 ? 1 : sps.SubWidthC, sy = c == 0 ? 1 : sps.SubHeightC;
        int corner = 2 * n;

        // Availability (6.4.1) is the same for all samples of a 4x4 luma unit: checked once per unit. The units are segments of
        // _ref: the left column's from the bottom up, the corner, then the top row's.
        int xCurr = x0 * sx, yCurr = y0 * sy;
        int zCurr = picture.ZScan[((yCurr >> 2) * picture.Width4) + (xCurr >> 2)];
        int rows = 4 / sy, columns = 4 / sx, leftUnits = 2 * n / rows, units = leftUnits + 1 + (2 * n / columns);
        Span<bool> available = stackalloc bool[units];
        ref int r = ref MemoryMarshal.GetArrayDataReference(_ref);
        ref ushort samples = ref MemoryMarshal.GetArrayDataReference(plane);
        int found = 0;
        for (int u = 0; u < leftUnits; u++)
        {
            int y = y0 + (2 * n) - 1 - (u * rows);
            bool a = Neighbour(picture, xCurr, yCurr, zCurr, (x0 - 1) * sx, y * sy);
            available[u] = a;
            if (a)
            {
                found++;
                for (int k = 0; k < rows; k++)
                {
                    Unsafe.Add(ref r, (u * rows) + k) = Unsafe.Add(ref samples, ((y - k) * stride) + x0 - 1);
                }
            }
        }

        if (available[leftUnits] = Neighbour(picture, xCurr, yCurr, zCurr, (x0 - 1) * sx, (y0 - 1) * sy))
        {
            found++;
            Unsafe.Add(ref r, corner) = Unsafe.Add(ref samples, ((y0 - 1) * stride) + x0 - 1);
        }

        int top = (y0 - 1) * stride;
        for (int u = leftUnits + 1; u < units; u++)
        {
            int dx = (u - leftUnits - 1) * columns;
            bool a = Neighbour(picture, xCurr, yCurr, zCurr, (x0 + dx) * sx, (y0 - 1) * sy);
            available[u] = a;
            if (a)
            {
                found++;
                for (int k = 0; k < columns; k++)
                {
                    Unsafe.Add(ref r, corner + 1 + dx + k) = Unsafe.Add(ref samples, top + x0 + dx + k);
                }
            }
        }

        int total = (4 * n) + 1;
        if (found == 0)
        {
            Array.Fill(_ref, 1 << (bitDepth - 1), 0, total);
        }
        else if (found < units)
        {
            // Substitution (8.4.4.2.2): from the bottom of the left column, up, then along the top; a missing unit repeats the
            // sample before it, and missing units at the start the first sample there is.
            int u = 0;
            while (!available[u])
            {
                u++;
            }

            _ref.AsSpan(0, Segment(u, leftUnits, rows, columns, corner, out _)).Fill(_ref[Segment(u, leftUnits, rows, columns, corner, out _)]);
            for (u++; u < units; u++)
            {
                if (!available[u])
                {
                    int first = Segment(u, leftUnits, rows, columns, corner, out int length);
                    _ref.AsSpan(first, length).Fill(_ref[first - 1]);
                }
            }
        }

        int[] p = _ref;
        bool filter = mode != 1 && n != 4 && !sps.IntraSmoothingDisabled && (c == 0 || sps.ChromaArrayType == 3)
            && Math.Min(Math.Abs(mode - 26), Math.Abs(mode - 10)) > (n == 8 ? 7 : n == 16 ? 1 : 0);
        if (filter)
        {
            int last = 4 * n;
            int threshold = 1 << (bitDepth - 5);
            if (sps.StrongIntraSmoothing && c == 0 && n == 32
                && Math.Abs(p[corner] + p[last] - (2 * p[corner + n])) < threshold
                && Math.Abs(p[corner] + p[0] - (2 * p[corner - n])) < threshold)
            {
                // Strong intra smoothing (8-30 to 8-33): linear interpolation between the corner and the far ends.
                _filtered[corner] = p[corner];
                _filtered[0] = p[0];
                _filtered[last] = p[last];
                for (int i = 0; i < 63; i++)
                {
                    _filtered[corner - 1 - i] = (((63 - i) * p[corner]) + ((i + 1) * p[0]) + 32) >> 6;
                    _filtered[corner + 1 + i] = (((63 - i) * p[corner]) + ((i + 1) * p[last]) + 32) >> 6;
                }
            }
            else
            {
                _filtered[0] = p[0];
                _filtered[last] = p[last];
                int i = 1;
                if (Vector256.IsHardwareAccelerated)
                {
                    ref int s0 = ref MemoryMarshal.GetArrayDataReference(p);
                    ref int f0 = ref MemoryMarshal.GetArrayDataReference(_filtered);
                    Vector256<int> two = Vector256.Create(2);
                    for (; i + 8 <= last; i += 8)
                    {
                        Vector256<int> middle = Vector256.LoadUnsafe(ref s0, (nuint)i);
                        ((Vector256.LoadUnsafe(ref s0, (nuint)(i - 1)) + middle + middle + Vector256.LoadUnsafe(ref s0, (nuint)(i + 1)) + two) >> 2).StoreUnsafe(ref f0, (nuint)i);
                    }
                }

                for (; i < last; i++)
                {
                    _filtered[i] = (p[i - 1] + (2 * p[i]) + p[i + 1] + 2) >> 2;
                }
            }

            p = _filtered;
        }

        int max = (1 << bitDepth) - 1;
        int at = (y0 * stride) + x0;
        if (mode == 0)
        {
            // Planar (8.4.4.2.5).
            int topRight = p[corner + 1 + n], bottomLeft = p[corner - 1 - n];
            if (Vector128.IsHardwareAccelerated)
            {
                Planar(p, corner, n, log2, topRight, bottomLeft, plane, at, stride);
                return;
            }

            for (int y = 0; y < n; y++)
            {
                int left = p[corner - 1 - y];
                for (int x = 0; x < n; x++)
                {
                    int above = p[corner + 1 + x];
                    plane[at + (y * stride) + x] = (ushort)((((n - 1 - x) * left) + ((x + 1) * topRight) + ((n - 1 - y) * above) + ((y + 1) * bottomLeft) + n) >> (log2 + 1));
                }
            }
        }
        else if (mode == 1)
        {
            // DC (8.4.4.2.6), with the edge filter for luma blocks below 32x32.
            int sum = n;
            for (int i = 0; i < n; i++)
            {
                sum += p[corner + 1 + i] + p[corner - 1 - i];
            }

            int dc = sum >> (log2 + 1);
            Fill(plane, at, stride, n, (ushort)dc);

            if (c == 0 && n < 32)
            {
                plane[at] = (ushort)((p[corner - 1] + (2 * dc) + p[corner + 1] + 2) >> 2);
                for (int x = 1; x < n; x++)
                {
                    plane[at + x] = (ushort)((p[corner + 1 + x] + (3 * dc) + 2) >> 2);
                }

                for (int y = 1; y < n; y++)
                {
                    plane[at + (y * stride)] = (ushort)((p[corner - 1 - y] + (3 * dc) + 2) >> 2);
                }
            }
        }
        else
        {
            Angular(p, corner, n, mode, plane, at, stride, c == 0 && n < 32 && !disableBoundaryFilter, max);
        }
    }

    /// <summary>Angular modes 2 to 34 (8.4.4.2.6).</summary>
    private void Angular(int[] p, int corner, int n, int mode, ushort[] plane, int at, int stride, bool edgeFilter, int max)
    {
        int angle = Angles[mode - 2];
        int[] line = _line;

        // ref[k] for k = -n .. 2n is line[k + n].
        int origin = n;
        bool vertical = mode >= 18;

        // Main reference: the top row (vertical modes) or the left column (horizontal modes), starting at the corner.
        for (int x = 0; x <= 2 * n; x++)
        {
            line[origin + x] = vertical ? p[corner + x] : p[corner - x];
        }

        if (angle < 0 && ((n * angle) >> 5) < -1)
        {
            int inverse = InverseAngles[mode - 11];
            for (int x = (n * angle) >> 5; x <= -1; x++)
            {
                int k = (x * inverse + 128) >> 8;
                line[origin + x] = vertical ? p[corner - k] : p[corner + k];
            }
        }

        if (!vertical && Vector128.IsHardwareAccelerated)
        {
            // Horizontal modes predict columns: each into a row of a block, which then goes into the plane transposed.
            for (int j = 0; j < n; j++)
            {
                int position = (j + 1) * angle;
                int k = origin + (position >> 5) + 1, fraction = position & 31;
                if (fraction == 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        _block[(j * n) + i] = (ushort)line[k + i];
                    }
                }
                else
                {
                    Interpolate(line, k, n, fraction, _block, j * n);
                }
            }

            ref ushort b0 = ref MemoryMarshal.GetArrayDataReference(_block);
            ref ushort p0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), at);
            for (int i = 0; i < n; i++)
            {
                ref ushort row = ref Unsafe.Add(ref p0, i * stride);
                for (int j = 0; j < n; j++)
                {
                    Unsafe.Add(ref row, j) = Unsafe.Add(ref b0, (j * n) + i);
                }
            }
        }

        for (int j = 0; j < n && (vertical || !Vector128.IsHardwareAccelerated); j++)
        {
            int position = (j + 1) * angle;
            int index = position >> 5, fraction = position & 31;
            int k = origin + index + 1;
            int target = vertical ? at + (j * stride) : at + j;
            int next = vertical ? 1 : stride;
            if (fraction == 0)
            {
                for (int i = 0; i < n; i++, target += next)
                {
                    plane[target] = (ushort)line[k + i];
                }
            }
            else if (vertical && Vector128.IsHardwareAccelerated)
            {
                Interpolate(line, k, n, fraction, plane, target);
            }
            else
            {
                int weight = 32 - fraction;
                for (int i = 0; i < n; i++, target += next)
                {
                    plane[target] = (ushort)(((weight * line[k + i]) + (fraction * line[k + i + 1]) + 16) >> 5);
                }
            }
        }

        if (edgeFilter && angle == 0)
        {
            if (vertical)
            {
                // Mode 26: the left column follows the left reference samples' gradient.
                for (int y = 0; y < n; y++)
                {
                    plane[at + (y * stride)] = (ushort)Math.Clamp(p[corner + 1] + ((p[corner - 1 - y] - p[corner]) >> 1), 0, max);
                }
            }
            else
            {
                // Mode 10: the top row follows the top reference samples' gradient.
                for (int x = 0; x < n; x++)
                {
                    plane[at + x] = (ushort)Math.Clamp(p[corner - 1] + ((p[corner + 1 + x] - p[corner]) >> 1), 0, max);
                }
            }
        }
    }

    /// <summary>Fills an n by n block of the plane with one value.</summary>
    private static void Fill(ushort[] plane, int at, int stride, int n, ushort value)
    {
        ref ushort p0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), at);
        Vector256<ushort> v = Vector256.Create(value);
        for (int y = 0; y < n; y++)
        {
            ref ushort row = ref Unsafe.Add(ref p0, y * stride);
            switch (n)
            {
                case 4:
                    Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref row), v.AsUInt64().ToScalar());
                    break;
                case 8:
                    v.GetLower().StoreUnsafe(ref row);
                    break;
                case 16:
                    v.StoreUnsafe(ref row);
                    break;
                default:
                    v.StoreUnsafe(ref row);
                    v.StoreUnsafe(ref row, 16);
                    break;
            }
        }
    }

    /// <summary>The start in the reference samples of availability unit <paramref name="u"/>, and its length.</summary>
    private static int Segment(int u, int leftUnits, int rows, int columns, int corner, out int length)
    {
        if (u < leftUnits)
        {
            length = rows;
            return u * rows;
        }

        length = u == leftUnits ? 1 : columns;
        return u == leftUnits ? corner : corner + 1 + ((u - leftUnits - 1) * columns);
    }

    /// <summary>
    /// Gets whether an intra block at luma (xCurr, yCurr), whose MinTbAddrZs is <paramref name="zCurr"/>, may predict from the
    /// samples at (xN, yN): <see cref="HevcPicture.IntraReferenceAvailable"/>, with only the z-scan order to compare inside the
    /// block's own CTB.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Neighbour(HevcPicture picture, int xCurr, int yCurr, int zCurr, int xN, int yN)
    {
        HevcSps sps = picture.Sps;
        if ((uint)xN >= (uint)sps.Width || (uint)yN >= (uint)sps.Height)
        {
            return false;
        }

        int n = ((yN >> 2) * picture.Width4) + (xN >> 2);
        int shift = sps.CtbLog2Size;
        bool available = ((xN ^ xCurr) >> shift) == 0 && ((yN ^ yCurr) >> shift) == 0 ? picture.ZScan[n] <= zCurr : picture.Available(xCurr, yCurr, xN, yN);
        return available && (!picture.Pps.ConstrainedIntraPred || picture.PredFlags[n] == 0);
    }

    /// <summary>Planar prediction 4 or 8 samples at a time: each row is the one above plus bottomLeft - p[x][-1], plus (n - 1 - x) times its left sample.</summary>
    private static void Planar(int[] p, int corner, int n, int log2, int topRight, int bottomLeft, ushort[] plane, int at, int stride)
    {
        ref int above = ref MemoryMarshal.GetArrayDataReference(p);
        ref short out0 = ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), at));
        int shift = log2 + 1;
        int width = Math.Min(n, 8);
        for (int c = 0; c < n; c += width)
        {
            Vector256<int> x = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7) + Vector256.Create(c);
            Vector256<int> left = Vector256.Create(n - 1) - x;
            Vector256<int> top = width == 8 ? Vector256.LoadUnsafe(ref above, (nuint)(corner + 1 + c)) : Vector128.LoadUnsafe(ref above, (nuint)(corner + 1)).ToVector256();
            Vector256<int> row = ((x + Vector256<int>.One) * topRight) + (top * (n - 1)) + Vector256.Create(bottomLeft + n);
            Vector256<int> down = Vector256.Create(bottomLeft) - top;
            for (int y = 0; y < n; y++)
            {
                Vector256<int> v = (row + (left * p[corner - 1 - y])) >> shift;
                Vector128<short> narrow = Vector128.Narrow(v.GetLower(), v.GetUpper());
                ref short d = ref Unsafe.Add(ref out0, (y * stride) + c);
                if (width == 8)
                {
                    narrow.StoreUnsafe(ref d);
                }
                else
                {
                    Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref d), narrow.AsUInt64().ToScalar());
                }

                row += down;
            }
        }
    }

    /// <summary>One row of a vertical angular mode: ((32 - fraction) line[k + i] + fraction line[k + i + 1] + 16) &gt;&gt; 5, 8 or 4 at a time.</summary>
    private static void Interpolate(int[] line, int k, int n, int fraction, ushort[] plane, int target)
    {
        ref int l = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(line), k);
        ref short d = ref Unsafe.As<ushort, short>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), target));
        if (n == 4)
        {
            Vector128<int> v4 = ((Vector128.LoadUnsafe(ref l) * (32 - fraction)) + (Vector128.LoadUnsafe(ref l, 1) * fraction) + Vector128.Create(16)) >> 5;
            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref d), Vector128.Narrow(v4, v4).AsUInt64().ToScalar());
            return;
        }

        Vector256<int> weight = Vector256.Create(32 - fraction), f = Vector256.Create(fraction), round = Vector256.Create(16);
        for (int i = 0; i < n; i += 8)
        {
            Vector256<int> v = ((Vector256.LoadUnsafe(ref l, (nuint)i) * weight) + (Vector256.LoadUnsafe(ref l, (nuint)i + 1) * f) + round) >> 5;
            Vector128.Narrow(v.GetLower(), v.GetUpper()).StoreUnsafe(ref d, (nuint)i);
        }
    }
}
