using System.Runtime.CompilerServices;

namespace Egami.Formats.Jxl;

/// <summary>
/// Decodes Modular channels (H.3 to H.5): for each sample, the properties, the MA tree's leaf, its predictor and the entropy
/// coded residual. Edge samples follow the spec's defaults (W from N on the left edge, N from W on the top row, and so on).
/// </summary>
internal static class JxlModularDecoder
{
    /// <summary>Decodes channels [<paramref name="first"/>, <paramref name="end"/>) of <paramref name="channels"/> as one stream.</summary>
    internal static void DecodeChannels(JxlBitReader r, JxlTree tree, JxlWpParams wp, List<JxlChannel> channels, int first, int end, int stream)
    {
        if (first >= end)
        {
            return;
        }

        uint multiplier = 0;
        for (int i = first; i < end; i++)
        {
            multiplier = Math.Max(multiplier, (uint)channels[i].Width);
        }

        JxlEntropyDecoder decoder = tree.Decoder.Clone();
        decoder.Begin(r);
        int maxPrevious = tree.MaxProperty >= 16 ? ((tree.MaxProperty - 16) / 4) + 1 : 0;
        Span<int> properties = stackalloc int[16 + (4 * maxPrevious)];
        List<JxlChannel> previous = [];
        for (int i = first; i < end; i++)
        {
            JxlChannel c = channels[i];
            if (c.Width == 0 || c.Height == 0)
            {
                continue;
            }

            // Earlier channels of this stream with the same size and shifts, the most recent first, feed properties 16 and up.
            previous.Clear();
            for (int j = i - 1; j >= first && previous.Count < maxPrevious; j--)
            {
                if (channels[j].Width > 0 && channels[j].Height > 0 && channels[j].SameShape(c))
                {
                    previous.Add(channels[j]);
                }
            }

            int root = tree.Root(i - first, stream);
            if (tree.IsLeaf(root) && tree.Predictor(root) == 0)
            {
                DecodeZero(r, tree, decoder, root, c, multiplier);
                continue;
            }

            DecodeChannel(r, tree, decoder, root, wp, c, previous, properties, i - first, stream, multiplier);
        }

        decoder.Finish(r);
    }

    /// <summary>Predicts sample (x, y) with <paramref name="predictor"/> from its neighbours (H.5).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Predict(int predictor, int w, int n, int nw, int ne, int nn, int ww, int nee, long wpPrediction)
    {
        return predictor switch
        {
            0 => 0,
            1 => w,
            2 => n,
            3 => ((long)w + n) / 2,
            4 => Math.Abs((long)n - nw) < Math.Abs((long)w - nw) ? w : n,
            5 => Math.Clamp((long)n + w - nw, Math.Min(w, n), Math.Max(w, n)),
            6 => (wpPrediction + 3) >> 3,
            7 => ne,
            8 => nw,
            9 => ww,
            10 => ((long)w + nw) / 2,
            11 => ((long)n + nw) / 2,
            12 => ((long)n + ne) / 2,
            _ => ((6L * n) - (2L * nn) + (7L * w) + ww + nee + (3L * ne) + 8) / 16,
        };
    }

    private static void DecodeZero(JxlBitReader r, JxlTree tree, JxlEntropyDecoder decoder, int leaf, JxlChannel c, uint multiplier)
    {
        int cluster = tree.Cluster(leaf);
        int offset = tree.Offset(leaf);
        int mul = tree.Multiplier(leaf);
        long single = decoder.SingleToken(cluster);
        int[] data = c.Data;
        if (single >= 0)
        {
            Array.Fill(data, unchecked((JxlHeaders.UnpackSigned((uint)single) * mul) + offset));
            return;
        }

        for (int i = 0; i < data.Length; i++)
        {
            data[i] = unchecked((JxlHeaders.UnpackSigned(decoder.ReadUintClustered(r, cluster, multiplier)) * mul) + offset);
        }
    }

