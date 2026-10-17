namespace Egami.Formats.Jxl;

/// <summary>
/// What the chunks of a large lossy frame share (Phase 2.9.7 task 14, libjxl's streaming mode): the image is coded one LF group
/// (2048×2048) at a time, each with a border of a block for the inverse Gaborish and the adaptive quantization; the first chunk
/// fixes the frame's settings (matrix scales, block contexts, the Modular tree and its distributions, with every symbol given
/// a count so later chunks can use any), each chunk adds its own HF distributions and coefficient orders, and only the coded
/// sections are kept, so memory does not grow with the image.
/// </summary>
internal sealed class JxlChunkedFrame
{
    private const int ClustersLimit = 128;

    internal JxlChunkedFrame(int width, int height, JxlEncodeInput header, JxlLossySettings settings, float intensityTarget, int extraCount)
    {
        Width = width;
        Height = height;
        Header = header;
        Settings = settings;
        IntensityTarget = intensityTarget;
        ExtraCount = extraCount;
        int blocksX = (width + 7) / 8, blocksY = (height + 7) / 8;
        GroupsX = (width + 255) / 256;
        Groups = GroupsX * ((height + 255) / 256);
        LfGroupsX = (blocksX + 255) / 256;
        LfGroups = LfGroupsX * ((blocksY + 255) / 256);
        Sections = new byte[2 + LfGroups + Groups][];
        HfParts = new JxlEntropyCode[LfGroups];
        if (settings.PhotonNoiseIso > 0)
        {
            Noise = JxlVarDctEncoder.SimulatePhotonNoise(width, height, settings.PhotonNoiseIso);
        }
    }

    internal int Width { get; }

    internal int Height { get; }

    internal JxlEncodeInput Header { get; }

    internal JxlLossySettings Settings { get; }

    internal float IntensityTarget { get; }

    internal int ExtraCount { get; }

    internal int GroupsX { get; }

    internal int Groups { get; }

    internal int LfGroupsX { get; }

    internal int LfGroups { get; }

    internal float[]? Noise { get; }

    /// <summary>Gets the sections in TOC order, each filled when its chunk is coded (HF global at the end).</summary>
    internal byte[][] Sections { get; }

    /// <summary>Gets each chunk's HF distributions, which HF global joins.</summary>
    internal JxlEntropyCode[] HfParts { get; }

    // Set by the first chunk.
    internal JxlBitWriter? FrameHeader { get; set; }

    internal int XQmScale { get; set; }

    internal int BQmScale { get; set; }

    internal byte[] BlockContexts { get; set; } = [];

    internal int BlockClusters { get; set; }

    internal int[] QfThresholds { get; set; } = [];

    internal (int[] Property, int[] Value, int[] Left, int[] Right, int[] Predictor, int[] Context, int[] Multiplier, bool UsesWp)? Tree { get; set; }

    internal JxlEntropyCode? ModularCode { get; set; }

    internal List<List<JxlToken>>? TreeTokens { get; set; }

    // The coefficient orders committed so far: once a transform's order is chosen, later chunks keep it (libjxl's used_acs).
    internal int[][] Orders { get; } = new int[13 * 3][];

    internal int UsedOrders { get; set; }

    internal int UsedAcs { get; set; }

    /// <summary>Gets how many new HF distributions a chunk may add to the <paramref name="clusters"/> there are (libjxl's budget in TokenizeAllCoefficients).</summary>
    internal static int HistogramBudget(int clusters)
    {
        int remaining = ClustersLimit - clusters;
        return remaining < 20 ? Math.Min(remaining, 4) : remaining / 4;
    }

    /// <summary>Gets whether libjxl codes a frame like this in chunks (CanDoStreamingEncoding with its default buffering).</summary>
    internal static bool Applies(int width, int height, JxlLossySettings settings, bool lossyExtras)
    {
        int effort = Math.Clamp(settings.Effort, 1, 7);
        if (effort == 7 && settings.Distance >= 3)
        {
            return false;
        }

        long groups = (long)((width + 255) / 256) * ((height + 255) / 256);
        return groups > 8 && !settings.Progressive && settings.Distance < 10 && !lossyExtras;
    }

