namespace Egami.Metadata;

/// <summary>The unit of a <see cref="Resolution"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="metadata"/>
/// </example>
public enum ResolutionUnit
{
    /// <summary>No unit: the values only give the pixel aspect ratio.</summary>
    None,

    /// <summary>Pixels per inch.</summary>
    PerInch,

    /// <summary>Pixels per centimeter.</summary>
    PerCentimeter,

    /// <summary>Pixels per meter.</summary>
    PerMeter,
}

/// <summary>Physical pixel density.</summary>
/// <param name="X">Horizontal density.</param>
/// <param name="Y">Vertical density.</param>
/// <param name="Unit">The unit of both values.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="metadata"/>
/// </example>
public readonly record struct Resolution(double X, double Y, ResolutionUnit Unit)
{
    /// <summary>Returns the same density in pixels per inch. A resolution with no unit is returned unchanged.</summary>
    /// <returns>The density in pixels per inch.</returns>
    public Resolution ToPerInch()
    {
        double factor = Unit switch
        {
            ResolutionUnit.PerCentimeter => 2.54,
            ResolutionUnit.PerMeter => 0.0254,
            _ => 1,
        };
        return Unit == ResolutionUnit.None ? this : new Resolution(X * factor, Y * factor, ResolutionUnit.PerInch);
    }
}

/// <summary>What the raster's pixel values mean, as far as the file said.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="color-space-hint"/>
/// </example>
public enum ColorSpaceHint
{
    /// <summary>The file did not say. Egami treats the pixels as sRGB.</summary>
    Unknown,

    /// <summary>sRGB.</summary>
    Srgb,

    /// <summary>Linear light.</summary>
    Linear,

    /// <summary>A plain gamma curve, given by <see cref="RasterMetadata.Gamma"/>.</summary>
    Gamma,

    /// <summary>Described by the embedded <see cref="RasterMetadata.IccProfile"/>, which Egami keeps but does not apply.</summary>
    EmbeddedIcc,
}

/// <summary>A text entry, such as a PNG text chunk or a GIF comment.</summary>
/// <param name="Key">The keyword, such as "Title" or "Comment".</param>
/// <param name="Value">The text.</param>
/// <param name="Language">The language tag, if the format gives one.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="metadata"/>
/// </example>
public sealed record TextEntry(string Key, string Value, string? Language = null);

/// <summary>Format-specific details the reader found, such as the original color type or palette. Each format derives its own.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="format-details"/>
/// </example>
public abstract class FormatDetails
{
    /// <summary>Returns a deep copy.</summary>
    /// <returns>The copy.</returns>
    public abstract FormatDetails Copy();

    /// <summary>Forgets what the details say about color, once the pixels have been converted to another encoding.</summary>
    /// <summary>Notes that an operation changed the pixels, so details that describe the stored pixels as loaded no longer hold.</summary>
    internal virtual void PixelsChanged()
    {
    }

    internal virtual void ClearColorTags()
    {
    }

    /// <summary>Gets a gamma the format gives outside <see cref="RasterMetadata.Gamma"/> (a BMP's calibrated curve), or <see langword="null"/>.</summary>
    internal virtual double? CalibratedGamma => null;
}

/// <summary>
/// Metadata that belongs to the whole raster. It is kept apart from the pixels and survives operations unless an operation makes it wrong.
/// Metadata problems never fail a load: broken blocks are kept as bytes and noted in <see cref="Warnings"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="metadata"/>
/// </example>
public sealed class RasterMetadata
{
    private ParsedProfile? _parsed;
    private IptcData? _iptc;
    private ReadOnlyMemory<byte> _xmp;
    private XmpData? _xmpView;
    private bool _xmpParsed;
    private ReadOnlyMemory<byte> _iptcSource;
    private long _iptcLimit;

    /// <summary>Gets or sets the physical pixel density, if known.</summary>
    public Resolution? Resolution { get; set; }

