namespace Egami.Formats.Av1;

/// <summary>
/// Loop restoration, after libaom's restoration.c and convolve.c (BSD 2-clause, see THIRD-PARTY-NOTICES): each unit's Wiener or
/// self-guided filter, applied stripe by stripe (64 luma rows, the first 8 shorter) with the two rows beyond a stripe taken
/// from the deblocked frame rather than the CDEF output, and samples beyond the frame's edges repeated.
/// </summary>
internal static class Av1Restoration
{
    /// <summary>av1_sgr_params: each self-guided parameter set's radii and scales.</summary>
    private static ReadOnlySpan<int> SgrParams =>
    [
        2, 1, 140, 3236, 2, 1, 112, 2158, 2, 1, 93, 1618, 2, 1, 80, 1438, 2, 1, 70, 1295, 2, 1, 58, 1177, 2, 1, 47, 1079, 2, 1, 37, 996,
        2, 1, 30, 925, 2, 1, 25, 863, 0, 1, -1, 2589, 0, 1, -1, 1618, 0, 1, -1, 1177, 0, 1, -1, 925, 2, 0, 56, -1, 2, 0, 22, -1,
    ];

    /// <summary>
    /// Restores the frame's planes: <paramref name="cdef"/> is the CDEF output and <paramref name="deblocked"/> the deblocked
    /// frame, both at the upscaled width with the given strides; the result replaces the frame's planes.
    /// </summary>
    internal static void Apply<T>(Av1Frame frame, T[][] cdef, T[][] deblocked, int[] strides)
        where T : unmanaged
    {
        for (int plane = 0; plane < frame.Header.NumPlanes; plane++)
        {
            Av1RestorationPlane lr = frame.Restoration[plane];
            if (lr.Type == Av1RestorationType.None)
            {
                continue;
            }

            T[] output = (T[])cdef[plane].Clone();
            PlaneFilter<T>.Run(frame, plane, lr, cdef[plane], deblocked[plane], output, strides[plane]);
            frame.Planes![plane] = output;
        }
    }

