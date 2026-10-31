namespace Egami.Formats.Jxl;

/// <summary>
/// A patch the encoder found: its samples relative to the background around it, quantized to compare patches (libjxl's
/// QuantizedPatch), and where in the frame it occurs.
/// </summary>
internal sealed class JxlPatchCandidate(int width, int height)
{
    internal int Width { get; } = width;

    internal int Height { get; } = height;

    /// <summary>Gets the quantized samples of each channel, which decide whether two patches are the same.</summary>
    internal sbyte[][] Quantized { get; } = [new sbyte[width * height], new sbyte[width * height], new sbyte[width * height]];

    /// <summary>Gets the samples themselves, which go into the reference frame.</summary>
    internal float[][] Values { get; } = [new float[width * height], new float[width * height], new float[width * height]];

    internal List<(int X, int Y)> Positions { get; } = [];

    /// <summary>libjxl's QuantizedPatch ordering: by width, then height, then the quantized bytes of each channel as unsigned bytes.</summary>
    internal int ComparePatch(JxlPatchCandidate other)
    {
        if (Width != other.Width)
        {
            return Width.CompareTo(other.Width);
        }

        if (Height != other.Height)
        {
            return Height.CompareTo(other.Height);
        }

        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < Width * Height; i++)
            {
                int a = (byte)Quantized[c][i], b = (byte)other.Quantized[c][i];
                if (a != b)
                {
                    return a.CompareTo(b);
                }
            }
        }

        return 0;
    }
}

/// <summary>
/// The patch dictionary search of libjxl 0.12.0's lossy encoder at effort 7 (enc_patch_dictionary.cc, enc_detect_dots.cc and
/// enc_dot_dictionary.cc): text-like shapes on flat backgrounds that repeat, else, from distance 3, small Gaussian dots; then
/// the bin packing of the patches into one reference frame.
/// </summary>
internal static class JxlPatchSearch
{
    /// <summary>
    /// Writes a patch dictionary (PatchDictionaryEncoder::Encode): per patch its reference frame, place and size, how many
    /// times it occurs, and each occurrence's position (the first, then offsets) and blend mode for color and each extra channel.
    /// </summary>
    internal static void WriteDictionary(JxlBitWriter w, JxlPatch[] patches)
    {
        List<JxlToken> tokens = [new JxlToken(0, (uint)patches.Length)];
        foreach (JxlPatch p in patches)
        {
            tokens.Add(new JxlToken(1, (uint)p.Reference));
            tokens.Add(new JxlToken(3, (uint)p.X0));
            tokens.Add(new JxlToken(3, (uint)p.Y0));
            tokens.Add(new JxlToken(2, (uint)(p.Width - 1)));
            tokens.Add(new JxlToken(2, (uint)(p.Height - 1)));
            tokens.Add(new JxlToken(7, (uint)(p.Targets.Length - 1)));
            for (int j = 0; j < p.Targets.Length; j++)
            {
                if (j == 0)
                {
                    tokens.Add(new JxlToken(4, (uint)p.Targets[j].X));
                    tokens.Add(new JxlToken(4, (uint)p.Targets[j].Y));
                }
                else
                {
                    tokens.Add(new JxlToken(6, JxlTreeLearner.PackSigned(p.Targets[j].X - p.Targets[j - 1].X)));
                    tokens.Add(new JxlToken(6, JxlTreeLearner.PackSigned(p.Targets[j].Y - p.Targets[j - 1].Y)));
                }

                foreach (JxlPatchBlending b in p.Targets[j].Blending)
                {
                    tokens.Add(new JxlToken(5, (uint)b.Mode));
                }
            }
        }

        List<List<JxlToken>> streams = [tokens];
        JxlEntropyCode code = JxlEntropyCode.Write(w, new JxlHistogramParams { Rle = true }, 10, streams);
        code.WriteTokens(w, streams[0]);
    }

