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

    /// <summary>Gets the enumerated color encoding; ignored when <see cref="Icc"/> is set.</summary>
    internal JxlColorEncoding ColorEncoding { get; init; } = new();

    /// <summary>Gets an ICC profile to store instead of an enumerated encoding, or <see langword="null"/>.</summary>
    internal byte[]? Icc { get; init; }

    /// <summary>Gets the EXIF orientation, 1 to 8.</summary>
    internal int Orientation { get; init; } = 1;

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
        if (input.Color.Count is not (1 or 3))
        {
            throw new ArgumentException("A JPEG XL image has one or three color channels.", nameof(input));
        }

        JxlBitWriter w = new();
        WriteImageHeader(w, input);
        if (input.Icc is not null)
        {
            JxlIccEncoder.Write(w, input.Icc);
        }

        w.ZeroPadToByte();

        int bits = Math.Max(input.BitDepth.BitsPerSample, input.Alpha is null ? 0 : input.AlphaBitDepth.BitsPerSample);
        int effort = Math.Clamp(input.Effort, 1, 7);
        bool fast = effort == 1 && bits <= 16;
        int groupShift = fast ? 1 : JxlModularEncoder.GroupShift(input.Width, input.Height);
        // The fast path only reads the planes; the Modular encoder transforms copies of them.
        int[][] planes = [.. input.Color, .. input.Alpha is null ? [] : new[] { input.Alpha }];
        List<JxlChannel> channels = fast ? [] : [.. planes.Select(p => new JxlChannel(0, 0, 0, 0) { Data = (int[])p.Clone() })];
        for (int i = 0; i < channels.Count; i++)
        {
            // JxlChannel sizes come from the constructor; make full-size channels around the copied samples.
            channels[i] = new JxlChannel(input.Width, input.Height, 0, 0) { Data = channels[i].Data };
        }

        List<byte[]> sections = fast
            ? JxlFastLossless.Encode(planes, input.Width, input.Height, bits, input.Parallelism)
            : JxlModularEncoder.Encode(channels, input.Color.Count, bits, effort, input.Width, input.Height, groupShift, input.Parallelism);

        WriteFrameHeader(w, input, groupShift);
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
        bool extra = input.Orientation != 1;
        w.WriteBool(extra);
        if (extra)
        {
            w.Write((ulong)(input.Orientation - 1), 3);
            w.WriteBool(false); // intrinsic size
            w.WriteBool(false); // preview
            w.WriteBool(false); // animation
        }

        WriteBitDepth(w, input.BitDepth);
        bool sixteen = input.BitDepth.BitsPerSample <= 12 && (input.Alpha is null || input.AlphaBitDepth.BitsPerSample <= 12);
        w.WriteBool(sixteen);
        w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 4, 1, 12), input.Alpha is null ? 0u : 1u);
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

        w.WriteBool(input.Xyb);
        WriteColorEncoding(w, input);
        if (extra)
        {
            w.WriteBool(true); // default tone mapping
        }

        w.WriteNoExtensions();
        w.WriteBool(true); // default CustomTransformData
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

    /// <summary>Writes the frame header (C.2): a last regular Modular frame, no crop or blending, Gaborish and EPF off.</summary>
    private static void WriteFrameHeader(JxlBitWriter w, JxlEncodeInput input, int groupShift)
    {
        int extras = input.Alpha is null ? 0 : 1;
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
        w.WriteBool(false); // no crop
        JxlU32 mode = new(0, 0, 1, 0, 2, 0, 3, 2);
        w.WriteU32(mode, 0); // replace
        for (int i = 0; i < extras; i++)
        {
            w.WriteU32(mode, 0);
        }

        w.WriteBool(true); // last frame
        w.WriteU32(new JxlU32(0, 0, 0, 4, 16, 5, 48, 10), 0); // no name

        // Loop filter: not all default, Gaborish off, no EPF.
        w.WriteBool(false);
        w.WriteBool(false);
        w.Write(0, 2);
        w.WriteNoExtensions();
        w.WriteNoExtensions();
    }
}
