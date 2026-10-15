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
        bool needProperties = !tree.IsLeaf(root);
        properties[0] = channel;
        properties[1] = stream;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int prevGrad = 0;
            for (int x = 0; x < width; x++)
            {
                // Neighbours with the spec's edge rules.
                int w = x > 0 ? data[row + x - 1] : y > 0 ? data[row - width + x] : 0;
                int n = y > 0 ? data[row - width + x] : w;
                int nw = x > 0 && y > 0 ? data[row - width + x - 1] : w;
                int ne = x + 1 < width && y > 0 ? data[row - width + x + 1] : n;
                int nn = y > 1 ? data[row - (2 * width) + x] : n;
                int ww = x > 1 ? data[row + x - 2] : w;
                int nee = x + 2 < width && y > 0 ? data[row - width + x + 2] : ne;
                long wpPrediction = weighted?.Predict(n, nw, ne, w, nn) ?? 0;
                int leaf = root;
                int grad = unchecked(w + n - nw);
                if (needProperties)
                {
                    properties[2] = y;
                    properties[3] = x;
                    properties[4] = (int)(uint)Math.Abs((long)n);
                    properties[5] = (int)(uint)Math.Abs((long)w);
                    properties[6] = n;
                    properties[7] = w;
                    properties[8] = unchecked(w - prevGrad);
                    properties[9] = grad;
                    properties[10] = unchecked(w - nw);
                    properties[11] = unchecked(nw - n);
                    properties[12] = unchecked(n - ne);
                    properties[13] = unchecked(n - nn);
                    properties[14] = unchecked(w - ww);
                    properties[15] = weighted?.MaxError ?? 0;
                    for (int k = 0; k < previous.Count; k++)
                    {
                        PreviousProperties(previous[k], x, y, properties.Slice(16 + (4 * k), 4));
                    }

                    for (int k = previous.Count; 16 + (4 * k) < properties.Length; k++)
                    {
                        properties.Slice(16 + (4 * k), 4).Clear();
                    }

                    leaf = tree.LeafFrom(root, properties);
                }

                prevGrad = grad;
                long prediction = Predict(tree.Predictor(leaf), w, n, nw, ne, nn, ww, nee, wpPrediction);
                int diff = unchecked((JxlHeaders.UnpackSigned(decoder.ReadUintClustered(r, tree.Cluster(leaf), multiplier)) * tree.Multiplier(leaf)) + tree.Offset(leaf));
                int value = unchecked((int)prediction + diff);
                data[row + x] = value;
                weighted?.Record(value);
            }
        }
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