    /// <summary>
    /// Gets or sets the EXIF block, parsed, or <see langword="null"/> if the file had none. Unedited, it is written back byte for
    /// byte.
    /// </summary>
    public ExifData? Exif { get; set; }

    /// <summary>
    /// Gets or sets the IPTC-IIM datasets, parsed on first use, or <see langword="null"/> if the file had none. JPEG keeps them in a
    /// Photoshop resource block (APP13), PNG in a "Raw profile type iptc" text chunk. Unedited, they are written back byte for byte.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="iptc-values"/>
    /// </example>
    public IptcData? Iptc
    {
        get
        {
            if (_iptc is null && !_iptcSource.IsEmpty)
            {
                _iptc = IptcData.Parse(_iptcSource.Span, _iptcLimit);
                foreach (string warning in _iptc.Warnings)
                {
                    WarningList.Add(_iptc.IsDamaged ? "Iptc.Damaged" : "Iptc.ValueTooLong", warning + " The IPTC block is kept as it was.");
                }

                _iptcSource = default;
                WarningList.Flush();
            }

            return _iptc;
        }

        set
        {
            _iptc = value;
            _iptcSource = default;
        }
    }

    /// <summary>Gets or sets the embedded ICC profile. Egami keeps and writes it back but does not apply it. Empty if none.</summary>
    public ReadOnlyMemory<byte> IccProfile { get; set; }

    /// <summary>
    /// Gets or sets the XMP packet as raw UTF-8 XML. Empty if none. Once <see cref="XmpData"/> has been edited, this returns the
    /// packet written from it; setting it drops the parsed view.
    /// </summary>
    public ReadOnlyMemory<byte> Xmp
    {
        get => _xmpView is { IsEdited: true } view ? view.ToBytes() : _xmp;
        set
        {
            _xmp = value;
            _xmpView = null;
            _xmpParsed = false;
        }
    }

    /// <summary>
    /// Gets or sets the XMP packet parsed: a live view of <see cref="Xmp"/>, read on first use, and <see langword="null"/> when
    /// there is no packet or it is not well-formed XML (the bytes are then kept and a warning added). Edits to the view are what
    /// <see cref="Xmp"/> returns and what writers save; assign an empty <see cref="Metadata.XmpData"/> to start a packet.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="xmp-live"/>
    /// </example>
    public XmpData? XmpData
    {
        get
        {
            if (!_xmpParsed)
            {
                _xmpParsed = true;
                if (!_xmp.IsEmpty)
                {
                    XmpData parsed = Metadata.XmpData.Parse(_xmp.Span);
                    if (parsed.IsDamaged)
                    {
                        foreach (string warning in parsed.Warnings)
                        {
                            WarningList.Add("Xmp.Damaged", warning + " The XMP packet is kept as it was.");
                        }

                        WarningList.Flush();
                    }
                    else
                    {
                        _xmpView = parsed;
                    }
                }
            }

            return _xmpView;
        }

        set
        {
            _xmp = value?.ToBytes() ?? default;
            _xmpView = value;
            _xmpParsed = true;
        }
    }

    /// <summary>Gets or sets what the pixel values mean.</summary>
    public ColorSpaceHint ColorSpace { get; set; }

    /// <summary>Gets or sets the file's gamma when <see cref="ColorSpace"/> is <see cref="ColorSpaceHint.Gamma"/>, as the exponent that decodes values to linear light.</summary>
    public double? Gamma { get; set; }

    /// <summary>
    /// Gets or sets the ITU-T H.273 code points the file gave (PNG cICP), or <see langword="null"/>. They take precedence over
    /// every other color tag in <see cref="GetColorProfile"/>.
    /// </summary>
    public Colors.ColorCodePoints? CodePoints { get; set; }

    /// <summary>
    /// Gets or sets the primaries and white point the file gave (PNG cHRM, BMP calibrated endpoints), or <see langword="null"/>.
    /// They apply with a gamma, linear or unspecified curve; an ICC profile or code points carry their own.
    /// </summary>
    public Colors.ColorPrimaries? Primaries { get; set; }

