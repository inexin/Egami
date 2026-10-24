using Egami.Pixels;

namespace Egami.Formats.Av1;

/// <summary>
/// One frame for <see cref="Av1StreamEncoder.Analyze"/>: its picture and the frames before it in the list (coding order) that it
/// will predict from.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-analyze"/>
/// </example>
public sealed class Av1AnalysisFrame
{
    private readonly IReadOnlyList<int> _references = [];

    /// <summary>Gets the picture at the stream's size; only its luma is read.</summary>
    public required YuvRaster Picture { get; init; }

    /// <summary>
    /// Gets the frames, by their place in the list, that this one will predict from: each before it in the list. Empty (the
    /// default) for a key frame.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public IReadOnlyList<int> References
    {
        get => _references;
        init => _references = value is null ? throw new ArgumentNullException(nameof(value)) : [.. value];
    }
}

/// <summary>
/// What <see cref="Av1StreamEncoder.Analyze"/> found for one frame: how much of each 16x16 block's content the frames after it
/// predict from it. A plan carries it (<see cref="Av1FramePlan.Analysis"/>) to give the blocks that matter most finer quantizers on
/// a group's base frames, or lower rate-distortion multipliers on the others.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Av1.cs" region="av1-analyze"/>
/// </example>
public sealed class Av1FrameAnalysis
{
    internal Av1FrameAnalysis(int width, int height, int columns, int rows, double[] intra, double[] propagated)
    {
        (Width, Height, Columns, Rows, Intra, Propagated) = (width, height, columns, rows, intra, propagated);
        double sumIntra = 0, sumAll = 0;
        for (int i = 0; i < intra.Length; i++)
        {
            sumIntra += intra[i];
            sumAll += intra[i] + propagated[i];
        }

        Importance = sumIntra > 0 ? sumAll / sumIntra : 1;
    }

    /// <summary>
    /// Gets how much the frame's content matters to the frames after it: 1 when no later frame predicts from it, more the more
    /// of it they reuse (the frame's own intra cost plus what propagates into it, over its intra cost). A group's base frame's
    /// quantizer step can fall with its square root, as libaom's and SVT-AV1's temporal models make it.
    /// </summary>
    public double Importance { get; }

    /// <summary>Gets the picture width the analysis is for.</summary>
    internal int Width { get; }

    /// <summary>Gets the picture height the analysis is for.</summary>
    internal int Height { get; }

    /// <summary>Gets the columns of 16x16 blocks.</summary>
    internal int Columns { get; }

    /// <summary>Gets the rows of 16x16 blocks.</summary>
    internal int Rows { get; }

    /// <summary>Gets each block's intra cost (a sum of absolute transformed differences against its DC prediction).</summary>
    internal double[] Intra { get; }

    /// <summary>Gets the cost of later frames' content each block carries, propagated back through the frames that predict from it.</summary>
    internal double[] Propagated { get; }

    /// <summary>
    /// Gets each superblock's quantizer from the base quantizer: its step divided by the square root of how much more the
    /// superblock propagates than the frame does, within about nine steps of four either way (libaom's and SVT-AV1's objective
    /// delta-q is the idea).
    /// </summary>
    internal int[] SuperblockQIndices(int sbCols, int sbRows, int sbSize, int baseQIndex, int bitDepth)
    {
        int[] q = new int[sbCols * sbRows];
        double r0 = 1 / Importance;
        int cells = sbSize / 16;
        double baseStep = AcQ(baseQIndex, bitDepth);
        for (int sr = 0; sr < sbRows; sr++)
        {
            for (int sc = 0; sc < sbCols; sc++)
            {
                double intra = 0, all = 0;
                for (int r = sr * cells; r < Math.Min(Rows, (sr + 1) * cells); r++)
                {
                    for (int c = sc * cells; c < Math.Min(Columns, (sc + 1) * cells); c++)
                    {
                        intra += Intra[(r * Columns) + c];
                        all += Intra[(r * Columns) + c] + Propagated[(r * Columns) + c];
                    }
                }

                double rk = all > 0 ? intra / all : r0;
                double beta = r0 / Math.Max(rk, 1e-6);
                double step = baseStep / Math.Sqrt(beta);
                int target = baseQIndex;
                while (target > 1 && AcQ(target - 1, bitDepth) >= step)
                {
                    target--;
                }

                while (target < 255 && AcQ(target, bitDepth) < step)
                {
                    target++;
                }

                q[(sr * sbCols) + sc] = Math.Clamp(target, Math.Max(1, baseQIndex - 36), Math.Min(255, baseQIndex + 36));
            }
        }

        return q;
    }

