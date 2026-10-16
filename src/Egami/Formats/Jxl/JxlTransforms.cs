namespace Egami.Formats.Jxl;

/// <summary>The inverse Modular transforms (H.6), ported from jxl-oxide's jxl-modular crate.</summary>
internal static class JxlTransforms
{
    private static readonly short[] DeltaPalette =
    [
        0, 0, 0, 4, 4, 4, 11, 0, 0, 0, 0, -13, 0, -12, 0, -10, -10, -10,
        -18, -18, -18, -27, -27, -27, -18, -18, 0, 0, 0, -32, -32, 0, 0, -37, -37, -37,
        0, -32, -32, 24, 24, 45, 50, 50, 50, -45, -24, -24, -24, -45, -45, 0, -24, -24,
        -34, -34, 0, -24, 0, -24, -45, -45, -24, 64, 64, 64, -32, 0, -32, 0, -32, 0,
        -32, 0, 32, -24, -45, -24, 45, 24, 45, 24, -24, -45, -45, -24, 24, 80, 80, 80,
        64, 0, 0, 0, 0, -64, 0, -64, -64, -24, -24, 45, 96, 96, 96, 64, 64, 0,
        45, -24, -24, 34, -34, 0, 112, 112, 112, 24, -45, -45, 45, 45, -24, 0, -32, 32,
        24, -24, 45, 0, 96, 96, 45, -24, 24, 24, -45, -24, -24, -45, 24, 0, -64, 0,
        96, 0, 0, 128, 128, 128, 64, 0, 64, 144, 144, 144, 96, 96, 0, -36, -36, 36,
        45, -24, -45, 45, -45, -24, 0, 0, -96, 0, 128, 128, 0, 96, 0, 45, 24, -45,
        -128, 0, 0, 24, -45, 24, -45, 24, -45, 64, 0, -64, 64, -64, -64, 96, 0, 96,
        45, -45, 24, 24, 45, -45, 64, 64, -64, 128, 128, 0, 0, 0, -128, -24, 45, -45,
    ];

    /// <summary>Undoes a reversible color transform: one of seven lifting steps, then one of six channel orders.</summary>
    internal static void InverseRct(JxlRct rct, List<JxlChannel> channels)
    {
        int type = rct.Type % 7;
        int permutation = rct.Type / 7;
        int[] a = channels[rct.Begin].Data;
        int[] b = channels[rct.Begin + 1].Data;
        int[] c = channels[rct.Begin + 2].Data;
        for (int i = 0; i < a.Length; i++)
        {
            int va = a[i];
            int vb = b[i];
            int vc = c[i];
            int d, e, f;
            unchecked
            {
                if (type == 6)
                {
                    int tmp = va - (vc >> 1);
                    e = vc + tmp;
                    f = tmp - (vb >> 1);
                    d = f + vb;
                }
                else
                {
                    d = va;
                    f = (type & 1) != 0 ? vc + va : vc;
                    e = (type >> 1) switch { 1 => vb + va, 2 => vb + ((va + f) >> 1), _ => vb };
                }
            }

            a[i] = d;
            b[i] = e;
            c[i] = f;
        }

        JxlChannel x = channels[rct.Begin];
        JxlChannel y = channels[rct.Begin + 1];
        JxlChannel z = channels[rct.Begin + 2];
        (channels[rct.Begin], channels[rct.Begin + 1], channels[rct.Begin + 2]) = permutation switch
        {
            1 => (z, x, y),
            2 => (y, z, x),
            3 => (x, z, y),
            4 => (y, x, z),
            5 => (z, y, x),
            _ => (x, y, z),
        };
    }

