namespace Egami.Formats.Jxl;

/// <summary>A Modular transform (H.6): RCT, palette or squeeze, with its parameters.</summary>
internal abstract record JxlTransform
{
    internal static JxlTransform Read(JxlBitReader r, JxlWpParams wp)
    {
        switch (r.ReadBits(2))
        {
            case 0:
                return new JxlRct((int)r.ReadU32(BeginC), (int)r.ReadU32(new JxlU32(6, 0, 0, 2, 2, 4, 10, 6)));
            case 1:
                {
                    int begin = (int)r.ReadU32(BeginC);
                    int count = (int)r.ReadU32(new JxlU32(1, 0, 3, 0, 4, 0, 1, 13));
                    int colours = (int)r.ReadU32(new JxlU32(0, 8, 256, 10, 1280, 12, 5376, 16));
                    int deltas = (int)r.ReadU32(new JxlU32(0, 0, 1, 8, 257, 10, 1281, 16));
                    int predictor = (int)r.ReadBits(4);
                    return predictor > 13 ? throw r.Corrupt($"Palette predictor {predictor} is unknown.") : new JxlPalette(begin, count, colours, deltas, predictor, wp);
                }

            case 2:
                {
                    int count = (int)r.ReadU32(new JxlU32(0, 0, 1, 4, 9, 6, 41, 8));
                    List<JxlSqueezeStep> steps = new(count);
                    for (int i = 0; i < count; i++)
                    {
                        bool horizontal = r.ReadBool();
                        bool inPlace = r.ReadBool();
                        steps.Add(new JxlSqueezeStep(horizontal, inPlace, (int)r.ReadU32(BeginC), (int)r.ReadU32(new JxlU32(1, 0, 2, 0, 3, 0, 4, 4))));
                    }

                    return new JxlSqueeze(steps);
                }

            default:
                throw r.Corrupt("Modular transform 3 is unknown.");
        }
    }

    private static JxlU32 BeginC => new(0, 3, 8, 6, 72, 10, 1096, 13);
}

/// <summary>A reversible color transform of three channels (H.6.3).</summary>
internal sealed record JxlRct(int Begin, int Type) : JxlTransform;

/// <summary>A palette transform (H.6.4).</summary>
internal sealed record JxlPalette(int Begin, int Count, int Colours, int Deltas, int Predictor, JxlWpParams Wp) : JxlTransform;

/// <summary>One step of a squeeze.</summary>
internal readonly record struct JxlSqueezeStep(bool Horizontal, bool InPlace, int Begin, int Count);

/// <summary>A squeeze transform (H.6.2); empty steps mean the default parameters for the channels it meets.</summary>
internal sealed record JxlSqueeze(List<JxlSqueezeStep> Steps) : JxlTransform;

/// <summary>A Modular sub-bitstream's header: tree choice, predictor parameters and transforms (H.2).</summary>
internal sealed record JxlModularHeader(bool UseGlobalTree, JxlWpParams Wp, List<JxlTransform> Transforms)
{
    internal static JxlModularHeader Read(JxlBitReader r)
    {
        bool global = r.ReadBool();
        JxlWpParams wp = JxlWpParams.Read(r);
        uint count = r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 4, 18, 8));
        if (count > 512)
        {
            throw r.Corrupt($"A Modular stream has {count} transforms; at most 512 are allowed.");
        }

        List<JxlTransform> transforms = new((int)count);
        for (int i = 0; i < count; i++)
        {
            transforms.Add(JxlTransform.Read(r, wp));
        }

        return new JxlModularHeader(global, wp, transforms);
    }
}

/// <summary>
/// A Modular image (Annex H): the channels after the transforms, which a stream decodes, and the inverse transforms that give
/// back the coded channels. Squeeze default parameters are worked out against the channels as they stand.
/// </summary>
internal sealed class JxlModularImage
{
    private JxlModularImage(JxlModularHeader header, JxlTree tree, List<JxlChannel> channels, int metaChannels, List<JxlTransform> transforms, int bitDepth)
    {
        Header = header;
        Tree = tree;
        Channels = channels;
        MetaChannels = metaChannels;
        Transforms = transforms;
        BitDepth = bitDepth;
    }

    internal JxlModularHeader Header { get; }