    /// <summary>Gets or sets the HDR mastering display and light levels the file gave (PNG mDCV and cLLI), or <see langword="null"/>.</summary>
    public Colors.HdrMetadata? Hdr { get; set; }

    /// <summary>
    /// Gets or sets the HDR gain map the file carried (Ultra HDR, ISO 21496-1 or Apple JPEGs), or <see langword="null"/>. The JPEG
    /// writer writes it back while the raster keeps the size it was read at; geometry operations carry it, and operations that
    /// change pixel values drop it.
    /// </summary>
    public Colors.GainMap? GainMap { get; set; }

    /// <summary>Gets or sets the name of the format the raster was read from, such as "PNG", or <see langword="null"/>.</summary>
    public string? FormatName { get; set; }

    /// <summary>Gets or sets format-specific details from the reader.</summary>
    public FormatDetails? FormatDetails { get; set; }

    /// <summary>Gets or sets how many times an animation plays. 0 means forever. Ignored for single-frame rasters.</summary>
    public int LoopCount { get; set; }

    /// <summary>Gets the text entries, in file order.</summary>
    public IList<TextEntry> Text { get; } = [];

    /// <summary>
    /// Gets the problems a lenient read recovered from, such as a bad checksum after valid pixel data, in the order found. Each has a
    /// stable <see cref="EgamiDiagnostic.Id"/>; one id is listed at most 10 times, then once more as a count of the rest. The same
    /// entries go to <see cref="EgamiContext.Diagnostics"/>.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Diagnostics.cs" region="warnings"/>
    /// </example>
    public IReadOnlyList<EgamiDiagnostic> Warnings => WarningList;

    /// <summary>Gets the warnings for readers to add to; outside readers use <see cref="Formats.EgamiInput.Warn(Raster, string, string, long?)"/>.</summary>
    internal WarningList WarningList { get; } = new();