    private sealed class PlaneFilter<T>
        where T : unmanaged
    {
        private readonly T[] _cdef, _deblocked, _output;
        private readonly int _stride, _ssy, _planeW, _planeH, _bitDepth, _plane;
        private int _stripeStart, _stripeEnd;

        internal PlaneFilter(Av1Frame frame, int plane, T[] cdef, T[] deblocked, T[] output, int stride)
        {
            _cdef = cdef;
            _deblocked = deblocked;
            _output = output;
            _stride = stride;
            _plane = plane;
            int ssx = plane > 0 ? frame.Sequence.SubsamplingX : 0;
            _ssy = plane > 0 ? frame.Sequence.SubsamplingY : 0;
            _planeW = (frame.Header.UpscaledWidth + ssx) >> ssx;
            _planeH = (frame.Header.FrameHeight + _ssy) >> _ssy;
            _bitDepth = frame.Sequence.BitDepth;
        }

        /// <summary>Filters a plane's units, each row of units on its own thread: they read the CDEF and deblocked planes only.</summary>
        internal static void Run(Av1Frame frame, int plane, Av1RestorationPlane lr, T[] cdef, T[] deblocked, T[] output, int stride)
        {
            PlaneFilter<T> shape = new(frame, plane, cdef, deblocked, output, stride);
            int unitSize = lr.UnitSize, extSize = unitSize * 3 / 2;
            int voffset = 8 >> shape._ssy;
            List<(int VStart, int VEnd)> rows = [];
            for (int y0 = 0; y0 < shape._planeH;)
            {
                int h = shape._planeH - y0 < extSize ? shape._planeH - y0 : unitSize;
                int vEnd = y0 + h;
                rows.Add((Math.Max(0, y0 - voffset), vEnd < shape._planeH ? vEnd - voffset : vEnd));
                y0 += h;
            }

            frame.For(rows.Count, row =>
            {
                PlaneFilter<T> filter = new(frame, plane, cdef, deblocked, output, stride);
                int x0 = 0, col = 0;
                while (x0 < filter._planeW)
                {
                    int w = filter._planeW - x0 < extSize ? filter._planeW - x0 : unitSize;
                    ref Av1RestorationUnit unit = ref lr.Units[(row * lr.HorzUnits) + col];
                    if (unit.Type != 0)
                    {
                        filter.FilterUnit(ref unit, x0, x0 + w, rows[row].VStart, rows[row].VEnd);
                    }

                    x0 += w;
                    col++;
                }
            });
        }

        private void FilterUnit(ref Av1RestorationUnit unit, int hStart, int hEnd, int vStart, int vEnd)
        {
            int fullStripe = 64 >> _ssy, offset = 8 >> _ssy;
            int i = 0;
            while (i < vEnd - vStart)
            {
                int v = vStart + i;
                int stripe = (v + offset) / fullStripe;
                int nominal = fullStripe - (stripe == 0 ? offset : 0);
                int h = Math.Min(nominal, vEnd - v);
                _stripeStart = (stripe * fullStripe) - offset;
                _stripeEnd = _stripeStart + fullStripe - 1;
                if (unit.Type == 1)
                {
                    Wiener(ref unit, hStart, hEnd, v, h);
                }
                else
                {
                    SelfGuided(ref unit, hStart, hEnd, v, h);
                }

                i += h;
            }
        }

        /// <summary>The sample a filter sees at a position: the frame's edge repeated, and the deblocked rows beyond the stripe.</summary>
        private int Source(int x, int y)
        {
            x = Math.Clamp(x, 0, _planeW - 1);
            y = Math.Clamp(y, 0, _planeH - 1);
            if (y < _stripeStart)
            {
                return Av1Samples.Get(_deblocked[(Math.Max(_stripeStart - 2, y) * _stride) + x]);
            }

            if (y > _stripeEnd)
            {
                return Av1Samples.Get(_deblocked[(Math.Min(_stripeEnd + 2, y) * _stride) + x]);
            }

            return Av1Samples.Get(_cdef[(y * _stride) + x]);
        }

        /// <summary>av1_highbd_wiener_convolve_add_src: the 7-tap filters, horizontal into a clamped intermediate, then vertical.</summary>
        private void Wiener(ref Av1RestorationUnit unit, int hStart, int hEnd, int v, int h)
        {
            int bd = _bitDepth;
            int round0 = bd == 12 ? 5 : 3, round1 = bd == 12 ? 9 : 11;
            int limit = (1 << (bd + 1 + 7 - round0)) - 1;
            Span<int> hf = [unit.H0, unit.H1, unit.H2, -2 * (unit.H0 + unit.H1 + unit.H2), unit.H2, unit.H1, unit.H0];
            Span<int> vf = [unit.V0, unit.V1, unit.V2, -2 * (unit.V0 + unit.V1 + unit.V2), unit.V2, unit.V1, unit.V0];
            int width = hEnd - hStart;
            int[] temp = new int[width * (h + 6)];
            for (int r = 0; r < h + 6; r++)
            {
                int y = v + r - 3;
                for (int x = 0; x < width; x++)
                {
                    int sx = hStart + x;
                    int sum = (Source(sx, y) << 7) + (1 << (bd + 6));
                    for (int k = 0; k < 7; k++)
                    {
                        sum += hf[k] * Source(sx - 3 + k, y);
                    }

                    temp[(r * width) + x] = Math.Clamp((sum + (1 << (round0 - 1))) >> round0, 0, limit);
                }
            }

            int max = (1 << bd) - 1;
            for (int r = 0; r < h; r++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sum = (temp[((r + 3) * width) + x] << 7) - (1 << (bd + round1 - 1));
                    for (int k = 0; k < 7; k++)
                    {
                        sum += vf[k] * temp[((r + k) * width) + x];
                    }

                    _output[((v + r) * _stride) + hStart + x] = Av1Samples.From<T>(Math.Clamp((sum + (1 << (round1 - 1))) >> round1, 0, max));
                }
            }
        }

        private void SelfGuided(ref Av1RestorationUnit unit, int hStart, int hEnd, int v, int h)
        {
            ReadOnlySpan<int> parameters = SgrParams.Slice(unit.SgrSet * 4, 4);
            int r0 = parameters[0], r1 = parameters[1];
            int width = hEnd - hStart;
            const int border = 3;
            int dgdStride = width + (2 * border);
            int[] dgd = new int[dgdStride * (h + (2 * border))];
            for (int y = -border; y < h + border; y++)
            {
                for (int x = -border; x < width + border; x++)
                {
                    dgd[((y + border) * dgdStride) + x + border] = Source(hStart + x, v + y);
                }
            }

            int[]? flt0 = r0 > 0 ? Box(dgd, dgdStride, width, h, r0, parameters[2], fast: true) : null;
            int[]? flt1 = r1 > 0 ? Box(dgd, dgdStride, width, h, r1, parameters[3], fast: false) : null;

            // av1_decode_xq
            int xq0, xq1;
            if (r0 == 0)
            {
                xq0 = 0;
                xq1 = 128 - unit.Xqd1;
            }
            else if (r1 == 0)
            {
                xq0 = unit.Xqd0;
                xq1 = 0;
            }
            else
            {
                xq0 = unit.Xqd0;
                xq1 = 128 - xq0 - unit.Xqd1;
            }

            int max = (1 << _bitDepth) - 1;
            for (int i = 0; i < h; i++)
            {
                for (int j = 0; j < width; j++)
                {
                    int u = dgd[((i + border) * dgdStride) + j + border] << 4;
                    int value = u << 7;
                    if (flt0 is not null)
                    {
                        value += xq0 * (flt0[(i * width) + j] - u);
                    }

                    if (flt1 is not null)
                    {
                        value += xq1 * (flt1[(i * width) + j] - u);
                    }

                    short w = (short)((value + (1 << 10)) >> 11);
                    _output[((v + i) * _stride) + hStart + j] = Av1Samples.From<T>(Math.Clamp((int)w, 0, max));
                }
            }
        }

