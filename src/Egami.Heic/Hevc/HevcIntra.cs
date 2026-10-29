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
    private readonly bool[] _available = new bool[(4 * 64) + 1];
    private readonly int[] _line = new int[(3 * 64) + 1];

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

        // Availability (6.4.1) is the same for all samples of a 4x4 luma unit: checked once per unit.
        int xCurr = x0 * sx, yCurr = y0 * sy;
        int count = 0;
        int unit = -1;
        bool available = false;
        for (int i = 0; i < 2 * n; i++)
        {
            // Left column, from y0 + 2n - 1 up to y0.
            int y = y0 + (2 * n) - 1 - i;
            if ((y * sy) >> 2 != unit)
            {
                unit = (y * sy) >> 2;
                available = picture.IntraReferenceAvailable(xCurr, yCurr, (x0 - 1) * sx, y * sy);
            }

            _available[i] = available;
            if (available)
            {
                _ref[i] = plane[(y * stride) + x0 - 1];
                count++;
            }
        }

        _available[corner] = picture.IntraReferenceAvailable(xCurr, yCurr, (x0 - 1) * sx, (y0 - 1) * sy);
        if (_available[corner])
        {
            _ref[corner] = plane[((y0 - 1) * stride) + x0 - 1];
            count++;
        }

        unit = -1;
        int top = (y0 - 1) * stride;
        for (int i = 0; i < 2 * n; i++)
        {
            int x = x0 + i;
            if ((x * sx) >> 2 != unit)
            {
                unit = (x * sx) >> 2;
                available = picture.IntraReferenceAvailable(xCurr, yCurr, x * sx, (y0 - 1) * sy);
            }

            _available[corner + 1 + i] = available;
            if (available)
            {
                _ref[corner + 1 + i] = plane[top + x];
                count++;
            }
        }

        int total = (4 * n) + 1;
        if (count == 0)
        {
            Array.Fill(_ref, 1 << (bitDepth - 1), 0, total);
        }
        else if (count < total)
        {
            // Substitution (8.4.4.2.2): from the bottom of the left column, up, then along the top.
            if (!_available[0])
            {
                int first = 1;
                while (!_available[first])
                {
                    first++;
                }

                _ref[0] = _ref[first];
            }

            for (int i = 1; i < total; i++)
            {
                if (!_available[i])
                {
                    _ref[i] = _ref[i - 1];
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
                for (int i = 1; i < last; i++)
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
            for (int y = 0; y < n; y++)
            {
                plane.AsSpan(at + (y * stride), n).Fill((ushort)dc);
            }

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

        for (int j = 0; j < n; j++)
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
}
