using System.Buffers.Binary;

namespace Egami.Formats.Extended.Dds.Blocks;

/// <summary>
/// Encodes BC1 to BC5 blocks. Colour blocks fit their endpoints along the principal axis of the block's colours, then refine
/// them by least squares on the chosen indices and by trying neighbouring endpoints, as far as the effort asks; single-colour
/// blocks use the endpoint pair whose interpolated colour is nearest. Every candidate is scored with the decoder's own
/// palette, so the error measured is the error a reader sees. The work for one block depends only on that block, so the output
/// never depends on how many threads encode.
/// </summary>
internal static class Bc1To5Encoder
{
    private static readonly byte[][] Single5 = SingleColorTable(31);
    private static readonly byte[][] Single6 = SingleColorTable(63);

    /// <summary>
    /// Encodes 16 RGBA pixels (4 bytes each) into a BC1 colour block. With <paramref name="transparent"/>, pixels with alpha
    /// below 128 become the three-colour mode's transparent black; otherwise alpha is ignored (BC2 and BC3 colour).
    /// </summary>
    public static void EncodeBc1(ReadOnlySpan<byte> rgba, Span<byte> block, bool transparent, int effort)
    {
        Span<int> colors = stackalloc int[48];
        Span<bool> clear = stackalloc bool[16];
        int count = 0;
        bool anyClear = false;
        for (int i = 0; i < 16; i++)
        {
            clear[i] = transparent && rgba[(i * 4) + 3] < 128;
            anyClear |= clear[i];
            colors[i * 3] = rgba[i * 4];
            colors[(i * 3) + 1] = rgba[(i * 4) + 1];
            colors[(i * 3) + 2] = rgba[(i * 4) + 2];
            count += clear[i] ? 0 : 1;
        }

        if (count == 0)
        {
            Write(block, 0, 0xFFFF, uint.MaxValue);
            return;
        }

        Candidate best = Candidate.Worst;
        bool four = !anyClear;
        if (Uniform(colors, clear, out int r, out int g, out int b))
        {
            // One colour: the endpoints whose two-thirds point lands nearest, every pixel at index 2.
            byte[] rs = Single5[r], gs = Single6[g], bs = Single5[b];
            ushort c0 = (ushort)((rs[0] << 11) | (gs[0] << 5) | bs[0]);
            ushort c1 = (ushort)((rs[1] << 11) | (gs[1] << 5) | bs[1]);
            if (four)
            {
                best = Score(colors, clear, c0, c1, fourColors: true, fixedOrder: false);
            }

            Candidate exact = Score(colors, clear, Quantize(r, g, b), Quantize(r, g, b), fourColors: four, fixedOrder: false);
            best = Better(best, exact);
            if (!four)
            {
                best = Better(best, Score(colors, clear, c0, c1, fourColors: false, fixedOrder: false));
            }

            Emit(block, best);
            return;
        }

        Span<float> lo = stackalloc float[3];
        Span<float> hi = stackalloc float[3];
        Fit(colors, clear, effort, lo, hi);
        ushort e0 = Quantize(lo[0], lo[1], lo[2]);
        ushort e1 = Quantize(hi[0], hi[1], hi[2]);
        if (four)
        {
            best = Score(colors, clear, e0, e1, fourColors: true, fixedOrder: false);
        }

        if (!four || effort >= 4)
        {
            best = Better(best, Score(colors, clear, e0, e1, fourColors: false, fixedOrder: false));
        }

        int passes = effort switch { <= 1 => 0, 2 => 1, 3 => 2, 4 => 3, 5 => 5, _ => 8 };
        for (int pass = 0; pass < passes; pass++)
        {
            Candidate refined = Refine(colors, clear, best);
            if (refined.Error >= best.Error)
            {
                break;
            }

            best = refined;
        }

        if (effort >= 5)
        {
            best = Perturb(colors, clear, best, effort >= 6 ? 2 : 1);
        }

        Emit(block, best);
    }

