namespace Egami.Formats.Jxl;

/// <summary>What the lossless encoder writes: the samples as integers (float samples as their bit patterns) and their description.</summary>
internal sealed record JxlEncodeInput
{
    internal required int Width { get; init; }

    internal required int Height { get; init; }

    /// <summary>Gets the color planes (one for gray, three for RGB), each <see cref="Width"/> by <see cref="Height"/>, row by row.</summary>
    internal required IReadOnlyList<int[]> Color { get; init; }

    /// <summary>Gets the alpha plane, or <see langword="null"/>.</summary>
    internal int[]? Alpha { get; init; }

    internal JxlBitDepth BitDepth { get; init; } = new(8, 0);

    internal JxlBitDepth AlphaBitDepth { get; init; } = new(8, 0);

    internal bool AlphaPremultiplied { get; init; }

    /// <summary>Gets whether the image has a black (K) extra channel, after alpha if there is one.</summary>
    internal bool HasBlack { get; init; }

    /// <summary>Gets the black samples a lossless image codes (0 full ink), or <see langword="null"/>; the lossy encoder takes black from its image.</summary>
    internal int[]? Black { get; init; }

    /// <summary>Gets the extra channels after alpha and black, each with its samples.</summary>
    internal IReadOnlyList<JxlExtraPlane> Extras { get; init; } = [];

    internal JxlBitDepth BlackBitDepth { get; init; } = new(16, 0);

    /// <summary>Gets the enumerated color encoding; ignored when <see cref="Icc"/> is set.</summary>
    internal JxlColorEncoding ColorEncoding { get; init; } = new();

    /// <summary>Gets an ICC profile to store instead of an enumerated encoding, or <see langword="null"/>.</summary>
    internal byte[]? Icc { get; init; }

    /// <summary>Gets the cd/m² of the brightest light (ToneMapping's intensity_target): 255 for SDR, 10,000 for PQ, 1,000 for HLG.</summary>
    internal float IntensityTarget { get; init; } = 255;

    /// <summary>Gets the EXIF orientation, 1 to 8.</summary>
    internal int Orientation { get; init; } = 1;

    /// <summary>Gets the animation header of an animated file, or <see langword="null"/> for a still image or layers without durations.</summary>
    internal JxlAnimation? Animation { get; init; }

    internal int Effort { get; init; } = 5;

    /// <summary>Gets whether the image is XYB encoded (a lossy VarDCT image) rather than kept in its own color space.</summary>
    internal bool Xyb { get; init; }

    /// <summary>Gets how many threads the encoder may use.</summary>
    internal int Parallelism { get; init; } = Environment.ProcessorCount;
}

/// <summary>
/// Writes a lossless JPEG XL codestream (ISO/IEC 18181-1): the image header, then one Modular frame with the restoration
/// filters off, its TOC and its sections. The header fields are those libjxl 0.12.0 writes for the same image.
/// </summary>
internal static class JxlLosslessEncoder
{
    /// <summary>Encodes the image as a bare codestream.</summary>
    internal static byte[] Encode(JxlEncodeInput input)
    {
        JxlBitWriter w = new();
        WriteHeaders(w, input);
        return EncodeFrame(w, input, JxlFrameInfo.Still, input.Width, input.Height);
    }

    /// <summary>Writes the image header and the ICC profile, if any, padded to a whole byte as the first frame needs.</summary>
    internal static void WriteHeaders(JxlBitWriter w, JxlEncodeInput input)
    {
        if (input.Color.Count is not (1 or 3))
        {
            throw new ArgumentException("A JPEG XL image has one or three color channels.", nameof(input));
        }

        WriteImageHeader(w, input);
        if (input.Icc is not null)
        {
            JxlIccEncoder.Write(w, input.Icc);
        }

        w.ZeroPadToByte();
    }