    /// <summary>
    /// Sends the Warnings to <paramref name="handler"/> from now on, flushed as the load ends and on each lazy parse, when there are
    /// any or a block parsed on first use (XMP, IPTC, ICC) may add some; otherwise nothing is allocated.
    /// </summary>
    /// <returns>Whether the Warnings were bound.</returns>
    internal bool BindWarnings(Action<EgamiDiagnostic> handler)
    {
        if (WarningList.Count > 0 || WarningList.IsBound || (!_xmp.IsEmpty && !_xmpParsed) || !_iptcSource.IsEmpty || !IccProfile.IsEmpty)
        {
            WarningList.Bind(handler);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns what the raster's pixel values mean, resolved from everything the file said, in this order: the H.273
    /// <see cref="CodePoints"/> (PNG cICP), the embedded ICC profile, an sRGB tag, a gamma or linear tag with the file's
    /// <see cref="Primaries"/> (sRGB's when it has none), the EXIF marker of an Adobe RGB camera, primaries alone with the sRGB
    /// curve, and sRGB when the file said nothing. A damaged or unsupported profile, or code points Egami does not know, count as
    /// saying nothing. PNG's gAMA 45455 (1/2.2) with no cHRM, or with sRGB's, counts as sRGB, which is what such files mean.
    /// </summary>
    /// <returns>The encoding: RGB unless an ICC profile says gray or CMYK. Gray pixels are read with an RGB encoding's curve.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="convert"/>
    /// </example>
    public Colors.ColorProfile GetColorProfile()
    {
        if (CodePoints is Colors.ColorCodePoints codes && Colors.ColorProfile.FromCodePoints(codes) is Colors.ColorProfile coded)
        {
            return coded;
        }

        if (!IccProfile.IsEmpty && ProfileFromIcc() is Colors.ColorProfile fromIcc)
        {
            return fromIcc;
        }

        Colors.ColorPrimaries? primaries = Primaries is { IsValid: true } p ? p : null;
        bool srgbPrimaries = primaries is null || primaries.IsCloseTo(Colors.ColorPrimaries.Rec709);
        double? gamma = ColorSpace == ColorSpaceHint.Gamma ? Gamma : FormatDetails?.CalibratedGamma;
        if (ColorSpace == ColorSpaceHint.Srgb)
        {
            return Colors.ColorProfile.Srgb;
        }

        if (ColorSpace == ColorSpaceHint.Linear)
        {
            return srgbPrimaries ? Colors.ColorProfile.LinearSrgb : Colors.ColorProfile.Create(primaries!, Colors.TransferFunction.Linear, "Linear");
        }

        if (gamma is double g && double.IsFinite(g) && g > 0)
        {
            if (srgbPrimaries && Math.Abs(g - (100_000.0 / 45_455)) < 1e-6)
            {
                return Colors.ColorProfile.Srgb;
            }

            return Colors.ColorProfile.Create(primaries ?? Colors.ColorPrimaries.Rec709, Colors.TransferFunction.Gamma(g), $"Gamma {g:0.###}");
        }

        if (IsAdobeRgbCamera(Exif))
        {
            return Colors.ColorProfile.AdobeRgb;
        }

        return srgbPrimaries ? Colors.ColorProfile.Srgb : Colors.ColorProfile.Create(primaries!, Colors.TransferFunction.Srgb);
    }

    // The profile parsed from IccProfile, kept while the same bytes are set, so planning a chain does not parse it again; null
    // when conversion cannot use it, which counts as no profile, as the design says for damaged ones.
    private Colors.ColorProfile? ProfileFromIcc()
    {
        ReadOnlyMemory<byte> icc = IccProfile;
        if (_parsed is ParsedProfile parsed && parsed.Bytes.Equals(icc))
        {
            return parsed.Profile;
        }

        Colors.ColorProfile? result;
        try
        {
            result = Colors.ColorProfile.FromIcc(icc.Span);
            if (result.Model == Colors.ColorModel.NColor)
            {
                // No pixel type holds an n-colour profile's inks, so it cannot say what a raster's pixels mean.
                throw new UnsupportedFormatException($"'{result.Name}' is a {result.ChannelCount}-colour ink profile, which describes no pixel type.");
            }
        }
        catch (EgamiException e)
        {
            result = null;
            lock (WarningList)
            {
                WarningList.Add("Icc.Unusable", $"The ICC profile cannot be used: {e.Message} Colors are read as if it were absent; it is kept as it was.");
                WarningList.Flush();
            }
        }

        _parsed = new ParsedProfile(icc, result);
        return result;
    }

    // One object, so a reader on another thread sees the bytes and their profile together.
    private sealed record ParsedProfile(ReadOnlyMemory<byte> Bytes, Colors.ColorProfile? Profile);

    // DCF: EXIF ColorSpace "uncalibrated" (0xFFFF) with interoperability index "R03" is a camera set to Adobe RGB.
    private static bool IsAdobeRgbCamera(ExifData? exif)
    {
        return exif is not null && exif.TryGet(ExifKey.ColorSpace, out ExifEntry? space) && space.Count > 0 && space.AsDouble() == 0xFFFF
            && exif.TryGet(new ExifKey(0x0001, ExifIfd.Interoperability), out ExifEntry? index) && index.AsString().StartsWith("R03", StringComparison.Ordinal);
    }

    /// <summary>
    /// Records that the pixels are now in <paramref name="profile"/>: no ICC profile and the sRGB or linear hint for those two,
    /// otherwise the profile's ICC bytes. Format details that described the old encoding (PNG gAMA, cHRM, sRGB, cICP) are cleared.
    /// </summary>
    internal void SetColorProfile(Colors.ColorProfile profile)
    {
        Gamma = null;
        Primaries = null;
        Hdr = profile.IsHdr ? Hdr : null;
        CodePoints = profile.Icc is null && !profile.IsEquivalentTo(Colors.ColorProfile.Srgb) ? profile.ToCodePoints() : null;
        FormatDetails?.ClearColorTags();
        Colors.ColorProfile? plain = profile.Model == Colors.ColorModel.Gray
            ? (profile.IsEquivalentTo(Colors.ColorProfile.GraySrgb) ? Colors.ColorProfile.Srgb : profile.IsEquivalentTo(Colors.ColorProfile.GrayLinear) ? Colors.ColorProfile.LinearSrgb : null)
            : profile.IsEquivalentTo(Colors.ColorProfile.Srgb) ? Colors.ColorProfile.Srgb : profile.IsEquivalentTo(Colors.ColorProfile.LinearSrgb) ? Colors.ColorProfile.LinearSrgb : null;
        if (plain is not null)
        {
            IccProfile = default;
            ColorSpace = plain == Colors.ColorProfile.Srgb ? ColorSpaceHint.Srgb : ColorSpaceHint.Linear;
            return;
        }

        IccProfile = profile.ToIcc();
        ColorSpace = ColorSpaceHint.EmbeddedIcc;
    }

    /// <summary>Gets whether there is IPTC, parsed or not.</summary>
    internal bool HasIptc => _iptc is not null || !_iptcSource.IsEmpty;

    /// <summary>Gets the IPTC bytes to write: the source bytes until parsed, then the parsed block's.</summary>
    internal ReadOnlyMemory<byte> IptcBytes => _iptc?.ToBytes() ?? _iptcSource;

    /// <summary>Keeps IPTC bytes a reader found, to parse on first use under <paramref name="maxBytes"/>.</summary>
    internal void SetIptcSource(ReadOnlyMemory<byte> iim, long maxBytes)
    {
        _iptc = null;
        _iptcSource = iim;
        _iptcLimit = maxBytes;
    }

    /// <summary>Takes <paramref name="source"/>'s IPTC, parsed or not, sharing it.</summary>
    internal void ShareIptc(RasterMetadata source)
    {
        _iptc = source._iptc;
        _iptcSource = source._iptcSource;
        _iptcLimit = source._iptcLimit;
    }

    /// <summary>Returns a deep copy.</summary>
    /// <returns>The copy.</returns>
    public RasterMetadata Copy()
    {
        RasterMetadata copy = new()
        {
            Resolution = Resolution,
            Exif = Exif?.Copy(),
            _iptc = _iptc?.Copy(),
            _iptcSource = _iptcSource,
            _iptcLimit = _iptcLimit,
            IccProfile = IccProfile.ToArray(),
            Xmp = Xmp.ToArray(),
            ColorSpace = ColorSpace,
            Gamma = Gamma,
            CodePoints = CodePoints,
            Primaries = Primaries,
            Hdr = Hdr,
            GainMap = GainMap,
            FormatName = FormatName,
            FormatDetails = FormatDetails?.Copy(),
            LoopCount = LoopCount,
        };
        foreach (TextEntry entry in Text)
        {
            copy.Text.Add(entry);
        }

        copy.WarningList.CopyOf(WarningList);
        return copy;
    }

    /// <summary>Replaces everything this metadata holds with what <paramref name="source"/> holds, sharing its values.</summary>
    internal void ReplaceWith(RasterMetadata source)
    {
        Resolution = source.Resolution;
        Exif = source.Exif;
        ShareIptc(source);
        IccProfile = source.IccProfile;
        _xmp = source._xmp;
        _xmpView = source._xmpView;
        _xmpParsed = source._xmpParsed;
        ColorSpace = source.ColorSpace;
        Gamma = source.Gamma;
        CodePoints = source.CodePoints;
        Primaries = source.Primaries;
        Hdr = source.Hdr;
        GainMap = source.GainMap;
        FormatName = source.FormatName;
        FormatDetails = source.FormatDetails;
        LoopCount = source.LoopCount;
        Text.Clear();
        foreach (TextEntry entry in source.Text)
        {
            Text.Add(entry);
        }

        WarningList.ReplaceWith(source.WarningList);
    }
}
