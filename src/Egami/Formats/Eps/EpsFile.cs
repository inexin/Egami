using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Egami.Formats.Eps;

/// <summary>
/// The parts of an Encapsulated PostScript file, read without running the PostScript: the PostScript itself, the TIFF or Windows
/// metafile preview a DOS binary header points at, an EPSI bitmap preview, the DSC header comments and Photoshop's sections.
/// Written from Adobe's EPSF 3.0 specification (Technical Note 5002) and the Document Structuring Conventions 3.0.
/// <see cref="Raster.Load(ReadOnlyMemory{byte}, EgamiContext?, ReaderOptions?)"/> reads the same parts to load the preview; load
/// an <see cref="EpsFile"/> to get at them yourself, such as to draw a metafile preview as vectors.
/// </summary>
/// <remarks>
/// Damage is reported in <see cref="Warnings"/>, never thrown: sections that run past the end of the file are cut short or
/// dropped, and comments that cannot be read are left unset.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Eps.cs" region="eps-file"/>
/// </example>
public sealed class EpsFile
{
    /// <summary>The first four bytes of a DOS EPS binary header.</summary>
    internal static ReadOnlySpan<byte> BinarySignature => [0xC5, 0xD0, 0xD3, 0xC6];

    /// <summary>The length of the DOS EPS binary header: magic, three offset and length pairs, and the checksum.</summary>
    internal const int BinaryHeaderLength = 30;

    /// <summary>
    /// The most whitespace accepted before <c>%!PS</c>. EPS files start at <c>%!</c>, but some programs and servers wrote spaces or
    /// line breaks in front of it (a USGS figure from Micrografx has 32 spaces), which PostScript interpreters skip.
    /// </summary>
    internal const int MaxLeadingWhitespace = 64;

    /// <summary>Gets the signature length detection needs: the whitespace allowed before it and <c>%!PS</c>.</summary>
    internal const int SignatureLength = MaxLeadingWhitespace + 4;

    private const int MaxTextLength = 1024;

    private readonly WarningList _warnings;

    private EpsFile(ReadOnlyMemory<byte> data, WarningList warnings)
    {
        Data = data;
        _warnings = warnings;
    }

    /// <summary>Gets the PostScript, from its <c>%!PS</c> line to its end; the whole file when there is no binary header.</summary>
    public ReadOnlyMemory<byte> PostScript { get; private set; }

    /// <summary>Gets the TIFF preview the binary header points at, or empty memory when there is none.</summary>
    public ReadOnlyMemory<byte> TiffPreview { get; private set; }

    /// <summary>Gets the Windows metafile (WMF) preview the binary header points at, or empty memory when there is none.</summary>
    public ReadOnlyMemory<byte> WmfPreview { get; private set; }

    /// <summary>
    /// Gets the preview Load uses: the TIFF preview, else the metafile preview, else the EPSI bitmap, else
    /// <see cref="EpsPreviewKind.None"/>. A metafile preview needs a format that reads it registered in the context, such as
    /// Egami.Metafiles' <c>MetafileFormat.Info</c>; without one Load falls back to the EPSI bitmap when the file has one.
    /// </summary>
    public EpsPreviewKind Preview => Photoshop is { DataStart: >= 0, Mode: not 2 } ? EpsPreviewKind.PhotoshopImage : !TiffPreview.IsEmpty ? EpsPreviewKind.Tiff : !WmfPreview.IsEmpty ? EpsPreviewKind.Wmf : Epsi is not null ? EpsPreviewKind.Epsi : EpsPreviewKind.None;

    /// <summary>Gets whether the version comment names EPSF (<c>%!PS-Adobe-3.0 EPSF-3.0</c>); plain PostScript does not.</summary>
    public bool IsEncapsulated { get; private set; }

    /// <summary>
    /// Gets the bounding box in points: <c>%%HiResBoundingBox</c> when the file has a valid one, else <c>%%BoundingBox</c>, read from
    /// the trailer when the header defers it with <c>(atend)</c>. <see langword="null"/> when neither can be read.
    /// </summary>
    public EpsBoundingBox? BoundingBox { get; private set; }