    private static int AcQ(int qindex, int bitDepth) => bitDepth switch
    {
        10 => Av1Tables.AcQLookup10[qindex],
        12 => Av1Tables.AcQLookup12[qindex],
        _ => Av1Tables.AcQLookup8[qindex],
    };

    /// <summary>
    /// Gets each 16x16 block's rate-distortion multiplier scale: how much less the frame propagates than the block, normalized to
    /// a geometric mean of 1 and kept within a factor of 2 either way (libaom's av1_get_cb_rdmult and SVT-AV1's lambda modulation
    /// are the idea); null when nothing propagates into the frame.
    /// </summary>
    internal double[]? RdScales()
    {
        if (Importance <= 1)
        {
            return null;
        }

        double r0 = 1 / Importance;
        double[] scale = new double[Intra.Length];
        double logSum = 0;
        for (int i = 0; i < scale.Length; i++)
        {
            double all = Intra[i] + Propagated[i];
            double rk = all > 0 ? Intra[i] / all : r0;
            scale[i] = Math.Clamp(rk / r0, 0.5, 2);
            logSum += Math.Log(scale[i]);
        }

        double mean = Math.Exp(logSum / scale.Length);
        for (int i = 0; i < scale.Length; i++)
        {
            scale[i] /= mean;
        }

        return scale;
    }
}

/// <summary>
/// The temporal dependency model of <see cref="Av1StreamEncoder.Analyze"/>, after the mbtree-like models of SVT-AV1 and libaom
/// (the ideas of their temporal model documents; BSD): for every 16x16 luma block of every frame, an intra cost (the summed
/// absolute Hadamard transform of its residual against its DC prediction from the source) and an inter cost (the same of its
/// residual at the whole-sample vector the motion pre-pass finds on the source, against each reference the frame names, the
/// best kept); then, in reverse coding order, each block hands the share of its cost and of what it carries that its
/// reference explains (1 − inter / intra) to the reference blocks its vector covers, by area. The order is fixed, so the result
/// does not depend on the thread count.
/// </summary>
internal static class Av1TemporalModel
{
    private const int Cell = 16;