    /// <summary>
    /// Encodes <paramref name="input"/>'s planes as one Modular frame placed on a <paramref name="canvasWidth"/> by
    /// <paramref name="canvasHeight"/> canvas as <paramref name="frame"/> says, and returns <paramref name="w"/>'s bytes (the
    /// headers before it, or none) with the frame after them.
    /// </summary>
    internal static byte[] EncodeFrame(JxlBitWriter w, JxlEncodeInput input, JxlFrameInfo frame, int canvasWidth, int canvasHeight)
    {
        int bits = Math.Max(input.BitDepth.BitsPerSample, input.Alpha is null ? 0 : input.AlphaBitDepth.BitsPerSample);
        bits = Math.Max(bits, input.Black is null ? 0 : input.BlackBitDepth.BitsPerSample);
        bits = input.Extras.Aggregate(bits, (b, e) => Math.Max(b, e.Info.BitDepth.BitsPerSample));
        int effort = Math.Clamp(input.Effort, 1, 7);
        bool fast = effort == 1 && bits <= 16 && input.Black is null && input.Extras.Count == 0;
        if (effort == 1 && (input.Black is not null || input.Extras.Count > 0))
        {
            // libjxl's fast lossless path takes color and alpha only; cjxl -e 1 codes other extra channels at effort 2.
            effort = 2;
        }
        int groupShift = fast ? 1 : JxlModularEncoder.GroupShift(input.Width, input.Height);
        // The fast path only reads the planes; the Modular encoder transforms copies of them.
        int[][] planes = [.. input.Color, .. input.Alpha is null ? [] : new[] { input.Alpha }, .. input.Black is null ? [] : new[] { input.Black }];
        List<JxlChannel> channels = fast ? [] : [.. planes.Select(p => new JxlChannel(input.Width, input.Height, 0, 0) { Data = (int[])p.Clone() })];
        if (!fast)
        {
            foreach (JxlExtraPlane extra in input.Extras)
            {
                // A subsampled channel is coded at its own size, shifted as the decoder places it.
                int shift = extra.Info.DimShift;
                channels.Add(new JxlChannel(extra.Width, extra.Height, shift, shift) { Data = (int[])extra.Samples.Clone() });
            }
        }

        List<byte[]> sections = fast
            ? JxlFastLossless.Encode(planes, input.Width, input.Height, bits, input.Parallelism)
            : JxlModularEncoder.Encode(channels, input.Color.Count, input.BitDepth.BitsPerSample, effort, input.Width, input.Height, groupShift, input.Parallelism);

        WriteFrameHeader(w, input, groupShift, frame, canvasWidth, canvasHeight);
        w.WriteBool(false); // no TOC permutation
        w.ZeroPadToByte();
        JxlU32 size = new(0, 10, 1024, 14, 17408, 22, 4211712, 30);
        foreach (byte[] s in sections)
        {
            w.WriteU32(size, (uint)s.Length);
        }

        // The headers and TOC, then the sections, copied once into the file.
        byte[] head = w.ToArray();
        byte[] file = new byte[head.Length + sections.Sum(s => (long)s.Length)];
        head.CopyTo(file, 0);
        int at = head.Length;
        foreach (byte[] s in sections)
        {
            s.CopyTo(file, at);
            at += s.Length;
        }

        return file;
    }

