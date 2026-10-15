namespace Egami.Formats.Jxl;

/// <summary>The kinds of extra channel (ISO/IEC 18181-1 table D.9).</summary>
internal enum JxlExtraChannelType
{
    Alpha = 0,
    Depth = 1,
    SpotColor = 2,
    SelectionMask = 3,
    Black = 4,
    Cfa = 5,
    Thermal = 6,
    NonOptional = 15,
    Optional = 16,
}

/// <summary>A sample format: integer, or floating point with <see cref="ExponentBits"/> exponent bits.</summary>
internal readonly record struct JxlBitDepth(int BitsPerSample, int ExponentBits)
{
    internal bool IsFloat => ExponentBits > 0;

    internal static JxlBitDepth Read(JxlBitReader r)
    {
        if (r.ReadBool())
        {
            int bits = (int)r.ReadU32(new JxlU32(32, 0, 16, 0, 24, 0, 1, 6));
            int exponent = (int)r.ReadBits(4) + 1;
            int mantissa = bits - exponent - 1;
            if (exponent < 2 || exponent > 8 || mantissa < 2 || mantissa > 23)
            {
                throw r.Corrupt($"A float sample of {bits} bits with {exponent} exponent bits is not allowed.");
            }

            return new JxlBitDepth(bits, exponent);
        }

        int integer = (int)r.ReadU32(new JxlU32(8, 0, 10, 0, 12, 0, 1, 6));
        return integer <= 31 ? new JxlBitDepth(integer, 0) : throw r.Corrupt($"Integer samples of {integer} bits are not allowed.");
    }
}

/// <summary>One extra channel's description (D.3.6).</summary>
internal sealed record JxlExtraChannelInfo
{
    internal JxlExtraChannelType Type { get; init; }

    internal JxlBitDepth BitDepth { get; init; } = new(8, 0);

    internal int DimShift { get; init; }

    internal string Name { get; init; } = string.Empty;

    internal bool AlphaAssociated { get; init; }

    /// <summary>Gets a spot color's red, green, blue and solidity, or <see langword="null"/>.</summary>
    internal float[]? SpotColor { get; init; }

    internal uint CfaChannel { get; init; }

    internal static JxlExtraChannelInfo Read(JxlBitReader r)
    {
        if (r.ReadBool())
        {
            return new JxlExtraChannelInfo();
        }

        uint type = r.ReadEnum();
        if (type is > 6 and < 15 or > 16)
        {
            throw r.Corrupt($"Extra channel type {type} is unknown.");
        }

        JxlBitDepth depth = JxlBitDepth.Read(r);
        int shift = (int)r.ReadU32(new JxlU32(0, 0, 3, 0, 4, 0, 1, 3));
        if (shift > 3)
        {
            throw r.Corrupt($"An extra channel's dim_shift of {shift} is above 3.");
        }

        string name = JxlHeaders.ReadName(r);
        JxlExtraChannelInfo info = new() { Type = (JxlExtraChannelType)type, BitDepth = depth, DimShift = shift, Name = name };
        return (JxlExtraChannelType)type switch
        {
            JxlExtraChannelType.Alpha => info with { AlphaAssociated = r.ReadBool() },
            JxlExtraChannelType.SpotColor => info with { SpotColor = [r.ReadF16(), r.ReadF16(), r.ReadF16(), r.ReadF16()] },
            JxlExtraChannelType.Cfa => info with { CfaChannel = r.ReadU32(new JxlU32(1, 0, 0, 2, 3, 4, 19, 8)) },
            _ => info,
        };
    }
}

/// <summary>The color encoding's color space (table D.6).</summary>
internal enum JxlColorSpace
{
    Rgb = 0,
    Gray = 1,
    Xyb = 2,
    Unknown = 3,
}

/// <summary>A color encoding (D.3.7): an ICC profile follows, or the enumerated description.</summary>
internal sealed record JxlColorEncoding
{
    internal bool WantIcc { get; init; }

    internal JxlColorSpace ColorSpace { get; init; }

    /// <summary>Gets the white point enum: 1 D65, 2 custom, 10 E, 11 DCI.</summary>
    internal uint WhitePoint { get; init; } = 1;

    /// <summary>Gets the custom white point's x and y, or <see langword="null"/>.</summary>
    internal (double X, double Y)? WhiteXy { get; init; }