    /// <summary>Writes the codestream: image header, frame header, TOC and the sections, HF global joining the chunks' codes.</summary>
    internal byte[] Finish()
    {
        JxlBitWriter hf = new();
        hf.WriteBool(true); // default matrices
        hf.Write((ulong)(LfGroups - 1), CeilLog2(Groups));
        hf.WriteU32(new JxlU32(0x5F, 0, 0x13, 0, 0, 0, 0, 13), (uint)UsedOrders);
        if (UsedOrders != 0)
        {
            JxlVarDctEncoder.WriteFrameOrders(hf, Orders, UsedOrders);
        }

        JxlEntropyCode.Merge(hf, HfParts);
        hf.ZeroPadToByte();
        Sections[1 + LfGroups] = hf.ToArray();

        JxlBitWriter w = new();
        JxlLosslessEncoder.WriteImageHeader(w, Header with { Xyb = true });
        if (Header.Icc is not null)
        {
            JxlIccEncoder.Write(w, Header.Icc);
        }

        w.ZeroPadToByte();
        w.AppendBits(FrameHeader!);
        w.WriteBool(false); // no TOC permutation
        w.ZeroPadToByte();
        JxlU32 size = new(0, 10, 1024, 14, 17408, 22, 4211712, 30);
        foreach (byte[] s in Sections)
        {
            w.WriteU32(size, (uint)s.Length);
        }

        w.ZeroPadToByte();
        foreach (byte[] s in Sections)
        {
            w.Append(s);
        }

        return w.ToArray();
    }

    private static int CeilLog2(long x) => x <= 1 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)(x - 1));
}

/// <summary>The lossy encoder's chunk mode (see <see cref="JxlChunkedFrame"/>).</summary>
internal sealed partial class JxlVarDctEncoder
{
    // In chunk mode: the frame, this chunk's index (its LF group), and the XYB planes with their border.
    private readonly JxlChunkedFrame? _frame;
    private readonly int _chunk;
    private readonly float[][]? _bordered;
    private readonly int _borderStride;
    private readonly int _borderRows;
    private readonly int _borderLeft;
    private readonly int _borderTop;
    private int _usedThisChunk;

    /// <summary>
    /// Codes one chunk of <paramref name="frame"/>: <paramref name="patch"/> is the LF group's linear image with its
    /// <paramref name="border"/>, <paramref name="alpha"/> and <paramref name="black"/> its extra channels (patch size, or none).
    /// </summary>
    internal static void EncodeChunk(JxlChunkedFrame frame, int chunk, JxlLinearImage patch, (int L, int T, int R, int B) border, int[]? alpha, int alphaBits, float[]? black)
    {
        JxlLossySettings settings = frame.Settings;
        JxlVarDctEncoder e = new(patch, settings, frame.IntensityTarget, 1, settings.Distance, alpha, frame, chunk, border);
        e.ChooseParameters();
        if (chunk > 0)
        {
            (e._xQmScale, e._bQmScale) = (frame.XQmScale, frame.BQmScale);
        }

        e.ComputeCoefficients();
        e.QuantizeLf();
        e._passCoeffs = [e._coeffs];
        e.ComputeOrders();
        e.CommitOrders();
        if (chunk == 0)
        {
            e.FindBlockEntropyModel();
            (frame.BlockContexts, frame.BlockClusters, frame.QfThresholds) = (e._blockContexts, e._blockClusters, e._qfThresholds);
            (frame.XQmScale, frame.BQmScale) = (e._xQmScale, e._bQmScale);
        }
        else
        {
            (e._blockContexts, e._blockClusters, e._qfThresholds) = (frame.BlockContexts, frame.BlockClusters, frame.QfThresholds);
        }

        // The EPF search decodes the chunk as an image of its own, without extra channels.
        e.ChooseEpfSharpness(frame.Header with { Width = e._width, Height = e._height, Alpha = null, HasBlack = false });
        e.PrepareChunkExtras(patch.Width, border, alpha, alphaBits, black);
        e.ChunkSections();
    }