    /// <summary>Undoes a palette: the meta channel 0 holds the colours; the index channel becomes the palette's channels.</summary>
    internal static void InversePalette(JxlPalette p, List<JxlChannel> channels, int bitDepth)
    {
        JxlChannel palette = channels[0];
        channels.RemoveAt(0);
        JxlChannel index = channels[p.Begin];
        int count = p.Count;
        JxlChannel[] outputs = new JxlChannel[count];
        for (int c = 0; c < count; c++)
        {
            outputs[c] = new JxlChannel(index.Width, index.Height, index.HShift, index.VShift);
        }

        int colours = p.Colours;
        int[] indices = index.Data;
        int[] pal = palette.Data;
        bool anyDelta = false;
        for (int i = 0; i < indices.Length; i++)
        {
            int v = indices[i];
            if (v < p.Deltas)
            {
                anyDelta = true;
            }

            if ((uint)v < (uint)colours)
            {
                for (int c = 0; c < count; c++)
                {
                    outputs[c].Data[i] = pal[(c * colours) + v];
                }
            }
            else if (v >= colours)
            {
                // The implicit cubes give the first three channels; a fourth and later are 0, as libjxl decodes them.
                int k = v - colours;
                if (k < 64)
                {
                    for (int c = 0; c < count; c++)
                    {
                        outputs[c].Data[i] = c >= 3 ? 0 : (((k >> (2 * c)) % 4) * ((1 << bitDepth) - 1) / 4) + (1 << Math.Max(bitDepth - 3, 0));
                    }
                }
                else
                {
                    k -= 64;
                    for (int c = 0; c < count; c++)
                    {
                        outputs[c].Data[i] = c >= 3 ? 0 : (k % 5) * ((1 << bitDepth) - 1) / 4;
                        k /= 5;
                    }
                }
            }
            else
            {
                int k = (-(v + 1)) % 143;
                for (int c = 0; c < count; c++)
                {
                    if (c >= 3)
                    {
                        outputs[c].Data[i] = 0;
                        continue;
                    }

                    int sample = DeltaPalette[(3 * ((k + 1) >> 1)) + c];
                    if ((k & 1) == 0)
                    {
                        sample = -sample;
                    }

                    if (bitDepth > 8)
                    {
                        sample <<= Math.Min(bitDepth, 24) - 8;
                    }

                    outputs[c].Data[i] = sample;
                }
            }
        }

        if (anyDelta)
        {
            // Samples with an index below the delta count are differences from the palette's own predictor.
            for (int c = 0; c < count; c++)
            {
                AddPredictions(outputs[c], indices, p);
            }
        }

        channels.RemoveAt(p.Begin);
        channels.InsertRange(p.Begin, outputs);
    }

    /// <summary>Undoes a squeeze, its steps last first: each pair of average and residual channels merges back.</summary>
    internal static void InverseSqueeze(JxlSqueeze squeeze, List<JxlChannel> channels, JxlBitReader r)
    {
        for (int s = squeeze.Steps.Count - 1; s >= 0; s--)
        {
            JxlSqueezeStep step = squeeze.Steps[s];
            int end = step.Begin + step.Count;
            int residualStart = step.InPlace ? end : channels.Count - step.Count;
            for (int i = 0; i < step.Count; i++)
            {
                JxlChannel average = channels[step.Begin + i];
                JxlChannel residual = channels[residualStart + i];
                channels[step.Begin + i] = step.Horizontal ? MergeHorizontal(average, residual, r) : MergeVertical(average, residual, r);
            }

            channels.RemoveRange(residualStart, step.Count);
        }
    }