    /// <summary>Gets the primaries enum: 1 sRGB, 2 custom, 9 BT.2100, 11 P3.</summary>
    internal uint Primaries { get; init; } = 1;

    /// <summary>Gets the custom red, green and blue x and y, or <see langword="null"/>.</summary>
    internal (double X, double Y)[]? PrimariesXy { get; init; }

    /// <summary>Gets the gamma, as 1/γ times 10⁷, or 0 when <see cref="TransferFunction"/> is used.</summary>
    internal uint Gamma { get; init; }

    /// <summary>Gets the transfer function enum: 1 BT.709, 2 unknown, 8 linear, 13 sRGB, 16 PQ, 17 DCI, 18 HLG.</summary>
    internal uint TransferFunction { get; init; } = 13;

    /// <summary>Gets the rendering intent: 0 perceptual, 1 relative, 2 saturation, 3 absolute.</summary>
    internal uint RenderingIntent { get; init; } = 1;

    internal static JxlColorEncoding Read(JxlBitReader r)
    {
        if (r.ReadBool())
        {
            return new JxlColorEncoding();
        }

        bool icc = r.ReadBool();
        uint space = r.ReadEnum();
        if (space > 3)
        {
            throw r.Corrupt($"Color space {space} is unknown.");
        }

        JxlColorEncoding c = new() { WantIcc = icc, ColorSpace = (JxlColorSpace)space };
        if (icc)
        {
            return c;
        }

        if (c.ColorSpace != JxlColorSpace.Xyb)
        {
            uint white = r.ReadEnum();
            c = white switch
            {
                1 or 10 or 11 => c with { WhitePoint = white },
                2 => c with { WhitePoint = 2, WhiteXy = ReadXy(r) },
                _ => throw r.Corrupt($"White point {white} is unknown."),
            };
        }

        if (c.ColorSpace is JxlColorSpace.Rgb or JxlColorSpace.Unknown)
        {
            uint primaries = r.ReadEnum();
            c = primaries switch
            {
                1 or 9 or 11 => c with { Primaries = primaries },
                2 => c with { Primaries = 2, PrimariesXy = [ReadXy(r), ReadXy(r), ReadXy(r)] },
                _ => throw r.Corrupt($"Primaries {primaries} are unknown."),
            };
        }

        if (c.ColorSpace == JxlColorSpace.Xyb)
        {
            // XYB implies its transfer function (a gamma of 1/3); nothing is stored.
            c = c with { Gamma = 3333333, TransferFunction = 0 };
        }
        else if (r.ReadBool())
        {
            uint gamma = r.ReadBits(24);
            if (gamma == 0 || gamma > 10_000_000 || (ulong)gamma * 8192 < 10_000_000)
            {
                throw r.Corrupt($"The gamma field {gamma} is out of range.");
            }

            c = c with { Gamma = gamma, TransferFunction = 0 };
        }
        else
        {
            uint tf = r.ReadEnum();
            c = tf is 1 or 2 or 8 or 13 or 16 or 17 or 18 ? c with { TransferFunction = tf } : throw r.Corrupt($"Transfer function {tf} is unknown.");
        }

        uint intent = r.ReadEnum();
        if (intent > 3)
        {
            throw r.Corrupt($"Rendering intent {intent} is unknown.");
        }

        if (c.ColorSpace == JxlColorSpace.Unknown || c.TransferFunction == 2)
        {
            throw r.Corrupt("A color encoding without an ICC profile must not have an unknown color space or transfer function.");
        }

        return c with { RenderingIntent = intent };
    }

    private static (double X, double Y) ReadXy(JxlBitReader r)
    {
        JxlU32 d = new(0, 19, 524288, 19, 1048576, 20, 2097152, 21);
        int x = JxlHeaders.UnpackSigned(r.ReadU32(d));
        int y = JxlHeaders.UnpackSigned(r.ReadU32(d));
        return (x / 1e6, y / 1e6);
    }
}

/// <summary>The tone mapping fields (D.3.8).</summary>
internal sealed record JxlToneMapping(float IntensityTarget = 255, float MinNits = 0, bool RelativeToMaxDisplay = false, float LinearBelow = 0)
{
    internal static JxlToneMapping Read(JxlBitReader r)
    {
        if (r.ReadBool())
        {
            return new JxlToneMapping();
        }

        float target = r.ReadF16();
        float min = r.ReadF16();
        bool relative = r.ReadBool();
        float below = r.ReadF16();
        if (target <= 0 || min < 0 || min > target || below < 0 || (relative && below > 1))
        {
            throw r.Corrupt("The tone mapping fields are out of range.");
        }

        return new JxlToneMapping(target, min, relative, below);
    }
}

