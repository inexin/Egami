using System.Buffers;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Demosaicing: fills in the two colours each pixel of a colour filter array did not sample. Bilinear and Malvar-He-Cutler
/// (gradient-corrected linear interpolation, Malvar, He and Cutler, ICASSP 2004) work on Bayer patterns; AHD is Hirakawa and
/// Parks' adaptive homogeneity-directed method (IEEE TIP 2005); any other pattern gets a same-colour neighbourhood average.
/// Rows are processed in bands of their own, each reading a reflected border around it, so the result is the same for any
/// thread count.
/// </summary>
internal static class RawDemosaic
{
    private const int Pad = 6;
    private const int Band = 16;

    /// <summary>
    /// Demosaics <paramref name="mosaic"/> (<paramref name="width"/> × <paramref name="height"/>, white-balanced) into
    /// interleaved RGB. <paramref name="toXyz"/> is the camera-to-XYZ matrix AHD measures colour differences with.
    /// </summary>
    public static void Run(ReadOnlyMemory<float> mosaic, int width, int height, RawCfa cfa, RawDemosaicMethod method, double[] toXyz, Memory<float> rgb, ParallelOptions parallel)
    {
        int red = cfa.BayerRed;
        if (red < 0)
        {
            method = (RawDemosaicMethod)(-1);
        }

        int bands = (height + Band - 1) / Band;
        float[] xyz = [.. toXyz.Select(v => (float)v)];
        Parallel.For(0, bands, parallel, () => new Scratch(width), (band, _, scratch) =>
        {
            int y0 = band * Band;
            int y1 = Math.Min(height, y0 + Band);
            scratch.Fill(mosaic.Span, width, height, y0, y1);
            Span<float> output = rgb.Span.Slice(y0 * width * 3, (y1 - y0) * width * 3);
            switch (method)
            {
                case RawDemosaicMethod.Bilinear:
                    Bilinear(scratch, width, y0, y1, red, output);
                    break;
                case RawDemosaicMethod.Malvar:
                    Malvar(scratch, width, y0, y1, red, output);
                    break;
                case RawDemosaicMethod.Ahd:
                    Ahd(scratch, width, y0, y1, red, xyz, output);
                    break;
                default:
                    Generic(scratch, width, y0, y1, cfa, output);
                    break;
            }

            return scratch;
        }, scratch => scratch.Dispose());
    }

    /// <summary>Averages each 2 × 2 Bayer block into one pixel, for a half-size develop: no demosaicing at all.</summary>
    public static void Half(ReadOnlySpan<float> mosaic, int width, int height, RawCfa cfa, Span<float> rgb)
    {
        int w = width / 2, h = height / 2;
        int red = cfa.BayerRed;
        if (red >= 0)
        {
            // Bayer: red and blue once in each block, green twice.
            int r0 = red, b0 = red ^ 3, g0 = red ^ 1, g1 = red ^ 2;
            for (int y = 0; y < h; y++)
            {
                ReadOnlySpan<float> top = mosaic.Slice(y * 2 * width, width);
                ReadOnlySpan<float> bottom = mosaic.Slice(((y * 2) + 1) * width, width);
                Span<float> output = rgb.Slice(y * w * 3, w * 3);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 2;
                    float p0 = top[i], p1 = top[i + 1], p2 = bottom[i], p3 = bottom[i + 1];
                    output[x * 3] = Pick(r0, p0, p1, p2, p3);
                    output[(x * 3) + 1] = (Pick(g0, p0, p1, p2, p3) + Pick(g1, p0, p1, p2, p3)) * 0.5f;
                    output[(x * 3) + 2] = Pick(b0, p0, p1, p2, p3);
                }
            }

            return;
        }