    private static void AddPredictions(JxlChannel channel, int[] indices, JxlPalette p)
    {
        int width = channel.Width;
        int height = channel.Height;
        int[] data = channel.Data;
        JxlWeightedPredictor? weighted = p.Predictor == 6 ? new JxlWeightedPredictor(width, p.Wp) : null;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int w = x > 0 ? data[row + x - 1] : y > 0 ? data[row - width + x] : 0;
                int n = y > 0 ? data[row - width + x] : w;
                int nw = x > 0 && y > 0 ? data[row - width + x - 1] : w;
                int ne = x + 1 < width && y > 0 ? data[row - width + x + 1] : n;
                int nn = y > 1 ? data[row - (2 * width) + x] : n;
                int ww = x > 1 ? data[row + x - 2] : w;
                int nee = x + 2 < width && y > 0 ? data[row - width + x + 2] : ne;
                long wpPrediction = weighted?.Predict(n, nw, ne, w, nn) ?? 0;
                int value = data[row + x];
                if (indices[row + x] < p.Deltas)
                {
                    value = unchecked(value + (int)JxlModularDecoder.Predict(p.Predictor, w, n, nw, ne, nn, ww, nee, wpPrediction));
                    data[row + x] = value;
                }

                weighted?.Record(value);
            }
        }
    }

    private static JxlChannel MergeHorizontal(JxlChannel average, JxlChannel residual, JxlBitReader r)
    {
        int width = average.Width + residual.Width;
        if (average.Height != residual.Height || (average.Width != residual.Width && average.Width != residual.Width + 1))
        {
            throw r.Corrupt("A squeeze's average and residual channels do not fit together.");
        }

        JxlChannel output = new(width, average.Height, Unshift(average.HShift), average.VShift);
        for (int y = 0; y < average.Height; y++)
        {
            ReadOnlySpan<int> avg = average.Row(y);
            ReadOnlySpan<int> res = residual.Row(y);
            Span<int> o = output.Row(y);
            int a = avg[0];
            int left = a;
            for (int x = 0; x < res.Length; x++)
            {
                int next = x + 1 < avg.Length ? avg[x + 1] : a;
                int diff = unchecked(res[x] + Tendency(left, a, next));
                int first = unchecked(a + (diff / 2));
                int second = unchecked(first - diff);
                o[2 * x] = first;
                o[(2 * x) + 1] = second;
                a = next;
                left = second;
            }

            if ((width & 1) != 0)
            {
                o[width - 1] = avg[^1];
            }
        }

        return output;
    }

    private static JxlChannel MergeVertical(JxlChannel average, JxlChannel residual, JxlBitReader r)
    {
        int height = average.Height + residual.Height;
        if (average.Width != residual.Width || (average.Height != residual.Height && average.Height != residual.Height + 1))
        {
            throw r.Corrupt("A squeeze's average and residual channels do not fit together.");
        }

        int width = average.Width;
        JxlChannel output = new(width, height, average.HShift, Unshift(average.VShift));
        int[] avg = average.Data;
        int[] res = residual.Data;
        int[] o = output.Data;
        for (int x = 0; x < width; x++)
        {
            int a = avg[x];
            int top = a;
            for (int y = 0; y < residual.Height; y++)
            {
                int next = y + 1 < average.Height ? avg[((y + 1) * width) + x] : a;
                int diff = unchecked(res[(y * width) + x] + Tendency(top, a, next));
                int first = unchecked(a + (diff / 2));
                int second = unchecked(first - diff);
                o[(2 * y * width) + x] = first;
                o[(((2 * y) + 1) * width) + x] = second;
                a = next;
                top = second;
            }

            if ((height & 1) != 0)
            {
                o[((height - 1) * width) + x] = avg[((average.Height - 1) * width) + x];
            }
        }

        return output;
    }

    private static int Unshift(int shift) => shift > 0 ? shift - 1 : shift;

    /// <summary>The smooth tendency of three averages (H.6.2.2).</summary>
    private static int Tendency(int a, int b, int c)
    {
        unchecked
        {
            if (a >= b && b >= c)
            {
                int x = ((4 * a) - (3 * c) - b + 6) / 12;
                if (x - (x & 1) > 2 * (a - b))
                {
                    x = (2 * (a - b)) + 1;
                }

                if (x + (x & 1) > 2 * (b - c))
                {
                    x = 2 * (b - c);
                }

                return x;
            }

            if (a <= b && b <= c)
            {
                int x = ((4 * a) - (3 * c) - b - 6) / 12;
                if (x + (x & 1) < 2 * (a - b))
                {
                    x = (2 * (a - b)) - 1;
                }

                if (x - (x & 1) < 2 * (b - c))
                {
                    x = 2 * (b - c);
                }

                return x;
            }

            return 0;
        }
    }
}