/// <summary>The animation header (D.3.4).</summary>
internal sealed record JxlAnimation(uint TicksNumerator, uint TicksDenominator, uint Loops, bool HaveTimecodes);

/// <summary>The image header: SizeHeader, ImageMetadata and the custom transform data (D.2, D.3).</summary>
internal sealed record JxlImageHeader
{
    internal int Width { get; init; }

    internal int Height { get; init; }

    /// <summary>Gets the EXIF-style orientation, 1 to 8.</summary>
    internal int Orientation { get; init; } = 1;

    internal (int Width, int Height)? IntrinsicSize { get; init; }

    internal (int Width, int Height)? Preview { get; init; }

    internal JxlAnimation? Animation { get; init; }

    internal JxlBitDepth BitDepth { get; init; } = new(8, 0);

    internal bool Modular16BitBuffers { get; init; } = true;

    internal IReadOnlyList<JxlExtraChannelInfo> ExtraChannels { get; init; } = [];

    internal bool XybEncoded { get; init; } = true;

    internal JxlColorEncoding Color { get; init; } = new();

    internal JxlToneMapping ToneMapping { get; init; } = new();

    /// <summary>Gets the custom opsin inverse matrix (9 values), biases (3) and quantization biases (4), or <see langword="null"/> for the defaults.</summary>
    internal float[]? OpsinInverse { get; init; }

    /// <summary>Gets which upsampling weights are custom: bit 0 for 2x, 1 for 4x, 2 for 8x.</summary>
    internal int CustomWeightsMask { get; init; }

    internal float[]? Upsampling2Weights { get; init; }

    internal float[]? Upsampling4Weights { get; init; }

    internal float[]? Upsampling8Weights { get; init; }

    internal int ColorChannels => Color.ColorSpace == JxlColorSpace.Gray ? 1 : 3;

    internal int AlphaIndex
    {
        get
        {
            for (int i = 0; i < ExtraChannels.Count; i++)
            {
                if (ExtraChannels[i].Type == JxlExtraChannelType.Alpha)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>Gets the width after orientation, as it is shown.</summary>
    internal int DisplayWidth => Orientation > 4 ? Height : Width;

    /// <summary>Gets the height after orientation, as it is shown.</summary>
    internal int DisplayHeight => Orientation > 4 ? Width : Height;

    /// <summary>Reads the signature and the image header, checking the size against the limits.</summary>
    internal static JxlImageHeader Read(JxlBitReader r, SafetyLimits limits)
    {
        if (r.ReadBits(16) != 0x0AFF)
        {
            throw r.Corrupt("The codestream does not start with FF 0A.");
        }

        (int width, int height) = JxlHeaders.ReadSize(r);
        Check(r, limits, width, height);
        JxlImageHeader h = new() { Width = width, Height = height };
        if (!r.ReadBool())
        {
            h = ReadMetadata(r, limits, h);
        }

        // CustomTransformData (D.3.9) follows the metadata bundle, all-default or not.
        if (r.ReadBool())
        {
            return h;
        }

        float[]? opsin = null;
        if (h.XybEncoded && !r.ReadBool())
        {
            opsin = new float[16];
            for (int i = 0; i < 16; i++)
            {
                opsin[i] = r.ReadF16();
            }
        }

        int mask = (int)r.ReadBits(3);
        return h with
        {
            OpsinInverse = opsin,
            CustomWeightsMask = mask,
            Upsampling2Weights = (mask & 1) != 0 ? ReadF16s(r, 15) : null,
            Upsampling4Weights = (mask & 2) != 0 ? ReadF16s(r, 55) : null,
            Upsampling8Weights = (mask & 4) != 0 ? ReadF16s(r, 210) : null,
        };
    }

    /// <summary>Reads the ImageMetadata fields after its all_default flag.</summary>
    private static JxlImageHeader ReadMetadata(JxlBitReader r, SafetyLimits limits, JxlImageHeader h)
    {
        bool extra = r.ReadBool();
        if (extra)
        {
            int orientation = (int)r.ReadBits(3) + 1;
            (int Width, int Height)? intrinsic = r.ReadBool() ? JxlHeaders.ReadSize(r) : null;
            (int Width, int Height)? preview = r.ReadBool() ? JxlHeaders.ReadPreviewSize(r) : null;
            JxlAnimation? animation = r.ReadBool()
                ? new JxlAnimation(
                    r.ReadU32(new JxlU32(100, 0, 1000, 0, 1, 10, 1, 30)),
                    r.ReadU32(new JxlU32(1, 0, 1001, 0, 1, 8, 1, 10)),
                    r.ReadU32(new JxlU32(0, 0, 0, 3, 0, 16, 0, 32)),
                    r.ReadBool())
                : null;
            if (preview is (int pw, int ph))
            {
                Check(r, limits, pw, ph);
            }

            h = h with { Orientation = orientation, IntrinsicSize = intrinsic, Preview = preview, Animation = animation };
        }

        JxlBitDepth depth = JxlBitDepth.Read(r);
        bool buffers = r.ReadBool();
        int count = (int)r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 4, 1, 12));
        if (count > 256)
        {
            // Level 10 allows 256 extra channels at most (18181-2 table A.1).
            throw r.Corrupt($"The image has {count} extra channels; at most 256 are allowed.");
        }

        JxlExtraChannelInfo[] channels = new JxlExtraChannelInfo[count];
        for (int i = 0; i < count; i++)
        {
            channels[i] = JxlExtraChannelInfo.Read(r);
        }

        bool xyb = r.ReadBool();
        JxlColorEncoding color = JxlColorEncoding.Read(r);
        JxlToneMapping tone = extra ? JxlToneMapping.Read(r) : new JxlToneMapping();
        r.ReadExtensions();
        return h with { BitDepth = depth, Modular16BitBuffers = buffers, ExtraChannels = channels, XybEncoded = xyb, Color = color, ToneMapping = tone };
    }

    private static float[] ReadF16s(JxlBitReader r, int count)
    {
        float[] values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = r.ReadF16();
        }

        return values;
    }