        /// <summary>
        /// The self-guided filter of one radius (calculate_intermediate_result and the filters after it): radius 2 computes its
        /// box statistics on every other row and weights rows differently by parity.
        /// </summary>
        private int[] Box(int[] dgd, int dgdStride, int width, int height, int r, int s, bool fast)
        {
            const int border = 3;
            int bufStride = width + 2;
            int[] a = new int[bufStride * (height + 2)];
            int[] b = new int[bufStride * (height + 2)];
            int n = ((2 * r) + 1) * ((2 * r) + 1);
            int oneByX = Av1Tables.OneByX[n - 1];
            int bd = _bitDepth;
            for (int i = -1; i < height + 1; i += fast ? 2 : 1)
            {
                for (int j = -1; j < width + 1; j++)
                {
                    int sum = 0, squares = 0;
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int at = ((i + dy + border) * dgdStride) + j + border;
                        for (int dx = -r; dx <= r; dx++)
                        {
                            int value = dgd[at + dx];
                            sum += value;
                            squares += value * value;
                        }
                    }

                    uint aa = (uint)RoundPower(squares, 2 * (bd - 8));
                    uint bb = (uint)RoundPower(sum, bd - 8);
                    uint p = aa * (uint)n < bb * bb ? 0 : (aa * (uint)n) - (bb * bb);
                    uint z = unchecked((p * (uint)s) + (1u << 19)) >> 20;
                    int aValue = Av1Tables.XByXPlus1[(int)Math.Min(z, 255)];
                    int k = ((i + 1) * bufStride) + j + 1;
                    a[k] = aValue;
                    b[k] = (int)(unchecked(((uint)(256 - aValue) * (uint)sum * (uint)oneByX) + (1u << 11)) >> 12);
                }
            }

            int[] output = new int[width * height];
            for (int i = 0; i < height; i++)
            {
                for (int j = 0; j < width; j++)
                {
                    int k = ((i + 1) * bufStride) + j + 1;
                    int source = dgd[((i + border) * dgdStride) + j + border];
                    int av, bv, shift;
                    if (!fast)
                    {
                        av = ((a[k] + a[k - 1] + a[k + 1] + a[k - bufStride] + a[k + bufStride]) * 4)
                            + ((a[k - 1 - bufStride] + a[k - 1 + bufStride] + a[k + 1 - bufStride] + a[k + 1 + bufStride]) * 3);
                        bv = ((b[k] + b[k - 1] + b[k + 1] + b[k - bufStride] + b[k + bufStride]) * 4)
                            + ((b[k - 1 - bufStride] + b[k - 1 + bufStride] + b[k + 1 - bufStride] + b[k + 1 + bufStride]) * 3);
                        shift = 9;
                    }
                    else if ((i & 1) == 0)
                    {
                        av = ((a[k - bufStride] + a[k + bufStride]) * 6) + ((a[k - 1 - bufStride] + a[k - 1 + bufStride] + a[k + 1 - bufStride] + a[k + 1 + bufStride]) * 5);
                        bv = ((b[k - bufStride] + b[k + bufStride]) * 6) + ((b[k - 1 - bufStride] + b[k - 1 + bufStride] + b[k + 1 - bufStride] + b[k + 1 + bufStride]) * 5);
                        shift = 9;
                    }
                    else
                    {
                        av = (a[k] * 6) + ((a[k - 1] + a[k + 1]) * 5);
                        bv = (b[k] * 6) + ((b[k - 1] + b[k + 1]) * 5);
                        shift = 8;
                    }

                    output[(i * width) + j] = RoundPower((av * source) + bv, shift);
                }
            }

            return output;
        }

        private static int RoundPower(int value, int bits) => bits == 0 ? value : (value + (1 << (bits - 1))) >> bits;
    }
}