    /// <summary>Writes the coefficient orders of a chunked frame's HF global.</summary>
    internal static void WriteFrameOrders(JxlBitWriter w, int[][] orders, int used) => WriteOrders(w, orders, used);

    /// <summary>
    /// libjxl's ComputeCoeffOrder across chunks: a transform's order is chosen by the first chunk that uses it, from that chunk's
    /// coefficients, and kept; the chunk then codes with the frame's orders.
    /// </summary>
    private void CommitOrders()
    {
        JxlChunkedFrame frame = _frame!;
        int fresh = _usedThisChunk & ~frame.UsedAcs & ~frame.UsedOrders;
        for (int ord = 0; ord < NumOrders; ord++)
        {
            if ((fresh & (1 << ord)) != 0 && (_passUsed[0] & (1 << ord)) != 0)
            {
                for (int c = 0; c < 3; c++)
                {
                    frame.Orders[(ord * 3) + c] = _passOrders[0][(ord * 3) + c];
                }

                frame.UsedOrders |= 1 << ord;
            }
        }

        frame.UsedAcs |= _usedThisChunk;
        _passOrders[0] = frame.Orders;
        _passUsed[0] = frame.UsedOrders;
    }

    /// <summary>Takes the inside of the patch's extra channels and prepares their Modular streams for this chunk's groups.</summary>
    private void PrepareChunkExtras(int patchWidth, (int L, int T, int R, int B) border, int[]? alpha, int alphaBits, float[]? black)
    {
        List<(int[] Data, int Bits)> planes = [];
        if (alpha is not null)
        {
            planes.Add((alpha, alphaBits));
        }

        if (black is not null)
        {
            planes.Add(([.. black.Select(v => (int)MathF.Round(Math.Clamp(v, 0, 1) * 65535))], 16));
        }

        _extraCount = planes.Count;
        if (planes.Count == 0)
        {
            return;
        }

        List<JxlChannel> channels = [];
        foreach ((int[] data, _) in planes)
        {
            JxlChannel ch = new(_width, _height, 0, 0);
            for (int y = 0; y < _height; y++)
            {
                Array.Copy(data, ((y + border.T) * patchWidth) + border.L, ch.Data, y * _width, _width);
            }

            channels.Add(ch);
        }

        float[] distances = new float[planes.Count];
        (_extraStreams, _extraTree) = JxlModularEncoder.PrepareExtraChannels(channels, [.. planes.Select(p => p.Bits)], distances, Speed, _width, _height, _settings.Parallelism, chunk: true);
    }

    /// <summary>Gets the frame's group index of this chunk's group <paramref name="g"/>.</summary>
    private int FrameGroup(int g)
    {
        JxlChunkedFrame frame = _frame!;
        int cx = _chunk % frame.LfGroupsX, cy = _chunk / frame.LfGroupsX;
        return (((cy * 8) + (g / _groupsX)) * frame.GroupsX) + (cx * 8) + (g % _groupsX);
    }