    /// <summary>Analyzes the frames, in coding order, of a picture size, on up to <paramref name="parallelism"/> threads.</summary>
    internal static Av1FrameAnalysis[] Analyze(IReadOnlyList<Av1AnalysisFrame> frames, int width, int height, int parallelism)
    {
        int n = frames.Count;
        int cols = (width + Cell - 1) / Cell, rows = (height + Cell - 1) / Cell, cells = cols * rows;
        int stride = (width + 63) & ~63, padded = (height + 63) & ~63;
        ushort[][] luma = new ushort[n][];
        Av1ReferencePlane?[] planes = new Av1ReferencePlane?[n];
        double[][] intra = new double[n][], inter = new double[n][], propagated = new double[n][];
        int[][] best = new int[n][];
        int[][] vectors = new int[n][];
        void For(int count, Action<int> body)
        {
            if (parallelism > 1 && count > 1)
            {
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, body);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    body(i);
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            luma[i] = Luma(frames[i].Picture, width, height, stride, padded);
        }

        for (int i = 0; i < n; i++)
        {
            IReadOnlyList<int> refs = frames[i].References;
            ushort[] source = luma[i];
            double[] intraCost = intra[i] = new double[cells];
            double[] interCost = inter[i] = new double[cells];
            propagated[i] = new double[cells];
            int[] chosen = best[i] = new int[cells];
            int[] mv = vectors[i] = new int[cells * 2];
            For(rows, row =>
            {
                for (int col = 0; col < cols; col++)
                {
                    intraCost[(row * cols) + col] = IntraCost(source, stride, col * Cell, row * Cell, Math.Min(Cell, width - (col * Cell)), Math.Min(Cell, height - (row * Cell)));
                }
            });
            Array.Copy(intraCost, interCost, cells);
            Array.Fill(chosen, -1);
            List<int> valid = [];
            foreach (int r in refs)
            {
                if (r >= 0 && r < i && !valid.Contains(r))
                {
                    valid.Add(r);
                }
            }

            if (valid.Count == 0)
            {
                continue;
            }

            // The pre-pass searches its references by slot (1 to 7); seven references at most.
            Av1ReferencePlane?[] slots = new Av1ReferencePlane?[8];
            int[] slotRefs = new int[Math.Min(valid.Count, 7)];
            for (int k = 0; k < slotRefs.Length; k++)
            {
                slots[k + 1] = planes[valid[k]] ??= new Av1ReferencePlane(luma[valid[k]], stride, width, height);
                _ = slots[k + 1]!.Half.Half;
                slotRefs[k] = k + 1;
            }

            Av1MotionPrepass pass = Av1MotionPrepass.Build(width, height, source, stride, slots, slotRefs, For);
            For(rows, row =>
            {
                for (int col = 0; col < cols; col++)
                {
                    int at = (row * cols) + col;
                    int w = Math.Min(Cell, width - (col * Cell)), h = Math.Min(Cell, height - (row * Cell));
                    for (int k = 0; k < slotRefs.Length; k++)
                    {
                        (int vr, int vc) = pass.Vector(k + 1, row, col);
                        Av1ReferencePlane plane = slots[k + 1]!;
                        int x = (col * Cell) + vc, y = (row * Cell) + vr;
                        if (!plane.Holds(x, y, w, h))
                        {
                            continue;
                        }

                        double cost = Satd(source, (row * Cell * stride) + (col * Cell), stride, plane.Samples, plane.At(x, y), plane.Stride, w, h);
                        if (cost < interCost[at])
                        {
                            interCost[at] = cost;
                            chosen[at] = valid[k];
                            (mv[2 * at], mv[(2 * at) + 1]) = (vr, vc);
                        }
                    }
                }
            });
        }

        // Back from the last frame coded: what each block's reference explains of it, and of what it carries, goes to the
        // reference blocks under its vector, by area.
        for (int i = n - 1; i >= 0; i--)
        {
            double[] prop = propagated[i];
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    int at = (row * cols) + col;
                    int r = best[i][at];
                    double cost = intra[i][at];
                    if (r < 0 || cost <= 0)
                    {
                        continue;
                    }

                    double amount = (cost + prop[at]) * (1 - (Math.Min(inter[i][at], cost) / cost));
                    if (amount <= 0)
                    {
                        continue;
                    }

                    int x = (col * Cell) + vectors[i][(2 * at) + 1], y = (row * Cell) + vectors[i][2 * at];
                    int c0 = (int)Math.Floor(x / (double)Cell), r0 = (int)Math.Floor(y / (double)Cell);
                    int dx = x - (c0 * Cell), dy = y - (r0 * Cell);
                    double[] target = propagated[r];
                    Give(target, r0, c0, (Cell - dy) * (Cell - dx));
                    Give(target, r0, c0 + 1, (Cell - dy) * dx);
                    Give(target, r0 + 1, c0, dy * (Cell - dx));
                    Give(target, r0 + 1, c0 + 1, dy * dx);

                    void Give(double[] to, int tr, int tc, int area)
                    {
                        if (area > 0 && tr >= 0 && tc >= 0 && tr < rows && tc < cols)
                        {
                            to[(tr * cols) + tc] += amount * area / (Cell * Cell);
                        }
                    }
                }
            }
        }

        Av1FrameAnalysis[] result = new Av1FrameAnalysis[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = new Av1FrameAnalysis(width, height, cols, rows, intra[i], propagated[i]);
        }

        return result;
    }

    /// <summary>Gets a picture's luma as samples padded to whole 64x64 blocks by repeating the last column and row.</summary>
    private static ushort[] Luma(YuvRaster picture, int width, int height, int stride, int rows)
    {
        ushort[] samples = new ushort[stride * rows];
        for (int y = 0; y < rows; y++)
        {
            Span<ushort> row = samples.AsSpan(y * stride, stride);
            int from = Math.Min(y, height - 1);
            if (picture is YuvRaster<byte> bytes)
            {
                ReadOnlySpan<byte> source = bytes.Y.Row(from)[..width];
                for (int x = 0; x < width; x++)
                {
                    row[x] = source[x];
                }
            }
            else
            {
                ((YuvRaster<ushort>)picture).Y.Row(from)[..width].CopyTo(row);
            }

            row[width..].Fill(row[width - 1]);
        }

        return samples;
    }

    /// <summary>Gets a block's intra cost: the transformed residual against the mean of the source row above and column to its left.</summary>
    private static double IntraCost(ushort[] source, int stride, int x0, int y0, int w, int h)
    {
        int sum = 0, count = 0;
        if (y0 > 0)
        {
            for (int x = 0; x < w; x++)
            {
                sum += source[((y0 - 1) * stride) + x0 + x];
            }

            count += w;
        }

        if (x0 > 0)
        {
            for (int y = 0; y < h; y++)
            {
                sum += source[((y0 + y) * stride) + x0 - 1];
            }

            count += h;
        }

        int dc = count > 0 ? (sum + (count / 2)) / count : 128;
        Span<int> residual = stackalloc int[Cell * Cell];
        for (int y = 0; y < Cell; y++)
        {
            for (int x = 0; x < Cell; x++)
            {
                residual[(y * Cell) + x] = y < h && x < w ? source[((y0 + y) * stride) + x0 + x] - dc : 0;
            }
        }

        return Hadamard(residual);
    }

    /// <summary>Gets the summed absolute Hadamard transform of the difference of a 16x16 source block and a reference block.</summary>
    private static double Satd(ushort[] source, int sourceAt, int sourceStride, ushort[] reference, int referenceAt, int referenceStride, int w, int h)
    {
        Span<int> residual = stackalloc int[Cell * Cell];
        for (int y = 0; y < Cell; y++)
        {
            for (int x = 0; x < Cell; x++)
            {
                residual[(y * Cell) + x] = y < h && x < w ? source[sourceAt + (y * sourceStride) + x] - reference[referenceAt + (y * referenceStride) + x] : 0;
            }
        }

        return Hadamard(residual);
    }

    /// <summary>Gets the sum of the absolute 8x8 Hadamard transforms of a 16x16 residual's four quarters.</summary>
    private static double Hadamard(Span<int> residual)
    {
        long total = 0;
        Span<int> m = stackalloc int[64];
        for (int quarter = 0; quarter < 4; quarter++)
        {
            int ox = (quarter & 1) * 8, oy = (quarter >> 1) * 8;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    m[(y * 8) + x] = residual[((oy + y) * Cell) + ox + x];
                }
            }

            for (int y = 0; y < 8; y++)
            {
                Butterfly(m.Slice(y * 8, 8), 1);
            }

            for (int x = 0; x < 8; x++)
            {
                Butterfly(m[x..], 8);
            }

            for (int i = 0; i < 64; i++)
            {
                total += Math.Abs(m[i]);
            }
        }

        return total / 8.0;

        static void Butterfly(Span<int> v, int step)
        {
            for (int half = 1; half < 8; half <<= 1)
            {
                for (int i = 0; i < 8; i += 2 * half)
                {
                    for (int j = i; j < i + half; j++)
                    {
                        int a = v[j * step], b = v[(j + half) * step];
                        v[j * step] = a + b;
                        v[(j + half) * step] = a - b;
                    }
                }
            }
        }
    }
}