    internal const int MaxPatchSize = 32;
    private const int PatchSide = 4;
    // PatchColorspaceInfo: the quantization steps and weights of X, Y and B, or of R, G and B on a 0 to 1 scale (lossless).
    private static readonly float[] XybDequant = [0.01615f, 0.08875f, 0.1922f];
    private static readonly float[] XybWeights = [30.0f, 3.0f, 1.0f];
    private static readonly float[] RgbDequant = [20.0f / 255, 22.0f / 255, 20.0f / 255];
    private static readonly float[] RgbWeights = [0.017f * 255, 0.02f * 255, 0.017f * 255];

    private static bool IsSimilar(float[][] p, int a, int b, float threshold, float[] weights)
    {
        float distance = 0;
        for (int c = 0; c < 3; c++)
        {
            distance += MathF.Abs(p[c][a] - p[c][b]) * weights[c];
        }

        return distance <= threshold;
    }

    private static bool IsSimilar(float r0, float r1, float r2, float[][] p, int b, float threshold, float[] weights)
    {
        float distance = (MathF.Abs(r0 - p[0][b]) * weights[0]) + (MathF.Abs(r1 - p[1][b]) * weights[1]);
        distance += MathF.Abs(r2 - p[2][b]) * weights[2];
        return distance <= threshold;
    }

    private static int Quantize(float v, int c, float[] dequant) => (int)MathF.Truncate(Math.Clamp(v / dequant[c], -32768.0f, 32767.0f));