    /// <summary>Encodes a BC2 block: 4-bit alpha rounded from 16 RGBA pixels, then the colour as BC1's four-colour mode.</summary>
    public static void EncodeBc2(ReadOnlySpan<byte> rgba, Span<byte> block, int effort)
    {
        ulong alpha = 0;
        for (int i = 0; i < 16; i++)
        {
            alpha |= (ulong)((rgba[(i * 4) + 3] + 8) / 17) << (4 * i);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(block, alpha);
        EncodeBc1(rgba, block[8..], transparent: false, effort);
    }

    /// <summary>Encodes a BC3 block: alpha as a BC4 block, then the colour as BC1's four-colour mode.</summary>
    public static void EncodeBc3(ReadOnlySpan<byte> rgba, Span<byte> block, int effort)
    {
        EncodeUnorm(rgba[3..], 4, block, effort);
        EncodeBc1(rgba, block[8..], transparent: false, effort);
    }

    /// <summary>
    /// Encodes 16 values, every <paramref name="stride"/>th byte of <paramref name="values"/>, into an unsigned BC4 block
    /// (also BC3's alpha and each half of BC5). Both modes are tried: eight interpolated levels, and six with exact 0 and 255.
    /// </summary>
    public static void EncodeUnorm(ReadOnlySpan<byte> values, int stride, Span<byte> block, int effort)
    {
        Span<int> v = stackalloc int[16];
        int min = 255, max = 0, innerMin = 255, innerMax = 0;
        for (int i = 0; i < 16; i++)
        {
            v[i] = values[i * stride];
            min = Math.Min(min, v[i]);
            max = Math.Max(max, v[i]);
            if (v[i] is > 0 and < 255)
            {
                innerMin = Math.Min(innerMin, v[i]);
                innerMax = Math.Max(innerMax, v[i]);
            }
        }

        if (min == max)
        {
            Alpha(block, max, min, 0);
            return;
        }

        (int Error, int A0, int A1) best = (int.MaxValue, 0, 0);
        best = BetterAlpha(best, v, max, min);
        if (effort >= 1)
        {
            best = innerMin <= innerMax ? BetterAlpha(best, v, innerMin, innerMax) : BetterAlpha(best, v, 0, 255);
        }

        if (effort >= 3)
        {
            // Refit the endpoints by least squares to the indices they chose, then try their neighbours.
            int refits = effort - 2;
            for (int i = 0; i < refits; i++)
            {
                (int Error, int A0, int A1) refit = RefitAlpha(best, v);
                if (refit.Error >= best.Error)
                {
                    break;
                }

                best = refit;
            }

            int reach = effort switch { 3 => 0, 4 => 1, 5 => 2, _ => 4 };
            (int Error, int A0, int A1) start = best;
            for (int d0 = -reach; d0 <= reach; d0++)
            {
                for (int d1 = -reach; d1 <= reach; d1++)
                {
                    best = BetterAlpha(best, v, Math.Clamp(start.A0 + d0, 0, 255), Math.Clamp(start.A1 + d1, 0, 255));
                }
            }
        }

        ulong indices = 0;
        Span<int> palette = stackalloc int[8];
        AlphaPalette(best.A0, best.A1, palette);
        for (int i = 0; i < 16; i++)
        {
            indices |= (ulong)Nearest(palette, v[i]) << (3 * i);
        }

        Alpha(block, best.A0, best.A1, indices);
    }

    /// <summary>Solves for the endpoints that best fit each value's chosen interpolation weight, in the same mode.</summary>
    private static (int Error, int A0, int A1) RefitAlpha((int Error, int A0, int A1) current, ReadOnlySpan<int> v)
    {
        Span<int> palette = stackalloc int[8];
        AlphaPalette(current.A0, current.A1, palette);
        bool eight = current.A0 > current.A1;
        float aa = 0, ab = 0, bb = 0, av = 0, bv = 0;
        for (int i = 0; i < 16; i++)
        {
            int index = Nearest(palette, v[i]);
            if (!eight && index >= 6)
            {
                continue;
            }

            float w = index switch { 0 => 0, 1 => 1, _ => (index - 1) / (eight ? 7f : 5f) };
            float a = 1 - w;
            aa += a * a;
            ab += a * w;
            bb += w * w;
            av += a * v[i];
            bv += w * v[i];
        }

        float det = (aa * bb) - (ab * ab);
        if (Math.Abs(det) < 1e-6f)
        {
            return current;
        }

        int a0 = Math.Clamp((int)MathF.Round(((bb * av) - (ab * bv)) / det), 0, 255);
        int a1 = Math.Clamp((int)MathF.Round(((aa * bv) - (ab * av)) / det), 0, 255);
        if (eight ? a0 <= a1 : a0 > a1)
        {
            (a0, a1) = (a1, a0);
        }

        return BetterAlpha(current, v, a0, a1);
    }

    private static (int Error, int A0, int A1) BetterAlpha((int Error, int A0, int A1) best, ReadOnlySpan<int> v, int a0, int a1)
    {
        if (a0 == a1)
        {
            return best;
        }

        Span<int> palette = stackalloc int[8];
        AlphaPalette(a0, a1, palette);
        int error = 0;
        if (a0 > a1)
        {
            // Eight levels from a1 up to a0: the nearest is the rounded position or a neighbour of it.
            int span = a0 - a1;
            for (int i = 0; i < 16 && error < best.Error; i++)
            {
                int p = Math.Clamp((((v[i] - a1) * 14) + span) / (2 * span), 0, 7);
                int d = int.MaxValue;
                for (int q = Math.Max(0, p - 1); q <= Math.Min(7, p + 1); q++)
                {
                    int level = q == 0 ? a1 : q == 7 ? a0 : palette[8 - q];
                    d = Math.Min(d, Math.Abs(level - v[i]));
                }

                error += d * d;
            }
        }
        else
        {
            for (int i = 0; i < 16 && error < best.Error; i++)
            {
                int d = palette[Nearest(palette, v[i])] - v[i];
                error += d * d;
            }
        }

        return error < best.Error ? (error, a0, a1) : best;
    }

    /// <summary>Fills the palette a BC4 decoder makes from the endpoints, with the decoder's rounding.</summary>
    private static void AlphaPalette(int a0, int a1, Span<int> palette)
    {
        palette[0] = a0;
        palette[1] = a1;
        if (a0 > a1)
        {
            for (int i = 1; i < 7; i++)
            {
                palette[i + 1] = (((((7 - i) * a0) + (i * a1)) * 2) + 7) / 14;
            }
        }
        else
        {
            for (int i = 1; i < 5; i++)
            {
                palette[i + 1] = (((((5 - i) * a0) + (i * a1)) * 2) + 5) / 10;
            }

            palette[6] = 0;
            palette[7] = 255;
        }
    }

    private static int Nearest(ReadOnlySpan<int> palette, int value)
    {
        int best = 0, error = int.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            int d = Math.Abs(palette[i] - value);
            if (d < error)
            {
                (best, error) = (i, d);
            }
        }

        return best;
    }