    private static void DecodeChannel(JxlBitReader r, JxlTree tree, JxlEntropyDecoder decoder, int root, JxlWpParams wp, JxlChannel c, List<JxlChannel> previous, Span<int> properties, int channel, int stream, uint multiplier)
    {
        int width = c.Width;
        int height = c.Height;
        int[] data = c.Data;
        JxlWeightedPredictor? weighted = tree.UsesWp ? new JxlWeightedPredictor(width, wp) : null;
        int[] property = tree.Properties;
        int[] value = tree.Values;
        int[] left = tree.Lefts;
        int[] right = tree.Rights;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int prevGrad = 0;
            bool inner = y > 1;
            for (int x = 0; x < width; x++)
            {
                int w, n, nw, ne, nn, ww, nee;
                if (inner && x > 1 && x + 2 < width)
                {
                    // Away from the edges every neighbour exists.
                    int up = row - width + x;
                    w = data[row + x - 1];
                    n = data[up];
                    nw = data[up - 1];
                    ne = data[up + 1];
                    nn = data[up - width];
                    ww = data[row + x - 2];
                    nee = data[up + 2];
                }
                else
                {
                    // Neighbours with the spec's edge rules.
                    w = x > 0 ? data[row + x - 1] : y > 0 ? data[row - width + x] : 0;
                    n = y > 0 ? data[row - width + x] : w;
                    nw = x > 0 && y > 0 ? data[row - width + x - 1] : w;
                    ne = x + 1 < width && y > 0 ? data[row - width + x + 1] : n;
                    nn = y > 1 ? data[row - (2 * width) + x] : n;
                    ww = x > 1 ? data[row + x - 2] : w;
                    nee = x + 2 < width && y > 0 ? data[row - width + x + 2] : ne;
                }

                long wpPrediction = weighted?.Predict(n, nw, ne, w, nn) ?? 0;
                int grad = unchecked(w + n - nw);

                // The tree is walked with each property worked out only when a node tests it.
                int leaf = root;
                while (true)
                {
                    int p = property[leaf];
                    if (p < 0)
                    {
                        break;
                    }

                    int v = p switch
                    {
                        0 => channel,
                        1 => stream,
                        2 => y,
                        3 => x,
                        4 => (int)(uint)Math.Abs((long)n),
                        5 => (int)(uint)Math.Abs((long)w),
                        6 => n,
                        7 => w,
                        8 => unchecked(w - prevGrad),
                        9 => grad,
                        10 => unchecked(w - nw),
                        11 => unchecked(nw - n),
                        12 => unchecked(n - ne),
                        13 => unchecked(n - nn),
                        14 => unchecked(w - ww),
                        15 => weighted?.MaxError ?? 0,
                        _ => PreviousProperty(previous, p, x, y),
                    };
                    leaf = v > value[leaf] ? left[leaf] : right[leaf];
                }

                prevGrad = grad;
                long prediction = Predict(tree.Predictor(leaf), w, n, nw, ne, nn, ww, nee, wpPrediction);
                int diff = unchecked((JxlHeaders.UnpackSigned(decoder.ReadUintClustered(r, tree.Cluster(leaf), multiplier)) * tree.Multiplier(leaf)) + tree.Offset(leaf));
                int sample = unchecked((int)prediction + diff);
                data[row + x] = sample;
                weighted?.Record(sample);
            }
        }
    }

    /// <summary>Gets property 16 or above: one of the four a previous channel gives, or 0 when there is no such channel.</summary>
    private static int PreviousProperty(List<JxlChannel> previous, int p, int x, int y)
    {
        int k = (p - 16) / 4;
        if (k >= previous.Count)
        {
            return 0;
        }

        Span<int> four = stackalloc int[4];
        PreviousProperties(previous[k], x, y, four);
        return four[(p - 16) % 4];
    }

    /// <summary>The four properties a previous channel gives: |c|, c, |c - g| and c - g, with g its clamped gradient.</summary>
    private static void PreviousProperties(JxlChannel p, int x, int y, Span<int> output)
    {
        int width = p.Width;
        int[] d = p.Data;
        int c = d[(y * width) + x];
        int g;
        if (x == 0 && y == 0)
        {
            g = 0;
        }
        else if (x == 0)
        {
            g = d[((y - 1) * width) + x];
        }
        else if (y == 0)
        {
            g = d[x - 1];
        }
        else
        {
            int n = d[((y - 1) * width) + x];
            int w = d[(y * width) + x - 1];
            int nw = d[((y - 1) * width) + x - 1];
            g = (int)Math.Clamp((long)n + w - nw, Math.Min(n, w), Math.Max(n, w));
        }

        output[0] = (int)(uint)Math.Abs((long)c);
        output[1] = c;
        output[2] = (int)(uint)Math.Abs((long)c - g);
        output[3] = unchecked(c - g);
    }
}