    private static void Check(JxlBitReader r, SafetyLimits limits, long width, long height)
    {
        if (width > limits.MaxWidth)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxWidth), limits.MaxWidth, width);
        }

        if (height > limits.MaxHeight)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxHeight), limits.MaxHeight, height);
        }

        if (width * height > limits.MaxPixels)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), limits.MaxPixels, width * height);
        }

        _ = r;
    }
}

/// <summary>The kinds of frame (table F.2).</summary>
internal enum JxlFrameType
{
    Regular = 0,
    Lf = 1,
    ReferenceOnly = 2,
    SkipProgressive = 3,
}

/// <summary>How a frame blends into the canvas (table F.3).</summary>
internal enum JxlBlendMode
{
    Replace = 0,
    Add = 1,
    Blend = 2,
    MulAdd = 3,
    Mul = 4,
}

/// <summary>A blending description (F.2, BlendingInfo).</summary>
internal readonly record struct JxlBlending(JxlBlendMode Mode, uint AlphaChannel, bool Clamp, uint Source);

/// <summary>A frame header (F.2), with the dimensions its TOC depends on.</summary>
internal sealed record JxlFrameHeader
{
    internal JxlFrameType Type { get; init; }

    internal bool Modular { get; init; }

    internal ulong Flags { get; init; }

    internal bool YCbCr { get; init; }

    /// <summary>Gets the chroma subsampling modes of the Y, Cb and Cr channels (0 to 3) when <see cref="YCbCr"/>.</summary>
    internal int[] JpegUpsampling { get; init; } = [0, 0, 0];

    internal int Upsampling { get; init; } = 1;

    internal int[] ExtraChannelUpsampling { get; init; } = [];

    internal int GroupSizeShift { get; init; } = 1;

    internal int XqmScale { get; init; } = 3;

    internal int BqmScale { get; init; } = 2;

    internal int Passes { get; init; } = 1;

    internal int[] Shifts { get; init; } = [];

    internal int[] Downsample { get; init; } = [];

    internal int[] LastPass { get; init; } = [];

    /// <summary>Gets the LF level of an LF frame, 1 to 4, or 0.</summary>
    internal int LfLevel { get; init; }

    internal bool HaveCrop { get; init; }

    internal int X0 { get; init; }

    internal int Y0 { get; init; }

    internal int Width { get; init; }

    internal int Height { get; init; }

    internal JxlBlending Blending { get; init; }