    private static void Alpha(Span<byte> block, int a0, int a1, ulong indices)
    {
        block[0] = (byte)a0;
        block[1] = (byte)a1;
        for (int i = 0; i < 6; i++)
        {
            block[2 + i] = (byte)(indices >> (8 * i));
        }
    }

    private static bool Uniform(ReadOnlySpan<int> colors, ReadOnlySpan<bool> clear, out int r, out int g, out int b)
    {
        r = g = b = -1;
        for (int i = 0; i < 16; i++)
        {
            if (clear[i])
            {
                continue;
            }

            if (r < 0)
            {
                (r, g, b) = (colors[i * 3], colors[(i * 3) + 1], colors[(i * 3) + 2]);
            }
            else if (colors[i * 3] != r || colors[(i * 3) + 1] != g || colors[(i * 3) + 2] != b)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Finds the line through the colours: the bounding box's diagonal at effort 0, else the principal axis.</summary>
    private static void Fit(ReadOnlySpan<int> colors, ReadOnlySpan<bool> clear, int effort, Span<float> lo, Span<float> hi)
    {
        Span<float> mean = stackalloc float[3];
        Span<float> min = [255, 255, 255];
        Span<float> max = [0, 0, 0];
        int n = 0;
        for (int i = 0; i < 16; i++)
        {
            if (clear[i])
            {
                continue;
            }

            n++;
            for (int c = 0; c < 3; c++)
            {
                float v = colors[(i * 3) + c];
                mean[c] += v;
                min[c] = Math.Min(min[c], v);
                max[c] = Math.Max(max[c], v);
            }
        }

        for (int c = 0; c < 3; c++)
        {
            mean[c] /= n;
        }

        Span<float> axis = stackalloc float[3];
        if (effort == 0)
        {
            for (int c = 0; c < 3; c++)
            {
                axis[c] = max[c] - min[c];
            }
        }
        else
        {
            // Covariance, then power iteration from the bounding box's diagonal.
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            for (int i = 0; i < 16; i++)
            {
                if (clear[i])
                {
                    continue;
                }

                float dx = colors[i * 3] - mean[0], dy = colors[(i * 3) + 1] - mean[1], dz = colors[(i * 3) + 2] - mean[2];
                xx += dx * dx;
                xy += dx * dy;
                xz += dx * dz;
                yy += dy * dy;
                yz += dy * dz;
                zz += dz * dz;
            }

            float ax = max[0] - min[0], ay = max[1] - min[1], az = max[2] - min[2];
            if (xy < 0)
            {
                ay = -ay;
            }

            if (xz < 0)
            {
                az = -az;
            }

            for (int it = 0; it < 8; it++)
            {
                float nx = (xx * ax) + (xy * ay) + (xz * az);
                float ny = (xy * ax) + (yy * ay) + (yz * az);
                float nz = (xz * ax) + (yz * ay) + (zz * az);
                float m = Math.Max(Math.Abs(nx), Math.Max(Math.Abs(ny), Math.Abs(nz)));
                if (m < 1e-6f)
                {
                    break;
                }

                (ax, ay, az) = (nx / m, ny / m, nz / m);
            }

            axis[0] = ax;
            axis[1] = ay;
            axis[2] = az;
        }

        float length = (axis[0] * axis[0]) + (axis[1] * axis[1]) + (axis[2] * axis[2]);
        if (length < 1e-6f)
        {
            mean.CopyTo(lo);
            mean.CopyTo(hi);
            return;
        }

        float tMin = float.MaxValue, tMax = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            if (clear[i])
            {
                continue;
            }

            float t = (((colors[i * 3] - mean[0]) * axis[0]) + ((colors[(i * 3) + 1] - mean[1]) * axis[1]) + ((colors[(i * 3) + 2] - mean[2]) * axis[2])) / length;
            tMin = Math.Min(tMin, t);
            tMax = Math.Max(tMax, t);
        }

        for (int c = 0; c < 3; c++)
        {
            lo[c] = Math.Clamp(mean[c] + (tMin * axis[c]), 0, 255);
            hi[c] = Math.Clamp(mean[c] + (tMax * axis[c]), 0, 255);
        }
    }

    /// <summary>Refits the endpoints by least squares to the indices <paramref name="current"/> chose.</summary>
    private static Candidate Refine(ReadOnlySpan<int> colors, ReadOnlySpan<bool> clear, Candidate current)
    {
        // Each pixel is (1 - w) e0 + w e1 for its index's weight w; solve the 2 × 2 normal equations per channel.
        float aa = 0, ab = 0, bb = 0;
        Span<float> ax = stackalloc float[3];
        Span<float> bx = stackalloc float[3];
        for (int i = 0; i < 16; i++)
        {
            int index = (int)((current.Indices >> (2 * i)) & 3);
            if (clear[i] || (!current.FourColors && index == 3))
            {
                continue;
            }

            float w = current.FourColors ? index switch { 0 => 0, 1 => 1, 2 => 1 / 3f, _ => 2 / 3f } : index switch { 0 => 0, 1 => 1, _ => 0.5f };
            float a = 1 - w;
            aa += a * a;
            ab += a * w;
            bb += w * w;
            for (int c = 0; c < 3; c++)
            {
                ax[c] += a * colors[(i * 3) + c];
                bx[c] += w * colors[(i * 3) + c];
            }
        }

        float det = (aa * bb) - (ab * ab);
        if (Math.Abs(det) < 1e-6f)
        {
            return current;
        }

        Span<float> e0 = stackalloc float[3];
        Span<float> e1 = stackalloc float[3];
        for (int c = 0; c < 3; c++)
        {
            e0[c] = Math.Clamp(((bb * ax[c]) - (ab * bx[c])) / det, 0, 255);
            e1[c] = Math.Clamp(((aa * bx[c]) - (ab * ax[c])) / det, 0, 255);
        }

        return Better(current, Score(colors, clear, Quantize(e0[0], e0[1], e0[2]), Quantize(e1[0], e1[1], e1[2]), current.FourColors, fixedOrder: false));
    }

    /// <summary>Tries moving each channel of each endpoint by up to <paramref name="reach"/> steps, keeping what helps.</summary>
    private static Candidate Perturb(ReadOnlySpan<int> colors, ReadOnlySpan<bool> clear, Candidate best, int reach)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            Candidate start = best;
            for (int end = 0; end < 2; end++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int shift = channel switch { 0 => 11, 1 => 5, _ => 0 };
                    int top = channel == 1 ? 63 : 31;
                    for (int d = -reach; d <= reach; d++)
                    {
                        if (d == 0)
                        {
                            continue;
                        }

                        ushort e = end == 0 ? best.C0 : best.C1;
                        int level = (e >> shift) & top;
                        int moved = level + d;
                        if (moved < 0 || moved > top)
                        {
                            continue;
                        }

                        ushort changed = (ushort)((e & ~(top << shift)) | (moved << shift));
                        Candidate c = end == 0
                            ? Score(colors, clear, changed, best.C1, best.FourColors, fixedOrder: false)
                            : Score(colors, clear, best.C0, changed, best.FourColors, fixedOrder: false);
                        best = Better(best, c);
                    }
                }
            }