    internal JxlTree Tree { get; }

    /// <summary>Gets the channels as coded: meta channels first, after every transform.</summary>
    internal List<JxlChannel> Channels { get; }

    internal int MetaChannels { get; }

    /// <summary>Gets the transforms with their squeeze steps filled in.</summary>
    internal List<JxlTransform> Transforms { get; }

    internal int BitDepth { get; }

    /// <summary>
    /// Reads a Modular header (and its tree unless it uses the global one) for <paramref name="original"/>, the channels the
    /// stream codes, and lays out the transformed channels to decode into.
    /// </summary>
    internal static JxlModularImage Read(JxlBitReader r, IReadOnlyList<JxlChannel> original, JxlTree? globalTree, int bitDepth, SafetyLimits limits)
    {
        if (original.Count == 0)
        {
            // A stream with no channels has no header either.
            return new JxlModularImage(new JxlModularHeader(true, new JxlWpParams(), []), globalTree!, [], 0, [], bitDepth);
        }

        JxlModularHeader header = JxlModularHeader.Read(r);
        List<JxlChannel> channels = [.. original];
        int meta = 0;
        List<JxlTransform> transforms = new(header.Transforms.Count);
        long samples = 0;
        foreach (JxlTransform t in header.Transforms)
        {
            JxlTransform applied = t is JxlSqueeze { Steps.Count: 0 } ? new JxlSqueeze(DefaultSqueeze(channels, meta, r)) : t;
            meta = Forward(applied, channels, meta, r, limits);
            transforms.Add(applied);
        }

        if (channels.Count > 1 << 16)
        {
            throw r.Corrupt($"The transforms make {channels.Count} channels.");
        }

        foreach (JxlChannel c in channels)
        {
            samples += (long)c.Width * c.Height;
        }

        JxlTree tree = header.UseGlobalTree ? globalTree ?? throw r.Corrupt("A Modular stream uses the global tree, which the frame does not have.")
            : JxlTree.Read(r, Math.Min(1024 + samples, 1 << 20));
        return new JxlModularImage(header, tree, channels, meta, transforms, bitDepth);
    }

    /// <summary>Decodes the channels <paramref name="first"/> to <paramref name="end"/> (exclusive) as stream <paramref name="stream"/>.</summary>
    internal void Decode(JxlBitReader r, int stream, int first, int end)
    {
        JxlModularDecoder.DecodeChannels(r, Tree, Header.Wp, Channels, first, end, stream);
    }

    /// <summary>Undoes the transforms, last first, leaving <see cref="Channels"/> as the coded channels.</summary>
    internal void Inverse(JxlBitReader r)
    {
        for (int i = Transforms.Count - 1; i >= 0; i--)
        {
            switch (Transforms[i])
            {
                case JxlRct rct:
                    JxlTransforms.InverseRct(rct, Channels);
                    break;
                case JxlPalette palette:
                    JxlTransforms.InversePalette(palette, Channels, BitDepth);
                    break;
                case JxlSqueeze squeeze:
                    JxlTransforms.InverseSqueeze(squeeze, Channels, r);
                    break;
            }
        }
    }

