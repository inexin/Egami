using System.Buffers.Binary;
using System.Text;

using Egami.Formats.Ico;
using Egami.Metadata;

namespace Egami.Formats.Ani;

/// <summary>
/// An animated cursor's structure, written from Microsoft's description of the RIFF <c>ACON</c> form: an <c>anih</c> header, an
/// optional <c>rate</c> list (jiffies, 1/60 s, a step), an optional <c>seq </c> list (the stored frame each step shows), an
/// optional <c>INFO</c> list with the title and artist, and a <c>fram</c> list of <c>icon</c> chunks, each a whole icon or cursor
/// file (or raw bitmap data when the header's icon flag is clear). Every count, index and size is checked against the data before
/// it is used: CVE-2007-0038 came from trusting the <c>anih</c> size.
/// </summary>
internal sealed class AniFile
{
    internal const int SignatureLength = 12;
    internal const int HeaderLength = 36;

    /// <summary>The header flag that says the frames are icons or cursors; without it they are raw bitmaps.</summary>
    internal const uint IconFlag = 1;

    /// <summary>The header flag that says a <c>seq </c> chunk is present.</summary>
    internal const uint SequenceFlag = 2;

    private AniFile(EgamiInput input, ReadOnlyMemory<byte> data, bool strict)
    {
        Data = data;
        Strict = strict;
        Input = input;
    }

    /// <summary>Gets the whole file.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Gets the stored frames, in file order. A frame that could not be read has a <see cref="Picture.Problem"/>.</summary>
    public List<Picture> Pictures { get; } = [];

    /// <summary>Gets the stored frame each step shows, steps whose frame is missing or damaged left out.</summary>
    public List<int> Sequence { get; } = [];

    /// <summary>Gets each step's display time in jiffies, parallel to <see cref="Sequence"/>.</summary>
    public List<int> Rates { get; } = [];

    /// <summary>Gets the header's default display time in jiffies, as used: never 0.</summary>
    public int DefaultRate { get; private set; }

    /// <summary>Gets the header's width, height and bit count, which only raw-bitmap files use.</summary>
    public (int Width, int Height, int Bits) RawHeader { get; private set; }

    /// <summary>Gets whether the frames are raw bitmaps (the header's icon flag is clear).</summary>
    public bool RawBitmaps { get; private set; }

    /// <summary>Gets the title from the <c>INFO</c> list's <c>INAM</c>, or <see langword="null"/>.</summary>
    public string? Title { get; private set; }

    /// <summary>Gets the artist from the <c>INFO</c> list's <c>IART</c>, or <see langword="null"/>.</summary>
    public string? Artist { get; private set; }

    /// <summary>Gets what was wrong with the file, for the loaded raster's warnings.</summary>
    public WarningList Warnings { get; } = new();

    private EgamiInput Input { get; }

    private bool Strict { get; }