            if (best.Error >= start.Error)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// Scores endpoints in a mode: orders them as the mode needs (endpoint 0 above endpoint 1 for four colours), picks each
    /// pixel's nearest palette entry and sums the squared error.
    /// </summary>
    private static Candidate Score(ReadOnlySpan<int> colors, ReadOnlySpan<bool> clear, ushort c0, ushort c1, bool fourColors, bool fixedOrder)
    {
        if (!fixedOrder)
        {
            if (fourColors && c0 < c1)
            {
                (c0, c1) = (c1, c0);
            }
            else if (!fourColors && c0 > c1)
            {
                (c0, c1) = (c1, c0);
            }
        }

        if (fourColors && c0 == c1)
        {
            // Equal endpoints decode in the three-colour mode; index 0 is still endpoint 0.
            fourColors = false;
        }

        Span<int> palette = stackalloc int[12];
        Palette(c0, c1, fourColors, palette);
        int entries = fourColors ? 4 : 3;
        long error = 0;
        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            if (clear[i])
            {
                indices |= 3u << (2 * i);
                continue;
            }

            int r = colors[i * 3], g = colors[(i * 3) + 1], b = colors[(i * 3) + 2];
            int bestIndex = 0, bestError = int.MaxValue;
            for (int p = 0; p < entries; p++)
            {
                int dr = palette[p * 3] - r, dg = palette[(p * 3) + 1] - g, db = palette[(p * 3) + 2] - b;
                int e = (dr * dr) + (dg * dg) + (db * db);
                if (e < bestError)
                {
                    (bestIndex, bestError) = (p, e);
                }
            }

            error += bestError;
            indices |= (uint)bestIndex << (2 * i);
        }