    /// <summary>
    /// libjxl's FindTextLikePatches on XYB planes of <paramref name="stride"/> samples a row: 4×4 squares of one exact colour among
    /// at least seven like them seed a flood of background (pixels close in colour within 50 of a seed), and each connected group
    /// of other pixels up to 32 on a side, ringed by background of one colour, is a patch; patches that occur twice or more stay,
    /// unless all are under 20 pixels. Fewer seeds than <c>minSeedFraction</c> of the blocks end the search early (photos have few).
    /// </summary>
    internal static List<JxlPatchCandidate> FindTextLike(float[][] xyb, int stride, int width, int height, ParallelOptions parallel, bool isXyb = true, float minSeedFraction = 0)
    {
        float[] weights = isXyb ? XybWeights : RgbWeights;
        float[] dequant = isXyb ? XybDequant : RgbDequant;
        List<JxlPatchCandidate> info = [];
        int pw = width / PatchSide, ph = height / PatchSide;
        int sw = (width + PatchSide - 1) / PatchSide;
        byte[] screenshotLike = new byte[sw * ((height + PatchSide - 1) / PatchSide)];

        bool SameColor(int at, float b0, float b1, float b2) =>
            MathF.Abs(b0 - xyb[0][at]) <= 1e-4f && MathF.Abs(b1 - xyb[1][at]) <= 1e-4f && MathF.Abs(b2 - xyb[2][at]) <= 1e-4f;

        bool canHaveSeeds = pw >= 3 && ph >= 3;
        int seeds = 0;
        if (canHaveSeeds)
        {
            // libjxl runs the rows 1 to ph − 3.
            Parallel.For(1, ph - 2, parallel, py =>
            {
                int found = 0;
                for (int px = 1; px <= pw - 2; px++)
                {
                    int o = (py * PatchSide * stride) + (px * PatchSide);
                    float b0 = xyb[0][o], b1 = xyb[1][o], b2 = xyb[2][o];
                    bool flat = true;
                    for (int iy = 0; iy < PatchSide && flat; iy++)
                    {
                        for (int ix = 0; ix < PatchSide; ix++)
                        {
                            if (!SameColor(o + (iy * stride) + ix, b0, b1, b2))
                            {
                                flat = false;
                                break;
                            }
                        }
                    }

                    if (!flat)
                    {
                        continue;
                    }

                    int same = 0;
                    for (int y = (py - 1) * PatchSide; y <= (py + 1) * PatchSide; y += PatchSide)
                    {
                        for (int x = (px - 1) * PatchSide; x <= (px + 1) * PatchSide; x += PatchSide)
                        {
                            same += SameColor((y * stride) + x, b0, b1, b2) ? 1 : 0;
                        }
                    }

                    if (same < 8)
                    {
                        continue;
                    }

                    screenshotLike[(py * sw) + px] = 1;
                    found++;
                }

                Interlocked.Add(ref seeds, found);
            });
        }

        if (seeds == 0 || seeds < minSeedFraction * Math.Max(1, (pw - 2) * (ph - 3)))
        {
            return info;
        }

        // Pixels "similar enough" to a seed's colour, and near it, are background; each takes its seed's colour.
        byte[] isBackground = new byte[width * height];
        float[][] background = [new float[width * height], new float[width * height], new float[width * height]];
        List<(int Cur, int Src)> queue = new(2 * seeds * PatchSide * PatchSide);
        for (int py = 1; py < ph - 1; py++)
        {
            for (int px = 1; px < pw - 1; px++)
            {
                if (screenshotLike[(py * sw) + px] == 0)
                {
                    continue;
                }

                for (int y = py * PatchSide; y < (py + 1) * PatchSide; y++)
                {
                    for (int x = px * PatchSide; x < (px + 1) * PatchSide; x++)
                    {
                        queue.Add(((y * width) + x, (y * width) + x));
                        isBackground[(y * width) + x] = 1;
                    }
                }
            }
        }

        for (int front = 0; front < queue.Count; front++)
        {
            (int cur, int src) = queue[front];
            int cx = cur % width, cy = cur / width;
            int sx = src % width, sy = src / width;
            int srcAt = (sy * stride) + sx;
            for (int c = 0; c < 3; c++)
            {
                background[c][cur] = xyb[c][srcAt];
            }

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height || isBackground[(ny * width) + nx] != 0)
                    {
                        continue;
                    }

                    if (Math.Abs(nx - sx) + Math.Abs(ny - sy) > 50)
                    {
                        continue;
                    }

                    if (IsSimilar(xyb, srcAt, (ny * stride) + nx, 0.8f, weights))
                    {
                        queue.Add(((ny * width) + nx, src));
                        isBackground[(ny * width) + nx] = 1;
                    }
                }
            }
        }

        queue.Clear();

        // Small connected groups of the other pixels, ringed by one background colour, become patches.
        byte[] visited = new byte[width * height];
        List<(int X, int Y)> stack = [];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (isBackground[(y * width) + x] != 0)
                {
                    continue;
                }

                stack.Clear();
                stack.Add((x, y));
                int minX = x, maxX = x, minY = y, maxY = y;
                int reference = -1;
                bool allSimilar = true;
                while (stack.Count > 0)
                {
                    (int qx, int qy) = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if (visited[(qy * width) + qx] != 0)
                    {
                        continue;
                    }

                    visited[(qy * width) + qx] = 1;
                    minX = Math.Min(minX, qx);
                    maxX = Math.Max(maxX, qx);
                    minY = Math.Min(minY, qy);
                    maxY = Math.Max(maxY, qy);
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            int nx = qx + dx, ny = qy + dy;
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                            {
                                continue;
                            }

                            int next = (ny * width) + nx;
                            if (isBackground[next] == 0)
                            {
                                stack.Add((nx, ny));
                            }
                            else if (reference < 0)
                            {
                                reference = next;
                            }
                            else if (!IsSimilar(background, next, reference, 0.03f, weights))
                            {
                                allSimilar = false;
                            }
                        }
                    }
                }

                if (reference < 0 || !allSimilar || maxX - minX >= MaxPatchSize || maxY - minY >= MaxPatchSize)
                {
                    continue;
                }

                float r0 = background[0][reference], r1 = background[1][reference], r2 = background[2][reference];
                bool hasSimilar = false;
                for (int iy = Math.Max(minY - 2, 0); iy < Math.Min(maxY + 3, height); iy++)
                {
                    for (int ix = Math.Max(minX - 2, 0); ix < Math.Min(maxX + 3, width); ix++)
                    {
                        if (IsSimilar(r0, r1, r2, xyb, (iy * stride) + ix, 0.03f, weights))
                        {
                            hasSimilar = true;
                        }
                    }
                }

                if (!hasSimilar)
                {
                    continue;
                }

                JxlPatchCandidate patch = new(maxX - minX + 1, maxY - minY + 1);
                patch.Positions.Add((minX, minY));
                float[] refColor = [r0, r1, r2];
                bool tooBig = false, tooSmall = true;
                foreach (int c in (ReadOnlySpan<int>)[1, 0, 2])
                {
                    for (int iy = minY; iy <= maxY; iy++)
                    {
                        for (int ix = minX; ix <= maxX; ix++)
                        {
                            int offset = ((iy - minY) * patch.Width) + ix - minX;
                            float v = xyb[c][(iy * stride) + ix] - refColor[c];
                            patch.Values[c][offset] = v;
                            int q = Quantize(v, c, dequant);
                            sbyte b = unchecked((sbyte)q);
                            patch.Quantized[c][offset] = b;
                            tooBig |= q != b;
                            tooSmall &= q is < 2 and > -2;
                        }
                    }
                }

                if (!tooSmall && !tooBig)
                {
                    info.Add(patch);
                }
            }
        }

        if (info.Count == 0)
        {
            return info;
        }

        // Equal patches merge; those seen fewer than twice go.
        info.Sort((a, b) =>
        {
            int c = a.ComparePatch(b);
            return c != 0 ? c : a.Positions[0].CompareTo(b.Positions[0]);
        });
        int unique = 0;
        for (int i = 1; i < info.Count; i++)
        {
            if (info[i].ComparePatch(info[unique]) == 0)
            {
                info[unique].Positions.AddRange(info[i].Positions);
            }
            else
            {
                if (info[unique].Positions.Count >= 2)
                {
                    unique++;
                }

                info[unique] = info[i];
            }
        }

        if (info[unique].Positions.Count >= 2)
        {
            unique++;
        }

        info.RemoveRange(unique, info.Count - unique);
        if (info.Count == 0 || info.Max(p => p.Width * p.Height) < 20)
        {
            info.Clear();
        }

        return info;
    }

    /// <summary>
    /// libjxl's FindDotDictionary and DetectGaussianEllipses on the padded XYB planes (<paramref name="paddedWidth"/> by
    /// <paramref name="paddedHeight"/>): pixels whose luma stands out from a heavily blurred copy form small groups, and a
    /// Gaussian ellipse fitted to each, if it fits well enough, makes the group a patch of the difference.
    /// </summary>
    internal static List<JxlPatchCandidate> FindDots(float[][] xyb, int paddedWidth, int paddedHeight, int width, int height, ParallelOptions parallel)
    {
        int n = paddedWidth * paddedHeight;
        float[][] smooth = [new float[n], new float[n], new float[n]];
        float[][] filtered = [new float[n], new float[n], new float[n]];
        ReadOnlySpan<float> w1 = [0.558311f, 0.210395f, 0.010449f];
        ReadOnlySpan<float> w3 = [0.222338f, 0.210431f, 0.1784f];
        for (int c = 0; c < 3; c++)
        {
            Separable5(xyb[c], filtered[c], paddedWidth, paddedHeight, w3, parallel);
            Separable5(filtered[c], smooth[c], paddedWidth, paddedHeight, w3, parallel);
            Separable5(xyb[c], filtered[c], paddedWidth, paddedHeight, w1, parallel);
        }

        float[] energy = new float[n];
        for (int i = 0; i < n; i++)
        {
            // SumOfSquareDifferences: only luma counts (weights 0, 10 and 0).
            float d1 = filtered[1][i] - smooth[1][i];
            energy[i] = d1 * d1 * 10.0f;
        }

        List<Component> components = FindComponents(energy, paddedWidth, width, height);
        if (components.Count > 100)
        {
            components = [.. components.OrderByDescending(c => c.Score).Take(100)];
        }

        List<JxlPatchCandidate> dots = [];
        foreach (Component cc in components)
        {
            Ellipse e = FitGaussian(cc, xyb, smooth, paddedWidth, width, height);
            if (e.X < 0.0 || Math.Ceiling(e.X) >= width || e.Y < 0.0 || Math.Ceiling(e.Y) >= height || e.NegPixels > 0)
            {
                continue;
            }

            double intensity = (0.21 * e.Intensity[0]) + (0.72 * e.Intensity[1]) + (0.07 * e.Intensity[2]);
            double sqDistMeanMode = ((e.X - cc.ModeX) * (e.X - cc.ModeX)) + ((e.Y - cc.ModeY) * (e.Y - cc.ModeY));
            if (e.L2Loss < 0.005 && e.CustomLoss < 300 && intensity * intensity > 0.12 * 0.12 && sqDistMeanMode < 1.0)
            {
                JxlPatchCandidate patch = new(cc.W, cc.H);
                patch.Positions.Add((cc.X0, cc.Y0));
                for (int y = 0; y < cc.H; y++)
                {
                    for (int x = 0; x < cc.W; x++)
                    {
                        int at = ((cc.Y0 + y) * paddedWidth) + cc.X0 + x;
                        for (int c = 0; c < 3; c++)
                        {
                            patch.Values[c][(y * cc.W) + x] = xyb[c][at] - smooth[c][at];
                        }
                    }
                }

                dots.Add(patch);
            }
        }

        return dots;
    }

    /// <summary>libjxl's Separable5 with mirrored borders: the 5-tap kernel across, then down.</summary>
    private static void Separable5(float[] src, float[] dst, int width, int height, ReadOnlySpan<float> w, ParallelOptions parallel)
    {
        float w0 = w[0], w1 = w[1], w2 = w[2];
        float[] tmp = new float[width * height];
        Parallel.For(0, height, parallel, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                tmp[row + x] = (w0 * src[row + x]) + (w1 * (src[row + Mirror(x - 1, width)] + src[row + Mirror(x + 1, width)]))
                    + (w2 * (src[row + Mirror(x - 2, width)] + src[row + Mirror(x + 2, width)]));
            }
        });
        Parallel.For(0, height, parallel, y =>
        {
            int m1 = Mirror(y - 1, height) * width, p1 = Mirror(y + 1, height) * width;
            int m2 = Mirror(y - 2, height) * width, p2 = Mirror(y + 2, height) * width;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                dst[row + x] = (w0 * tmp[row + x]) + (w1 * (tmp[m1 + x] + tmp[p1 + x])) + (w2 * (tmp[m2 + x] + tmp[p2 + x]));
            }
        });
    }

    private static int Mirror(int x, int size)
    {
        while ((uint)x >= (uint)size)
        {
            x = x < 0 ? -x - 1 : (2 * size) - 1 - x;
        }

        return x;
    }

    private sealed class Component
    {
        internal int X0;
        internal int Y0;
        internal int W;
        internal int H;
        internal float Score;
        internal int ModeX;
        internal int ModeY;
    }

    private sealed class Ellipse
    {
        internal double X;
        internal double Y;
        internal double SigmaX;
        internal double SigmaY;
        internal double Angle;
        internal double[] Intensity = new double[3];
        internal double[] Background = new double[3];
        internal double L2Loss;
        internal double CustomLoss;
        internal int NegPixels;
    }

    /// <summary>
    /// libjxl's FindCC: from each pixel of energy above 0.04 the group of pixels above 0.02 around it (up to 1000), kept when it
    /// fits in 4×4 and stands out from its surroundings by a score of 12.
    /// </summary>
    private static List<Component> FindComponents(float[] energy, int stride, int width, int height)
    {
        float[] img = (float[])energy.Clone();
        List<Component> found = [];
        List<(int X, int Y)> pixels = [];
        List<(int X, int Y)> q = [];
        // libjxl's neighbour list, which names (1, 0) twice and leaves out (−1, 0).
        ReadOnlySpan<(int, int)> neighbors = [(1, -1), (1, 0), (1, 1), (0, -1), (0, 1), (-1, -1), (-1, 1), (1, 0)];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!(img[(y * stride) + x] > 0.04))
                {
                    continue;
                }

                pixels.Clear();
                img[(y * stride) + x] = 0;
                q.Clear();
                q.Add((x, y));
                bool ok = true;
                while (q.Count > 0)
                {
                    (int cx, int cy) = q[^1];
                    q.RemoveAt(q.Count - 1);
                    pixels.Add((cx, cy));
                    if (pixels.Count > 1000)
                    {
                        ok = false;
                        break;
                    }

                    foreach ((int dx, int dy) in neighbors)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx >= 0 && nx < width && ny >= 0 && ny < height && img[(ny * stride) + nx] > 0.02)
                        {
                            img[(ny * stride) + nx] = 0;
                            q.Add((nx, ny));
                        }
                    }
                }

                if (!ok)
                {
                    continue;
                }

                int lx = pixels.Min(p => p.X), hx = pixels.Max(p => p.X), ly = pixels.Min(p => p.Y), hy = pixels.Max(p => p.Y);
                Component cc = new() { X0 = lx, Y0 = ly, W = hx - lx + 1, H = hy - ly + 1 };
                if (cc.W >= 5 || cc.H >= 5)
                {
                    continue;
                }

                // CompStats over the box grown by 4.
                float maxEnergy = 0, meanEnergy = 0, meanBg = 0, varBg = 0;
                int nIn = 0, nOut = 0;
                for (int sy = -4; sy < cc.H + 4; sy++)
                {
                    int yy = sy + cc.Y0;
                    if (yy < 0 || yy >= height)
                    {
                        continue;
                    }

                    for (int sx = -4; sx < cc.W + 4; sx++)
                    {
                        int xx = sx + cc.X0;
                        if (xx < 0 || xx >= width)
                        {
                            continue;
                        }

                        float e = energy[(yy * stride) + xx];
                        if (e > maxEnergy)
                        {
                            maxEnergy = e;
                            cc.ModeX = xx;
                            cc.ModeY = yy;
                        }

                        if (xx >= cc.X0 && xx < cc.X0 + cc.W && yy >= cc.Y0 && yy < cc.Y0 + cc.H)
                        {
                            meanEnergy += e;
                            nIn++;
                        }
                        else
                        {
                            meanBg += e;
                            varBg += e * e;
                            nOut++;
                        }
                    }
                }

                meanEnergy /= nIn;
                meanBg /= nOut;
                varBg = (varBg / nOut) - (meanBg * meanBg);
                cc.Score = (meanEnergy - meanBg) / MathF.Sqrt(varBg);
                if (cc.Score < 12)
                {
                    continue;
                }

                found.Add(cc);
            }
        }

        return found;
    }

    private static double DotModel(double dx, double dy, double ct, double st, double sigmaX, double sigmaY, double intensity)
    {
        double rx = (ct * dx) + (st * dy);
        double ry = (-st * dx) + (ct * dy);
        double md = (rx * rx / sigmaX) + (ry * ry / sigmaY);
        return intensity * Math.Exp(-0.5 * md);
    }

    /// <summary>libjxl's FitGaussian (FitGaussianFast, then the angle and axes normalized).</summary>
    private static Ellipse FitGaussian(Component cc, float[][] img, float[][] bg, int stride, int width, int height)
    {
        const int Bounds = 2;
        Ellipse ans = new();
        double sum = 0;
        int n = 0;
        double[] m1 = new double[3], m2 = new double[3], color = new double[3], bgColor = new double[3];
        int mode = (cc.ModeY * stride) + cc.ModeX;
        for (int c = 0; c < 3; c++)
        {
            color[c] = img[c][mode] - bg[c][mode];
        }

        double sign = color[1] > 0 ? 1 : -1;
        for (int sy = -Bounds; sy <= Bounds; sy++)
        {
            int y = sy + cc.ModeY;
            if (y < 0 || y >= height)
            {
                continue;
            }

            for (int sx = -Bounds; sx <= Bounds; sx++)
            {
                int x = sx + cc.ModeX;
                if (x < 0 || x >= width)
                {
                    continue;
                }

                int at = (y * stride) + x;
                double wt = Math.Max(1e-6, sign * (img[1][at] - bg[1][at]));
                sum += wt;
                m1[0] += wt * x;
                m1[1] += wt * y;
                m2[0] += wt * x * x;
                m2[1] += wt * x * y;
                m2[2] += wt * y * y;
                for (int c = 0; c < 3; c++)
                {
                    bgColor[c] += bg[c][at];
                }

                n++;
            }
        }

        for (int i = 0; i < 3; i++)
        {
            m1[i] /= sum;
            m2[i] /= sum;
            bgColor[i] /= n;
        }

        ans.X = m1[0];
        ans.Y = m1[1];
        double s00 = m2[0] - (m1[0] * m1[0]);
        double s11 = m2[2] - (m1[1] * m1[1]);
        double s01 = m2[1] - (m1[0] * m1[1]);

        // libjxl's ConvertToDiagonal of the 2×2 covariance.
        double d0, d1, u10, u11;
        double b = -(s00 + s11);
        double cdet = (s00 * s11) - (s01 * s01);
        double disc = (b * b) - (4.0 * cdet);
        if (Math.Abs(s01) < 1e-10 || disc < 0)
        {
            (d0, d1, u10, u11) = (s00, s11, 0.0, 1.0);
        }
        else
        {
            double sq = Math.Sqrt(disc);
            double l1 = (-b - sq) * 0.5, l2 = (-b + sq) * 0.5;
            double v0 = s00 - l1, v1 = s01;
            double vn = 1.0 / double.Hypot(v0, v1);
            (d0, d1, u10, u11) = (l1, l2, v0 * vn, v1 * vn);
        }

        double[] dd = [d0, d1];
        double[] u = [u10, u11];
        int p1 = 0, p2 = 1;
        if (d0 < d1)
        {
            (p1, p2) = (p2, p1);
        }

        ans.SigmaX = dd[p1];
        ans.SigmaY = dd[p2];
        ans.Angle = Math.Atan2(u[p1], u[p2]);
        ans.Background = bgColor;
        double ct = Math.Cos(ans.Angle), st = Math.Sin(ans.Angle);
        for (int c = 0; c < 3; c++)
        {
            // Least squares intensity over the background.
            double gg = 0, gd = 0;
            for (int y = cc.ModeY - Bounds; y <= cc.ModeY + Bounds; y++)
            {
                if (y < 0 || y >= height)
                {
                    continue;
                }

                for (int x = cc.ModeX - Bounds; x <= cc.ModeX + Bounds; x++)
                {
                    if (x < 0 || x >= width)
                    {
                        continue;
                    }

                    int at = (y * stride) + x;
                    double target = img[c][at] - bg[c][at];
                    double g = DotModel(x - ans.X, y - ans.Y, ct, st, ans.SigmaX, ans.SigmaY, 1.0);
                    gg += g * g;
                    gd += g * target;
                }
            }

            ans.Intensity[c] = gd / (gg + 1e-6);
        }

        DotLosses(ans, cc, img, stride, width, height);
        if (ans.SigmaX < ans.SigmaY)
        {
            (ans.SigmaX, ans.SigmaY) = (ans.SigmaY, ans.SigmaX);
            ans.Angle += Math.PI / 2.0;
        }

        ans.Angle -= Math.PI * Math.Floor(ans.Angle / Math.PI);
        if (Math.Abs(ans.Angle - Math.PI) < 1e-6 || Math.Abs(ans.Angle) < 1e-6)
        {
            ans.Angle = 0.0;
        }

        return ans;
    }

    /// <summary>libjxl's ComputeDotLosses over the group's box grown by 2, each channel on the fitted background.</summary>
    private static void DotLosses(Ellipse e, Component cc, float[][] img, int stride, int width, int height)
    {
        double ct = Math.Cos(e.Angle), st = Math.Sin(e.Angle);
        int n = 0;
        double l2Loss = 0, custom = 0;
        double[] negValue = new double[3];
        double distMeanModeSq = ((cc.ModeX - e.X) * (cc.ModeX - e.X)) + ((cc.ModeY - e.Y) * (cc.ModeY - e.Y));
        for (int c = 0; c < 3; c++)
        {
            for (int sy = -2; sy < cc.H + 2; sy++)
            {
                int y = sy + cc.Y0;
                if (y < 0 || y >= height)
                {
                    continue;
                }

                for (int sx = -2; sx < cc.W + 2; sx++)
                {
                    int x = sx + cc.X0;
                    if (x < 0 || x >= width)
                    {
                        continue;
                    }

                    double target = img[c][(y * stride) + x];
                    double dot = DotModel(x - e.X, y - e.Y, ct, st, e.SigmaX, e.SigmaY, e.Intensity[c]);
                    if (dot > target + 0.1)
                    {
                        e.NegPixels++;
                        negValue[c] += dot - target;
                    }

                    double diff = target - (e.Background[c] + dot);
                    double l2 = diff * diff;
                    l2Loss += l2;
                    double wt = DotModel(x - cc.ModeX, y - cc.ModeY, 1.0, 0.0, 1.0 + e.SigmaX, 1.0 + e.SigmaY, 1.0);
                    custom += wt * l2;
                    n++;
                }
            }
        }

        e.L2Loss = l2Loss / n;
        e.CustomLoss = (custom / n) + (20.0 * distMeanModeSq) + negValue[1];
    }

    /// <summary>
    /// libjxl's bin packing in FindBestPatchDictionary: patches, largest first, go at the first free place of a square frame
    /// that grows by 5% until all fit; gives the frame (cut to the rows used) and each patch's place in it.
    /// </summary>
    internal static (float[][] Reference, int Width, int Height, (int X, int Y)[] Places) Pack(List<JxlPatchCandidate> info)
    {
        int maxX = info.Max(p => p.Width), maxY = info.Max(p => p.Height);
        long total = info.Sum(p => (long)p.Width * p.Height);
        float root = (float)Math.Sqrt(total);
        long refW = (long)MathF.Max(maxX, root), refH = (long)MathF.Max(maxY, root);
        (int X, int Y)[] places = new (int, int)[info.Count];
        long usedY;
        while (true)
        {
            usedY = 0;
            refW = (long)((refW * 1.05f) + 1);
            refH = (long)((refH * 1.05f) + 1);
            byte[] occupied = new byte[refW * refH];
            bool success = true;
            for (int p = 0; p < info.Count && success; p++)
            {
                int w = info[p].Width, h = info[p].Height;
                long x0 = 0, y0 = 0;
                bool found = false;
                for (; y0 + h <= refH; y0++)
                {
                    x0 = 0;
                    for (; x0 + w <= refW; x0++)
                    {
                        bool hasOccupied = false;
                        long x = x0;
                        for (long y = y0; y < y0 + h; y++)
                        {
                            for (x = x0; x < x0 + w; x++)
                            {
                                if (occupied[(y * refW) + x] != 0)
                                {
                                    hasOccupied = true;
                                    break;
                                }
                            }
                        }

                        if (!hasOccupied)
                        {
                            found = true;
                            break;
                        }

                        // libjxl jumps past the occupied pixel of the last row checked.
                        x0 = x;
                    }

                    if (found)
                    {
                        break;
                    }
                }

                if (!found)
                {
                    success = false;
                    break;
                }

                places[p] = ((int)x0, (int)y0);
                for (long y = y0; y < y0 + h; y++)
                {
                    occupied.AsSpan((int)((y * refW) + x0), w).Fill(1);
                }

                usedY = Math.Max(usedY, y0 + h);
            }

            if (success)
            {
                break;
            }
        }

        int fw = (int)refW, fh = (int)usedY;
        float[][] reference = [new float[fw * fh], new float[fw * fh], new float[fw * fh]];
        for (int i = 0; i < info.Count; i++)
        {
            JxlPatchCandidate p = info[i];
            for (int y = 0; y < p.Height; y++)
            {
                for (int x = 0; x < p.Width; x++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        reference[c][((y + places[i].Y) * fw) + x + places[i].X] = p.Values[c][(y * p.Width) + x];
                    }
                }
            }
        }

        return (reference, fw, fh, places);
    }
}