        Span<float> sum = stackalloc float[3];
        Span<int> count = stackalloc int[3];
        int stepY = cfa.Rows == 1 ? 1 : 2;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                sum.Clear();
                count.Clear();
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = (x * 2) + dx, sy = (y * stepY) + dy;
                        sy = Math.Min(sy, height - 1);
                        int c = cfa.ColorAt(sx, sy);
                        if (c < 3)
                        {
                            sum[c] += mosaic[(sy * width) + sx];
                            count[c]++;
                        }
                    }
                }

                int o = ((y * w) + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    rgb[o + c] = count[c] > 0 ? sum[c] / count[c] : 0;
                }
            }
        }
    }

    private static float Pick(int position, float p0, float p1, float p2, float p3) => position switch { 0 => p0, 1 => p1, 2 => p2, _ => p3 };

    private static void Bilinear(Scratch s, int width, int y0, int y1, int red, Span<float> output)
    {
        int stride = s.Stride;
        float[] m = s.Mosaic;
        for (int y = y0; y < y1; y++)
        {
            int row = (y - y0 + Pad) * stride;
            int o = (y - y0) * width * 3;
            for (int x = 0; x < width; x++)
            {
                int i = row + x + Pad;
                int phase = (((y & 1) << 1) | (x & 1)) ^ red;
                float c = m[i];
                float cross = (m[i - 1] + m[i + 1] + m[i - stride] + m[i + stride]) * 0.25f;
                float diagonal = (m[i - stride - 1] + m[i - stride + 1] + m[i + stride - 1] + m[i + stride + 1]) * 0.25f;
                float horizontal = (m[i - 1] + m[i + 1]) * 0.5f;
                float vertical = (m[i - stride] + m[i + stride]) * 0.5f;
                float r, g, b;
                switch (phase)
                {
                    case 0:
                        (r, g, b) = (c, cross, diagonal);
                        break;
                    case 3:
                        (r, g, b) = (diagonal, cross, c);
                        break;
                    case 1:
                        (r, g, b) = (horizontal, c, vertical);
                        break;
                    default:
                        (r, g, b) = (vertical, c, horizontal);
                        break;
                }

                output[o++] = r;
                output[o++] = g;
                output[o++] = b;
            }
        }
    }

    private static void Malvar(Scratch s, int width, int y0, int y1, int red, Span<float> output)
    {
        int st = s.Stride;
        float[] m = s.Mosaic;
        for (int y = y0; y < y1; y++)
        {
            int row = (y - y0 + Pad) * st;
            int o = (y - y0) * width * 3;
            for (int x = 0; x < width; x++)
            {
                int i = row + x + Pad;
                int phase = (((y & 1) << 1) | (x & 1)) ^ red;
                float c = m[i];
                float n = m[i - st], so = m[i + st], w = m[i - 1], e = m[i + 1];
                float nn = m[i - (2 * st)], ss = m[i + (2 * st)], ww = m[i - 2], ee = m[i + 2];
                float diag = m[i - st - 1] + m[i - st + 1] + m[i + st - 1] + m[i + st + 1];
                float r, g, b;
                if (phase is 0 or 3)
                {
                    // Green at red or blue, and the other colour from the diagonals.
                    float green = ((4 * c) + (2 * (n + so + w + e)) - (nn + ss + ww + ee)) * 0.125f;
                    float other = ((6 * c) + (2 * diag) - (1.5f * (nn + ss + ww + ee))) * 0.125f;
                    (r, g, b) = phase == 0 ? (c, green, other) : (other, green, c);
                }
                else
                {
                    // At green: the colour of this row from left and right, the other from above and below.
                    float rowColour = ((5 * c) + (4 * (w + e)) - (ww + ee) - diag + (0.5f * (nn + ss))) * 0.125f;
                    float columnColour = ((5 * c) + (4 * (n + so)) - (nn + ss) - diag + (0.5f * (ww + ee))) * 0.125f;
                    (r, g, b) = phase == 1 ? (rowColour, c, columnColour) : (columnColour, c, rowColour);
                }

                output[o++] = r;
                output[o++] = g;
                output[o++] = b;
            }
        }
    }

    /// <summary>
    /// AHD: green interpolated along rows and along columns, red and blue from colour differences for each, both candidates
    /// measured in CIELab, and each pixel takes the direction whose neighbourhood is more homogeneous.
    /// </summary>
    private static void Ahd(Scratch s, int width, int y0, int y1, int red, float[] xyz, Span<float> output)
    {
        int st = s.Stride;
        int rows = y1 - y0 + (2 * Pad);
        float[] m = s.Mosaic;
        float[] gh = s.GreenH, gv = s.GreenV;

        // Directional green, with the Laplacian of the sampled colour as correction, kept between its two neighbours.
        for (int r = 2; r < rows - 2; r++)
        {
            int y = y0 - Pad + r;
            for (int col = 2; col < st - 2; col++)
            {
                int i = (r * st) + col;
                int x = col - Pad;
                int phase = (((y & 1) << 1) | (x & 1)) ^ red;
                if (phase is 1 or 2)
                {
                    gh[i] = gv[i] = m[i];
                    continue;
                }

                float c = m[i];
                float h = ((m[i - 1] + m[i + 1]) * 0.5f) + (((2 * c) - m[i - 2] - m[i + 2]) * 0.25f);
                float v = ((m[i - st] + m[i + st]) * 0.5f) + (((2 * c) - m[i - (2 * st)] - m[i + (2 * st)]) * 0.25f);
                gh[i] = Math.Clamp(h, Math.Min(m[i - 1], m[i + 1]), Math.Max(m[i - 1], m[i + 1]));
                gv[i] = Math.Clamp(v, Math.Min(m[i - st], m[i + st]), Math.Max(m[i - st], m[i + st]));
            }
        }

        Candidates(s, gh, s.RgbH, width, y0, y1, red);
        Candidates(s, gv, s.RgbV, width, y0, y1, red);
        Lab(s.RgbH, s.LabH, st, rows, xyz);
        Lab(s.RgbV, s.LabV, st, rows, xyz);

        // Homogeneity: neighbours within the adaptive luminance and chroma tolerances, per direction.
        byte[] hh = s.HomH, hv = s.HomV;
        float[] lh = s.LabH, lv = s.LabV;
        for (int r = 4; r < rows - 4; r++)
        {
            for (int col = 4; col < st - 4; col++)
            {
                int i = (r * st) + col;
                int k = i * 3;
                float epsL = MathF.Min(
                    MathF.Max(MathF.Abs(lh[k] - lh[k - 3]), MathF.Abs(lh[k] - lh[k + 3])),
                    MathF.Max(MathF.Abs(lv[k] - lv[k - (3 * st)]), MathF.Abs(lv[k] - lv[k + (3 * st)])));
                float epsC = MathF.Min(
                    MathF.Max(Chroma(lh, k, k - 3), Chroma(lh, k, k + 3)),
                    MathF.Max(Chroma(lv, k, k - (3 * st)), Chroma(lv, k, k + (3 * st))));
                hh[i] = (byte)(Near(lh, k, k - 3, epsL, epsC) + Near(lh, k, k + 3, epsL, epsC) + Near(lh, k, k - (3 * st), epsL, epsC) + Near(lh, k, k + (3 * st), epsL, epsC));
                hv[i] = (byte)(Near(lv, k, k - 3, epsL, epsC) + Near(lv, k, k + 3, epsL, epsC) + Near(lv, k, k - (3 * st), epsL, epsC) + Near(lv, k, k + (3 * st), epsL, epsC));
            }
        }

        float[] ch = s.RgbH, cv = s.RgbV;
        for (int y = y0; y < y1; y++)
        {
            int r = y - y0 + Pad;
            int o = (y - y0) * width * 3;
            for (int x = 0; x < width; x++)
            {
                int i = (r * st) + x + Pad;
                int sh = 0, sv = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int j = i + (dy * st);
                    sh += hh[j - 1] + hh[j] + hh[j + 1];
                    sv += hv[j - 1] + hv[j] + hv[j + 1];
                }

                int k = i * 3;
                if (sh > sv)
                {
                    output[o] = ch[k];
                    output[o + 1] = ch[k + 1];
                    output[o + 2] = ch[k + 2];
                }
                else if (sv > sh)
                {
                    output[o] = cv[k];
                    output[o + 1] = cv[k + 1];
                    output[o + 2] = cv[k + 2];
                }
                else
                {
                    output[o] = (ch[k] + cv[k]) * 0.5f;
                    output[o + 1] = (ch[k + 1] + cv[k + 1]) * 0.5f;
                    output[o + 2] = (ch[k + 2] + cv[k + 2]) * 0.5f;
                }

                o += 3;
            }
        }
    }

    private static float Chroma(float[] lab, int a, int b)
    {
        float da = lab[a + 1] - lab[b + 1], db = lab[a + 2] - lab[b + 2];
        return (da * da) + (db * db);
    }

    private static int Near(float[] lab, int a, int b, float epsL, float epsC)
    {
        return MathF.Abs(lab[a] - lab[b]) <= epsL && Chroma(lab, a, b) <= epsC ? 1 : 0;
    }

    /// <summary>Fills in red and blue around one green candidate from colour differences (R − G, B − G) of the neighbours.</summary>
    private static void Candidates(Scratch s, float[] g, float[] rgb, int width, int y0, int y1, int red)
    {
        int st = s.Stride;
        int rows = y1 - y0 + (2 * Pad);
        float[] m = s.Mosaic;
        for (int r = 3; r < rows - 3; r++)
        {
            int y = y0 - Pad + r;
            for (int col = 3; col < st - 3; col++)
            {
                int i = (r * st) + col;
                int x = col - Pad;
                int phase = (((y & 1) << 1) | (x & 1)) ^ red;
                float green = g[i];
                float rr, bb;
                switch (phase)
                {
                    case 0:
                        rr = m[i];
                        bb = green + (((m[i - st - 1] - g[i - st - 1]) + (m[i - st + 1] - g[i - st + 1]) + (m[i + st - 1] - g[i + st - 1]) + (m[i + st + 1] - g[i + st + 1])) * 0.25f);
                        break;
                    case 3:
                        bb = m[i];
                        rr = green + (((m[i - st - 1] - g[i - st - 1]) + (m[i - st + 1] - g[i - st + 1]) + (m[i + st - 1] - g[i + st - 1]) + (m[i + st + 1] - g[i + st + 1])) * 0.25f);
                        break;
                    case 1:
                        rr = green + (((m[i - 1] - g[i - 1]) + (m[i + 1] - g[i + 1])) * 0.5f);
                        bb = green + (((m[i - st] - g[i - st]) + (m[i + st] - g[i + st])) * 0.5f);
                        break;
                    default:
                        bb = green + (((m[i - 1] - g[i - 1]) + (m[i + 1] - g[i + 1])) * 0.5f);
                        rr = green + (((m[i - st] - g[i - st]) + (m[i + st] - g[i + st])) * 0.5f);
                        break;
                }

                rgb[i * 3] = rr;
                rgb[(i * 3) + 1] = green;
                rgb[(i * 3) + 2] = bb;
            }
        }
    }

    /// <summary>Converts camera RGB to CIELab (D50) for AHD's comparisons.</summary>
    private static void Lab(float[] rgb, float[] lab, int stride, int rows, float[] m)
    {
        for (int r = 3; r < rows - 3; r++)
        {
            for (int col = 3; col < stride - 3; col++)
            {
                int k = ((r * stride) + col) * 3;
                float cr = rgb[k], cg = rgb[k + 1], cb = rgb[k + 2];
                float x = ((m[0] * cr) + (m[1] * cg) + (m[2] * cb)) * (1 / 0.9642f);
                float y = (m[3] * cr) + (m[4] * cg) + (m[5] * cb);
                float z = ((m[6] * cr) + (m[7] * cg) + (m[8] * cb)) * (1 / 0.8251f);
                float fx = F(x), fy = F(y), fz = F(z);
                lab[k] = (116 * fy) - 16;
                lab[k + 1] = 500 * (fx - fy);
                lab[k + 2] = 200 * (fy - fz);
            }
        }
    }

    private static float F(float t)
    {
        // CIELab's f(t), through a table over 0 to 2 with linear interpolation; AHD compares Lab differences, so the table's
        // error (below 1e-5) does not change a single decision that matters.
        float x = t * (CubeSize / 2f);
        if (x >= 0 && x < CubeSize)
        {
            int k = (int)x;
            return Cube[k] + ((Cube[k + 1] - Cube[k]) * (x - k));
        }

        return t > 0.008856f ? MathF.Cbrt(t) : (7.787f * t) + (16f / 116);
    }

    private const int CubeSize = 1 << 14;

    private static readonly float[] Cube = MakeCube();

    private static float[] MakeCube()
    {
        float[] table = new float[CubeSize + 1];
        for (int i = 0; i <= CubeSize; i++)
        {
            float t = i * (2f / CubeSize);
            table[i] = t > 0.008856f ? MathF.Cbrt(t) : (7.787f * t) + (16f / 116);
        }

        return table;
    }

    /// <summary>Any colour filter array: each missing colour is the average of that colour's samples in the 3 × 3, else 5 × 5, neighbourhood.</summary>
    private static void Generic(Scratch s, int width, int y0, int y1, RawCfa cfa, Span<float> output)
    {
        int st = s.Stride;
        float[] m = s.Mosaic;
        Span<float> sum = stackalloc float[3];
        Span<int> count = stackalloc int[3];
        for (int y = y0; y < y1; y++)
        {
            int o = (y - y0) * width * 3;
            for (int x = 0; x < width; x++)
            {
                int own = cfa.ColorAt(x, y);
                for (int radius = 1; radius <= 2; radius++)
                {
                    sum.Clear();
                    count.Clear();
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        // The padded band holds the reflected pixel's value; its colour is the CFA's at the reflected place.
                        int yy = Reflect(y + dy, s.Height);
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int c = cfa.ColorAt(Reflect(x + dx, width), yy);
                            if (c < 3)
                            {
                                sum[c] += m[((y + dy - y0 + Pad) * st) + x + dx + Pad];
                                count[c]++;
                            }
                        }
                    }

                    if (count[0] > 0 && count[1] > 0 && count[2] > 0)
                    {
                        break;
                    }
                }

                for (int c = 0; c < 3; c++)
                {
                    output[o + c] = c == own ? m[((y - y0 + Pad) * st) + x + Pad] : count[c] > 0 ? sum[c] / count[c] : 0;
                }

                o += 3;
            }
        }
    }

    private static int Reflect(int v, int length)
    {
        if (length == 1)
        {
            return 0;
        }

        while (v < 0 || v >= length)
        {
            v = v < 0 ? -v : (2 * (length - 1)) - v;
        }

        return v;
    }

    /// <summary>One band's working memory, padded by <see cref="Pad"/> on every side, reused by a thread across bands.</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly float[][] _floats;
        private readonly byte[][] _bytes;

        public Scratch(int width)
        {
            Stride = width + (2 * Pad);
            int size = Stride * (Band + (2 * Pad));
            _floats = [Rent(size), Rent(size), Rent(size), Rent(size * 3), Rent(size * 3), Rent(size * 3), Rent(size * 3)];
            _bytes = [ArrayPool<byte>.Shared.Rent(size), ArrayPool<byte>.Shared.Rent(size)];
            Array.Clear(_bytes[0]);
            Array.Clear(_bytes[1]);
        }

        public int Stride { get; }

        public int Height { get; private set; }

        public float[] Mosaic => _floats[0];

        public float[] GreenH => _floats[1];

        public float[] GreenV => _floats[2];

        public float[] RgbH => _floats[3];

        public float[] RgbV => _floats[4];

        public float[] LabH => _floats[5];

        public float[] LabV => _floats[6];

        public byte[] HomH => _bytes[0];

        public byte[] HomV => _bytes[1];

        /// <summary>Copies rows <paramref name="y0"/> − Pad to <paramref name="y1"/> + Pad, reflecting at the edges so the CFA phase holds.</summary>
        public void Fill(ReadOnlySpan<float> mosaic, int width, int height, int y0, int y1)
        {
            Height = height;
            int rows = y1 - y0 + (2 * Pad);
            for (int r = 0; r < rows; r++)
            {
                int y = ReflectEven(y0 - Pad + r, height);
                ReadOnlySpan<float> source = mosaic.Slice(y * width, width);
                Span<float> target = Mosaic.AsSpan(r * Stride, Stride);
                source.CopyTo(target[Pad..]);
                for (int p = 1; p <= Pad; p++)
                {
                    target[Pad - p] = source[ReflectEven(-p, width)];
                    target[Pad + width - 1 + p] = source[ReflectEven(width - 1 + p, width)];
                }
            }
        }

        public void Dispose()
        {
            foreach (float[] a in _floats)
            {
                ArrayPool<float>.Shared.Return(a);
            }

            foreach (byte[] a in _bytes)
            {
                ArrayPool<byte>.Shared.Return(a);
            }
        }

        private static float[] Rent(int size)
        {
            float[] a = ArrayPool<float>.Shared.Rent(size);
            Array.Clear(a, 0, size);
            return a;
        }

        /// <summary>Reflects about the edge pixel; a 2 × 2 pattern keeps its phase, since the distance to the mirror image is even.</summary>
        private static int ReflectEven(int v, int length)
        {
            if (length <= 2)
            {
                return Math.Clamp(v, 0, length - 1);
            }

            while (v < 0 || v >= length)
            {
                v = v < 0 ? -v : (2 * (length - 1)) - v;
            }

            return v;
        }
    }
}