    internal JxlBlending[] ExtraChannelBlending { get; init; } = [];

    internal uint Duration { get; init; }

    internal uint Timecode { get; init; }

    internal bool IsLast { get; init; } = true;

    internal int SaveAsReference { get; init; }

    internal bool SaveBeforeColorTransform { get; init; }

    internal string Name { get; init; } = string.Empty;

    internal bool Gaborish { get; init; } = true;

    internal int EpfIterations { get; init; } = 1;

    /// <summary>Gets whether the frame is shown: a regular or skip-progressive frame (not an LF or reference-only one).</summary>
    internal bool IsNormal => Type is JxlFrameType.Regular or JxlFrameType.SkipProgressive;

    /// <summary>Gets whether the frame ends a displayed animation frame once blended.</summary>
    internal bool IsKeyFrame => IsNormal && (IsLast || Duration != 0);

    internal int GroupDim => 128 << GroupSizeShift;

    /// <summary>Gets the size the frame's color channels are coded at: the crop, divided by the upsampling and the LF level.</summary>
    internal (long Width, long Height) CodedSize
    {
        get
        {
            long w = Width;
            long h = Height;
            if (LfLevel > 0)
            {
                w = Ceil(w, 1L << (3 * LfLevel));
                h = Ceil(h, 1L << (3 * LfLevel));
            }

            return (Ceil(w, Upsampling), Ceil(h, Upsampling));
        }
    }

    internal long Groups
    {
        get
        {
            (long w, long h) = CodedSize;
            return Ceil(w, GroupDim) * Ceil(h, GroupDim);
        }
    }

    internal long LfGroups
    {
        get
        {
            (long w, long h) = CodedSize;
            int hShift = YCbCr ? JpegUpsampling.Max(m => m is 1 or 2 ? 1 : 0) : 0;
            int vShift = YCbCr ? JpegUpsampling.Max(m => m is 1 or 3 ? 1 : 0) : 0;
            long xBlocks = Ceil(w, 8L << hShift) << hShift;
            long yBlocks = Ceil(h, 8L << vShift) << vShift;
            return Ceil(xBlocks, GroupDim) * Ceil(yBlocks, GroupDim);
        }
    }

    /// <summary>Gets the number of TOC entries: one for a single group and pass, else LF global, LF groups, HF global and each pass of each group.</summary>
    internal long TocEntries => Groups == 1 && Passes == 1 ? 1 : 1 + LfGroups + 1 + (Groups * Passes);