    internal static void WriteImageHeader(JxlBitWriter w, JxlEncodeInput input)
    {
        w.Write(0x0AFF, 16);
        WriteSize(w, input.Width, input.Height);

        // ImageMetadata, written out: lossless keeps its color space instead of XYB, and lossy names its own.
        w.WriteBool(false);
        bool extra = input.Orientation != 1 || input.IntensityTarget != 255 || input.Animation is not null;
        w.WriteBool(extra);
        if (extra)
        {
            w.Write((ulong)(input.Orientation - 1), 3);
            w.WriteBool(false); // intrinsic size
            w.WriteBool(false); // preview
            w.WriteBool(input.Animation is not null);
            if (input.Animation is JxlAnimation animation)
            {
                JxlFrameWriter.WriteAnimation(w, animation);
            }
        }

        WriteBitDepth(w, input.BitDepth);
        bool sixteen = input.BitDepth.BitsPerSample <= 12 && (input.Alpha is null || input.AlphaBitDepth.BitsPerSample <= 12)
            && (!input.HasBlack || input.BlackBitDepth.BitsPerSample <= 12) && input.Extras.All(e => e.Info.BitDepth.BitsPerSample <= 12);
        w.WriteBool(sixteen);
        w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 4, 1, 12), (uint)Extras(input));
        if (input.Alpha is not null)
        {
            if (input.AlphaBitDepth == new JxlBitDepth(8, 0) && !input.AlphaPremultiplied)
            {
                w.WriteBool(true);
            }
            else
            {
                w.WriteBool(false);
                w.WriteEnum(0); // alpha
                WriteBitDepth(w, input.AlphaBitDepth);
                w.WriteU32(new JxlU32(0, 0, 3, 0, 4, 0, 1, 3), 0);
                w.WriteU32(new JxlU32(0, 0, 0, 4, 16, 5, 48, 10), 0);
                w.WriteBool(input.AlphaPremultiplied);
            }
        }

        if (input.HasBlack)
        {
            w.WriteBool(false);
            w.WriteEnum((uint)JxlExtraChannelType.Black);
            WriteBitDepth(w, input.BlackBitDepth);
            w.WriteU32(new JxlU32(0, 0, 3, 0, 4, 0, 1, 3), 0);
            w.WriteU32(new JxlU32(0, 0, 0, 4, 16, 5, 48, 10), 0);
        }

        foreach (JxlExtraPlane plane in input.Extras)
        {
            WriteExtraChannelInfo(w, plane.Info);
        }

        w.WriteBool(input.Xyb);
        WriteColorEncoding(w, input);
        if (extra)
        {
            bool tone = input.IntensityTarget == 255;
            w.WriteBool(tone); // default tone mapping
            if (!tone)
            {
                w.WriteF16(input.IntensityTarget);
                w.WriteF16(0); // min_nits
                w.WriteBool(false); // relative_to_max_display
                w.WriteF16(0); // linear_below
            }
        }

        w.WriteNoExtensions();
        w.WriteBool(true); // default CustomTransformData
    }

    /// <summary>Gets how many extra channels the image header lists: alpha, then black, then the others.</summary>
    internal static int Extras(JxlEncodeInput input) => (input.Alpha is null ? 0 : 1) + (input.HasBlack ? 1 : 0) + input.Extras.Count;

    /// <summary>Writes an ExtraChannelInfo (D.3.6) in full.</summary>
    private static void WriteExtraChannelInfo(JxlBitWriter w, JxlExtraChannelInfo info)
    {
        w.WriteBool(false);
        w.WriteEnum((uint)info.Type);
        WriteBitDepth(w, info.BitDepth);
        w.WriteU32(new JxlU32(0, 0, 3, 0, 4, 0, 1, 3), (uint)info.DimShift);
        JxlFrameWriter.WriteName(w, info.Name);
        switch (info.Type)
        {
            case JxlExtraChannelType.Alpha:
                w.WriteBool(info.AlphaAssociated);
                break;
            case JxlExtraChannelType.SpotColor:
                foreach (float v in info.SpotColor!)
                {
                    w.WriteF16(v);
                }

                break;
            case JxlExtraChannelType.Cfa:
                w.WriteU32(new JxlU32(1, 0, 0, 2, 3, 4, 19, 8), info.CfaChannel);
                break;
        }
    }

    private static void WriteBitDepth(JxlBitWriter w, JxlBitDepth depth)
    {
        w.WriteBool(depth.IsFloat);
        if (depth.IsFloat)
        {
            w.WriteU32(new JxlU32(32, 0, 16, 0, 24, 0, 1, 6), (uint)depth.BitsPerSample);
            w.Write((ulong)(depth.ExponentBits - 1), 4);
        }
        else
        {
            w.WriteU32(new JxlU32(8, 0, 10, 0, 12, 0, 1, 6), (uint)depth.BitsPerSample);
        }
    }

    /// <summary>Writes a SizeHeader (D.2): the small form for multiples of 8 up to 256, and an aspect ratio when one fits exactly.</summary>
    private static void WriteSize(JxlBitWriter w, int width, int height)
    {
        int ratio = 0;
        for (int r = 1; r <= 7; r++)
        {
            if (Ratio(r, height) == (ulong)width)
            {
                ratio = r;
                break;
            }
        }

        bool small = height % 8 == 0 && height <= 256 && (ratio != 0 || (width % 8 == 0 && width <= 256));
        w.WriteBool(small);
        JxlU32 d = new(1, 9, 1, 13, 1, 18, 1, 30);
        if (small)
        {
            w.Write((ulong)((height / 8) - 1), 5);
        }
        else
        {
            w.WriteU32(d, (uint)height);
        }

        w.Write((ulong)ratio, 3);
        if (ratio == 0)
        {
            if (small)
            {
                w.Write((ulong)((width / 8) - 1), 5);
            }
            else
            {
                w.WriteU32(d, (uint)width);
            }
        }
    }

    private static ulong Ratio(int ratio, ulong height) => ratio switch
    {
        1 => height,
        2 => height * 12 / 10,
        3 => height * 4 / 3,
        4 => height * 3 / 2,
        5 => height * 16 / 9,
        6 => height * 5 / 4,
        _ => height * 2,
    };

    private static ulong Ratio(int ratio, int height) => Ratio(ratio, (ulong)height);

    /// <summary>Writes the ColorEncoding (D.3.5): all default for sRGB, else the enumerated fields, or the want-ICC flag.</summary>
    private static void WriteColorEncoding(JxlBitWriter w, JxlEncodeInput input)
    {
        bool gray = input.Color.Count == 1;
        JxlColorEncoding c = input.ColorEncoding;
        if (input.Icc is not null)
        {
            w.WriteBool(false);
            w.WriteBool(true);
            w.WriteEnum(gray ? 1u : 0u);
            return;
        }

        bool srgb = !gray && c.WhitePoint == 1 && c.Primaries == 1 && c.Gamma == 0 && c.TransferFunction == 13 && c.RenderingIntent == 1;
        if (srgb)
        {
            w.WriteBool(true);
            return;
        }

        w.WriteBool(false);
        w.WriteBool(false); // no ICC
        w.WriteEnum(gray ? 1u : 0u);
        w.WriteEnum(c.WhitePoint);
        if (c.WhitePoint == 2)
        {
            WriteXy(w, c.WhiteXy!.Value);
        }

        if (!gray)
        {
            w.WriteEnum(c.Primaries);
            if (c.Primaries == 2)
            {
                foreach ((double X, double Y) xy in c.PrimariesXy!)
                {
                    WriteXy(w, xy);
                }
            }
        }

        w.WriteBool(c.Gamma != 0);
        if (c.Gamma != 0)
        {
            w.Write(c.Gamma, 24);
        }
        else
        {
            w.WriteEnum(c.TransferFunction);
        }

        w.WriteEnum(c.RenderingIntent);
    }

    private static void WriteXy(JxlBitWriter w, (double X, double Y) xy)
    {
        JxlU32 d = new(0, 19, 524288, 19, 1048576, 20, 2097152, 21);
        w.WriteU32(d, JxlTreeLearner.PackSigned((int)Math.Round(xy.X * 1e6)));
        w.WriteU32(d, JxlTreeLearner.PackSigned((int)Math.Round(xy.Y * 1e6)));
    }

    /// <summary>Writes the frame header (C.2): a regular Modular frame placed as <paramref name="frame"/> says, Gaborish and EPF off.</summary>
    private static void WriteFrameHeader(JxlBitWriter w, JxlEncodeInput input, int groupShift, JxlFrameInfo frame, int canvasWidth, int canvasHeight)
    {
        int extras = Extras(input);
        JxlU32 factor = new(1, 0, 2, 0, 4, 0, 8, 0);
        w.WriteBool(false);
        w.Write(0, 2); // regular frame
        w.Write(1, 1); // Modular
        w.WriteU64(0); // flags
        w.WriteBool(false); // not YCbCr
        w.WriteU32(factor, 1);
        for (int i = 0; i < extras; i++)
        {
            w.WriteU32(factor, 1);
        }

        w.Write((ulong)groupShift, 2);
        w.WriteU32(new JxlU32(1, 0, 2, 0, 3, 0, 4, 3), 1); // one pass
        JxlFrameWriter.WriteLayout(w, frame, input.Width, input.Height, canvasWidth, canvasHeight, extras, input.Animation is not null);

        // Loop filter: not all default, Gaborish off, no EPF.
        w.WriteBool(false);
        w.WriteBool(false);
        w.Write(0, 2);
        w.WriteNoExtensions();
        w.WriteNoExtensions();
    }
}
