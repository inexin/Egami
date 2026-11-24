using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

            DecodeChannel(r, tree, decoder, root, wp, c, previous, i - first, stream, multiplier);
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
            5 => Gradient(w, n, nw),
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

    private static void DecodeChannel(JxlBitReader r, JxlTree tree, JxlEntropyDecoder decoder, int root, JxlWpParams wp, JxlChannel c, List<JxlChannel> previous, int channel, int stream, uint multiplier)
    {
        // The residual reader is a type argument, so each loop is compiled without the per-sample LZ77 and coding checks.
        JxlChannelPlan plan = tree.Plan(root);
        if (decoder.UsesLz77)
        {
            Decode(r, new JxlLz77UintReader(decoder, multiplier), tree, plan, root, wp, c, previous, channel, stream);
        }
        else if (decoder.IsAns)
        {
            Decode(r, new JxlAnsUintReader(decoder), tree, plan, root, wp, c, previous, channel, stream);
        }
        else
        {
            Decode(r, new JxlPrefixUintReader(decoder), tree, plan, root, wp, c, previous, channel, stream);
        }
    }

    private static void Decode<TResiduals>(JxlBitReader r, TResiduals residuals, JxlTree tree, JxlChannelPlan plan, int root, JxlWpParams wp, JxlChannel c, List<JxlChannel> previous, int channel, int stream)
        where TResiduals : struct, IJxlUintReader
    {
        if (plan.UsesWp)
        {
            // The weighted predictor runs for every sample, so its state stays in locals of one loop per subtree shape.
            DecodeWeighted(r, residuals, tree, plan, root, new JxlWeightedPredictor(c.Width, wp), c, previous, channel, stream);
            return;
        }

        switch (plan.Kind)
        {
            case JxlChannelPlanKind.Single when plan.Leaf.Predictor == 5:
                DecodeGradient(r, residuals, plan.Leaf, c);
                break;
            case JxlChannelPlanKind.Single:
                DecodeSingle(r, residuals, plan.Leaf, c);
                break;
            case JxlChannelPlanKind.Lookup when plan.Predictor == 5:
                DecodeLookup<TResiduals, JxlGradientPredictor>(r, residuals, tree.Leaves, plan, c, channel, stream);
                break;
            case JxlChannelPlanKind.Lookup:
                DecodeLookup<TResiduals, JxlAnyPredictor>(r, residuals, tree.Leaves, plan, c, channel, stream);
                break;
            default:
                DecodeWalk(r, residuals, tree, plan, c, previous, channel, stream);
                break;
        }
    }

    /// <summary>Decodes a channel whose subtree uses the weighted predictor, through its fused loop.</summary>
    private static void DecodeWeighted<TResiduals>(JxlBitReader r, TResiduals residuals, JxlTree tree, JxlChannelPlan plan, int root, JxlWeightedPredictor weighted, JxlChannel c, List<JxlChannel> previous, int channel, int stream)
        where TResiduals : struct, IJxlUintReader
    {
        JxlLeaf[] leaves = tree.Leaves;
        ref int data = ref Samples(c);
        int width = c.Width;
        int height = c.Height;
        switch (plan.Kind)
        {
            case JxlChannelPlanKind.Single:
                // Only a weighted-predictor leaf makes a single leaf use the predictor.
                weighted.Decode<TResiduals, JxlSinglePicker, JxlWpPredictor>(r, residuals, new JxlSinglePicker(root), leaves, ref data, width, height);
                break;
            case JxlChannelPlanKind.Lookup when plan.Property == 15:
                // The leaf picked by the predictor's own largest error, the usual LF coefficient tree.
                JxlMaxErrorPicker byError = new(plan.Lookup!, plan.Min, plan.Max);
                if (plan.Predictor == 6)
                {
                    weighted.Decode<TResiduals, JxlMaxErrorPicker, JxlWpPredictor>(r, residuals, byError, leaves, ref data, width, height);
                }
                else
                {
                    weighted.Decode<TResiduals, JxlMaxErrorPicker, JxlAnyPredictor>(r, residuals, byError, leaves, ref data, width, height);
                }

                break;
            case JxlChannelPlanKind.Lookup:
                weighted.Decode<TResiduals, JxlLookupPicker, JxlAnyPredictor>(r, residuals, new JxlLookupPicker(plan.Lookup!, plan.Property, plan.Min, plan.Max, channel, stream), leaves, ref data, width, height);
                break;
            default:
                JxlWalkPicker walk = new(plan.Flat!, tree.MaxProperty, plan.UsesPrevious ? previous : null, channel, stream);
                switch (plan.Predictor)
                {
                    case 5:
                        weighted.Decode<TResiduals, JxlWalkPicker, JxlGradientPredictor>(r, residuals, walk, leaves, ref data, width, height);
                        break;
                    case 6:
                        weighted.Decode<TResiduals, JxlWalkPicker, JxlWpPredictor>(r, residuals, walk, leaves, ref data, width, height);
                        break;
                    default:
                        weighted.Decode<TResiduals, JxlWalkPicker, JxlAnyPredictor>(r, residuals, walk, leaves, ref data, width, height);
                        break;
                }

                break;
        }
    }

    /// <summary>Decodes a channel whose one leaf predicts with the clamped gradient, the usual lossless leaf.</summary>
    private static void DecodeGradient<TResiduals>(JxlBitReader r, TResiduals residuals, JxlLeaf leaf, JxlChannel c)
        where TResiduals : struct, IJxlUintReader
    {
        int width = c.Width;
        int height = c.Height;
        ref int data = ref Samples(c);

        // The first row predicts W (0 at its start); later rows start from N, and W is the last sample.
        int w = 0;
        for (int x = 0; x < width; x++)
        {
            w = unchecked(w + Residual(r, residuals, leaf));
            Unsafe.Add(ref data, x) = w;
        }

        for (int y = 1; y < height; y++)
        {
            ref int row = ref Unsafe.Add(ref data, y * width);
            ref int up = ref Unsafe.Add(ref row, -width);
            w = unchecked(up + Residual(r, residuals, leaf));
            row = w;
            for (int x = 1; x < width; x++)
            {
                w = unchecked((int)Gradient(w, Unsafe.Add(ref up, x), Unsafe.Add(ref up, x - 1)) + Residual(r, residuals, leaf));
                Unsafe.Add(ref row, x) = w;
            }
        }
    }

    /// <summary>Decodes a channel with one leaf and any predictor but the weighted one.</summary>
    private static void DecodeSingle<TResiduals>(JxlBitReader r, TResiduals residuals, JxlLeaf leaf, JxlChannel c)
        where TResiduals : struct, IJxlUintReader
    {
        int width = c.Width;
        int height = c.Height;
        ref int data = ref Samples(c);
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                Neighbours(ref data, row, width, x, y, out int w, out int n, out int nw, out int ne, out int nn, out int ww, out int nee);
                long prediction = Predict(leaf.Predictor, w, n, nw, ne, nn, ww, nee, 0);
                Unsafe.Add(ref data, row + x) = unchecked((int)prediction + Residual(r, residuals, leaf));
            }
        }
    }

    /// <summary>Decodes a channel whose subtree tests one property (not the weighted predictor's), its leaf found in a table.</summary>
    private static void DecodeLookup<TResiduals, TPredictor>(JxlBitReader r, TResiduals residuals, JxlLeaf[] leaves, JxlChannelPlan plan, JxlChannel c, int channel, int stream)
        where TResiduals : struct, IJxlUintReader
        where TPredictor : struct, IJxlPredictor
    {
        int width = c.Width;
        int height = c.Height;
        ref int data = ref Samples(c);
        int[] lookup = plan.Lookup!;
        int property = plan.Property;
        int min = plan.Min;
        int max = plan.Max;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int prevGrad = 0;
            for (int x = 0; x < width; x++)
            {
                Neighbours(ref data, row, width, x, y, out int w, out int n, out int nw, out int ne, out int nn, out int ww, out int nee);
                int grad = unchecked(w + n - nw);
                int v = Property(property, channel, stream, x, y, w, n, nw, ne, nn, ww, prevGrad, grad, 0);
                JxlLeaf leaf = leaves[lookup[Math.Min(Math.Max(v, min), max) - min]];
                prevGrad = grad;
                long prediction = default(TPredictor).Predict(leaf.Predictor, w, n, nw, ne, nn, ww, nee, 0);
                Unsafe.Add(ref data, row + x) = unchecked((int)prediction + Residual(r, residuals, leaf));
            }
        }
    }

    /// <summary>
    /// Decodes a channel by walking its flattened subtree for every sample, with every property worked out first; the subtree
    /// does not use the weighted predictor, so property 15 is 0.
    /// </summary>
    private static void DecodeWalk<TResiduals>(JxlBitReader r, TResiduals residuals, JxlTree tree, JxlChannelPlan plan, JxlChannel c, List<JxlChannel> previous, int channel, int stream)
        where TResiduals : struct, IJxlUintReader
    {
        int width = c.Width;
        int height = c.Height;
        ref int data = ref Samples(c);
        JxlFlatNode[] flat = plan.Flat!;
        JxlLeaf[] leaves = tree.Leaves;
        bool usesPrevious = plan.UsesPrevious;
        int maxPrevious = tree.MaxProperty >= 16 ? ((tree.MaxProperty - 16) / 4) + 1 : 0;

        // Properties of channels this stream does not have stay 0.
        Span<int> props = stackalloc int[16 + (4 * maxPrevious)];
        props.Clear();
        props[0] = channel;
        props[1] = stream;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int prevGrad = 0;
            props[2] = y;
            for (int x = 0; x < width; x++)
            {
                Neighbours(ref data, row, width, x, y, out int w, out int n, out int nw, out int ne, out int nn, out int ww, out int nee);
                int grad = unchecked(w + n - nw);
                props[3] = x;
                props[4] = (int)(uint)Math.Abs((long)n);
                props[5] = (int)(uint)Math.Abs((long)w);
                props[6] = n;
                props[7] = w;
                props[8] = unchecked(w - prevGrad);
                props[9] = grad;
                props[10] = unchecked(w - nw);
                props[11] = unchecked(nw - n);
                props[12] = unchecked(n - ne);
                props[13] = unchecked(n - nn);
                props[14] = unchecked(w - ww);
                if (usesPrevious)
                {
                    for (int k = 0; k < previous.Count; k++)
                    {
                        PreviousProperties(previous[k], x, y, props.Slice(16 + (4 * k), 4));
                    }
                }

                int at = 0;
                while (true)
                {
                    JxlFlatNode f = flat[at];
                    if (f.Leaf >= 0)
                    {
                        at = f.Leaf;
                        break;
                    }

                    int inLeft = props[f.LeftProperty] > f.LeftValue ? 0 : 1;
                    int inRight = props[f.RightProperty] > f.RightValue ? 2 : 3;
                    at = f.Child + (props[f.Property] > f.Value ? inLeft : inRight);
                }

                prevGrad = grad;
                JxlLeaf leaf = leaves[at];
                long prediction = Predict(leaf.Predictor, w, n, nw, ne, nn, ww, nee, 0);
                Unsafe.Add(ref data, row + x) = unchecked((int)prediction + Residual(r, residuals, leaf));
            }
        }
    }

    /// <summary>Gets predictor 5: W + N - NW clamped between W and N.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Gradient(int w, int n, int nw) => Math.Min(Math.Max((long)n + w - nw, Math.Min(w, n)), Math.Max(w, n));

    /// <summary>Gets a reference to a channel's first sample, after checking it holds width × height samples.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref int Samples(JxlChannel c) => ref MemoryMarshal.GetReference(c.Data.AsSpan(0, c.Width * c.Height));

    /// <summary>Gets sample (x, y)'s neighbours, with the spec's edge rules near the edges.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Neighbours(ref int data, int row, int width, int x, int y, out int w, out int n, out int nw, out int ne, out int nn, out int ww, out int nee)
    {
        if (y > 1 && x > 1 && x + 2 < width)
        {
            // Away from the edges every neighbour exists.
            int up = row - width + x;
            w = Unsafe.Add(ref data, row + x - 1);
            n = Unsafe.Add(ref data, up);
            nw = Unsafe.Add(ref data, up - 1);
            ne = Unsafe.Add(ref data, up + 1);
            nn = Unsafe.Add(ref data, up - width);
            ww = Unsafe.Add(ref data, row + x - 2);
            nee = Unsafe.Add(ref data, up + 2);
        }
        else
        {
            w = x > 0 ? Unsafe.Add(ref data, row + x - 1) : y > 0 ? Unsafe.Add(ref data, row - width + x) : 0;
            n = y > 0 ? Unsafe.Add(ref data, row - width + x) : w;
            nw = x > 0 && y > 0 ? Unsafe.Add(ref data, row - width + x - 1) : w;
            ne = x + 1 < width && y > 0 ? Unsafe.Add(ref data, row - width + x + 1) : n;
            nn = y > 1 ? Unsafe.Add(ref data, row - (2 * width) + x) : n;
            ww = x > 1 ? Unsafe.Add(ref data, row + x - 2) : w;
            nee = x + 2 < width && y > 0 ? Unsafe.Add(ref data, row - width + x + 2) : ne;
        }
    }

    /// <summary>Gets property <paramref name="p"/>, below 16, of the current sample (H.4.1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Property(int p, int channel, int stream, int x, int y, int w, int n, int nw, int ne, int nn, int ww, int prevGrad, int grad, int maxError)
    {
        return p switch
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
            _ => maxError,
        };
    }

    /// <summary>Decodes the next residual in the leaf's context, scaled and offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Residual<TResiduals>(JxlBitReader r, TResiduals residuals, JxlLeaf leaf)
        where TResiduals : struct, IJxlUintReader
    {
        return unchecked((JxlHeaders.UnpackSigned(residuals.Read(r, leaf.Cluster)) * leaf.Multiplier) + leaf.Offset);
    }

    /// <summary>The four properties a previous channel gives: |c|, c, |c - g| and c - g, with g its clamped gradient.</summary>
    internal static void PreviousProperties(JxlChannel p, int x, int y, Span<int> output)
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