    /// <summary>Reads a frame header for an image (or its preview, when <paramref name="preview"/> is set).</summary>
    internal static JxlFrameHeader Read(JxlBitReader r, JxlImageHeader image, bool preview)
    {
        (int defaultWidth, int defaultHeight) = preview && image.Preview is (int pw, int ph) ? (pw, ph) : (image.Width, image.Height);
        int extras = image.ExtraChannels.Count;
        JxlFrameHeader f = new() { Width = defaultWidth, Height = defaultHeight, ExtraChannelUpsampling = Ones(extras, image), ExtraChannelBlending = new JxlBlending[extras], XqmScale = image.XybEncoded ? 3 : 2 };
        if (r.ReadBool())
        {
            return f;
        }

        JxlFrameType type = (JxlFrameType)r.ReadBits(2);
        if (preview && type != JxlFrameType.Regular)
        {
            throw r.Corrupt("A preview frame must be a regular frame.");
        }

        bool modular = r.ReadBool();
        ulong flags = r.ReadU64();
        bool lfFrame = (flags & 0x20) != 0;
        bool ycbcr = !image.XybEncoded && r.ReadBool();
        int[] jpeg = [0, 0, 0];
        if (ycbcr && !lfFrame)
        {
            for (int i = 0; i < 3; i++)
            {
                jpeg[i] = (int)r.ReadBits(2);
            }
        }

        int upsampling = 1;
        int[] ecUpsampling = Ones(extras, image);
        if (!lfFrame)
        {
            JxlU32 factor = new(1, 0, 2, 0, 4, 0, 8, 0);
            upsampling = (int)r.ReadU32(factor);
            for (int i = 0; i < extras; i++)
            {
                ecUpsampling[i] = (int)r.ReadU32(factor) << image.ExtraChannels[i].DimShift;
                if (ecUpsampling[i] < upsampling || ecUpsampling[i] > 8)
                {
                    throw r.Corrupt($"Extra channel {i} is upsampled {ecUpsampling[i]} times, against {upsampling} for color; it must be at least that and at most 8.");
                }
            }
        }

        int groupShift = modular ? (int)r.ReadBits(2) : 1;
        int xqm = image.XybEncoded ? 3 : 2;
        int bqm = 2;
        if (!modular && image.XybEncoded)
        {
            xqm = (int)r.ReadBits(3);
            bqm = (int)r.ReadBits(3);
        }

        int passes = 1;
        int[] shifts = [];
        int[] downsample = [];
        int[] lastPass = [];
        if (type != JxlFrameType.ReferenceOnly)
        {
            passes = (int)r.ReadU32(new JxlU32(1, 0, 2, 0, 3, 0, 4, 3));
            if (passes != 1)
            {
                int ds = (int)r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 1));
                if (ds > passes)
                {
                    throw r.Corrupt($"The frame has {ds} downsampled passes but only {passes} passes.");
                }

                shifts = new int[passes];
                for (int i = 0; i < passes - 1; i++)
                {
                    shifts[i] = (int)r.ReadBits(2);
                }

                downsample = new int[ds];
                for (int i = 0; i < ds; i++)
                {
                    downsample[i] = (int)r.ReadU32(new JxlU32(1, 0, 2, 0, 4, 0, 8, 0));
                    if (i > 0 && downsample[i] >= downsample[i - 1])
                    {
                        throw r.Corrupt("The pass downsampling factors must decrease.");
                    }
                }

                lastPass = new int[ds];
                for (int i = 0; i < ds; i++)
                {
                    lastPass[i] = (int)r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 0, 0, 3));
                    if ((i > 0 && lastPass[i] <= lastPass[i - 1]) || lastPass[i] >= passes)
                    {
                        throw r.Corrupt("The last-pass numbers must increase and stay below the pass count.");
                    }
                }
            }
        }

        int lfLevel = type == JxlFrameType.Lf ? (int)r.ReadU32(new JxlU32(1, 0, 2, 0, 3, 0, 4, 0)) : 0;
        bool crop = false;
        int x0 = 0;
        int y0 = 0;
        int width = defaultWidth;
        int height = defaultHeight;
        bool partial = false;
        if (type != JxlFrameType.Lf)
        {
            crop = r.ReadBool();
            if (crop)
            {
                JxlU32 d = new(0, 8, 256, 11, 2304, 14, 18688, 30);
                if (type is JxlFrameType.Regular or JxlFrameType.SkipProgressive)
                {
                    x0 = JxlHeaders.UnpackSigned(r.ReadU32(d));
                    y0 = JxlHeaders.UnpackSigned(r.ReadU32(d));
                }

                uint w = r.ReadU32(d);
                uint h = r.ReadU32(d);
                if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
                {
                    throw r.Corrupt($"The frame crop is {w}×{h}.");
                }

                (width, height) = ((int)w, (int)h);
                if (type is JxlFrameType.Regular or JxlFrameType.SkipProgressive)
                {
                    partial = x0 > 0 || y0 > 0 || (long)width + x0 < defaultWidth || (long)height + y0 < defaultHeight;
                }
            }
        }

        JxlBlending blending = default;
        JxlBlending[] ecBlending = new JxlBlending[extras];
        uint duration = 0;
        uint timecode = 0;
        bool isLast = false;
        if (type is JxlFrameType.Regular or JxlFrameType.SkipProgressive)
        {
            blending = ReadBlending(r, extras, partial);
            for (int i = 0; i < extras; i++)
            {
                ecBlending[i] = ReadBlending(r, extras, partial);
            }

            if (preview && (crop || blending.Mode != JxlBlendMode.Replace || ecBlending.Any(b => b.Mode != JxlBlendMode.Replace)))
            {
                throw r.Corrupt("A preview frame must not be cropped or blended.");
            }

            if (image.Animation is JxlAnimation animation)
            {
                duration = r.ReadU32(new JxlU32(0, 0, 1, 0, 0, 8, 0, 32));
                if (animation.HaveTimecodes)
                {
                    timecode = r.ReadBits(32);
                }
            }

            isLast = r.ReadBool();
        }

        int save = type != JxlFrameType.Lf && !isLast ? (int)r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 0)) : 0;
        bool saveBefore = type == JxlFrameType.Lf;
        if (type != JxlFrameType.Lf)
        {
            bool referenced = !isLast && (duration == 0 || save != 0);
            if (referenced && blending.Mode == JxlBlendMode.Replace && !partial && type is JxlFrameType.Regular or JxlFrameType.SkipProgressive)
            {
                saveBefore = r.ReadBool();
            }
            else if (type == JxlFrameType.ReferenceOnly)
            {
                saveBefore = r.ReadBool();
                if (!saveBefore && (width < image.Width || height < image.Height || x0 != 0 || y0 != 0))
                {
                    throw r.Corrupt("A reference-only frame kept after the color transform must cover the whole image.");
                }
            }
        }

        string name = JxlHeaders.ReadName(r);
        (bool gaborish, int epf) = ReadLoopFilter(r, modular);
        r.ReadExtensions();
        return new JxlFrameHeader
        {
            Type = type,
            Modular = modular,
            Flags = flags,
            YCbCr = ycbcr,
            JpegUpsampling = jpeg,
            Upsampling = upsampling,
            ExtraChannelUpsampling = ecUpsampling,
            GroupSizeShift = groupShift,
            XqmScale = xqm,
            BqmScale = bqm,
            Passes = passes,
            Shifts = shifts,
            Downsample = downsample,
            LastPass = lastPass,
            LfLevel = lfLevel,
            HaveCrop = crop,
            X0 = x0,
            Y0 = y0,
            Width = width,
            Height = height,
            Blending = blending,
            ExtraChannelBlending = ecBlending,
            Duration = duration,
            Timecode = timecode,
            IsLast = isLast,
            SaveAsReference = save,
            SaveBeforeColorTransform = saveBefore,
            Name = name,
            Gaborish = gaborish,
            EpfIterations = epf,
        };
    }

    private static int[] Ones(int count, JxlImageHeader image)
    {
        int[] ones = new int[count];
        for (int i = 0; i < count; i++)
        {
            ones[i] = 1 << image.ExtraChannels[i].DimShift;
        }

        return ones;
    }

    private static JxlBlending ReadBlending(JxlBitReader r, int extras, bool partial)
    {
        uint mode = r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 2));
        if (mode > 4)
        {
            throw r.Corrupt($"Blend mode {mode} is unknown.");
        }

        JxlBlendMode blend = (JxlBlendMode)mode;
        bool usesAlpha = extras > 0 && blend is JxlBlendMode.Blend or JxlBlendMode.MulAdd;
        uint alpha = 0;
        if (usesAlpha)
        {
            alpha = r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 3));
            if (alpha >= extras)
            {
                throw r.Corrupt($"Blending uses extra channel {alpha} as alpha, but there are {extras}.");
            }
        }

        bool clamp = (usesAlpha || blend == JxlBlendMode.Mul) && r.ReadBool();
        uint source = blend != JxlBlendMode.Replace || partial ? r.ReadU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 0)) : 0;
        return new JxlBlending(blend, alpha, clamp, source);
    }

    /// <summary>Reads the restoration filter fields (G.1); returns whether Gaborish is on and the EPF iterations.</summary>
    private static (bool Gaborish, int Epf) ReadLoopFilter(JxlBitReader r, bool modular)
    {
        if (r.ReadBool())
        {
            return (true, 1);
        }

        bool gaborish = r.ReadBool();
        if (gaborish && r.ReadBool())
        {
            for (int c = 0; c < 3; c++)
            {
                float w1 = r.ReadF16();
                float w2 = r.ReadF16();
                if (Math.Abs(1 + ((w1 + w2) * 4)) < 1e-8)
                {
                    throw r.Corrupt("The Gaborish weights make a kernel that sums to zero.");
                }
            }
        }

        int epf = (int)r.ReadBits(2);
        if (epf > 0)
        {
            if (!modular && r.ReadBool())
            {
                SkipF16(r, 8);
            }

            if (r.ReadBool())
            {
                SkipF16(r, 5);
            }

            if (r.ReadBool())
            {
                SkipF16(r, modular ? 3 : 4);
            }

            if (modular && r.ReadF16() < 1e-8)
            {
                throw r.Corrupt("The edge-preserving filter's sigma for Modular is too small.");
            }
        }

        r.ReadExtensions();
        return (gaborish, epf);
    }

    private static void SkipF16(JxlBitReader r, int count)
    {
        for (int i = 0; i < count; i++)
        {
            r.ReadF16();
        }
    }

    private static long Ceil(long a, long b) => (a + b - 1) / b;
}

