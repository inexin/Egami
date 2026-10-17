namespace Egami.Formats.Jxl;

/// <summary>A Modular image being encoded: its channels after the transforms so far, meta channels first.</summary>
internal sealed class JxlEncodeImage(int bitDepth)
{
    internal List<JxlChannel> Channels { get; } = [];

    internal int Meta { get; set; }

    internal List<JxlTransform> Transforms { get; } = [];

    internal int BitDepth { get; } = bitDepth;

    /// <summary>Gets the number of channels a stream codes: meta channels, then channels up to the first larger than a group.</summary>
    internal int CodedPrefix(int maxSize)
    {
        int n = 0;
        while (n < Channels.Count && (n < Meta || (Channels[n].Width <= maxSize && Channels[n].Height <= maxSize)))
        {
            n++;
        }

        return n;
    }
}

/// <summary>
/// The lossless Modular encoder (Annex H, inverted): global and per-group palettes and color transforms, an MA tree learned
/// from samples of every stream or a fixed one, and the residuals of each stream entropy coded with one set of distributions.
/// The choices at each effort follow libjxl 0.12.0's enc_modular.cc and enc_encoding.cc (see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed class JxlModularEncoder
{
    private static readonly int[] PropertyOrder = [0, 1, 15, 9, 10, 11, 12, 13, 14, 2, 3, 4, 5, 6, 7, 8];

    private static readonly int[] FixedCutoffs =
        [-500, -392, -255, -191, -127, -95, -63, -47, -31, -23, -15, -11, -7, -4, -3, -1, 0, 1, 3, 5, 7, 11, 15, 23, 31, 47, 63, 95, 127, 191, 255, 392, 500];

    private readonly int _effort;
    private readonly int _width;
    private readonly int _height;
    private readonly int _groupDim;
    private readonly int _groupsX;
    private readonly int _groups;
    private readonly int _lfGroups;
    private readonly JxlEncodeImage[] _streams;
    private readonly bool _color;
    private readonly ParallelOptions _parallel;
    private float[]? _lfDequant;

    private JxlModularEncoder(int effort, int width, int height, int groupShift, int streams, bool color, int parallelism)
    {
        _effort = effort;
        _parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism) };
        _width = width;
        _height = height;
        _groupDim = 128 << groupShift;
        _groupsX = Ceil(width, _groupDim);
        _groups = _groupsX * Ceil(height, _groupDim);
        int lfDim = _groupDim * 8;
        _lfGroups = Ceil(width, lfDim) * Ceil(height, lfDim);
        _streams = new JxlEncodeImage[streams];
        _color = color;
    }

    /// <summary>Gets the group size shift libjxl picks for a lossless frame (GetGroupSizeShift).</summary>
    internal static int GroupShift(int width, int height) => width <= 128 && height <= 128 ? 0 : width <= 256 && height <= 256 ? 1 : width <= 400 && height <= 400 ? 2 : 1;

    /// <summary>
    /// Encodes a frame's channels (one or three color channels, then the extra channels, all <paramref name="width"/> by
    /// <paramref name="height"/> integers of <paramref name="bitDepth"/> bits) and gives its sections in TOC order; a Modular XYB
    /// frame names its LF dequantization multipliers (times 128) in <paramref name="lfDequant"/>.
    /// </summary>
    internal static List<byte[]> Encode(List<JxlChannel> channels, int colorChannels, int bitDepth, int effort, int width, int height, int groupShift, int parallelism, float[]? lfDequant = null)
    {
        int groupDim = 128 << groupShift;
        int groups = Ceil(width, groupDim) * Ceil(height, groupDim);
        int lfDim = groupDim * 8;
        int lfGroups = Ceil(width, lfDim) * Ceil(height, lfDim);
        int streams = 1 + (3 * lfGroups) + 17 + groups;
        JxlModularEncoder e = new(effort, width, height, groupShift, streams, colorChannels == 3, parallelism);
        e._lfDequant = lfDequant;
        return e.Run(channels, bitDepth);
    }

    private static int Ceil(int a, int b) => (a + b - 1) / b;

    private int GroupStream(int g) => 1 + (3 * _lfGroups) + 17 + g;

    private List<byte[]> Run(List<JxlChannel> channels, int bitDepth)
    {
        // Stream 0 holds the whole image: global palettes and, at the lower efforts, YCoCg.
        JxlEncodeImage gi = new(bitDepth);
        gi.Channels.AddRange(channels);
        int maxBitDepth = bitDepth + 1;
        float channelColors = _effort >= 2 || bitDepth > 8 ? 95 : 0;
        TryPalettes(gi, channelColors);
        if (_effort <= 4 && _color && gi.Channels.Count - gi.Meta >= 3 && maxBitDepth + 1 < 32)
        {
            ForwardRct(gi, gi.Meta, 6);
        }

        for (int i = 0; i < _streams.Length; i++)
        {
            _streams[i] = new JxlEncodeImage(bitDepth);
        }

        _streams[0] = gi;
        if (_groups == 1)
        {
            SearchRct(gi);
        }
        else
        {
            int prefixBefore = gi.CodedPrefix(_groupDim);
            Parallel.For(0, _groups, _parallel, g => PrepareGroup(gi, g, prefixBefore));
        }

        // The channels the groups now hold leave the global stream, which keeps their sizes for the prefix test.
        int prefix = gi.CodedPrefix(_groupDim);
        for (int c = prefix; c < gi.Channels.Count; c++)
        {
            gi.Channels[c].Data = [];
        }

        List<JxlTreeNode> tree = BuildTree();
        List<List<JxlToken>> treeTokens = [[]];
        (int[] property, int[] value, int[] left, int[] right, int[] predictor, int[] context) = TokenizeTree(tree, treeTokens[0]);
        bool usesWp = property.Any(p => p == 15) || property.Select((p, i) => p < 0 && predictor[i] == 6).Any(b => b);

        List<(int Stream, int Channel)> units = Units();
        List<JxlToken>[] parts = new List<JxlToken>[units.Count];
        Parallel.For(0, units.Count, _parallel, u =>
        {
            (int st, int ch) = units[u];
            parts[u] = Tokenize(_streams[st].Channels[ch], ch, st, property, value, left, right, predictor, context, usesWp);
        });

        List<List<JxlToken>> tokens = [.. _streams.Select(_ => new List<JxlToken>())];
        int[] widths = new int[_streams.Length];
        for (int u = 0; u < units.Count; u++)
        {
            (int st, int ch) = units[u];
            tokens[st].AddRange(parts[u]);
            widths[st] = Math.Max(widths[st], _streams[st].Channels[ch].Width);
        }

        JxlHistogramParams p = JxlHistogramParams.ForModular(_effort);

        // LF global: the LF dequantization, the global tree and the data distributions, then the global stream.
        JxlBitWriter global = new();
        global.WriteBool(_lfDequant is null);
        foreach (float m in _lfDequant ?? [])
        {
            global.WriteF16(m);
        }

        global.WriteBool(true);
        JxlEntropyCode treeCode = JxlEntropyCode.Write(global, p, 6, treeTokens);
        treeCode.WriteTokens(global, treeTokens[0]);
        JxlEntropyCode code = JxlEntropyCode.Write(global, p with { ImageWidths = widths }, context.Max() + 1, tokens);
        WriteStream(global, _streams[0], code, tokens[0]);

        if (_groups == 1)
        {
            return [global.ToArray()];
        }

        List<byte[]> sections = [global.ToArray()];
        for (int i = 0; i < _lfGroups; i++)
        {
            sections.Add([]);
        }

        sections.Add([]);
        byte[][] groupSections = new byte[_groups][];
        Parallel.For(0, _groups, _parallel, g =>
        {
            int s = GroupStream(g);
            JxlBitWriter w = new();
            WriteStream(w, _streams[s], code, tokens[s]);
            groupSections[g] = w.ToArray();
        });
        sections.AddRange(groupSections);

        return sections;
    }

    /// <summary>Writes a stream: its header (global tree, default WP parameters, transforms) and tokens, or nothing when it has no channels.</summary>
    private static void WriteStream(JxlBitWriter w, JxlEncodeImage image, JxlEntropyCode code, List<JxlToken> tokens)
    {
        if (image.Channels.Count == 0)
        {
            return;
        }

        w.WriteBool(true); // use_global_tree
        w.WriteBool(true); // default WP parameters
        w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 4, 18, 8), (uint)image.Transforms.Count);
        JxlU32 beginC = new(0, 3, 8, 6, 72, 10, 1096, 13);
        foreach (JxlTransform t in image.Transforms)
        {
            switch (t)
            {
                case JxlRct rct:
                    w.Write(0, 2);
                    w.WriteU32(beginC, (uint)rct.Begin);
                    w.WriteU32(new JxlU32(6, 0, 0, 2, 2, 4, 10, 6), (uint)rct.Type);
                    break;
                case JxlPalette palette:
                    w.Write(1, 2);
                    w.WriteU32(beginC, (uint)palette.Begin);
                    w.WriteU32(new JxlU32(1, 0, 3, 0, 4, 0, 1, 13), (uint)palette.Count);
                    w.WriteU32(new JxlU32(0, 8, 256, 10, 1280, 12, 5376, 16), (uint)palette.Colours);
                    w.WriteU32(new JxlU32(0, 0, 1, 8, 257, 10, 1281, 16), (uint)palette.Deltas);
                    w.Write((ulong)palette.Predictor, 4);
                    break;
            }
        }

        // The ANS state follows even when every channel is left to the groups (as libjxl writes it): libjxl's decoder skips
        // a stream with nothing to code, but jxl-oxide reads the state of every stream that has channels.
        code.WriteTokens(w, tokens);
    }

    /// <summary>Copies a group's part of every channel larger than a group into its stream, then tries local palettes and RCTs.</summary>
    private void PrepareGroup(JxlEncodeImage full, int g, int prefix)
    {
        JxlEncodeImage gi = new(full.BitDepth);
        int x0 = g % _groupsX * _groupDim;
        int y0 = g / _groupsX * _groupDim;
        for (int c = prefix; c < full.Channels.Count; c++)
        {
            JxlChannel fc = full.Channels[c];
            int cx = x0 >> fc.HShift;
            int cy = y0 >> fc.VShift;
            int w = Math.Min(_groupDim >> fc.HShift, fc.Width - cx);
            int h = Math.Min(_groupDim >> fc.VShift, fc.Height - cy);
            if (w <= 0 || h <= 0)
            {
                continue;
            }

            JxlChannel gc = new(w, h, fc.HShift, fc.VShift);
            for (int y = 0; y < h; y++)
            {
                Array.Copy(fc.Data, ((cy + y) * fc.Width) + cx, gc.Data, y * w, w);
            }

            gi.Channels.Add(gc);
        }

        _streams[GroupStream(g)] = gi;
        if (gi.Channels.Count == 0)
        {
            return;
        }

        if (_effort >= 5)
        {
            TryPalettes(gi, 80);
        }

        SearchRct(gi);
    }

    /// <summary>At effort 5 and up, keeps whichever of a few RCTs (none and YCoCg first) makes the cheapest estimate (PrepareStreamParams).</summary>
    private void SearchRct(JxlEncodeImage gi)
    {
        int tries = _effort switch { >= 7 => 7, 6 => 5, 5 => 4, _ => 0 };
        if (!_color || gi.Channels.Count - gi.Meta < 3 || tries == 0)
        {
            return;
        }

        int[] order = [0, 6, 5, 10, 26, 40, 12, 19, 8, 4, 9, 15, 16, 17, 32, 33, 2, 1, 3];
        int begin = gi.Meta;
        JxlChannel[] original = [gi.Channels[begin], gi.Channels[begin + 1], gi.Channels[begin + 2]];
        if (!original[0].SameShape(original[1]) || !original[0].SameShape(original[2]))
        {
            return;
        }

        (long, float, long)[] baseCosts = new (long, float, long)[gi.Channels.Count];
        Parallel.For(0, gi.Channels.Count, _parallel, c => baseCosts[c] = ChannelCost(gi.Channels[c]));
        float[] costs = new float[tries];
        costs[0] = Combine(baseCosts);
        Parallel.For(1, tries, _parallel, k =>
        {
            (int[] a, int[] b, int[] c) = ApplyRct(original, order[k]);
            (long, float, long)[] mine = [.. baseCosts];
            JxlChannel shape = original[0];
            mine[begin] = ChannelCost(Wrap(shape, a));
            mine[begin + 1] = ChannelCost(Wrap(shape, b));
            mine[begin + 2] = ChannelCost(Wrap(shape, c));
            costs[k] = Combine(mine);
        });

        float best = costs[0];
        int bestRct = 0;
        for (int k = 1; k < tries; k++)
        {
            if (costs[k] < best)
            {
                (best, bestRct) = (costs[k], order[k]);
            }
        }

        if (bestRct != 0)
        {
            ForwardRct(gi, begin, bestRct);
        }
    }

    private static JxlChannel Wrap(JxlChannel shape, int[] data) => new(shape.Width, shape.Height, shape.HShift, shape.VShift) { Data = data };

    private static void ForwardRct(JxlEncodeImage gi, int begin, int type)
    {
        JxlChannel[] input = [gi.Channels[begin], gi.Channels[begin + 1], gi.Channels[begin + 2]];
        if (!input[0].SameShape(input[1]) || !input[0].SameShape(input[2]))
        {
            return;
        }

        (int[] a, int[] b, int[] c) = ApplyRct(input, type);
        JxlChannel shape = input[0];
        gi.Channels[begin] = new JxlChannel(shape.Width, shape.Height, shape.HShift, shape.VShift) { Data = a };
        gi.Channels[begin + 1] = new JxlChannel(shape.Width, shape.Height, shape.HShift, shape.VShift) { Data = b };
        gi.Channels[begin + 2] = new JxlChannel(shape.Width, shape.Height, shape.HShift, shape.VShift) { Data = c };
        gi.Transforms.Add(new JxlRct(begin, type));
    }

    /// <summary>The forward RCT (enc_rct.cc): a channel order, then one of six lifting steps or YCoCg.</summary>
    private static (int[] A, int[] B, int[] C) ApplyRct(JxlChannel[] input, int type)
    {
        int permutation = type / 7;
        int transform = type % 7;
        int[] p0 = input[permutation % 3].Data;
        int[] p1 = input[(permutation + 1 + (permutation / 3)) % 3].Data;
        int[] p2 = input[(permutation + 2 - (permutation / 3)) % 3].Data;
        int n = p0.Length;
        int[] o0 = new int[n];
        int[] o1 = new int[n];
        int[] o2 = new int[n];
        int second = transform >> 1;
        bool third = (transform & 1) != 0;
        for (int i = 0; i < n; i++)
        {
            unchecked
            {
                if (transform == 6)
                {
                    int r = p0[i];
                    int g = p1[i];
                    int b = p2[i];
                    int co = r - b;
                    int tmp = b + (co >> 1);
                    int cg = g - tmp;
                    o0[i] = tmp + (cg >> 1);
                    o1[i] = co;
                    o2[i] = cg;
                    continue;
                }

                int first = p0[i];
                int s = p1[i];
                int t = p2[i];
                s = second switch { 1 => s - first, 2 => s - ((first + t) >> 1), _ => s };
                if (third)
                {
                    t -= first;
                }

                o0[i] = first;
                o1[i] = s;
                o2[i] = t;
            }
        }

        return (o0, o1, o2);
    }

    /// <summary>libjxl's EstimateCost: gradient residuals in 18 contexts by the neighbours' spread, entropy plus raw bits.</summary>
    private static float EstimateCost(JxlEncodeImage img) => Combine([.. img.Channels.Select(ChannelCost)]);

    /// <summary>Adds up channel costs as EstimateCost does: whole bits, fractions and raw bits apart, in channel order.</summary>
    private static float Combine((long Histo, float Frac, long Extra)[] costs)
    {
        long histo = 0;
        float frac = 0;
        long extra = 0;
        foreach ((long h, float f, long e) in costs)
        {
            histo += h;
            frac += f;
            extra += e;
        }

        return histo + frac + extra;
    }

    /// <summary>One channel's part of <see cref="EstimateCost"/>.</summary>
    private static (long Histo, float Frac, long Extra) ChannelCost(JxlChannel ch)
    {
        // The context is the number of cutoffs (0, 1, 3, 5, ..., 500) the spread is below, looked up for spreads up to 500.
        ReadOnlySpan<uint> cutoffs = [0, 1, 3, 5, 7, 11, 15, 23, 31, 47, 63, 95, 127, 191, 255, 392, 500];
        Span<byte> contextOf = stackalloc byte[501];
        for (int v = 0; v <= 500; v++)
        {
            int ctx = 0;
            foreach (uint c in cutoffs)
            {
                ctx += (uint)v < c ? 1 : 0;
            }

            contextOf[v] = (byte)ctx;
        }

        const int Tokens = 256;
        long histoCost = 0;
        float frac = 0;
        long extra = 0;
        int[] histo = new int[18 * Tokens];
        {
            int w = ch.Width;
            int[] d = ch.Data;
            for (int y = 0; y < ch.Height; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int at = row + x;
                    long left = x > 0 ? d[at - 1] : y > 0 ? d[at - w] : 0;
                    long top = y > 0 ? d[at - w] : left;
                    long topleft = x > 0 && y > 0 ? d[at - 1 - w] : left;
                    long max = Math.Max(left, Math.Max(top, topleft));
                    long min = Math.Min(left, Math.Min(top, topleft));
                    ulong spread = (ulong)(max - min);
                    int ctx = spread <= 500 ? contextOf[(int)spread] : 0;
                    long grad = Math.Clamp(top + left - topleft, min, max);
                    uint v = JxlTreeLearner.PackSigned(unchecked(d[at] - (int)grad));

                    // The 4, 2, 0 hybrid integer token and raw bits.
                    uint tok;
                    if (v < 16)
                    {
                        tok = v;
                    }
                    else
                    {
                        int n = System.Numerics.BitOperations.Log2(v);
                        tok = (uint)(16 + ((n - 4) << 2) + ((v - (1u << n)) >> (n - 2)));
                        extra += n - 2;
                    }

                    histo[(ctx * Tokens) + tok]++;
                }
            }

            for (int c = 0; c < 18; c++)
            {
                float f = Entropy(histo.AsSpan(c * Tokens, Tokens));
                long i = (long)f;
                histoCost += i;
                frac += f - i;
            }
        }

        return (histoCost, frac, extra);
    }

    private static float Entropy(ReadOnlySpan<int> counts)
    {
        long total = 0;
        foreach (int c in counts)
        {
            total += c;
        }

        if (total == 0)
        {
            return 0;
        }

        float inv = 1f / total;
        float e = 0;
        foreach (int c in counts)
        {
            if (c != 0 && c != total)
            {
                e -= c * MathF.Log2(c * inv);
            }
        }

        return e;
    }

    /// <summary>
    /// Tries palettes (try_palettes): of all channels, else all but the last (RGB with alpha apart), then of each remaining
    /// channel alone when few of its values occur.
    /// </summary>
    private void TryPalettes(JxlEncodeImage gi, float channelColorsPercent)
    {
        int nbChans = gi.Channels.Count - gi.Meta;
        if (nbChans <= 0)
        {
            return;
        }

        float pixels = (float)gi.Channels[gi.Meta].Width * gi.Channels[gi.Meta].Height;
        float costBefore = _effort >= 7 ? EstimateCost(gi) : pixels * 0.2f * gi.BitDepth * nbChans;
        int maxColors = Math.Min((int)((costBefore * 0.0005f) + (pixels / 128) + 128), 1024);
        bool did = false;
        if (nbChans > 1)
        {
            did = Palette(gi, gi.Meta, nbChans, maxColors, costBefore);
        }

        if (!did && nbChans > 3)
        {
            did = Palette(gi, gi.Meta, nbChans - 1, maxColors, costBefore);
        }

        if (channelColorsPercent <= 0)
        {
            return;
        }

        int remaining = gi.Channels.Count - gi.Meta - (did ? 1 : 0);
        int start = did ? 1 : 0;
        for (int i = start; i < remaining + start; i++)
        {
            JxlChannel ch = gi.Channels[gi.Meta + i];
            (int min, int max) = MinMax(ch.Data);
            long colors = (long)max - min + 1;
            int limit = (int)Math.Min(pixels / 16, channelColorsPercent / 100.0 * colors);
            Palette(gi, gi.Meta + i, 1, limit, 0);
        }
    }

    private static (int Min, int Max) MinMax(int[] data)
    {
        if (data.Length == 0)
        {
            return (0, 0);
        }

        int min = int.MaxValue;
        int max = int.MinValue;
        foreach (int v in data)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        return (min, max);
    }

    /// <summary>
    /// Replaces channels [begin, begin + count) by palette indices when at most <paramref name="colors"/> distinct colors occur
    /// (FwdPalette, lossless): the palette becomes meta channel 0. Above effort 7 a palette that does not lower the estimate is undone.
    /// </summary>
    private bool Palette(JxlEncodeImage gi, int begin, int count, int colors, float costBefore)
    {
        if (gi.BitDepth >= 32 || colors < 2)
        {
            return false;
        }

        JxlChannel first = gi.Channels[begin];
        for (int c = 1; c < count; c++)
        {
            if (!gi.Channels[begin + c].SameShape(first))
            {
                return false;
            }
        }

        if (first.Width == 0 || first.Height == 0)
        {
            return false;
        }

        List<JxlChannel> before = [.. gi.Channels];
        int metaBefore = gi.Meta;
        bool done = count == 1 ? ChannelPalette(gi, begin, colors) : ColorPalette(gi, begin, count, colors);
        if (!done)
        {
            return false;
        }

        if (_effort >= 8 && EstimateCost(gi) > costBefore)
        {
            gi.Channels.Clear();
            gi.Channels.AddRange(before);
            gi.Meta = metaBefore;
            gi.Transforms.RemoveAt(gi.Transforms.Count - 1);
            return false;
        }

        return true;
    }

    /// <summary>A one-channel palette: the values that occur, in increasing order, and each sample's index among them.</summary>
    private static bool ChannelPalette(JxlEncodeImage gi, int begin, int colors)
    {
        JxlChannel ch = gi.Channels[begin];
        (int min, int max) = MinMax(ch.Data);
        long range = (long)max - min + 1;
        SortedSet<int> values = [];
        if (range <= 1 << 16)
        {
            bool[] seen = new bool[range];
            int found = 0;
            foreach (int v in ch.Data)
            {
                if (!seen[v - min])
                {
                    seen[v - min] = true;
                    if (++found > colors)
                    {
                        return false;
                    }
                }
            }

            for (int i = 0; i < range; i++)
            {
                if (seen[i])
                {
                    values.Add(i + min);
                }
            }
        }
        else
        {
            foreach (int v in ch.Data)
            {
                if (values.Add(v) && values.Count > colors)
                {
                    return false;
                }
            }
        }

        int[] palette = [.. values];
        Dictionary<int, int> index = [];
        for (int i = 0; i < palette.Length; i++)
        {
            index[palette[i]] = i;
        }

        int[] indices = new int[ch.Data.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = index[ch.Data[i]];
        }

        JxlChannel pch = new(palette.Length, 1, -1, -1) { Data = palette };
        gi.Channels[begin] = new JxlChannel(ch.Width, ch.Height, ch.HShift, ch.VShift) { Data = indices };
        gi.Channels.Insert(0, pch);
        gi.Meta++;
        gi.Transforms.Add(new JxlPalette(begin, 1, palette.Length, 0, 0, new JxlWpParams()));
        return true;
    }

    /// <summary>
    /// A palette of several channels: colors in image order (the common ones before the rare ones, each by luma, for three
    /// channels or more), the implicit cube colors left to the decoder unless they are frequent.
    /// </summary>
    private static bool ColorPalette(JxlEncodeImage gi, int begin, int count, int colors)
    {
        JxlChannel first = gi.Channels[begin];
        int n = first.Data.Length;
        int[][] planes = [.. Enumerable.Range(0, count).Select(c => gi.Channels[begin + c].Data)];

        // The implicit colors, as the decoder makes them.
        Dictionary<ColorKey, int> implicitColors = [];
        List<int[]> implicitList = [];
        for (int k = 0; k < 64 + 125; k++)
        {
            int[] color = [.. Enumerable.Range(0, count).Select(c => ImplicitColor(k, c, gi.BitDepth))];
            implicitColors.TryAdd(new ColorKey(color), k);
            implicitList.Add(color);
        }

        HashSet<ColorKey> candidate = [];
        List<int[]> imageOrder = [];
        Dictionary<ColorKey, int> frequency = [];
        int implicitUsed = 0;
        int[] scratch = new int[count];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < count; c++)
            {
                scratch[c] = planes[c][i];
            }

            ColorKey key = new([.. scratch]);
            if (candidate.Add(key))
            {
                if (implicitColors.ContainsKey(key))
                {
                    implicitUsed++;
                }
                else
                {
                    imageOrder.Add(key.Values);
                    if (imageOrder.Count > colors)
                    {
                        return false;
                    }
                }
            }

            frequency[key] = frequency.GetValueOrDefault(key) + 1;
        }

        int nbColors = imageOrder.Count;
        if (nbColors + implicitUsed == 1)
        {
            return false;
        }

        // Frequent implicit colors still join the explicit palette.
        foreach (int[] color in implicitList)
        {
            if (frequency.GetValueOrDefault(new ColorKey(color)) > 10)
            {
                nbColors++;
                imageOrder.Add(color);
            }
        }

        Dictionary<ColorKey, int> inverse = [];
        for (int k = 0; k < implicitList.Count; k++)
        {
            inverse[new ColorKey(implicitList[k])] = nbColors + k;
        }

        if (count >= 3)
        {
            const float FreqThreshold = 4;
            float Luma(int[] p)
            {
                float y = (0.299f * p[0]) + (0.587f * p[1]) + (0.114f * p[2]) + 0.1f;
                if (p.Length > 3)
                {
                    y *= 1f + p[3];
                }

                return frequency.GetValueOrDefault(new ColorKey(p)) > FreqThreshold ? -y : y;
            }

            imageOrder = [.. imageOrder.OrderBy(Luma)];
        }

        int[] palette = new int[nbColors * count];
        for (int k = 0; k < imageOrder.Count; k++)
        {
            for (int c = 0; c < count; c++)
            {
                palette[(c * nbColors) + k] = imageOrder[k][c];
            }

            inverse[new ColorKey(imageOrder[k])] = k;
        }

        int[] indices = new int[n];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < count; c++)
            {
                scratch[c] = planes[c][i];
            }

            indices[i] = inverse[new ColorKey(scratch)];
        }

        JxlChannel pch = new(nbColors, count, -1, -1) { Data = palette };
        gi.Channels.RemoveRange(begin + 1, count - 1);
        gi.Channels[begin] = new JxlChannel(first.Width, first.Height, first.HShift, first.VShift) { Data = indices };
        gi.Channels.Insert(0, pch);
        gi.Meta++;
        gi.Transforms.Add(new JxlPalette(begin, count, nbColors, 0, 0, new JxlWpParams()));
        return true;
    }

    /// <summary>Gets implicit palette color <paramref name="k"/>'s channel <paramref name="c"/> (0 beyond the third) as the decoders make it.</summary>
    private static int ImplicitColor(int k, int c, int bitDepth)
    {
        if (c >= 3)
        {
            return 0;
        }

        if (k < 64)
        {
            return (((k >> (2 * c)) % 4) * ((1 << bitDepth) - 1) / 4) + (1 << Math.Max(bitDepth - 3, 0));
        }

        k -= 64;
        for (int i = 0; i < c; i++)
        {
            k /= 5;
        }

        return (k % 5) * ((1 << bitDepth) - 1) / 4;
    }

    /// <summary>Builds the tree: learned from samples at effort 4 and up, else libjxl's fixed trees.</summary>
    private List<JxlTreeNode> BuildTree()
    {
        if (_effort >= 4)
        {
            return LearnTree();
        }

        long pixels = 0;
        int bitDepth = 0;
        foreach (JxlEncodeImage img in _streams)
        {
            bitDepth = Math.Max(bitDepth, img.BitDepth);
            foreach (JxlChannel c in img.Channels)
            {
                pixels += (long)c.Width * c.Height;
            }
        }

        return _effort switch
        {
            3 => FixedTree(15, 6, pixels, bitDepth),
            2 => FixedTree(9, 5, pixels, bitDepth),
            _ => [JxlTreeNode.Leaf(5)],
        };
    }

    /// <summary>libjxl's MakeFixedTree: a balanced tree over the cutoffs of one property, shallower for small images.</summary>
    internal static List<JxlTreeNode> FixedTree(int property, int predictor, long pixels, int bitDepth)
    {
        int logPixels = pixels <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(pixels - 1));
        int minGap = logPixels < 14 ? 8 * (14 - logPixels) : 0;
        int shift = bitDepth > 11 ? Math.Min(4, bitDepth - 11) : 0;
        int mul = 1 << shift;
        List<JxlTreeNode> tree = [JxlTreeNode.Leaf(predictor)];
        Queue<(int Begin, int End, int Pos)> q = new();
        q.Enqueue((0, FixedCutoffs.Length, 0));
        while (q.Count > 0)
        {
            (int begin, int end, int pos) = q.Dequeue();
            if (begin + minGap >= end)
            {
                continue;
            }

            int split = (begin + end) / 2;
            tree[pos] = JxlTreeNode.Split(property, FixedCutoffs[split] * mul, tree.Count);
            q.Enqueue((split + 1, end, tree.Count));
            tree.Add(JxlTreeNode.Leaf(predictor));
            q.Enqueue((begin, split, tree.Count));
            tree.Add(JxlTreeNode.Leaf(predictor));
        }

        return tree;
    }

    /// <summary>Learns the tree from samples of every stream (libjxl's LearnTree, CollectPixelSamples and GatherTreeData).</summary>
    private List<JxlTreeNode> LearnTree()
    {
        int count = _effort switch { >= 7 => 7, 6 => 5, 5 => 4, _ => 3 };
        int maxValues = _effort switch { >= 7 => 96, 6 => 64, 5 => 48, _ => 32 };
        float repeats = 0.5f * _effort switch { >= 7 => 1f, 6 => 0.7f, 5 => 0.5f, _ => 0.3f };
        float threshold = 75 + (14 * (10 - _effort));
        List<int> order = [.. PropertyOrder];
        if (_streams.Length < 30)
        {
            // Few groups: the group is no use as a property.
            order.RemoveAt(1);
        }

        int[] properties = [.. order.Take(count)];
        int[] predictors = [6, 5];
        JxlTreeLearner learner = new(predictors, properties, 1 << 16);

        List<long> groupPixels = [];
        List<long> channelPixels = [];
        List<int> pixelSamples = [];
        List<int> diffSamples = [];
        for (int s = 0; s < _streams.Length; s++)
        {
            CollectPixelSamples(_streams[s], s, repeats, groupPixels, channelPixels, pixelSamples, diffSamples);
        }

        learner.Quantize(groupPixels, channelPixels, pixelSamples, diffSamples, maxValues);

        int start = Array.FindIndex(_streams, s => s.Channels.Count > 0);
        long totalPixels = 0;
        if (start >= 0)
        {
            JxlEncodeImage first = _streams[start];
            for (int c = 0; c < first.CodedPrefix(_groupDim); c++)
            {
                totalPixels += (long)first.Channels[c].Width * first.Channels[c].Height;
            }
        }

        totalPixels = Math.Max(totalPixels, 1);
        List<(int Stream, int Channel)> units = Units();
        SampleBuffer[] buffers = new SampleBuffer[units.Count];
        Parallel.For(0, units.Count, _parallel, u => buffers[u] = GatherTreeData(_streams[units[u].Stream].Channels[units[u].Channel], units[u].Channel, units[u].Stream, repeats, predictors, properties));
        Span<int> props = stackalloc int[16];
        Span<long> preds = stackalloc long[14];
        foreach (SampleBuffer b in buffers)
        {
            totalPixels += b.Pixels;
            for (int i = 0; i < b.Count; i++)
            {
                for (int k = 0; k < properties.Length; k++)
                {
                    props[properties[k]] = b.Properties[(i * properties.Length) + k];
                }

                for (int k = 0; k < predictors.Length; k++)
                {
                    preds[predictors[k]] = b.Predictions[(i * predictors.Length) + k];
                }

                learner.Add(b.Values[i], props, preds);
            }
        }

        if (learner.SampleCount == 0)
        {
            return [JxlTreeNode.Leaf(6)];
        }

        float fraction = learner.SampleCount * 1f / totalPixels;
        float required = (fraction * 0.9f) + 0.1f;
        return learner.Learn(threshold * required, 1.001f, _parallel.MaxDegreeOfParallelism);
    }

    private void CollectPixelSamples(JxlEncodeImage img, int stream, float repeats, List<long> groupPixels, List<long> channelPixels, List<int> pixelSamples, List<int> diffSamples)
    {
        while (groupPixels.Count <= stream)
        {
            groupPixels.Add(0);
        }

        while (channelPixels.Count < img.Channels.Count)
        {
            channelPixels.Add(0);
        }

        JxlRng rng = new((ulong)stream);
        float fraction = Math.Min(repeats * 0.1f, 0.99f);
        float dist = (float)(1.0 / Math.Log(1 - fraction));
        List<int> ids = [];
        int prefix = img.CodedPrefix(_groupDim);
        for (int i = 0; i < prefix; i++)
        {
            JxlChannel ch = img.Channels[i];
            if (ch.Width <= 1 || ch.Height == 0)
            {
                continue;
            }

            ids.Add(i);
            long px = (long)ch.Width * ch.Height;
            groupPixels[stream] += px;
            channelPixels[i] += px;
        }

        if (ids.Count == 0)
        {
            return;
        }

        int k = 0;
        int y = 0;
        int x = 0;
        bool Advance(uint amount)
        {
            x += (int)amount;
            while (x >= img.Channels[ids[k]].Width)
            {
                x -= img.Channels[ids[k]].Width;
                y++;
                if (y == img.Channels[ids[k]].Height)
                {
                    k++;
                    y = 0;
                    if (k >= ids.Count)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        if (!Advance(rng.Geometric(dist)))
        {
            return;
        }

        while (k < ids.Count)
        {
            JxlChannel ch = img.Channels[ids[k]];
            int row = y * ch.Width;
            pixelSamples.Add(ch.Data[row + x]);
            int xp = x == 0 ? 1 : x - 1;
            diffSamples.Add(unchecked(ch.Data[row + x] - ch.Data[row + xp]));
            if (!Advance(rng.Geometric(dist) + 1))
            {
                break;
            }
        }
    }

    /// <summary>Samples of one channel for the learner: values, the learner's properties and predictions, and the channel's pixel count.</summary>
    private sealed class SampleBuffer(int capacity, int properties, int predictors)
    {
        internal int Count;
        internal long Pixels;
        internal long[] Values = new long[capacity];
        internal int[] Properties = new int[capacity * properties];
        internal long[] Predictions = new long[capacity * predictors];
    }

    /// <summary>Takes samples of one channel, chosen by libjxl's xorshift (GatherTreeData).</summary>
    private static SampleBuffer GatherTreeData(JxlChannel ch, int c, int stream, float repeats, int[] predictors, int[] properties)
    {
        int width = ch.Width;
        int height = ch.Height;
        double fraction = Math.Min(1.0f, repeats);
        if (fraction > 0)
        {
            fraction = Math.Max(fraction, Math.Min(1.0, 1024.0 / ((double)width * height)));
        }

        ulong threshold = (ulong)((ulong.MaxValue >> 32) * fraction);
        ulong s0 = 0x94D049BB133111EB;
        ulong s1 = 0xBF58476D1CE4E5B9;
        SampleBuffer buffer = new((int)Math.Min((long)width * height, (long)(((double)width * height * fraction) + 64)), properties.Length, predictors.Length) { Pixels = (long)width * height };
        Span<int> props = stackalloc int[16];
        Span<long> near = stackalloc long[8];
        props[0] = c;
        props[1] = stream;
        JxlWeightedPredictor wp = new(width, new JxlWpParams());
        int[] d = ch.Data;
        for (int y = 0; y < height; y++)
        {
            props[2] = y;
            props[9] = 0;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                Compute(d, width, x, y, row, props, near, wp, false);
                ulong a = s0;
                ulong b = s1;
                ulong bits = a + b;
                s0 = b;
                a ^= a << 23;
                a ^= b ^ (a >> 18) ^ (b >> 5);
                s1 = a;
                if ((bits >> 32) <= threshold)
                {
                    int n = buffer.Count;
                    if (n == buffer.Values.Length)
                    {
                        Array.Resize(ref buffer.Values, n * 2);
                        Array.Resize(ref buffer.Properties, n * 2 * properties.Length);
                        Array.Resize(ref buffer.Predictions, n * 2 * predictors.Length);
                    }

                    buffer.Values[n] = d[row + x];
                    for (int k = 0; k < properties.Length; k++)
                    {
                        buffer.Properties[(n * properties.Length) + k] = props[properties[k]];
                    }

                    for (int k = 0; k < predictors.Length; k++)
                    {
                        buffer.Predictions[(n * predictors.Length) + k] = JxlModularDecoder.Predict(predictors[k], (int)near[1], (int)near[2], (int)near[3], (int)near[4], (int)near[5], (int)near[6], (int)near[7], near[0]);
                    }

                    buffer.Count++;
                }

                wp.Record(d[row + x]);
            }
        }

        return buffer;
    }

    /// <summary>
    /// Works out the properties (H.4.1) of sample (x, y) into <paramref name="p"/>, whose channel, stream and row entries the
    /// caller sets and whose entry 9 carries the previous sample's gradient, and every predictor's prediction.
    /// </summary>
    private static void Compute(int[] d, int width, int x, int y, int row, Span<int> p, Span<long> preds, JxlWeightedPredictor? wp, bool all)
    {
        int at = row + x;
        int w = x > 0 ? d[at - 1] : y > 0 ? d[at - width] : 0;
        int n = y > 0 ? d[at - width] : w;
        int nw = x > 0 && y > 0 ? d[at - width - 1] : w;
        int ne = x + 1 < width && y > 0 ? d[at - width + 1] : n;
        int ww = x > 1 ? d[at - 2] : w;
        int nn = y > 1 ? d[at - (2 * width)] : n;
        int nee = x + 2 < width && y > 0 ? d[at - width + 2] : ne;
        unchecked
        {
            p[3] = x;
            p[4] = (int)(uint)Math.Abs((long)n);
            p[5] = (int)(uint)Math.Abs((long)w);
            p[6] = n;
            p[7] = w;
            p[8] = w - p[9];
            p[9] = w + n - nw;
            p[10] = w - nw;
            p[11] = nw - n;
            p[12] = n - ne;
            p[13] = n - nn;
            p[14] = w - ww;
        }

        long wpPrediction = 0;
        if (wp is not null)
        {
            wpPrediction = wp.Predict(n, nw, ne, w, nn);
            p[15] = wp.MaxError;
        }
        else
        {
            p[15] = 0;
        }

        if (all)
        {
            for (int k = 0; k < 14; k++)
            {
                preds[k] = JxlModularDecoder.Predict(k, w, n, nw, ne, nn, ww, nee, wpPrediction);
            }
        }
        else
        {
            preds[0] = wpPrediction;
            preds[1] = w;
            preds[2] = n;
            preds[3] = nw;
            preds[4] = ne;
            preds[5] = nn;
            preds[6] = ww;
            preds[7] = nee;
        }
    }

    /// <summary>
    /// Writes the tree's tokens in breadth-first order (TokenizeTree) and gives it back as the decoder will lay it out: each
    /// node's property (−1 for a leaf), split value, children, predictor and leaf context.
    /// </summary>
    internal static (int[] Property, int[] Value, int[] Left, int[] Right, int[] Predictor, int[] Context) TokenizeTree(List<JxlTreeNode> tree, List<JxlToken> tokens)
    {
        List<int> property = [];
        List<int> value = [];
        List<int> left = [];
        List<int> right = [];
        List<int> predictor = [];
        List<int> context = [];
        Queue<int> q = new();
        q.Enqueue(0);
        int leaves = 0;
        while (q.Count > 0)
        {
            int cur = q.Dequeue();
            JxlTreeNode node = tree[cur];
            tokens.Add(new JxlToken(1, (uint)(node.Property + 1)));
            if (node.Property < 0)
            {
                tokens.Add(new JxlToken(2, (uint)node.Predictor));
                tokens.Add(new JxlToken(3, 0));
                tokens.Add(new JxlToken(4, 0));
                tokens.Add(new JxlToken(5, 0));
                property.Add(-1);
                value.Add(0);
                left.Add(0);
                right.Add(0);
                predictor.Add(node.Predictor);
                context.Add(leaves++);
                continue;
            }

            property.Add(node.Property);
            value.Add(node.SplitValue);
            left.Add(property.Count + q.Count);
            right.Add(property.Count + q.Count + 1);
            predictor.Add(0);
            context.Add(-1);
            q.Enqueue(node.Left);
            q.Enqueue(node.Right);
            tokens.Add(new JxlToken(0, JxlTreeLearner.PackSigned(node.SplitValue)));
        }

        return ([.. property], [.. value], [.. left], [.. right], [.. predictor], [.. context]);
    }

    /// <summary>Gets each stream's coded channels that have samples, in coding order: the units tokens and samples are made in.</summary>
    private List<(int Stream, int Channel)> Units()
    {
        List<(int, int)> units = [];
        for (int s = 0; s < _streams.Length; s++)
        {
            JxlEncodeImage img = _streams[s];
            int prefix = img.CodedPrefix(_groupDim);
            for (int c = 0; c < prefix; c++)
            {
                if (img.Channels[c].Width > 0 && img.Channels[c].Height > 0)
                {
                    units.Add((s, c));
                }
            }
        }

        return units;
    }

    /// <summary>Codes one channel as residual tokens through the tree (ModularCompress's per-channel loop).</summary>
    internal static List<JxlToken> Tokenize(JxlChannel ch, int c, int stream, int[] property, int[] value, int[] left, int[] right, int[] predictor, int[] context, bool usesWp)
    {
        if (SingleProperty(property, value, left, right, c, stream, out int only, out int range) is int[] lut)
        {
            return TokenizeLookup(ch, only, lut, range, predictor, context);
        }

        List<JxlToken> tokens = new(ch.Width * ch.Height);
        Span<int> props = stackalloc int[16];
        Span<long> near = stackalloc long[8];
        int width = ch.Width;
        int[] d = ch.Data;
        JxlWeightedPredictor? wp = usesWp ? new JxlWeightedPredictor(width, new JxlWpParams()) : null;
        props[0] = c;
        props[1] = stream;
        for (int y = 0; y < ch.Height; y++)
        {
            props[2] = y;
            props[9] = 0;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                Compute(d, width, x, y, row, props, near, wp, false);
                int node = 0;
                while (property[node] >= 0)
                {
                    node = props[property[node]] > value[node] ? left[node] : right[node];
                }

                long guess = JxlModularDecoder.Predict(predictor[node], (int)near[1], (int)near[2], (int)near[3], (int)near[4], (int)near[5], (int)near[6], (int)near[7], near[0]);
                int residual = unchecked(d[row + x] - (int)guess);
                tokens.Add(new JxlToken(context[node], JxlTreeLearner.PackSigned(residual)));
                wp?.Record(d[row + x]);
            }
        }

        return tokens;
    }

    /// <summary>
    /// Gets, when the tree's decisions for this channel and stream test only the gradient (9) or the WP error (15), the leaf
    /// for each value of that property from −range to range − 1, values beyond taking the nearest end (as libjxl's
    /// TreeToLookupTable, with the range widened to the split values).
    /// </summary>
    private static int[]? SingleProperty(int[] property, int[] value, int[] left, int[] right, int c, int stream, out int only, out int range)
    {
        only = -1;
        range = 512;
        Stack<int> pending = new();
        pending.Push(0);
        while (pending.Count > 0)
        {
            int n = pending.Pop();
            int p = property[n];
            if (p < 0)
            {
                continue;
            }

            if (p is 0 or 1)
            {
                pending.Push((p == 0 ? c : stream) > value[n] ? left[n] : right[n]);
                continue;
            }

            if (p is not (9 or 15) || (only >= 0 && only != p) || Math.Abs((long)value[n]) > 1 << 15)
            {
                return null;
            }

            only = p;
            range = Math.Max(range, Math.Abs(value[n]) + 2);
            pending.Push(left[n]);
            pending.Push(right[n]);
        }

        if (only < 0)
        {
            only = 9;
        }

        int[] lut = new int[2 * range];
        for (int v = -range; v < range; v++)
        {
            int n = 0;
            while (property[n] >= 0)
            {
                int pv = property[n] switch { 0 => c, 1 => stream, _ => v };
                n = pv > value[n] ? left[n] : right[n];
            }

            lut[v + range] = n;
        }

        return lut;
    }

    /// <summary>Codes one channel through a lookup table on one property, as libjxl's gradient-only and WP-only paths do.</summary>
    private static List<JxlToken> TokenizeLookup(JxlChannel ch, int only, int[] lut, int range, int[] predictor, int[] context)
    {
        int width = ch.Width;
        int height = ch.Height;
        int[] d = ch.Data;
        bool usesWp = only == 15 || lut.Any(leaf => predictor[leaf] == 6);
        JxlWeightedPredictor? wp = usesWp ? new JxlWeightedPredictor(width, new JxlWpParams()) : null;
        List<JxlToken> tokens = new(width * height);
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int at = row + x;
                int w = x > 0 ? d[at - 1] : y > 0 ? d[at - width] : 0;
                int n = y > 0 ? d[at - width] : w;
                int nw = x > 0 && y > 0 ? d[at - width - 1] : w;
                int ne = x + 1 < width && y > 0 ? d[at - width + 1] : n;
                int nn = y > 1 ? d[at - (2 * width)] : n;
                long wpPrediction = 0;
                int v;
                if (wp is not null)
                {
                    wpPrediction = wp.Predict(n, nw, ne, w, nn);
                }

                v = only == 15 ? wp!.MaxError : unchecked(w + n - nw);
                int leaf = lut[Math.Clamp(v, -range, range - 1) + range];
                int pr = predictor[leaf];
                long guess;
                if (pr == 5)
                {
                    long grad = (long)n + w - nw;
                    guess = Math.Clamp(grad, Math.Min(w, n), Math.Max(w, n));
                }
                else
                {
                    int ww = x > 1 ? d[at - 2] : w;
                    int nee = x + 2 < width && y > 0 ? d[at - width + 2] : ne;
                    guess = JxlModularDecoder.Predict(pr, w, n, nw, ne, nn, ww, nee, wpPrediction);
                }

                tokens.Add(new JxlToken(context[leaf], JxlTreeLearner.PackSigned(unchecked(d[at] - (int)guess))));
                wp?.Record(d[at]);
            }
        }

        return tokens;
    }

    /// <summary>A color's channel values, compared by value.</summary>
    private readonly struct ColorKey(int[] values) : IEquatable<ColorKey>
    {
        internal int[] Values { get; } = values;

        public bool Equals(ColorKey other) => Values.AsSpan().SequenceEqual(other.Values);

        public override bool Equals(object? obj) => obj is ColorKey k && Equals(k);

        public override int GetHashCode()
        {
            HashCode h = default;
            foreach (int v in Values)
            {
                h.Add(v);
            }

            return h.ToHashCode();
        }
    }
}

/// <summary>libjxl's Rng (xorshift128+), for the same sample positions.</summary>
internal struct JxlRng(ulong seed)
{
    private ulong _s0 = 0x94D049BB133111EB;
    private ulong _s1 = 0xBF58476D1CE4E5B9 + seed;

    internal ulong Next()
    {
        ulong a = _s0;
        ulong b = _s1;
        ulong bits = a + b;
        _s0 = b;
        a ^= a << 23;
        a ^= b ^ (a >> 18) ^ (b >> 5);
        _s1 = a;
        return bits;
    }

    internal uint Geometric(float invLog1mp)
    {
        uint u = (uint)(Next() >> (64 - 23)) | 0x3F800000;
        float f = BitConverter.UInt32BitsToSingle(u) - 1.0f;
        return (uint)(MathF.Log(1 - f) * invLog1mp);
    }
}
