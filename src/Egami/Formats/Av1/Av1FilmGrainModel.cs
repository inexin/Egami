namespace Egami.Formats.Av1;

/// <summary>
/// Takes film grain out of a picture and models it as AV1 film grain parameters (AV1 specification 5.9.30 and 7.18.3, the film
/// grain synthesis the decoder runs): the noise level is estimated from luma, each plane denoised by a Wiener filter in the
/// frequency domain of overlapping windowed blocks, and the difference in the flattest blocks fitted with a lag-3 autoregressive
/// model and a strength for each intensity, the scaling function. The design follows libaom's still-image denoiser (noise_model.c,
/// BSD 2-clause; ideas only, written anew): the noise level estimated as Immerkær's, a half-cosine window, the same Wiener gain
/// and power spectrum, and strengths in 8-bit units divided by the autoregressive filter's gain. Deterministic: the same picture
/// always gives the same planes and parameters.
/// </summary>
internal static class Av1FilmGrainModel
{
    private const int BlockSize = 32;
    private const int Lag = 3;
    private const int GrainSeed = 7391;

    /// <summary>
    /// Denoises <paramref name="input"/> and models its grain, or gives null when it has too little noise or too few flat blocks
    /// to measure it; <paramref name="level"/> (1 to 50) turns the denoiser on, as libaom's denoise-noise-level does for stills,
    /// whose strength comes from the noise measured in the picture.
    /// </summary>
    internal static (Av1EncoderInput Denoised, Av1FilmGrain Grain)? Run(Av1EncoderInput input, int level)
    {
        if (level <= 0 || input.Width < 2 * BlockSize || input.Height < 2 * BlockSize)
        {
            return null;
        }

        int depth = input.BitDepth;
        double max = (1 << depth) - 1;

        // libaom's still-image rule: no grain on a clean picture; otherwise the measured level, a little higher, at most 5.
        double sigma = NoiseLevel(input.Planes[0], input.Width, input.Height, depth) - 0.1;
        if (sigma <= 0)
        {
            return null;
        }

        float noiseLevel = (float)Math.Min(5, sigma + 0.5);
        bool mono = input.Chroma == Pixels.ChromaFormat.Yuv400;
        int ssx = input.Chroma is Pixels.ChromaFormat.Yuv420 or Pixels.ChromaFormat.Yuv422 ? 1 : 0, ssy = input.Chroma == Pixels.ChromaFormat.Yuv420 ? 1 : 0;
        int planes = mono ? 1 : 3;
        ushort[][] denoised = new ushort[planes][];
        for (int p = 0; p < planes; p++)
        {
            int sx = p == 0 ? 0 : ssx, sy = p == 0 ? 0 : ssy;
            int w = (input.Width + sx) >> sx, h = (input.Height + sy) >> sy, block = BlockSize >> sx;
            float psd = noiseLevel * noiseLevel / 10000 * block * block / 8;
            denoised[p] = Denoise(input.Planes[p], w, h, block, psd, max);
        }

        bool[] flat = FlatBlocks(denoised[0], input.Width, input.Height, max);
        if (flat.Count(f => f) < 2)
        {
            return null;
        }

        int blocksX = input.Width / BlockSize;
        double toEight = 255.0 / max;
        double[][] noise = new double[planes][];
        for (int p = 0; p < planes; p++)
        {
            noise[p] = new double[input.Planes[p].Length];
            for (int i = 0; i < noise[p].Length; i++)
            {
                noise[p][i] = (input.Planes[p][i] - denoised[p][i]) * toEight;
            }
        }

        // The luma model first: chroma's fit removes what is correlated with luma, whose strength it needs.
        Model luma = Fit(noise[0], null, input.Width, input.Height, 0, 0, flat, blocksX, denoised[0], input.Width, toEight, null);
        Model? cb = null, cr = null;
        if (!mono)
        {
            int cw = (input.Width + ssx) >> ssx, ch = (input.Height + ssy) >> ssy;
            double[] lumaNoise = ChromaLuma(noise[0], input.Width, input.Height, cw, ch, ssx, ssy);
            cb = Fit(noise[1], lumaNoise, cw, ch, ssx, ssy, flat, blocksX, denoised[0], input.Width, toEight, luma);
            cr = Fit(noise[2], lumaNoise, cw, ch, ssx, ssy, flat, blocksX, denoised[0], input.Width, toEight, luma);
        }

        Av1FilmGrain? grain = Parameters(luma, cb, cr);
        if (grain is null)
        {
            return null;
        }

        Av1EncoderInput result = new()
        {
            Width = input.Width,
            Height = input.Height,
            BitDepth = depth,
            Chroma = input.Chroma,
            Planes = denoised,
            ColorPrimaries = input.ColorPrimaries,
            TransferCharacteristics = input.TransferCharacteristics,
            MatrixCoefficients = input.MatrixCoefficients,
            FullRange = input.FullRange,
            ChromaSamplePosition = input.ChromaSamplePosition,
        };
        return (result, grain);
    }