/// <summary>
/// A frame's table of contents (F.3): the byte size of each section in codestream order, and where each section (LF global,
/// LF groups, HF global, then the pass groups) lies in the codestream when the TOC is permuted.
/// </summary>
internal sealed record JxlToc(uint[] Sizes, int[]? Permutation)
{
    /// <summary>Gets the bytes of the frame's sections in total.</summary>
    internal long TotalSize => Sizes.Sum(s => (long)s);

    /// <summary>Reads the TOC of <paramref name="frame"/>, leaving the reader at the first section.</summary>
    internal static JxlToc Read(JxlBitReader r, JxlFrameHeader frame)
    {
        long entries = frame.TocEntries;
        if (entries > 65536)
        {
            throw r.Corrupt($"The frame has {entries} sections; at most 65,536 are allowed.");
        }

        int[]? permutation = null;
        if (r.ReadBool())
        {
            JxlEntropyDecoder decoder = JxlEntropyDecoder.Read(r, 8);
            decoder.Begin(r);
            permutation = decoder.ReadPermutation(r, (int)entries, 0);
            decoder.Finish(r);
        }

        r.ZeroPadToByte();
        uint[] sizes = new uint[entries];
        JxlU32 d = new(0, 10, 1024, 14, 17408, 22, 4211712, 30);
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = r.ReadU32(d);
        }

