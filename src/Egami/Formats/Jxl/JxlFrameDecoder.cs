namespace Egami.Formats.Jxl;

/// <summary>A decoded frame's channels as the codestream gives them, before color conversion and blending.</summary>
internal sealed class JxlFrameChannels
{
    internal JxlFrameChannels(int width, int height, List<JxlChannel> channels, int colorChannels)
    {
        Width = width;
        Height = height;
        Channels = channels;
        ColorChannels = colorChannels;
    }

    /// <summary>Gets the frame's coded width (before upsampling).</summary>
    internal int Width { get; }

    internal int Height { get; }

    /// <summary>Gets the color channels (one or three) then the extra channels.</summary>
    internal List<JxlChannel> Channels { get; }

    internal int ColorChannels { get; }
}

/// <summary>
/// Decodes a frame's sections (Annex F, G): LF global, the LF groups, HF global and the pass groups, in TOC order, for Modular
/// frames. Translated from jxl-oxide's jxl-frame and jxl-modular crates (see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed class JxlFrameDecoder
{
    private readonly byte[] _codestream;
    private readonly JxlImageHeader _image;
    private readonly JxlFrameHeader _frame;
    private readonly JxlToc _toc;
    private readonly long _offset;
    private readonly SafetyLimits _limits;
    private JxlBitReader? _single;

    internal JxlFrameDecoder(byte[] codestream, JxlImageHeader image, JxlFrameHeader frame, JxlToc toc, long offset, SafetyLimits limits)
    {
        _codestream = codestream;
        _image = image;
        _frame = frame;
        _toc = toc;
        _offset = offset;
        _limits = limits;
    }

    /// <summary>Gets the global MA tree, or <see langword="null"/>.</summary>
    internal JxlTree? GlobalTree { get; private set; }

    /// <summary>Decodes the whole frame.</summary>
    internal JxlFrameChannels Decode()
    {
        if (!_frame.Modular)
        {
            throw new UnsupportedFormatException("Egami does not decode JPEG XL VarDCT frames yet.");
        }

        if ((_frame.Flags & 0x13) != 0)
        {
            throw new UnsupportedFormatException("Egami does not decode JPEG XL patches, splines or noise yet.");
        }

        (long w, long h) = _frame.CodedSize;
        int width = (int)w;
        int height = (int)h;

        // LF global: dequantization multipliers (used by XYB Modular frames), the global tree and the global Modular stream.
        JxlBitReader r = Section(0);
        float[] lfDequant = [1f / 32, 1f / 4, 1f / 2];
        if (!r.ReadBool())
        {
            lfDequant = [r.ReadF16(), r.ReadF16(), r.ReadF16()];
        }

        int colorChannels = !_image.XybEncoded && !_frame.YCbCr && _image.ColorChannels == 1 ? 1 : 3;
        long samples = (long)width * height * (colorChannels + _image.ExtraChannels.Count);
        if (r.ReadBool())
        {
            GlobalTree = JxlTree.Read(r, Math.Min(1 << 22, 1024 + (samples / 16)));
        }

        List<JxlChannel> coded = [];
        for (int c = 0; c < colorChannels; c++)
        {
            int hs = _frame.YCbCr ? HShift(_frame.JpegUpsampling[c]) : 0;
            int vs = _frame.YCbCr ? VShift(_frame.JpegUpsampling[c]) : 0;
            coded.Add(new JxlChannel(Shrink(width, hs), Shrink(height, vs), hs, vs));
        }

        int colorUpsampling = System.Numerics.BitOperations.Log2((uint)_frame.Upsampling);
        for (int e = 0; e < _image.ExtraChannels.Count; e++)
        {
            int shift = System.Numerics.BitOperations.Log2((uint)_frame.ExtraChannelUpsampling[e]) - colorUpsampling;
            coded.Add(new JxlChannel(Shrink(width, shift), Shrink(height, shift), shift, shift));
        }

        int bitDepth = _image.BitDepth.BitsPerSample;
        JxlModularImage global = JxlModularImage.Read(r, coded, GlobalTree, bitDepth, _limits);
        int groupDim = _frame.GroupDim;
        int prefix = 0;
        while (prefix < global.Channels.Count && (prefix < global.MetaChannels || (global.Channels[prefix].Width <= groupDim && global.Channels[prefix].Height <= groupDim)))
        {
            prefix++;
        }

        global.Decode(r, 0, 0, prefix);

        // The channels after the prefix are coded in groups: squeezed by 3 or more both ways in LF groups, the rest in pass groups.
        int lfGroups = (int)_frame.LfGroups;
        int groups = (int)_frame.Groups;
        int groupsPerRow = (int)Ceil(width, groupDim);
        int lfGroupsPerRow = (int)Ceil(width, groupDim * 8L);
        for (int lf = 0; lf < lfGroups; lf++)
        {
            List<(int Index, int X, int Y)> rects = [];
            List<JxlChannel> parts = [];
            for (int i = prefix; i < global.Channels.Count; i++)
            {
                JxlChannel c = global.Channels[i];
                if (c.HShift < 3 || c.VShift < 3)
                {
                    continue;
                }

                int gw = groupDim >> (c.HShift - 3);
                int gh = groupDim >> (c.VShift - 3);
                AddRect(c, i, lf % lfGroupsPerRow, lf / lfGroupsPerRow, gw, gh, rects, parts);
            }

            DecodeGroup(r, lf + 1, 1 + lfGroups + lf, global, rects, parts, bitDepth);
        }

        int passes = _frame.Passes;
        int[] passOfShift = PassOfShift();
        for (int p = 0; p < passes; p++)
        {
            for (int g = 0; g < groups; g++)
            {
                List<(int Index, int X, int Y)> rects = [];
                List<JxlChannel> parts = [];
                for (int i = prefix; i < global.Channels.Count; i++)
                {
                    JxlChannel c = global.Channels[i];
                    if (c.HShift >= 3 && c.VShift >= 3)
                    {
                        continue;
                    }

                    int shift = Math.Min(c.HShift, c.VShift);
                    if (shift < 0 || passOfShift[shift] != p)
                    {
                        continue;
                    }

                    AddRect(c, i, g % groupsPerRow, g / groupsPerRow, groupDim >> c.HShift, groupDim >> c.VShift, rects, parts);
                }

                int section = 1 + lfGroups + 1 + (p * groups) + g;
                DecodeGroup(r, section, 1 + (3 * lfGroups) + 17 + (p * groups) + g, global, rects, parts, bitDepth);
            }
        }

        global.Inverse(r);
        return new JxlFrameChannels(width, height, global.Channels, colorChannels);
    }

    private static int HShift(int mode) => mode is 1 or 2 ? 1 : 0;

    private static int VShift(int mode) => mode is 1 or 3 ? 1 : 0;

    private static int Shrink(int size, int shift) => shift <= 0 ? size : (int)Ceil(size, 1L << shift);

    private static long Ceil(long a, long b) => (a + b - 1) / b;

    private static void AddRect(JxlChannel c, int index, int gx, int gy, int gw, int gh, List<(int Index, int X, int Y)> rects, List<JxlChannel> parts)
    {
        if (gw == 0 || gh == 0)
        {
            throw new CorruptDataException(JxlFormat.Info.Name, -1, "A channel is squeezed beyond its group size.");
        }

        int x0 = gx * gw;
        int y0 = gy * gh;
        int w = Math.Min(gw, c.Width - x0);
        int h = Math.Min(gh, c.Height - y0);
        if (w <= 0 || h <= 0)
        {
            return;
        }

        rects.Add((index, x0, y0));
        parts.Add(new JxlChannel(w, h, c.HShift, c.VShift));
    }

    /// <summary>Gets which pass codes the channels of each squeeze shift (0 to 2), from the passes' downsampling (G.4.2).</summary>
    private int[] PassOfShift()
    {
        int[] pass = new int[3];
        int last = _frame.Passes - 1;
        List<(int Pass, int Min, int Max)> ranges = [];
        int max = 3;
        for (int i = 0; i < _frame.Downsample.Length; i++)
        {
            int min = System.Numerics.BitOperations.Log2((uint)_frame.Downsample[i]);
            ranges.Add((_frame.LastPass[i], min, max));
            max = min;
        }

        ranges.Add((last, 0, max));
        for (int s = 0; s < 3; s++)
        {
            pass[s] = last;
            foreach ((int p, int min, int maxShift) in ranges.OrderBy(t => t.Pass))
            {
                if (s >= min && s < maxShift)
                {
                    pass[s] = p;
                    break;
                }
            }
        }

        return pass;
    }

    private void DecodeGroup(JxlBitReader single, int section, int stream, JxlModularImage global, List<(int Index, int X, int Y)> rects, List<JxlChannel> parts, int bitDepth)
    {
        JxlBitReader r = _toc.Sizes.Length == 1 ? single : Section(section);
        if (parts.Count == 0)
        {
            return;
        }

        JxlModularImage image = JxlModularImage.Read(r, parts, GlobalTree, bitDepth, _limits);
        image.Decode(r, stream, 0, image.Channels.Count);
        image.Inverse(r);
        for (int k = 0; k < rects.Count; k++)
        {
            (int index, int x0, int y0) = rects[k];
            JxlChannel target = global.Channels[index];
            JxlChannel part = image.Channels[k];
            for (int y = 0; y < part.Height; y++)
            {
                part.Row(y).CopyTo(target.Data.AsSpan(((y0 + y) * target.Width) + x0, part.Width));
            }
        }
    }

    /// <summary>Gets a reader for section <paramref name="index"/>; a single-section frame shares one reader for all.</summary>
    private JxlBitReader Section(int index)
    {
        if (_toc.Sizes.Length == 1)
        {
            return _single ??= Reader(0, _toc.Sizes[0]);
        }

        (long offset, uint size) = _toc.Section(index);
        return Reader(offset, size);
    }

    private JxlBitReader Reader(long offset, uint size)
    {
        long start = _offset + offset;
        if (start + size > _codestream.Length)
        {
            throw new CorruptDataException(JxlFormat.Info.Name, start, "A frame section runs past the end of the codestream.");
        }

        return new JxlBitReader(_codestream, (int)start, (int)size);
    }
}