    /// <summary>Returns whether the data starts with a RIFF file of form type <c>ACON</c>.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= SignatureLength && header.StartsWith("RIFF"u8) && header.Slice(8, 4).SequenceEqual("ACON"u8);
    }

    /// <summary>
    /// Parses the chunks and every stored frame's directory, without decoding a pixel. In strict mode damage throws; in lenient mode
    /// it becomes a warning and the parts that can be used are kept. A file with no step left to show throws in both.
    /// </summary>
    internal static AniFile Parse(EgamiInput input, ReadOnlyMemory<byte> data, bool strict)
    {
        AniFile file = new(input, data, strict);
        file.ParseChunks();
        return file;
    }

    /// <summary>Gets every size some stored frame holds, largest first; for a tie in area, the one met first.</summary>
    public IReadOnlyList<Size> Sizes()
    {
        List<Size> sizes = [];
        foreach (Picture picture in Pictures)
        {
            foreach (Size size in picture.Sizes)
            {
                if (!sizes.Contains(size))
                {
                    sizes.Add(size);
                }
            }
        }

        return [.. sizes.Select((s, i) => (s, i)).OrderByDescending(p => (long)p.s.Width * p.s.Height).ThenBy(p => p.i).Select(p => p.s)];
    }

    private void ParseChunks()
    {
        ReadOnlySpan<byte> data = Data.Span;
        if (!Matches(data))
        {
            throw Input.Corrupt(0, "The file is not a RIFF file of form type ACON.");
        }

        long end = 8 + (long)BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (end > data.Length)
        {
            Problem("Ani.RiffSize", 4, $"The RIFF header says {end - 8} bytes; the file has {data.Length - 8}. The chunks that are there were read.", truncation: true);
            end = data.Length;
        }

        int[]? rates = null;
        int[]? sequence = null;
        bool header = false;
        uint flags = 0;
        int declaredFrames = 0;
        int declaredSteps = 0;
        long at = 12;
        while (at + 8 <= end)
        {
            Input.CancellationToken.ThrowIfCancellationRequested();
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)at..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(at + 4)..]);
            long body = at + 8;
            if (size > end - body)
            {
                Problem("Ani.ChunkPastEnd", at, $"The \"{FourCC(id)}\" chunk's {size} bytes run past the end of the file; the {end - body} that are there were read.", truncation: true);
                size = end - body;
            }

            ReadOnlySpan<byte> chunk = data.Slice((int)body, (int)size);
            switch (id)
            {
                case 0x68696E61: // anih
                    if (header)
                    {
                        Problem("Ani.ChunkRepeated", at, "The file has a second \"anih\" header; the first was used.");
                        break;
                    }

                    header = true;
                    (flags, declaredFrames, declaredSteps) = ReadHeader(chunk, at);
                    break;
                case 0x65746172: // rate
                    rates ??= ReadList(chunk, at, "rate");
                    break;
                case 0x20716573: // "seq "
                    sequence ??= ReadList(chunk, at, "seq ");
                    break;
                case 0x5453494C: // LIST
                    if (chunk.Length >= 4 && chunk[..4].SequenceEqual("fram"u8))
                    {
                        if (Pictures.Count > 0)
                        {
                            Problem("Ani.ChunkRepeated", at, "The file has a second \"fram\" list; the first was used.");
                            break;
                        }

                        ReadFrames(body + 4, body + size);
                    }
                    else if (chunk.Length >= 4 && chunk[..4].SequenceEqual("INFO"u8))
                    {
                        ReadInfo(body + 4, body + size);
                    }

                    break;
            }

            at = Next(data, body, size, end);
        }

        if (!header)
        {
            throw Input.Corrupt(12, "The file has no \"anih\" header.");
        }

        if (Pictures.Count == 0)
        {
            throw Input.Corrupt(12, "The file has no frames: its \"fram\" list is missing or empty.");
        }

        if (declaredFrames != Pictures.Count)
        {
            Problem("Ani.FrameCount", 12, $"The header says {declaredFrames} frames; the \"fram\" list holds {Pictures.Count}, which were used.");
        }

        if (((flags & SequenceFlag) != 0) != (sequence is not null))
        {
            Warnings.Add("Ani.SequenceFlag", sequence is null
                ? "The header says a \"seq \" chunk follows, but there is none; the frames play in stored order."
                : "The header does not say a \"seq \" chunk follows, but there is one; it was used.");
        }

        BuildSteps(sequence, rates, declaredSteps);
    }

    /// <summary>Reads the 36-byte header; another size is damage (CVE-2007-0038), and only its first 36 bytes are read.</summary>
    private (uint Flags, int Frames, int Steps) ReadHeader(ReadOnlySpan<byte> chunk, long at)
    {
        if (chunk.Length != HeaderLength)
        {
            if (chunk.Length < HeaderLength)
            {
                throw Input.Corrupt(at, $"The \"anih\" header has {chunk.Length} bytes; it needs {HeaderLength}.");
            }

            Problem("Ani.HeaderSize", at, $"The \"anih\" chunk has {chunk.Length} bytes instead of {HeaderLength}; the first {HeaderLength} were read.");
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(chunk);
        if (headerSize != HeaderLength)
        {
            Problem("Ani.HeaderSize", at + 8, $"The \"anih\" header says it is {headerSize} bytes; it is {HeaderLength}.");
        }

        uint frames = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
        uint steps = BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]);
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(chunk[12..]);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(chunk[16..]);
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(chunk[20..]);
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(chunk[28..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(chunk[32..]);
        RawBitmaps = (flags & IconFlag) == 0;
        RawHeader = ((int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue), (int)Math.Min(bits, 64));
        if (rate == 0)
        {
            Warnings.Add("Ani.ZeroRate", "The header's default display rate is 0; steps without their own rate show for one jiffy (1/60 s).", at + 8 + 28);
        }

        DefaultRate = (int)Math.Clamp(rate, 1, int.MaxValue);
        return (flags, (int)Math.Min(frames, int.MaxValue), (int)Math.Min(steps, int.MaxValue));
    }

    /// <summary>Reads a <c>rate</c> or <c>seq </c> chunk: one 32-bit number a step.</summary>
    private int[] ReadList(ReadOnlySpan<byte> chunk, long at, string name)
    {
        if (chunk.Length % 4 != 0)
        {
            Problem("Ani.ListSize", at, $"The \"{name}\" chunk's {chunk.Length} bytes are not a whole number of 4-byte entries; the extra bytes were ignored.");
        }

        Input.CheckFrameCount(chunk.Length / 4);
        int[] values = new int[chunk.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(chunk[(i * 4)..]), int.MaxValue);
        }

        return values;
    }

    /// <summary>Reads the <c>fram</c> list's <c>icon</c> chunks, each one stored frame.</summary>
    private void ReadFrames(long start, long end)
    {
        ReadOnlySpan<byte> data = Data.Span;
        long at = start;
        while (at + 8 <= end)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)at..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(at + 4)..]);
            long body = at + 8;
            if (size > end - body)
            {
                Problem("Ani.ChunkPastEnd", at, $"Frame {Pictures.Count}'s {size} bytes run past the end of the \"fram\" list; the {end - body} that are there were read.", truncation: true);
                size = end - body;
            }

            if (id == 0x6E6F6369) // icon
            {
                Input.CheckFrameCount(Pictures.Count + 1L);
                Pictures.Add(ReadPicture(Pictures.Count, (int)body, (int)size));
            }

            at = Next(data, body, size, end);
        }
    }

    /// <summary>Reads the title and artist from the <c>INFO</c> list; other entries are ignored.</summary>
    private void ReadInfo(long start, long end)
    {
        ReadOnlySpan<byte> data = Data.Span;
        long at = start;
        while (at + 8 <= end)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)at..]);
            long size = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(at + 4)..]), end - at - 8);
            string text = Text(data.Slice((int)(at + 8), (int)size));
            if (id == 0x4D414E49 && text.Length > 0) // INAM
            {
                Title ??= text;
            }
            else if (id == 0x54524149 && text.Length > 0) // IART
            {
                Artist ??= text;
            }

            at = Next(data, at + 8, size, end);
        }
    }

    /// <summary>Reads one stored frame's structure: an icon or cursor directory, or a raw bitmap's header.</summary>
    private Picture ReadPicture(int index, int offset, int length)
    {
        ReadOnlySpan<byte> bytes = Data.Span.Slice(offset, length);
        int type = bytes.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(bytes) == 0 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) : 0;
        if (type is IcoDirectory.IconType or IcoDirectory.CursorType && (!RawBitmaps || IcoDirectory.Matches(bytes, type)))
        {
            if (RawBitmaps)
            {
                Warnings.Add("Ani.IconInRawFile", $"The header says the frames are raw bitmaps, but frame {index} is an icon; it was read as one.", offset);
            }

            try
            {
                IcoDirectory directory = IcoDirectory.Parse(Input, bytes, type, Strict);
                return new Picture(index, offset, length, directory, null, null);
            }
            catch (CorruptDataException e) when (!Strict)
            {
                return new Picture(index, offset, length, null, null, $"Frame {index} is a damaged icon ({e.Message})");
            }
        }

        string? problem = RawFrame.TryRead(bytes, RawHeader, index, out RawFrame? raw);
        if (problem is not null && Strict)
        {
            throw Input.Corrupt(offset, problem);
        }

        return new Picture(index, offset, length, null, raw, problem);
    }

    /// <summary>
    /// Builds the steps from the <c>seq </c> and <c>rate</c> lists: without a sequence, step i shows frame i; a step without a rate,
    /// or with a rate of 0, uses the default. Steps that point at no frame or a damaged one are left out.
    /// </summary>
    private void BuildSteps(int[]? sequence, int[]? rates, int declaredSteps)
    {
        sequence ??= [.. Enumerable.Range(0, Pictures.Count)];
        if (declaredSteps != sequence.Length)
        {
            Problem("Ani.StepCount", 12, $"The header says {declaredSteps} steps; the {(sequence.Length == Pictures.Count && rates is null ? "frames give" : "\"seq \" chunk gives")} {sequence.Length}, which were used.");
        }

        if (rates is not null && rates.Length != sequence.Length)
        {
            Problem("Ani.RateCount", 12, $"The \"rate\" chunk has {rates.Length} entries for {sequence.Length} steps; {(rates.Length < sequence.Length ? "the steps without one use the default rate" : "the extra entries were ignored")}.");
        }

        bool zero = false;
        List<int> damaged = [];
        for (int step = 0; step < sequence.Length; step++)
        {
            int frame = sequence[step];
            if (frame >= Pictures.Count)
            {
                Problem("Ani.SequenceIndex", 12, $"Step {step} shows frame {frame}, but the file stores {Pictures.Count}; the step was left out.");
                continue;
            }

            if (Pictures[frame].Problem is string problem)
            {
                if (!damaged.Contains(frame))
                {
                    damaged.Add(frame);
                    Warnings.Add("Ani.FrameDamaged", problem + "; the steps that show it were left out.", Pictures[frame].Offset);
                }

                continue;
            }

            int rate = rates is not null && step < rates.Length ? rates[step] : DefaultRate;
            if (rate == 0)
            {
                zero = true;
                rate = DefaultRate;
            }

            Sequence.Add(frame);
            Rates.Add(rate);
        }

        if (zero)
        {
            Warnings.Add("Ani.ZeroRate", "A step's display rate is 0; it uses the default rate instead.");
        }

        if (Sequence.Count == 0)
        {
            throw Input.Corrupt(12, "The animation has no step that shows a readable frame.");
        }
    }

    /// <summary>Notes damage: in strict mode it throws, in lenient mode it is a warning.</summary>
    private void Problem(string id, long at, string message, bool truncation = false)
    {
        if (Strict)
        {
            throw new CorruptDataException(Input.FormatName, at, message) { IsTruncation = truncation };
        }

        Warnings.Add(id, message, at);
    }

    /// <summary>
    /// Returns where the chunk after one at <paramref name="body"/> starts: after the pad byte that RIFF puts after an odd-sized
    /// chunk, unless some writer left it out: then a chunk name ANI uses starts right after the data, and none after the pad byte.
    /// </summary>
    private static long Next(ReadOnlySpan<byte> data, long body, long size, long end)
    {
        long next = body + size;
        if ((size & 1) != 0 && next + 4 <= end && Known(data.Slice((int)next, 4)) && (next + 1 + 4 > end || !Known(data.Slice((int)next + 1, 4))))
        {
            return next;
        }

        return next + (size & 1);
    }

    /// <summary>Returns whether four bytes name a chunk animated cursors use.</summary>
    private static bool Known(ReadOnlySpan<byte> id)
    {
        return id.SequenceEqual("anih"u8) || id.SequenceEqual("rate"u8) || id.SequenceEqual("seq "u8) || id.SequenceEqual("LIST"u8)
            || id.SequenceEqual("icon"u8) || id.SequenceEqual("INAM"u8) || id.SequenceEqual("IART"u8) || id.SequenceEqual("ICOP"u8)
            || id.SequenceEqual("ICMT"u8);
    }

    private static string FourCC(uint id) => string.Create(4, id, (span, v) =>
    {
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(v >> (8 * i));
            span[i] = b is >= 0x20 and <= 0x7E ? (char)b : '?';
        }
    });

    /// <summary>Reads an <c>INFO</c> string: up to its first zero, as UTF-8 when it is valid UTF-8 and Windows-1252's Latin-1 subset otherwise.</summary>
    private static string Text(ReadOnlySpan<byte> bytes)
    {
        int zero = bytes.IndexOf((byte)0);
        if (zero >= 0)
        {
            bytes = bytes[..zero];
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).Trim();
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes).Trim();
        }
    }

    /// <summary>One stored frame: where its bytes lie, and an icon directory or a raw bitmap's header, or what is wrong with it.</summary>
    internal sealed record Picture(int Index, int Offset, int Length, IcoDirectory? Directory, RawFrame? Raw, string? Problem)
    {
        /// <summary>Gets the sizes the frame holds, in directory order.</summary>
        public IReadOnlyList<Size> Sizes =>
            Directory is not null ? [.. Directory.Entries.Select(e => new Size(e.Width, e.Height)).Distinct()]
            : Raw is not null ? [new Size(Raw.Width, Raw.Height)]
            : [];
    }
}