    /// <summary>
    /// Codes this chunk's sections into the frame: its LF group (LF image and HF metadata) and its pass groups (HF and extra
    /// channels), the first chunk also LF global and the frame header.
    /// </summary>
    private void ChunkSections()
    {
        JxlChunkedFrame frame = _frame!;
        int effort = Speed;
        int lf = frame.LfGroups;
        int dcId = 1 + _chunk;
        int acmId = 1 + (2 * lf) + _chunk;
        List<JxlChannel> dc = LfChannels(0);
        List<JxlChannel> meta = MetaChannels(0);
        int metaCount = meta[2].Width;

        // The extra channels' group streams under their frame ids.
        List<(int Id, JxlEncodeImage Image)> extras = [];
        if (_extraStreams is not null)
        {
            for (int g = 0; g < _groups; g++)
            {
                JxlEncodeImage img = _extraStreams[1 + 3 + 17 + g];
                if (img.Channels.Count > 0)
                {
                    extras.Add((1 + (3 * lf) + 17 + FrameGroup(g), img));
                }
            }
        }

        if (_chunk == 0)
        {
            List<JxlTreeNode> dcTree = JxlModularEncoder.FixedTree(15, 6, Math.Max(dc.Sum(ch => (long)ch.Width * ch.Height), 1), 8);
            List<JxlTreeNode> acmTree = effort >= 4 && meta.Sum(ch => (long)ch.Width * ch.Height) >= 1024 ? AcMetaTree() : [JxlTreeNode.Leaf(1)];
            List<JxlTreeNode> tree = [];
            int Add(List<JxlTreeNode> sub)
            {
                int root = tree.Count;
                foreach (JxlTreeNode n in sub)
                {
                    tree.Add(n.Property < 0 ? n : n with { Left = n.Left + root, Right = n.Right + root });
                }

                return root;
            }

            if (_extraTree is null)
            {
                tree.Add(new JxlTreeNode { Property = 1, SplitValue = acmId - _chunk - 1, Left = 1, Right = 1 + acmTree.Count });
                Add(acmTree);
                Add(dcTree);
            }
            else
            {
                tree.Add(default);
                int high = Add(_extraTree);
                int mid = tree.Count;
                tree.Add(default);
                int acm = Add(acmTree);
                int low = tree.Count;
                tree.Add(default);
                int dcRoot = Add(dcTree);
                int whole = Add(_extraTree);
                tree[0] = new JxlTreeNode { Property = 1, SplitValue = 3 * lf, Left = high, Right = mid };
                tree[mid] = new JxlTreeNode { Property = 1, SplitValue = (2 * lf), Left = acm, Right = low };
                tree[low] = new JxlTreeNode { Property = 1, SplitValue = 0, Left = dcRoot, Right = whole };
                tree = Prune(tree);
            }

            List<List<JxlToken>> treeTokens = [[]];
            List<int> multipliers = [];
            (int[] property, int[] value, int[] left, int[] right, int[] predictor, int[] context) = JxlModularEncoder.TokenizeTree(tree, treeTokens[0], multipliers);
            bool usesWp = property.Any(p => p == 15) || property.Select((p, i) => p < 0 && predictor[i] == 6).Any(b => b);
            frame.Tree = (property, value, left, right, predictor, context, [.. multipliers], usesWp);
            frame.TreeTokens = treeTokens;
        }

        var t = frame.Tree!.Value;
        List<JxlToken> Tokens(List<JxlChannel> chs, int id)
        {
            List<JxlToken> list = [];
            for (int c = 0; c < chs.Count; c++)
            {
                if (chs[c].Width > 0 && chs[c].Height > 0)
                {
                    list.AddRange(JxlModularEncoder.Tokenize(chs[c], c, id, t.Property, t.Value, t.Left, t.Right, t.Predictor, t.Context, t.UsesWp, t.Multiplier));
                }
            }

            return list;
        }

        List<JxlToken> dcTokens = Tokens(dc, dcId);
        List<JxlToken> acmTokens = Tokens(meta, acmId);
        List<JxlToken>[] extraTokens = new List<JxlToken>[extras.Count];
        Parallel.For(0, extras.Count, _parallel, k =>
        {
            JxlEncodeImage img = extras[k].Image;
            extraTokens[k] = Tokens([.. img.Channels.Take(img.CodedPrefix(GroupDim))], extras[k].Id);
        });

        if (_chunk == 0)
        {
            // LF global: noise, LF dequantization, quantizer, block contexts, chroma from luma, the tree and the distributions
            // (streaming: every symbol counted), then the global stream of the extra channels, which holds no samples.
            JxlBitWriter global = new();
            foreach (float v in _noise ?? [])
            {
                global.Write((ulong)Math.Round(v * 1024.0f, MidpointRounding.AwayFromZero), 10);
            }

            global.WriteBool(true); // default LF dequantization
            global.WriteU32(new JxlU32(1, 11, 2049, 11, 4097, 12, 8193, 16), (uint)_globalScale);
            global.WriteU32(new JxlU32(16, 0, 1, 5, 1, 8, 1, 16), (uint)_quantDc);
            WriteBlockContexts(global);
            global.WriteBool(true); // default chroma from luma
            global.WriteBool(true); // global tree
            JxlHistogramParams mp = new()
            {
                Clustering = JxlHistogramParams.ClusteringKind.Fast,
                AnsStrategy = JxlHistogramParams.AnsKind.Approximate,
                UintMethod = JxlHistogramParams.UintKind.None,
                Streaming = true,
            };
            JxlEntropyCode treeCode = JxlEntropyCode.Write(global, mp, 6, frame.TreeTokens!);
            treeCode.WriteTokens(global, frame.TreeTokens![0]);
            List<List<JxlToken>> all = [dcTokens, acmTokens, .. extraTokens];
            frame.ModularCode = JxlEntropyCode.Write(global, mp with { AddMissingSymbols = true }, t.Context.Max() + 1, all);
            if (_extraStreams is not null)
            {
                JxlEncodeImage whole = new(16);
                for (int i = 0; i < _extraCount; i++)
                {
                    whole.Channels.Add(new JxlChannel(frame.Width, frame.Height, 0, 0) { Data = [] });
                }

                JxlModularEncoder.WriteStream(global, whole, frame.ModularCode, []);
            }

            global.ZeroPadToByte();
            frame.Sections[0] = global.ToArray();
            JxlBitWriter header = new();
            WriteFrameHeader(header);
            frame.FrameHeader = header;
        }

        JxlEntropyCode code = frame.ModularCode!;

        // The LF group.
        JxlBitWriter lfw = new();
        lfw.Write((ulong)_lfPrecision, 2);
        WriteStream(lfw, code, dcTokens);
        (_, _, int bw, int bh) = LfGroupRect(0);
        lfw.Write((ulong)(metaCount - 1), CeilLog2((long)bw * bh));
        WriteStream(lfw, code, acmTokens);
        lfw.ZeroPadToByte();
        frame.Sections[1 + _chunk] = lfw.ToArray();

        // The pass groups: the chunk's own HF distributions, chosen as preset _chunk.
        List<JxlToken>[] acTokens = new List<JxlToken>[_groups];
        Parallel.For(0, _groups, _parallel, g => acTokens[g] = TokenizeGroup(g, 0));
        JxlHistogramParams acp = new()
        {
            Clustering = JxlHistogramParams.ClusteringKind.Fast,
            UintMethod = JxlHistogramParams.UintKind.None,
            AnsStrategy = JxlHistogramParams.AnsKind.Approximate,
        };
        JxlEntropyCode? previous = _chunk == 0 ? null : frame.HfParts[_chunk - 1];
        int budget = Math.Min(JxlChunkedFrame.HistogramBudget(previous?.Clusters ?? 1), effort <= 2 ? 4 : int.MaxValue);
        JxlEntropyCode part = JxlEntropyCode.BuildPart(acp, (NonZeroBuckets + ZeroDensityContexts) * _blockClusters, [.. acTokens], previous, budget);
        frame.HfParts[_chunk] = part;
        int presetBits = CeilLog2(frame.LfGroups);
        Dictionary<int, int> extraOf = [];
        for (int k = 0; k < extras.Count; k++)
        {
            extraOf[extras[k].Id] = k;
        }

        Parallel.For(0, _groups, _parallel, g =>
        {
            JxlBitWriter w = new();
            w.Write((ulong)_chunk, presetBits);
            part.WriteTokens(w, acTokens[g]);
            int id = 1 + (3 * lf) + 17 + FrameGroup(g);
            if (extraOf.TryGetValue(id, out int k))
            {
                JxlModularEncoder.WriteStream(w, extras[k].Image, code, extraTokens[k]);
            }

            w.ZeroPadToByte();
            frame.Sections[2 + lf + FrameGroup(g)] = w.ToArray();
        });
    }
}