/// <summary>A sample predictor (H.5) fixed at compile time, so hot loops need no switch when every leaf uses one.</summary>
internal interface IJxlPredictor
{
    long Predict(int predictor, int w, int n, int nw, int ne, int nn, int ww, int nee, long wpPrediction);
}

/// <summary>Any predictor, chosen per leaf.</summary>
internal readonly struct JxlAnyPredictor : IJxlPredictor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Predict(int predictor, int w, int n, int nw, int ne, int nn, int ww, int nee, long wpPrediction) => JxlModularDecoder.Predict(predictor, w, n, nw, ne, nn, ww, nee, wpPrediction);
}

/// <summary>Predictor 5, the clamped gradient, for subtrees whose leaves all use it.</summary>
internal readonly struct JxlGradientPredictor : IJxlPredictor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Predict(int predictor, int w, int n, int nw, int ne, int nn, int ww, int nee, long wpPrediction) => JxlModularDecoder.Gradient(w, n, nw);
}

/// <summary>Predictor 6, the weighted predictor, for subtrees whose leaves all use it.</summary>
internal readonly struct JxlWpPredictor : IJxlPredictor
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Predict(int predictor, int w, int n, int nw, int ne, int nn, int ww, int nee, long wpPrediction) => (wpPrediction + 3) >> 3;
}