/// <summary>How a raw-bitmap frame is stored.</summary>
internal enum RawFrameKind
{
    /// <summary>A whole BMP file.</summary>
    BmpFile,

    /// <summary>A BMP info header, color table and rows, without the file header.</summary>
    Dib,

    /// <summary>A DIB with the icon layout: the header's height counts an AND mask's rows too.</summary>
    IconDib,

    /// <summary>Bare bottom-up rows at the <c>anih</c> header's size and bit count (24 or 32).</summary>
    Rows,
}

/// <summary>The layout of a raw-bitmap frame, from its own header or the <c>anih</c> header.</summary>
internal sealed record RawFrame(RawFrameKind Kind, int Width, int Height, int Bits, DibLayout? Dib)
{
    /// <summary>Reads a raw frame's layout; returns what is wrong with it, or <see langword="null"/>.</summary>
    internal static string? TryRead(ReadOnlySpan<byte> bytes, (int Width, int Height, int Bits) header, int index, out RawFrame? frame)
    {
        frame = null;
        if (bytes.Length >= 26 && bytes.StartsWith("BM"u8))
        {
            uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
            int width = dibSize == 12 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]) : BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
            int height = dibSize == 12 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]) : Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]));
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(dibSize == 12 ? 24 : 28)..]);
            if (width < 1 || height < 1)
            {
                return $"Frame {index} is a BMP file of size {width}×{height}";
            }

            frame = new RawFrame(RawFrameKind.BmpFile, width, height, bits, null);
            return null;
        }

        if (bytes.Length >= 16 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) is 12 or 40 or 52 or 56 or 108 or 124)
        {
            string? problem = DibLayout.TryRead(bytes, index, out DibLayout? dib);
            if (problem is not null)
            {
                return problem.Replace("Entry", "Frame", StringComparison.Ordinal).TrimEnd('.');
            }

            // DibLayout halves the height, as icons count the mask's rows; a plain DIB at the header's size does not.
            int fullHeight = dib!.HeaderSize == 12 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) : BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
            frame = header.Height > 0 && dib.Height == header.Height && fullHeight != header.Height
                ? new RawFrame(RawFrameKind.IconDib, dib.Width, dib.Height, dib.Bits, dib)
                : new RawFrame(RawFrameKind.Dib, dib.Width, fullHeight, dib.Bits, dib);
            return null;
        }

        if (header.Bits is 24 or 32 && header.Width > 0 && header.Height > 0)
        {
            long stride = (((long)header.Width * header.Bits) + 31) / 32 * 4;
            if (stride * header.Height > bytes.Length)
            {
                return $"Frame {index} holds {bytes.Length} bytes; {header.Width}×{header.Height} rows at {header.Bits} bits need {stride * header.Height}";
            }

            frame = new RawFrame(RawFrameKind.Rows, header.Width, header.Height, header.Bits, null);
            return null;
        }

        return $"Frame {index} is neither an icon nor a bitmap Egami can read";
    }
}