    /// <summary>
    /// Estimates the standard deviation of white noise in a plane, in 8-bit units, as Immerkær's method does: the mean absolute
    /// response of a 3 x 3 Laplacian difference over the pixels away from edges (a Sobel gradient below 16 in 8-bit units).
    /// </summary>
    internal static double NoiseLevel(ushort[] plane, int width, int height, int depth)
    {
        double scale = 255.0 / ((1 << depth) - 1);
        double sum = 0;
        long count = 0;
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                int at = (y * width) + x;
                double Px(int dx, int dy) => plane[at + (dy * width) + dx] * scale;
                double gx = (Px(1, -1) + (2 * Px(1, 0)) + Px(1, 1)) - (Px(-1, -1) + (2 * Px(-1, 0)) + Px(-1, 1));
                double gy = (Px(-1, 1) + (2 * Px(0, 1)) + Px(1, 1)) - (Px(-1, -1) + (2 * Px(0, -1)) + Px(1, -1));
                if (Math.Abs(gx) + Math.Abs(gy) >= 16)
                {
                    continue;
                }

                double v = Px(-1, -1) + Px(1, -1) + Px(-1, 1) + Px(1, 1) - (2 * (Px(0, -1) + Px(-1, 0) + Px(1, 0) + Px(0, 1))) + (4 * Px(0, 0));
                sum += Math.Abs(v);
                count++;
            }
        }

        return count < 16 ? 0 : Math.Sqrt(Math.PI / 2) * sum / (6.0 * count);
    }

    /// <summary>
    /// Denoises a plane: blocks of <paramref name="block"/> samples at half overlap, each less its least-squares plane, windowed by
    /// a half cosine, filtered in the frequency domain by the Wiener gain for the noise power <paramref name="psd"/>, then put back
    /// with its plane and windowed again (the squared windows of the overlapping blocks sum to one), and rounded with error diffusion.
    /// </summary>
    private static ushort[] Denoise(ushort[] plane, int width, int height, int block, float psd, double max)
    {
        int blocksX = ((width + block - 1) / block) + 2, blocksY = ((height + block - 1) / block) + 2;
        int stride = blocksX * block;
        float[] result = new float[stride * blocksY * block];
        float[] window = new float[block * block];
        for (int y = 0; y < block; y++)
        {
            double wy = Math.Cos(((0.5 + y) * Math.PI / block) - (Math.PI / 2));
            for (int x = 0; x < block; x++)
            {
                window[(y * block) + x] = (float)(wy * Math.Cos(((0.5 + x) * Math.PI / block) - (Math.PI / 2)));
            }
        }

        float[] re = new float[block * block], im = new float[block * block], fit = new float[block * block];
        float scale = (float)(1 / max);
        for (int oy = 0; oy < block; oy += block / 2)
        {
            for (int ox = 0; ox < block; ox += block / 2)
            {
                for (int by = -1; by < blocksY - 2; by++)
                {
                    for (int bx = -1; bx < blocksX - 2; bx++)
                    {
                        int x0 = (bx * block) + ox, y0 = (by * block) + oy;
                        for (int y = 0; y < block; y++)
                        {
                            int sy = Math.Clamp(y0 + y, 0, height - 1);
                            for (int x = 0; x < block; x++)
                            {
                                re[(y * block) + x] = plane[(sy * width) + Math.Clamp(x0 + x, 0, width - 1)] * scale;
                            }
                        }

                        FitPlane(re, fit, block);
                        for (int i = 0; i < re.Length; i++)
                        {
                            re[i] = (re[i] - fit[i]) * window[i];
                            im[i] = 0;
                        }

                        Fft2(re, im, block, inverse: false);
                        for (int i = 0; i < re.Length; i++)
                        {
                            float power = (Math.Max(Math.Abs(re[i]), 1e-8f) * Math.Max(Math.Abs(re[i]), 1e-8f)) + (Math.Max(Math.Abs(im[i]), 1e-8f) * Math.Max(Math.Abs(im[i]), 1e-8f));
                            float gain = power > 1.1f * psd && power > 1e-6f ? (power - psd) / Math.Max(power, 1e-6f) : 0.1f / 1.1f;
                            re[i] *= gain;
                            im[i] *= gain;
                        }

                        Fft2(re, im, block, inverse: true);
                        for (int y = 0; y < block; y++)
                        {
                            int ry = y + ((by + 1) * block) + oy;
                            for (int x = 0; x < block; x++)
                            {
                                int i = (y * block) + x;
                                result[(ry * stride) + x + ((bx + 1) * block) + ox] += (re[i] + (fit[i] * window[i])) * window[i];
                            }
                        }
                    }
                }
            }
        }

        // Back to integers, the rounding error diffused onward (Floyd and Steinberg) so flat areas keep their mean.
        ushort[] output = new ushort[width * height];
        float top = (float)max;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y + block) * stride) + x + block;
                float value = result[at] * top;
                int q = (int)Math.Clamp(value + 0.5f, 0, top);
                float error = (value - q) / top;
                output[(y * width) + x] = (ushort)q;
                if (x + 1 < width)
                {
                    result[at + 1] += error * 7 / 16;
                }

                if (y + 1 < height)
                {
                    if (x > 0)
                    {
                        result[at + stride - 1] += error * 3 / 16;
                    }

                    result[at + stride] += error * 5 / 16;
                    if (x + 1 < width)
                    {
                        result[at + stride + 1] += error / 16;
                    }
                }
            }
        }

        return output;
    }

    /// <summary>Fits the plane a + b x + c y to a block by least squares (x and y centered, so the terms are independent).</summary>
    private static void FitPlane(float[] block, float[] fit, int size)
    {
        double mean = 0, sx = 0, sy = 0, center = (size - 1) / 2.0, sumSquares = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double v = block[(y * size) + x];
                mean += v;
                sx += v * (x - center);
                sy += v * (y - center);
            }
        }

        for (int i = 0; i < size; i++)
        {
            sumSquares += (i - center) * (i - center);
        }

        mean /= size * size;
        double b = sx / (sumSquares * size), c = sy / (sumSquares * size);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                fit[(y * size) + x] = (float)(mean + (b * (x - center)) + (c * (y - center)));
            }
        }
    }

    /// <summary>A two-dimensional complex FFT of a power-of-two square block, rows then columns; the inverse divides by the size.</summary>
    private static void Fft2(float[] re, float[] im, int size, bool inverse)
    {
        float[] rowRe = new float[size], rowIm = new float[size];
        for (int pass = 0; pass < 2; pass++)
        {
            for (int k = 0; k < size; k++)
            {
                for (int i = 0; i < size; i++)
                {
                    int at = pass == 0 ? (k * size) + i : (i * size) + k;
                    (rowRe[i], rowIm[i]) = (re[at], im[at]);
                }

                Fft(rowRe, rowIm, inverse);
                for (int i = 0; i < size; i++)
                {
                    int at = pass == 0 ? (k * size) + i : (i * size) + k;
                    (re[at], im[at]) = inverse ? (rowRe[i] / size, rowIm[i] / size) : (rowRe[i], rowIm[i]);
                }
            }
        }
    }

    /// <summary>An in-place radix-2 complex FFT.</summary>
    private static void Fft(float[] re, float[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }

            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int length = 2; length <= n; length <<= 1)
        {
            double angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            for (int start = 0; start < n; start += length)
            {
                for (int k = 0; k < length / 2; k++)
                {
                    float wr = (float)Math.Cos(angle * k), wi = (float)Math.Sin(angle * k);
                    int a = start + k, b = a + (length / 2);
                    float tr = (re[b] * wr) - (im[b] * wi), ti = (re[b] * wi) + (im[b] * wr);
                    (re[b], im[b]) = (re[a] - tr, im[a] - ti);
                    (re[a], im[a]) = (re[a] + tr, im[a] + ti);
                }
            }
        }
    }

    /// <summary>
    /// Marks the luma blocks flat enough to measure noise in: those whose denoised gradient energy is lowest, below a threshold of
    /// about two 8-bit levels a pixel, or the lowest tenth when too few are.
    /// </summary>
    private static bool[] FlatBlocks(ushort[] denoised, int width, int height, double max)
    {
        int blocksX = width / BlockSize, blocksY = height / BlockSize;
        double[] scores = new double[blocksX * blocksY];
        double scale = 1 / max;
        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                double energy = 0;
                for (int y = 1; y < BlockSize - 1; y++)
                {
                    int row = ((by * BlockSize) + y) * width;
                    for (int x = 1; x < BlockSize - 1; x++)
                    {
                        int at = row + (bx * BlockSize) + x;
                        double gx = (denoised[at + 1] - denoised[at - 1]) * scale / 2, gy = (denoised[at + width] - denoised[at - width]) * scale / 2;
                        energy += (gx * gx) + (gy * gy);
                    }
                }

                scores[(by * blocksX) + bx] = energy / ((BlockSize - 2) * (BlockSize - 2));
            }
        }

        double threshold = Math.Pow(2 / 255.0, 2);
        double[] sorted = [.. scores.Order()];
        double tenth = sorted[Math.Max(0, (sorted.Length / 10) - 1)];
        bool[] flat = new bool[scores.Length];
        int count = 0;
        for (int i = 0; i < scores.Length; i++)
        {
            flat[i] = scores[i] < threshold;
            count += flat[i] ? 1 : 0;
        }

        if (count < scores.Length / 10)
        {
            for (int i = 0; i < scores.Length; i++)
            {
                flat[i] = scores[i] <= tenth;
            }
        }

        return flat;
    }

    /// <summary>The mean luma noise under each chroma sample, which chroma's model correlates with.</summary>
    private static double[] ChromaLuma(double[] luma, int width, int height, int cw, int ch, int ssx, int ssy)
    {
        double[] result = new double[cw * ch];
        for (int y = 0; y < ch; y++)
        {
            for (int x = 0; x < cw; x++)
            {
                double sum = 0;
                int n = 0;
                for (int dy = 0; dy <= ssy; dy++)
                {
                    for (int dx = 0; dx <= ssx; dx++)
                    {
                        int ly = Math.Min((y << ssy) + dy, height - 1), lx = Math.Min((x << ssx) + dx, width - 1);
                        sum += luma[(ly * width) + lx];
                        n++;
                    }
                }

                result[(y * cw) + x] = sum / n;
            }
        }

        return result;
    }

    /// <summary>
    /// Fits a plane's noise in the flat blocks: the lag-3 autoregressive coefficients (with the co-located luma noise last, for
    /// chroma) by least squares, the filter's gain, and the strength (8-bit units, the gain divided out) for each of 16 luma bins.
    /// </summary>
    private static Model Fit(double[] noise, double[]? luma, int width, int height, int ssx, int ssy, bool[] flat, int blocksX,
        ushort[] lumaDenoised, int lumaWidth, double toEight, Model? lumaModel)
    {
        int count = 2 * Lag * (Lag + 1) + (luma is null ? 0 : 1);
        double[] a = new double[count * count], b = new double[count];
        double[] row = new double[count];
        long observations = 0;
        int block = BlockSize >> ssx, blockH = BlockSize >> ssy;
        for (int i = 0; i < flat.Length; i++)
        {
            if (!flat[i])
            {
                continue;
            }

            int x0 = (i % blocksX) * block, y0 = (i / blocksX) * blockH;
            for (int y = y0 + Lag; y < Math.Min(y0 + blockH, height); y++)
            {
                for (int x = x0 + Lag; x < Math.Min(x0 + block - Lag, width); x++)
                {
                    int k = 0;
                    for (int dy = -Lag; dy <= 0; dy++)
                    {
                        for (int dx = -Lag; dx <= Lag && !(dy == 0 && dx == 0); dx++)
                        {
                            row[k++] = noise[((y + dy) * width) + x + dx];
                        }
                    }

                    if (luma is not null)
                    {
                        row[k] = luma[(y * width) + x];
                    }

                    double target = noise[(y * width) + x];
                    for (int r = 0; r < count; r++)
                    {
                        b[r] += row[r] * target;
                        for (int c = 0; c < count; c++)
                        {
                            a[(r * count) + c] += row[r] * row[c];
                        }
                    }

                    observations++;
                }
            }
        }

        double[] coefficients = Solve(a, b, count);

        // The filter's gain: the noise's variance over the variance of what the filter cannot predict.
        int lagged = count - (luma is null ? 0 : 1);
        double variance = 0, explained = 0;
        for (int i = 0; i < lagged; i++)
        {
            variance += a[(i * count) + i] / Math.Max(1, observations);
            double bi = b[i] - (luma is null ? 0 : a[(i * count) + count - 1] * coefficients[count - 1]);
            explained += bi * coefficients[i] / Math.Max(1, observations);
        }

        variance /= Math.Max(1, lagged);
        double gain = Math.Max(1, Math.Sqrt(Math.Max(variance / Math.Max(variance - explained, 1e-6), 1e-6)));

        // Strength by the block's mean luma: the noise's deviation, less what is correlated with luma, over the filter's gain.
        double[] sum = new double[16];
        int[] samples = new int[16];
        for (int i = 0; i < flat.Length; i++)
        {
            if (!flat[i])
            {
                continue;
            }

            int x0 = (i % blocksX) * block, y0 = (i / blocksX) * blockH;
            double mean = 0, squares = 0;
            int n = 0;
            for (int y = y0; y < Math.Min(y0 + blockH, height); y++)
            {
                for (int x = x0; x < Math.Min(x0 + block, width); x++)
                {
                    double v = noise[(y * width) + x];
                    squares += v * v;
                    n++;
                }
            }

            int lx0 = (i % blocksX) * BlockSize, ly0 = (i / blocksX) * BlockSize;
            for (int y = ly0; y < ly0 + BlockSize; y++)
            {
                for (int x = lx0; x < lx0 + BlockSize; x++)
                {
                    mean += lumaDenoised[(y * lumaWidth) + x] * toEight;
                }
            }

            mean /= BlockSize * BlockSize;
            double noiseVariance = squares / Math.Max(1, n);
            int bin = Math.Clamp((int)(mean / 16), 0, 15);
            double strength = noiseVariance;
            if (lumaModel is not null && luma is not null)
            {
                double lumaStrength = lumaModel.Gain * lumaModel.Strength(mean);
                strength = Math.Max(noiseVariance / 16, noiseVariance - Math.Pow(coefficients[count - 1] * lumaStrength, 2));
            }

            sum[bin] += Math.Sqrt(strength) / gain;
            samples[bin]++;
        }

        double[] bins = new double[16];
        for (int i = 0; i < 16; i++)
        {
            bins[i] = samples[i] > 0 ? sum[i] / samples[i] : -1;
        }

        return new Model(coefficients, gain, bins);
    }

    /// <summary>Solves the normal equations by Gaussian elimination with partial pivoting, a small ridge keeping them well posed.</summary>
    private static double[] Solve(double[] a, double[] b, int n)
    {
        double[] m = (double[])a.Clone(), x = (double[])b.Clone();
        double trace = 0;
        for (int i = 0; i < n; i++)
        {
            trace += m[(i * n) + i];
        }

        for (int i = 0; i < n; i++)
        {
            m[(i * n) + i] += (1e-6 * trace / n) + 1e-12;
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(m[(r * n) + col]) > Math.Abs(m[(pivot * n) + col]))
                {
                    pivot = r;
                }
            }

            if (Math.Abs(m[(pivot * n) + col]) < 1e-12)
            {
                return new double[n];
            }

            if (pivot != col)
            {
                for (int c = 0; c < n; c++)
                {
                    (m[(col * n) + c], m[(pivot * n) + c]) = (m[(pivot * n) + c], m[(col * n) + c]);
                }

                (x[col], x[pivot]) = (x[pivot], x[col]);
            }

            for (int r = col + 1; r < n; r++)
            {
                double f = m[(r * n) + col] / m[(col * n) + col];
                for (int c = col; c < n; c++)
                {
                    m[(r * n) + c] -= f * m[(col * n) + c];
                }

                x[r] -= f * x[col];
            }
        }

        for (int r = n - 1; r >= 0; r--)
        {
            double v = x[r];
            for (int c = r + 1; c < n; c++)
            {
                v -= m[(r * n) + c] * x[c];
            }

            x[r] = v / m[(r * n) + r];
        }

        return x;
    }

    /// <summary>
    /// Turns the models into film grain parameters: up to 14 (luma) and 10 (chroma) scaling points at the measured bins, the
    /// scaling shift that fits the largest, the coefficients at the shift that fits them, chroma scaled by luma intensity.
    /// </summary>
    private static Av1FilmGrain? Parameters(Model luma, Model? cb, Model? cr)
    {
        (int Value, double Scaling)[] PointsOf(Model model, int max)
        {
            List<(int, double)> points = [];
            for (int i = 0; i < 16; i++)
            {
                if (model.Bins[i] >= 0)
                {
                    points.Add((Math.Min(255, (i * 16) + 8), Math.Min(255, model.Bins[i])));
                }
            }

            while (points.Count > max)
            {
                // Drop the point a line through its neighbors predicts best.
                int best = 1;
                double least = double.MaxValue;
                for (int i = 1; i < points.Count - 1; i++)
                {
                    double t = (points[i].Item1 - points[i - 1].Item1) / (double)(points[i + 1].Item1 - points[i - 1].Item1);
                    double error = Math.Abs(points[i].Item2 - (points[i - 1].Item2 + (t * (points[i + 1].Item2 - points[i - 1].Item2))));
                    if (error < least)
                    {
                        (least, best) = (error, i);
                    }
                }

                points.RemoveAt(best);
            }

            return [.. points];
        }

        (int, double)[] y = PointsOf(luma, 14), u = cb is null ? [] : PointsOf(cb, 10), v = cr is null ? [] : PointsOf(cr, 10);
        double maxScaling = 1e-4;
        foreach ((int, double Scaling) p in y.Concat(u).Concat(v))
        {
            maxScaling = Math.Max(maxScaling, p.Scaling);
        }

        // Grain under half an 8-bit level, such as rounding noise, is not worth its parameters or the denoised picture.
        if (y.Length == 0 || maxScaling < 0.5)
        {
            return null;
        }

        int log2 = Math.Clamp((int)Math.Floor(Math.Log2(maxScaling) + 1), 2, 5);
        double factor = 1 << (8 - log2);
        (int, int)[] Scaled((int Value, double Scaling)[] points) =>
            [.. points.Select(p => (p.Value, Math.Clamp((int)((factor * p.Scaling) + 0.5), 0, 255)))];

        double maxCoefficient = 1e-4, minCoefficient = -1e-4;
        foreach (Model m in new[] { luma, cb, cr }.OfType<Model>())
        {
            foreach (double c in m.Coefficients)
            {
                maxCoefficient = Math.Max(maxCoefficient, c);
                minCoefficient = Math.Min(minCoefficient, c);
            }
        }

        int shift = Math.Clamp(7 - (int)Math.Max(1 + Math.Floor(Math.Log2(maxCoefficient)), Math.Ceiling(Math.Log2(-minCoefficient))), 6, 9);
        int[] Coefficients(Model? m, int n) => m is null ? [] : [.. m.Coefficients.Take(n).Select(c => Math.Clamp((int)Math.Round(c * (1 << shift)), -128, 127) + 128)];
        int lumaCount = 2 * Lag * (Lag + 1);
        return new Av1FilmGrain
        {
            ApplyGrain = true,
            UpdateGrain = true,
            GrainSeed = GrainSeed,
            PointsY = Scaled(y),
            PointsCb = Scaled(u),
            PointsCr = Scaled(v),
            GrainScalingMinus8 = 5 + (8 - log2) - 8,
            ArCoeffLag = Lag,
            ArCoeffsYPlus128 = Coefficients(luma, lumaCount),
            ArCoeffsCbPlus128 = u.Length > 0 ? Coefficients(cb, lumaCount + 1) : [],
            ArCoeffsCrPlus128 = v.Length > 0 ? Coefficients(cr, lumaCount + 1) : [],
            ArCoeffShiftMinus6 = shift - 6,
            CbMult = 128,
            CbLumaMult = 192,
            CbOffset = 256,
            CrMult = 128,
            CrLumaMult = 192,
            CrOffset = 256,
            OverlapFlag = true,
        };
    }

    /// <summary>A plane's noise model: its coefficients, the filter's gain, and the strength of each 16-level luma bin (-1 unmeasured).</summary>
    private sealed record Model(double[] Coefficients, double Gain, double[] Bins)
    {
        /// <summary>Gets the strength at a luma value, from the nearest measured bins.</summary>
        internal double Strength(double luma)
        {
            int bin = Math.Clamp((int)(luma / 16), 0, 15);
            for (int d = 0; d < 16; d++)
            {
                if (bin - d >= 0 && Bins[bin - d] >= 0)
                {
                    return Bins[bin - d];
                }

                if (bin + d < 16 && Bins[bin + d] >= 0)
                {
                    return Bins[bin + d];
                }
            }

            return 0;
        }
    }
}