    /// <summary>Gets the <c>%%Creator</c> comment, the program that wrote the file, or <see langword="null"/>.</summary>
    public string? Creator { get; private set; }

    /// <summary>Gets the <c>%%Title</c> comment, or <see langword="null"/>.</summary>
    public string? Title { get; private set; }

    /// <summary>Gets the <c>%%CreationDate</c> comment as written, which DSC leaves free-form, or <see langword="null"/>.</summary>
    public string? CreationDate { get; private set; }

    /// <summary>Gets the ICC profile of Photoshop's <c>%%BeginICCProfile</c> section, or empty memory.</summary>
    public ReadOnlyMemory<byte> IccProfile { get; private set; }

    /// <summary>Gets the first XMP packet in the PostScript, or empty memory.</summary>
    public ReadOnlyMemory<byte> Xmp { get; private set; }

    /// <summary>Gets the Photoshop image resources of the <c>%BeginPhotoshop</c> section, or empty memory.</summary>
    public ReadOnlyMemory<byte> PhotoshopResources { get; private set; }

    /// <summary>Gets what was damaged or left out while reading the file.</summary>
    public IReadOnlyList<EgamiDiagnostic> Warnings => _warnings;

    /// <summary>Gets the whole file.</summary>
    internal ReadOnlyMemory<byte> Data { get; }

    /// <summary>Gets the EPSI bitmap's size, depth and hex data, or <see langword="null"/>.</summary>
    internal EpsiSection? Epsi { get; private set; }

    /// <summary>Gets what Photoshop's <c>%ImageData</c> comment says, and where its data starts, or <see langword="null"/>.</summary>
    internal PhotoshopImageSection? Photoshop { get; private set; }

    internal WarningList WarningList => _warnings;

    /// <summary>Reads an EPS file held in memory. The parts are slices of <paramref name="data"/>, not copies.</summary>
    /// <param name="data">The file.</param>
    /// <param name="limits">The limits on metadata size, or <see langword="null"/> for <see cref="SafetyLimits.Default"/>.</param>
    /// <returns>The file's parts.</returns>
    /// <exception cref="CorruptDataException">The data is not EPS: it has neither a binary header nor a <c>%!PS</c> line.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Eps.cs" region="eps-file"/>
    /// </example>
    public static EpsFile Load(ReadOnlyMemory<byte> data, SafetyLimits? limits = null)
    {
        return Parse(data, limits ?? SafetyLimits.Default);
    }

    /// <summary>Reads an EPS file from a stream, to its end.</summary>
    /// <param name="stream">The file, from its current position. It is left open.</param>
    /// <param name="limits">The limits on file and metadata size, or <see langword="null"/> for <see cref="SafetyLimits.Default"/>.</param>
    /// <returns>The file's parts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="LimitExceededException">The file is larger than <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <exception cref="CorruptDataException">The data is not EPS: it has neither a binary header nor a <c>%!PS</c> line.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Eps.cs" region="eps-file"/>
    /// </example>
    public static EpsFile Load(Stream stream, SafetyLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        limits ??= SafetyLimits.Default;
        long max = Math.Min(limits.MaxTotalBytes, Array.MaxLength);
        using MemoryStream copy = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (copy.Length + read > max)
            {
                throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limits.MaxTotalBytes, copy.Length + read);
            }