/// <summary>Picks a sample's leaf from its neighbours and properties, fixed at compile time for each subtree shape.</summary>
internal interface IJxlLeafPicker
{
    int Pick(int x, int y, int w, int n, int nw, int ne, int nn, int ww, int prevGrad, int grad, int maxError);
}

/// <summary>A subtree that is one leaf.</summary>
internal readonly struct JxlSinglePicker(int leaf) : IJxlLeafPicker
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Pick(int x, int y, int w, int n, int nw, int ne, int nn, int ww, int prevGrad, int grad, int maxError) => leaf;
}

/// <summary>A subtree that tests only the weighted predictor's largest error (property 15), through a table.</summary>
internal readonly struct JxlMaxErrorPicker(int[] lookup, int min, int max) : IJxlLeafPicker
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Pick(int x, int y, int w, int n, int nw, int ne, int nn, int ww, int prevGrad, int grad, int maxError) => lookup[Math.Min(Math.Max(maxError, min), max) - min];
}

/// <summary>A subtree that tests one property below 16, through a table.</summary>
internal readonly struct JxlLookupPicker(int[] lookup, int property, int min, int max, int channel, int stream) : IJxlLeafPicker
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Pick(int x, int y, int w, int n, int nw, int ne, int nn, int ww, int prevGrad, int grad, int maxError)
    {
        int v = JxlModularDecoder.Property(property, channel, stream, x, y, w, n, nw, ne, nn, ww, prevGrad, grad, maxError);
        return lookup[Math.Min(Math.Max(v, min), max) - min];
    }
}