        r.ZeroPadToByte();
        return new JxlToc(sizes, permutation);
    }

    /// <summary>Gets the offset from the end of the TOC and the size of section <paramref name="index"/> (in LF global, LF groups, HF global, pass groups order).</summary>
    internal (long Offset, uint Size) Section(int index)
    {
        int at = Permutation is null ? index : Permutation[index];
        long offset = 0;
        for (int i = 0; i < at; i++)
        {
            offset += Sizes[i];
        }

        return (offset, Sizes[at]);
    }
}

/// <summary>Small shared readers.</summary>
internal static class JxlHeaders
{
    internal static int UnpackSigned(uint value) => (int)(value >> 1) ^ -(int)(value & 1);

    /// <summary>Reads a SizeHeader (D.2).</summary>
    internal static (int Width, int Height) ReadSize(JxlBitReader r)
    {
        bool small = r.ReadBool();
        JxlU32 d = new(1, 9, 1, 13, 1, 18, 1, 30);
        uint height = small ? 8 * (r.ReadBits(5) + 1) : r.ReadU32(d);
        uint ratio = r.ReadBits(3);
        ulong width = ratio == 0 ? (small ? 8 * (r.ReadBits(5) + 1) : r.ReadU32(d)) : Ratio(ratio, height);
        return width > int.MaxValue ? throw r.Corrupt($"The image is {width} pixels wide.") : ((int)width, (int)height);
    }

    /// <summary>Reads a PreviewHeader (D.3.3).</summary>
    internal static (int Width, int Height) ReadPreviewSize(JxlBitReader r)
    {
        bool div8 = r.ReadBool();
        JxlU32 eighths = new(16, 0, 32, 0, 1, 5, 33, 9);
        JxlU32 d = new(1, 6, 65, 8, 321, 10, 1345, 12);
        uint height = div8 ? 8 * r.ReadU32(eighths) : r.ReadU32(d);
        uint ratio = r.ReadBits(3);
        ulong width = ratio == 0 ? (div8 ? 8 * r.ReadU32(eighths) : r.ReadU32(d)) : Ratio(ratio, height);
        return width > int.MaxValue ? throw r.Corrupt($"The preview is {width} pixels wide.") : ((int)width, (int)height);
    }

    /// <summary>Reads a name: a U32 length, then that many UTF-8 bytes.</summary>
    internal static string ReadName(JxlBitReader r)
    {
        int length = (int)r.ReadU32(new JxlU32(0, 0, 0, 4, 16, 5, 48, 10));
        if (length == 0)
        {
            return string.Empty;
        }

        Span<byte> bytes = stackalloc byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)r.ReadBits(8);
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static ulong Ratio(uint ratio, ulong height)
    {
        return ratio switch
        {
            1 => height,
            2 => height * 12 / 10,
            3 => height * 4 / 3,
            4 => height * 3 / 2,
            5 => height * 16 / 9,
            6 => height * 5 / 4,
            _ => height * 2,
        };
    }
}