            copy.Write(buffer, 0, read);
        }

        return Parse(copy.GetBuffer().AsMemory(0, (int)copy.Length), limits);
    }

    /// <summary>Returns whether the data starts like EPS or PostScript: a DOS binary header or <c>%!PS</c>.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.StartsWith(BinarySignature) || header[LeadingWhitespace(header)..].StartsWith("%!PS"u8);
    }

    /// <summary>
    /// Counts the spaces, tabs and line breaks before the first other byte, or returns 0 when there are more than
    /// <see cref="MaxLeadingWhitespace"/>. Only data whose first byte is whitespace is scanned.
    /// </summary>
    internal static int LeadingWhitespace(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data[0] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
        {
            return 0;
        }

        ReadOnlySpan<byte> head = data[..Math.Min(data.Length, MaxLeadingWhitespace + 1)];
        int count = head.IndexOfAnyExcept(" \t\r\n"u8);
        return count is > 0 and <= MaxLeadingWhitespace ? count : 0;
    }

    internal static EpsFile Parse(ReadOnlyMemory<byte> data, SafetyLimits limits)
    {
        ReadOnlySpan<byte> span = data.Span;
        EpsFile file = new(data, new WarningList());
        if (span.StartsWith(BinarySignature))
        {
            file.ReadBinaryHeader(span);
        }
        else if (span.StartsWith("%!"u8))
        {
            file.PostScript = data;
        }
        else if (LeadingWhitespace(span) is int skip and > 0 && span[skip..].StartsWith("%!"u8))
        {
            file.PostScript = data[skip..];
            file._warnings.Add("Eps.LeadingWhitespace", $"The file has {skip} bytes of whitespace before its %! line, which EPS does not allow; they were skipped.", 0);
        }
        else
        {
            throw new CorruptDataException(EpsFormat.Info.Name, 0, "The data has neither an EPS binary header nor a %!PS line.");
        }

        if (file.PostScript.Span.StartsWith("%!"u8))
        {
            file.ReadComments(limits);
        }
        else if (!file.PostScript.IsEmpty)
        {
            file._warnings.Add("Eps.NotPostScript", "The PostScript section the binary header points at does not start with %!; its comments were not read.");
        }

        return file;
    }

    /// <summary>Reads the binary header's table of sections: PostScript, metafile and TIFF, each an offset and a length.</summary>
    private void ReadBinaryHeader(ReadOnlySpan<byte> span)
    {
        if (span.Length < BinaryHeaderLength)
        {
            throw new CorruptDataException(EpsFormat.Info.Name, span.Length, "The file ends inside its 30-byte binary header.") { IsTruncation = true };
        }

        ushort checksum = BinaryPrimitives.ReadUInt16LittleEndian(span[28..]);
        if (checksum != 0xFFFF && checksum != 0 && !ChecksumMatches(span, checksum))
        {
            _warnings.Add("Eps.ChecksumMismatch", $"The binary header's checksum {checksum:X4} does not match the header; its offsets were used anyway.", 28);
        }

        PostScript = Section(span, 4, "PostScript", out int psAt);
        WmfPreview = Section(span, 12, "metafile preview", out int wmfAt);
        TiffPreview = Section(span, 20, "TIFF preview", out int tiffAt);
        (int Start, int End, string Name)[] sections =
        [
            (psAt, psAt + PostScript.Length, "PostScript"),
            (wmfAt, wmfAt + WmfPreview.Length, "metafile preview"),
            (tiffAt, tiffAt + TiffPreview.Length, "TIFF preview"),
        ];
        for (int i = 0; i < sections.Length; i++)
        {
            for (int j = i + 1; j < sections.Length; j++)
            {
                if (sections[i].End > sections[i].Start && sections[j].End > sections[j].Start && sections[i].Start < sections[j].End && sections[j].Start < sections[i].End)
                {
                    _warnings.Add("Eps.SectionsOverlap", $"The binary header's {sections[i].Name} and {sections[j].Name} overlap; each was read as the header gives it.");
                }
            }
        }
    }

    /// <summary>
    /// Returns whether <paramref name="checksum"/> is the XOR of the header's first 28 bytes, which the specification gives without
    /// saying how 28 bytes make a 16-bit value: taken as 14 little-endian words, or as bytes.
    /// </summary>
    private static bool ChecksumMatches(ReadOnlySpan<byte> header, ushort checksum)
    {
        ushort words = 0;
        byte bytes = 0;
        for (int i = 0; i < 28; i += 2)
        {
            words ^= BinaryPrimitives.ReadUInt16LittleEndian(header[i..]);
            bytes ^= (byte)(header[i] ^ header[i + 1]);
        }

        return checksum == words || checksum == bytes;
    }

    /// <summary>Returns the section the header's offset and length at <paramref name="at"/> give, cut to the file.</summary>
    private ReadOnlyMemory<byte> Section(ReadOnlySpan<byte> span, int at, string name, out int start)
    {
        start = 0;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 4)..]);
        if (length == 0)
        {
            return default;
        }

        if (offset < BinaryHeaderLength || offset >= (uint)span.Length)
        {
            _warnings.Add("Eps.SectionPastEnd", $"The binary header puts the {name} at byte {offset}, outside the {span.Length}-byte file; it was left out.", at);
            return default;
        }

        if (length > (uint)span.Length - offset)
        {
            _warnings.Add("Eps.SectionPastEnd", $"The binary header's {name} of {length} bytes runs past the end of the file; the {span.Length - offset} bytes there were read.", at + 4);
            length = (uint)span.Length - offset;
        }

        start = (int)offset;
        return Data.Slice((int)offset, (int)length);
    }

    /// <summary>Reads the DSC header comments, the EPSI and Photoshop sections, and the XMP packet.</summary>
    private void ReadComments(SafetyLimits limits)
    {
        ReadOnlySpan<byte> ps = PostScript.Span;
        int end = LineEnd(ps, 0);
        IsEncapsulated = ps[..end].IndexOf(" EPSF-"u8) >= 0;
        string? box = null, hiRes = null;
        int position = NextLine(ps, end);
        bool inHeader = true;
        byte[]? imageKeyword = null;
        int embedded = 0;
        while (position < ps.Length)
        {
            end = LineEnd(ps, position);
            ReadOnlySpan<byte> line = ps[position..end];
            int next = NextLine(ps, end);
            if (line.StartsWith("%%BeginDocument"u8))
            {
                // A placed EPS file (Illustrator embeds Photoshop's this way): its comments, previews and image belong to it,
                // not to this file, so they are passed over up to its %%EndDocument.
                embedded++;
                inHeader = false;
                position = next;
                continue;
            }

            if (embedded > 0)
            {
                if (line.StartsWith("%%EndDocument"u8))
                {
                    embedded--;
                }
                else if (line.StartsWith("%%BeginBinary:"u8) || line.StartsWith("%%BeginData:"u8))
                {
                    next = SkipBinary(ps, line, next);
                }

                position = next;
                continue;
            }

            if (imageKeyword is not null && Photoshop!.DataStart < 0 && line.TrimEnd(" \t"u8).SequenceEqual(imageKeyword))
            {
                // Photoshop's image data follows this line; it is binary or long hex, so the comments end here.
                Photoshop = Photoshop with { DataStart = next };
                break;
            }

            if (line.StartsWith("%ImageData:"u8))
            {
                if (Photoshop is null)
                {
                    imageKeyword = ReadImageData(line);
                }
            }
            else if (inHeader)
            {
                if (line.StartsWith("%%EndComments"u8) || line.IsEmpty || line[0] != (byte)'%' || line.StartsWith("%%Begin"u8))
                {
                    inHeader = false;
                }
                else
                {
                    if (Value(line, "%%BoundingBox:"u8) is string b)
                    {
                        box ??= b;
                    }
                    else if (Value(line, "%%HiResBoundingBox:"u8) is string h)
                    {
                        hiRes ??= h;
                    }
                    else if (Value(line, "%%Creator:"u8) is string c)
                    {
                        Creator ??= Text(c);
                    }
                    else if (Value(line, "%%Title:"u8) is string t)
                    {
                        Title ??= Text(t);
                    }
                    else if (Value(line, "%%CreationDate:"u8) is string d)
                    {
                        CreationDate ??= Text(d);
                    }

                    position = next;
                    continue;
                }
            }

            if (inHeader || line.StartsWith("%ImageData:"u8))
            {
            }
            else if (line.StartsWith("%%BeginPreview:"u8))
            {
                next = ReadEpsi(ps, line, next);
            }
            else if (line.StartsWith("%%BeginICCProfile:"u8))
            {
                next = ReadIcc(ps, line, next, limits);
            }
            else if (line.StartsWith("%BeginPhotoshop:"u8))
            {
                next = ReadHexSection(ps, next, "%EndPhotoshop"u8, limits, out byte[]? resources);
                if (resources is not null)
                {
                    PhotoshopResources = resources;
                }
            }
            else if ((line.StartsWith("%%BeginBinary:"u8) || line.StartsWith("%%BeginData:"u8)) && (imageKeyword is null || Photoshop!.DataStart >= 0))
            {
                // Photoshop wraps its start line and image in %%BeginBinary, so while the start line is still to come the
                // section is read line by line.
                next = SkipBinary(ps, line, next);
            }

            position = next;
        }

        BoundingBox = Box(ps, hiRes, "%%HiResBoundingBox:"u8, quiet: true) ?? Box(ps, box, "%%BoundingBox:"u8, quiet: false);
        ReadXmp(ps, limits);
    }

    /// <summary>Parses a bounding box comment's value, or the last such comment in the file when the value is <c>(atend)</c>.</summary>
    private EpsBoundingBox? Box(ReadOnlySpan<byte> ps, string? value, ReadOnlySpan<byte> keyword, bool quiet)
    {
        if (value is null)
        {
            return null;
        }

        if (value == "(atend)")
        {
            int at = ps.LastIndexOf(keyword);
            value = at < 0 ? null : Value(ps[at..LineEnd(ps, at)], keyword);
            if (value is null || value == "(atend)")
            {
                if (!quiet)
                {
                    _warnings.Add("Eps.BoundingBoxInvalid", "The %%BoundingBox comment says (atend), but the trailer has none; the file has no bounding box.");
                }

                return null;
            }
        }

        string[] parts = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        double[] numbers = new double[4];
        bool valid = parts.Length >= 4;
        for (int i = 0; valid && i < 4; i++)
        {
            valid = double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) && double.IsFinite(numbers[i]);
        }

        if (valid && numbers[2] > numbers[0] && numbers[3] > numbers[1])
        {
            return new EpsBoundingBox(numbers[0], numbers[1], numbers[2], numbers[3]);
        }

        if (!quiet)
        {
            _warnings.Add("Eps.BoundingBoxInvalid", $"The %%BoundingBox comment '{Truncate(value)}' is not four numbers enclosing an area; the file has no bounding box.");
        }

        return null;
    }

    /// <summary>
    /// Reads Photoshop's <c>%ImageData: columns rows depth mode padChannels blockSize encoding "start line"</c>, as Adobe's
    /// Photoshop file format specification gives it, and returns the start line, after which the image data follows.
    /// </summary>
    private byte[]? ReadImageData(ReadOnlySpan<byte> line)
    {
        string text = Latin1(line["%ImageData:"u8.Length..]);
        int quote = text.IndexOf('"');
        int close = quote < 0 ? -1 : text.LastIndexOf('"');
        string[] parts = (quote < 0 ? text : text[..quote]).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int[] values = new int[7];
        bool valid = close > quote && parts.Length >= 7;
        for (int i = 0; valid && i < 7; i++)
        {
            valid = int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]);
        }

        if (!valid || values[0] < 1 || values[1] < 1 || values[2] is not (1 or 8) || values[3] is < 1 or > 4 || values[4] is < 0 or > 32 || values[5] < 1)
        {
            _warnings.Add("Eps.ImageDataInvalid", $"The %ImageData comment '{Truncate(Latin1(line))}' cannot be read; Photoshop's image was left out.");
            return null;
        }

        Photoshop = new PhotoshopImageSection(values[0], values[1], values[2], values[3], values[4], values[5], values[6], -1);
        return Encoding.Latin1.GetBytes(text[(quote + 1)..close]);
    }

    /// <summary>Reads <c>%%BeginPreview: width height depth lines</c> and finds its hex lines, up to <c>%%EndPreview</c>.</summary>
    private int ReadEpsi(ReadOnlySpan<byte> ps, ReadOnlySpan<byte> line, int start)
    {
        string[] parts = Latin1(line["%%BeginPreview:"u8.Length..]).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int end = start;
        int stop = ps.Length;
        while (end < ps.Length)
        {
            int lineEnd = LineEnd(ps, end);
            if (ps[end..lineEnd].StartsWith("%%EndPreview"u8))
            {
                stop = end;
                end = NextLine(ps, lineEnd);
                break;
            }

            end = NextLine(ps, lineEnd);
        }

        if (Epsi is not null)
        {
            return end;
        }

        if (parts.Length >= 3
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) && width > 0
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) && height > 0
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int depth) && depth is 1 or 2 or 4 or 8)
        {
            Epsi = new EpsiSection(width, height, depth, start, stop - start, stop == ps.Length);
        }
        else
        {
            _warnings.Add("Eps.PreviewInvalid", $"The %%BeginPreview comment '{Truncate(Latin1(line))}' does not give a size and a depth of 1, 2, 4 or 8 bits; the EPSI preview was left out.");
        }

        return end;
    }

    /// <summary>Reads <c>%%BeginICCProfile: (name) count type</c>: hex lines up to <c>%%EndICCProfile</c>, or count binary bytes.</summary>
    private int ReadIcc(ReadOnlySpan<byte> ps, ReadOnlySpan<byte> line, int start, SafetyLimits limits)
    {
        string arguments = Latin1(line["%%BeginICCProfile:"u8.Length..]);
        int close = arguments.LastIndexOf(')');
        string[] parts = arguments[(close + 1)..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[1].Equals("Binary", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long count) && count >= 0)
        {
            if (count > ps.Length - start || count > limits.MaxMetadataBytes)
            {
                _warnings.Add("Eps.ProfileDamaged", $"The binary ICC profile of {count} bytes runs past the end of the file or the metadata limit; it was left out.");
                return ps.Length;
            }

            if (IccProfile.IsEmpty)
            {
                IccProfile = PostScript.Slice(start, (int)count);
            }

            return start + (int)count;
        }

        int next = ReadHexSection(ps, start, "%%EndICCProfile"u8, limits, out byte[]? profile);
        if (profile is null)
        {
            _warnings.Add("Eps.ProfileDamaged", "The hex ICC profile is larger than the metadata limit or has an odd number of digits; it was left out.");
        }
        else if (IccProfile.IsEmpty && profile.Length > 0)
        {
            IccProfile = profile;
        }

        return next;
    }

    /// <summary>
    /// Collects the hex digits of the lines from <paramref name="start"/> up to the line starting with <paramref name="endLine"/>,
    /// ignoring everything else, as EPSF's EPSI rules ask. <paramref name="bytes"/> is null when the digits are odd or too many.
    /// </summary>
    private static int ReadHexSection(ReadOnlySpan<byte> ps, int start, ReadOnlySpan<byte> endLine, SafetyLimits limits, out byte[]? bytes)
    {
        int position = start, stop = ps.Length, after = ps.Length;
        while (position < ps.Length)
        {
            int lineEnd = LineEnd(ps, position);
            if (ps[position..lineEnd].StartsWith(endLine))
            {
                stop = position;
                after = NextLine(ps, lineEnd);
                break;
            }

            position = NextLine(ps, lineEnd);
        }

        ReadOnlySpan<byte> hex = ps[start..stop];
        long digits = CountHex(hex);
        if (digits % 2 != 0 || digits / 2 > limits.MaxMetadataBytes)
        {
            bytes = null;
            return after;
        }

        bytes = new byte[digits / 2];
        DecodeHex(hex, bytes);
        return after;
    }

    internal static System.Buffers.SearchValues<byte> HexDigits { get; } = System.Buffers.SearchValues.Create("0123456789ABCDEFabcdef"u8);

    internal static long CountHex(ReadOnlySpan<byte> text)
    {
        long count = 0;
        while (!text.IsEmpty)
        {
            int run = text.IndexOfAnyExcept(HexDigits);
            if (run < 0)
            {
                return count + text.Length;
            }

            count += run;
            text = text[(run + 1)..];
            int skip = text.IndexOfAny(HexDigits);
            if (skip < 0)
            {
                break;
            }

            text = text[skip..];
        }

        return count;
    }

    /// <summary>Decodes hex digits into <paramref name="destination"/>, skipping anything else; returns the bytes written.</summary>
    internal static int DecodeHex(ReadOnlySpan<byte> text, Span<byte> destination)
    {
        int written = 0;
        int high = -1;
        foreach (byte c in text)
        {
            int nibble = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                _ => -1,
            };
            if (nibble < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = nibble;
                continue;
            }

            destination[written++] = (byte)((high << 4) | nibble);
            high = -1;
            if (written == destination.Length)
            {
                break;
            }
        }

        return written;
    }

    /// <summary>Skips the bytes a <c>%%BeginBinary: count</c> or <c>%%BeginData: count Binary Bytes</c> comment announces.</summary>
    private static int SkipBinary(ReadOnlySpan<byte> ps, ReadOnlySpan<byte> line, int start)
    {
        int colon = line.IndexOf((byte)':');
        string[] parts = Latin1(line[(colon + 1)..]).Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        bool binary = line.StartsWith("%%BeginBinary:"u8)
            || (parts.Length >= 2 && parts[1] == "Binary" && (parts.Length < 3 || parts[2] == "Bytes"));
        if (binary && parts.Length >= 1 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long count) && count >= 0)
        {
            return (int)Math.Min(ps.Length, start + count);
        }

        return start;
    }

    /// <summary>Finds the first XMP packet, by its <c>&lt;?xpacket</c> markers, as the XMP specification's packet scanning does.</summary>
    private void ReadXmp(ReadOnlySpan<byte> ps, SafetyLimits limits)
    {
        int begin = ps.IndexOf("<?xpacket begin="u8);
        if (begin < 0)
        {
            return;
        }

        int end = ps[begin..].IndexOf("<?xpacket end="u8);
        int close = end < 0 ? -1 : ps[(begin + end)..].IndexOf("?>"u8);
        if (close < 0)
        {
            _warnings.Add("Eps.XmpDamaged", "The XMP packet has no end marker; it was left out.", begin);
            return;
        }

        int length = end + close + 2;
        if (length > limits.MaxMetadataBytes)
        {
            _warnings.Add("Eps.XmpDamaged", $"The XMP packet of {length} bytes is larger than the metadata limit; it was left out.", begin);
            return;
        }

        Xmp = PostScript.Slice(begin, length);
    }

    /// <summary>Returns the value after <paramref name="keyword"/>, trimmed, or null when the line is another comment.</summary>
    private static string? Value(ReadOnlySpan<byte> line, ReadOnlySpan<byte> keyword)
    {
        return line.StartsWith(keyword) ? Latin1(line[keyword.Length..]).Trim() : null;
    }

    /// <summary>A DSC text value: a PostScript string's parentheses removed and its escapes undone, at most 1024 characters.</summary>
    private static string Text(string value)
    {
        if (value.Length >= 2 && value[0] == '(' && value[^1] == ')')
        {
            StringBuilder text = new(value.Length);
            for (int i = 1; i < value.Length - 1; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length - 1)
                {
                    c = value[++i];
                    c = c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => c };
                }

                text.Append(c);
            }

            value = text.ToString();
        }

        return value.Length > MaxTextLength ? value[..MaxTextLength] : value;
    }

    private static string Truncate(string value) => value.Length > 80 ? value[..80] + "…" : value;

    private static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, 4 * MaxTextLength)]);

    /// <summary>Returns where the line starting at <paramref name="start"/> ends: its CR or LF, or the end of the data.</summary>
    internal static int LineEnd(ReadOnlySpan<byte> text, int start)
    {
        int at = text[start..].IndexOfAny((byte)'\r', (byte)'\n');
        return at < 0 ? text.Length : start + at;
    }

    /// <summary>Returns where the next line starts after a line ending at <paramref name="end"/>: past CR, LF or CR LF.</summary>
    internal static int NextLine(ReadOnlySpan<byte> text, int end)
    {
        if (end < text.Length && text[end] == (byte)'\r')
        {
            end++;
        }

        if (end < text.Length && text[end] == (byte)'\n')
        {
            end++;
        }

        return end;
    }
}

/// <summary>An EPSI preview: its size and depth from <c>%%BeginPreview</c>, and where its hex lines are in the PostScript.</summary>
internal sealed record EpsiSection(int Width, int Height, int Depth, int Start, int Length, bool Unterminated);

/// <summary>
/// Photoshop's image: the <c>%ImageData</c> values (size, bits a channel, mode 1 gray or bitmap, 2 Lab, 3 RGB, 4 CMYK, padding
/// channels, bytes a block, encoding 1 binary or 2 hex) and where its data starts in the PostScript, -1 when its start line is missing.
/// </summary>
internal sealed record PhotoshopImageSection(int Width, int Height, int Depth, int Mode, int PadChannels, int BlockSize, int Encoding, int DataStart);