/// <summary>Any other subtree, walked two levels a step over every property of the sample.</summary>
internal readonly struct JxlWalkPicker : IJxlLeafPicker
{
    private readonly JxlFlatNode[] _flat;
    private readonly int[] _props;
    private readonly List<JxlChannel>? _previous;

    /// <summary>Walks <paramref name="flat"/>; <paramref name="previous"/> is null when the subtree tests no previous channel.</summary>
    internal JxlWalkPicker(JxlFlatNode[] flat, int maxProperty, List<JxlChannel>? previous, int channel, int stream)
    {
        // Every property the tree tests has a slot; those of channels this stream does not have stay 0.
        int maxPrevious = maxProperty >= 16 ? ((maxProperty - 16) / 4) + 1 : 0;
        _flat = flat;
        _props = new int[16 + (4 * maxPrevious)];
        _props[0] = channel;
        _props[1] = stream;
        _previous = previous;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Pick(int x, int y, int w, int n, int nw, int ne, int nn, int ww, int prevGrad, int grad, int maxError)
    {
        int[] props = _props;
        ref int p = ref MemoryMarshal.GetArrayDataReference(props);
        Unsafe.Add(ref p, 2) = y;
        Unsafe.Add(ref p, 3) = x;
        Unsafe.Add(ref p, 4) = (int)(uint)Math.Abs((long)n);
        Unsafe.Add(ref p, 5) = (int)(uint)Math.Abs((long)w);
        Unsafe.Add(ref p, 6) = n;
        Unsafe.Add(ref p, 7) = w;
        Unsafe.Add(ref p, 8) = unchecked(w - prevGrad);
        Unsafe.Add(ref p, 9) = grad;
        Unsafe.Add(ref p, 10) = unchecked(w - nw);
        Unsafe.Add(ref p, 11) = unchecked(nw - n);
        Unsafe.Add(ref p, 12) = unchecked(n - ne);
        Unsafe.Add(ref p, 13) = unchecked(n - nn);
        Unsafe.Add(ref p, 14) = unchecked(w - ww);
        Unsafe.Add(ref p, 15) = maxError;
        if (_previous is { } previous)
        {
            for (int k = 0; k < previous.Count; k++)
            {
                JxlModularDecoder.PreviousProperties(previous[k], x, y, props.AsSpan(16 + (4 * k), 4));
            }
        }

        // The flattened subtree's properties are all below the table's length (the tree's largest property sizes it), and its
        // children and leaves are indices Flatten made.
        ref JxlFlatNode flat = ref MemoryMarshal.GetArrayDataReference(_flat);
        int at = 0;
        while (true)
        {
            ref JxlFlatNode f = ref Unsafe.Add(ref flat, at);
            if (f.Leaf >= 0)
            {
                return f.Leaf;
            }

            int inLeft = Unsafe.Add(ref p, f.LeftProperty) > f.LeftValue ? 0 : 1;
            int inRight = Unsafe.Add(ref p, f.RightProperty) > f.RightValue ? 2 : 3;
            at = f.Child + (Unsafe.Add(ref p, f.Property) > f.Value ? inLeft : inRight);
        }
    }
}