        return new Candidate(error, c0, c1, indices, fourColors);
    }

    /// <summary>The colours a decoder makes from the endpoints, with <see cref="Bc1To5"/>'s rounding.</summary>
    private static void Palette(ushort c0, ushort c1, bool fourColors, Span<int> palette)
    {
        int r0 = c0 >> 11, g0 = (c0 >> 5) & 63, b0 = c0 & 31;
        int r1 = c1 >> 11, g1 = (c1 >> 5) & 63, b1 = c1 & 31;
        Set(palette, 0, Scale(r0, 1, 31), Scale(g0, 1, 63), Scale(b0, 1, 31));
        Set(palette, 1, Scale(r1, 1, 31), Scale(g1, 1, 63), Scale(b1, 1, 31));
        if (fourColors)
        {
            Set(palette, 2, Scale((2 * r0) + r1, 3, 31), Scale((2 * g0) + g1, 3, 63), Scale((2 * b0) + b1, 3, 31));
            Set(palette, 3, Scale(r0 + (2 * r1), 3, 31), Scale(g0 + (2 * g1), 3, 63), Scale(b0 + (2 * b1), 3, 31));
        }
        else
        {
            Set(palette, 2, Scale(r0 + r1, 2, 31), Scale(g0 + g1, 2, 63), Scale(b0 + b1, 2, 31));
        }
    }

    private static void Set(Span<int> palette, int index, int r, int g, int b)
    {
        palette[index * 3] = r;
        palette[(index * 3) + 1] = g;
        palette[(index * 3) + 2] = b;
    }

    private static int Scale(int n, int d, int max)
    {
        int den = d * max;
        return ((n * 255 * 2) + den) / (2 * den);
    }

    private static ushort Quantize(float r, float g, float b)
    {
        int qr = Math.Clamp((int)MathF.Round(r * 31 / 255f), 0, 31);
        int qg = Math.Clamp((int)MathF.Round(g * 63 / 255f), 0, 63);
        int qb = Math.Clamp((int)MathF.Round(b * 31 / 255f), 0, 31);
        return (ushort)((qr << 11) | (qg << 5) | qb);
    }

    private static Candidate Better(Candidate a, Candidate b) => b.Error < a.Error ? b : a;

    private static void Emit(Span<byte> block, Candidate c) => Write(block, c.C0, c.C1, c.Indices);

    private static void Write(Span<byte> block, ushort c0, ushort c1, uint indices)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(block, c0);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], c1);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], indices);
    }

    /// <summary>For each 8-bit level, the 5- or 6-bit endpoints whose two-thirds interpolation (2 e0 + e1) / 3 is nearest.</summary>
    private static byte[][] SingleColorTable(int max)
    {
        byte[][] table = new byte[256][];
        for (int v = 0; v < 256; v++)
        {
            int best = int.MaxValue;
            for (int a = 0; a <= max; a++)
            {
                for (int b = 0; b <= max; b++)
                {
                    int value = Scale((2 * a) + b, 3, max);
                    int error = (Math.Abs(value - v) * 1024) + Math.Abs(a - b);
                    if (error < best)
                    {
                        best = error;
                        table[v] = [(byte)a, (byte)b];
                    }
                }
            }
        }

        return table;
    }

    /// <summary>One way to code a colour block, with its squared error.</summary>
    private readonly record struct Candidate(long Error, ushort C0, ushort C1, uint Indices, bool FourColors)
    {
        public static Candidate Worst => new(long.MaxValue, 0, 0, 0, true);
    }
}