    /// <summary>Applies a transform to the channel list (H.6), allocating the channels it adds; returns the new meta channel count.</summary>
    private static int Forward(JxlTransform t, List<JxlChannel> channels, int meta, JxlBitReader r, SafetyLimits limits)
    {
        switch (t)
        {
            case JxlRct rct:
                {
                    if (rct.Begin + 3 > channels.Count || rct.Type > 41)
                    {
                        throw r.Corrupt("An RCT refers to channels that do not exist.");
                    }

                    JxlChannel a = channels[rct.Begin];
                    if (!SameSize(a, channels[rct.Begin + 1]) || !SameSize(a, channels[rct.Begin + 2]))
                    {
                        throw r.Corrupt("An RCT's channels differ in size.");
                    }

                    return meta;
                }

            case JxlPalette p:
                {
                    int end = p.Begin + p.Count;
                    if (end > channels.Count || p.Count == 0)
                    {
                        throw r.Corrupt("A palette refers to channels that do not exist.");
                    }

                    if (p.Begin < meta)
                    {
                        if (end > meta)
                        {
                            throw r.Corrupt("A palette mixes meta and image channels.");
                        }

                        meta = meta + 2 - p.Count;
                    }
                    else
                    {
                        meta++;
                    }

                    JxlChannel first = channels[p.Begin];
                    for (int i = p.Begin + 1; i < end; i++)
                    {
                        if (!SameSize(first, channels[i]))
                        {
                            throw r.Corrupt("A palette's channels differ in size.");
                        }
                    }

                    CheckSamples(limits, (long)p.Colours * p.Count);
                    channels.RemoveRange(p.Begin + 1, p.Count - 1);
                    channels[p.Begin] = new JxlChannel(first.Width, first.Height, first.HShift, first.VShift);
                    channels.Insert(0, new JxlChannel(p.Colours, p.Count, -1, -1));
                    return meta;
                }

            case JxlSqueeze sq:
                foreach (JxlSqueezeStep step in sq.Steps)
                {
                    int end = step.Begin + step.Count;
                    if (end > channels.Count || step.Count == 0)
                    {
                        throw r.Corrupt("A squeeze refers to channels that do not exist.");
                    }

                    if (step.Begin < meta)
                    {
                        if (!step.InPlace || end > meta)
                        {
                            throw r.Corrupt("A squeeze of meta channels must be in place and stay among them.");
                        }

                        meta += step.Count;
                    }

                    List<JxlChannel> residuals = new(step.Count);
                    for (int i = step.Begin; i < end; i++)
                    {
                        JxlChannel c = channels[i];
                        if (c.Width == 0 || c.Height == 0 || c.HShift > 30 || c.VShift > 30)
                        {
                            throw r.Corrupt("A squeeze meets an empty or over-squeezed channel.");
                        }

                        int hs = c.HShift >= 0 && step.Horizontal ? c.HShift + 1 : c.HShift;
                        int vs = c.VShift >= 0 && !step.Horizontal ? c.VShift + 1 : c.VShift;
                        if (step.Horizontal)
                        {
                            channels[i] = new JxlChannel((c.Width + 1) / 2, c.Height, hs, vs);
                            residuals.Add(new JxlChannel(c.Width / 2, c.Height, hs, vs));
                        }
                        else
                        {
                            channels[i] = new JxlChannel(c.Width, (c.Height + 1) / 2, hs, vs);
                            residuals.Add(new JxlChannel(c.Width, c.Height / 2, hs, vs));
                        }
                    }

                    if (step.InPlace)
                    {
                        channels.InsertRange(end, residuals);
                    }
                    else
                    {
                        channels.AddRange(residuals);
                    }
                }

                return meta;
        }

        return meta;
    }

    /// <summary>The default squeeze steps for the channels as they stand (H.6.2.1).</summary>
    private static List<JxlSqueezeStep> DefaultSqueeze(List<JxlChannel> channels, int meta, JxlBitReader r)
    {
        if (meta >= channels.Count)
        {
            throw r.Corrupt("A default squeeze has no channels to squeeze.");
        }

        List<JxlSqueezeStep> steps = [];
        JxlChannel first = channels[meta];
        int w = first.Width;
        int h = first.Height;
        if (channels.Count - meta >= 3)
        {
            JxlChannel next = channels[meta + 1];
            if (next.Width == w && next.Height == h)
            {
                steps.Add(new JxlSqueezeStep(true, false, meta + 1, 2));
                steps.Add(new JxlSqueezeStep(false, false, meta + 1, 2));
            }
        }

        int count = channels.Count - meta;
        if (h >= w && h > 8)
        {
            steps.Add(new JxlSqueezeStep(false, true, meta, count));
            h = (h + 1) / 2;
        }

        while (w > 8 || h > 8)
        {
            if (w > 8)
            {
                steps.Add(new JxlSqueezeStep(true, true, meta, count));
                w = (w + 1) / 2;
            }

            if (h > 8)
            {
                steps.Add(new JxlSqueezeStep(false, true, meta, count));
                h = (h + 1) / 2;
            }
        }

        return steps;
    }

    private static bool SameSize(JxlChannel a, JxlChannel b) => a.Width == b.Width && a.Height == b.Height;

    private static void CheckSamples(SafetyLimits limits, long samples)
    {
        if (samples > limits.MaxPixels)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), limits.MaxPixels, samples);
        }
    }
}
