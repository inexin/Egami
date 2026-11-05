using Egami.Metadata;

namespace Egami.Formats.Extended.Dpx;

/// <summary>
/// DPX (SMPTE ST 268, Digital Moving-Picture Exchange), the frame format of film scanning and visual effects: a 2048-byte header
/// of file, image, film and television fields, then up to eight image elements of 1 to 16-bit integer or 32 and 64-bit float
/// samples, usually 10-bit printing-density RGB. Load gives the first element as its stored code values; LoadAll gives every
/// element, and <see cref="DpxReaderOptions"/> picks one or turns log code values into linear light. Registered with
/// <see cref="ExtendedContextExtensions.WithDpx"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dpx-load"/>
/// </example>
public static class DpxFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "DPX",
        MimeTypes = ["image/x-dpx"],
        Extensions = ["dpx"],
        SignatureLength = 4,
        Matches = DpxHeader.Matches,
        Reader = new DpxReader(),
        Writer = new DpxWriter(),
    };
}

/// <summary>
/// Cineon, Kodak's film-scan format (version 4.5), from which DPX grew: a 1024-byte header and a 1024-byte film header, then
/// 10-bit printing-density samples, three to a 32-bit word. Load gives the stored code values; <see cref="DpxReaderOptions"/>
/// turns them into linear light. Registered with <see cref="ExtendedContextExtensions.WithCineon"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="cineon"/>
/// </example>
public static class CineonFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "Cineon",
        MimeTypes = ["image/cineon", "image/x-cineon"],
        Extensions = ["cin"],
        SignatureLength = 4,
        Matches = CineonReader.Matches,
        Reader = new CineonReader(),
        Writer = new CineonWriter(),
    };
}

/// <summary>What the samples of a DPX image element mean: the transfer characteristic and colorimetric fields of ST 268.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dpx-save"/>
/// </example>
public enum DpxTransfer
{
    /// <summary>Defined by the user (code 0).</summary>
    UserDefined = 0,

    /// <summary>Printing density, the log code values of film scans (code 1).</summary>
    PrintingDensity = 1,

    /// <summary>Linear (code 2).</summary>
    Linear = 2,

    /// <summary>Logarithmic (code 3).</summary>
    Logarithmic = 3,

    /// <summary>Unspecified video (code 4).</summary>
    UnspecifiedVideo = 4,

    /// <summary>SMPTE 274M, HD video (code 5).</summary>
    Smpte274M = 5,

    /// <summary>ITU-R BT.709 (code 6).</summary>
    Rec709 = 6,

    /// <summary>ITU-R BT.601, 625 lines (code 7).</summary>
    Rec601Bg = 7,

    /// <summary>ITU-R BT.601, 525 lines (code 8).</summary>
    Rec601M = 8,

    /// <summary>NTSC composite video (code 9).</summary>
    NtscComposite = 9,

    /// <summary>PAL composite video (code 10).</summary>
    PalComposite = 10,

    /// <summary>Linear depth (code 11).</summary>
    ZLinear = 11,

    /// <summary>Homogeneous depth (code 12).</summary>
    ZHomogeneous = 12,
}

/// <summary>Details only DPX has, kept in <see cref="RasterMetadata.FormatDetails"/>. The writer writes the text, film and television fields back.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dpx-details"/>
/// </example>
public sealed class DpxDetails : FormatDetails
{
    /// <summary>Gets or sets the header version, such as "V2.0".</summary>
    public string Version { get; set; } = "V2.0";

    /// <summary>Gets or sets whether the file is big endian ("SDPX"); little-endian files start "XPDS".</summary>
    public bool BigEndian { get; set; } = true;

    /// <summary>Gets or sets the image element this image came from, from 0.</summary>
    public int Element { get; set; }

    /// <summary>Gets or sets the number of image elements in the file.</summary>
    public int ElementCount { get; set; } = 1;

    /// <summary>Gets or sets the descriptor: 50 RGB, 51 RGBA, 52 ABGR, 6 luma, 4 alpha, 100 to 103 CbYCr and others.</summary>
    public int Descriptor { get; set; } = 50;

    /// <summary>Gets or sets the stored bits a sample: 1, 8, 10, 12, 16, 32 or 64.</summary>
    public int BitsPerSample { get; set; } = 10;

    /// <summary>Gets or sets the packing: 0 packed, 1 filled to 32-bit words with the padding in the low bits (method A), 2 with it in the high bits (method B).</summary>
    public int Packing { get; set; } = 1;

    /// <summary>Gets or sets whether the element was run-length coded.</summary>
    public bool RunLength { get; set; }

    /// <summary>Gets or sets the transfer characteristic.</summary>
    public DpxTransfer Transfer { get; set; } = DpxTransfer.PrintingDensity;

    /// <summary>Gets or sets the colorimetric specification.</summary>
    public DpxTransfer Colorimetric { get; set; } = DpxTransfer.PrintingDensity;

    /// <summary>Gets or sets the image orientation code: 0 left to right and top to bottom; 1 to 3 flip it, 4 to 7 transpose it.</summary>
    public int Orientation { get; set; }

    /// <summary>Gets or sets the element's description.</summary>
    public string Description { get; set; } = "";

    /// <summary>Gets or sets the file name the header records.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Gets or sets the creation time as the header gives it, "yyyy:mm:dd:hh:mm:ssLTZ".</summary>
    public string CreationTime { get; set; } = "";

    /// <summary>Gets or sets the creator.</summary>
    public string Creator { get; set; } = "";

    /// <summary>Gets or sets the project name.</summary>
    public string Project { get; set; } = "";

    /// <summary>Gets or sets the copyright statement.</summary>
    public string Copyright { get; set; } = "";

    /// <summary>Gets or sets the input device name.</summary>
    public string InputDevice { get; set; } = "";

    /// <summary>Gets or sets the SMPTE timecode, "hh:mm:ss:ff", or <see langword="null"/> when undefined.</summary>
    public string? Timecode { get; set; }

    /// <summary>Gets or sets the SMPTE user bits, or <see langword="null"/> when undefined.</summary>
    public uint? UserBits { get; set; }

    /// <summary>Gets or sets the frame rate, in frames a second, from the film header or else the television header.</summary>
    public float? FrameRate { get; set; }

    /// <summary>Gets or sets the film manufacturer's id code (2 characters).</summary>
    public string FilmManufacturer { get; set; } = "";

    /// <summary>Gets or sets the film type (2 characters).</summary>
    public string FilmType { get; set; } = "";

    /// <summary>Gets or sets the offset in perforations (2 characters).</summary>
    public string FilmOffset { get; set; } = "";

    /// <summary>Gets or sets the film edge code prefix (6 characters).</summary>
    public string FilmPrefix { get; set; } = "";

    /// <summary>Gets or sets the film edge code count (4 characters).</summary>
    public string FilmCount { get; set; } = "";

    /// <summary>Gets or sets the film format, such as "Academy".</summary>
    public string FilmFormat { get; set; } = "";

    /// <summary>Gets or sets the frame's position in the sequence, or <see langword="null"/> when undefined.</summary>
    public uint? FramePosition { get; set; }

    /// <summary>Gets or sets the length of the sequence in frames, or <see langword="null"/> when undefined.</summary>
    public uint? SequenceLength { get; set; }

    /// <summary>Gets or sets the frame id, such as a shot name.</summary>
    public string FrameId { get; set; } = "";

    /// <summary>Gets or sets the slate information.</summary>
    public string SlateInfo { get; set; } = "";

    /// <inheritdoc/>
    public override FormatDetails Copy() => (DpxDetails)MemberwiseClone();
}

/// <summary>Details only Cineon has, kept in <see cref="RasterMetadata.FormatDetails"/>. The writer writes the text and film fields back.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="cineon"/>
/// </example>
public sealed class CineonDetails : FormatDetails
{
    /// <summary>Gets or sets the number of channels the file stores: 1 or 3.</summary>
    public int Channels { get; set; } = 3;

    /// <summary>Gets or sets the stored bits a sample: 8, 10, 12 or 16.</summary>
    public int BitsPerSample { get; set; } = 10;

    /// <summary>Gets or sets the packing code: 5, three 10-bit samples to a 32-bit word from the top, is the usual one.</summary>
    public int Packing { get; set; } = 5;

    /// <summary>Gets or sets the image orientation code: 0 left to right and top to bottom.</summary>
    public int Orientation { get; set; }

    /// <summary>Gets or sets the file name the header records.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Gets or sets the creation date, "yyyy:mm:dd".</summary>
    public string CreationDate { get; set; } = "";

    /// <summary>Gets or sets the creation time, "hh:mm:ssxxx".</summary>
    public string CreationTime { get; set; } = "";

    /// <summary>Gets or sets the label text.</summary>
    public string Label { get; set; } = "";

    /// <summary>Gets or sets the input device.</summary>
    public string InputDevice { get; set; } = "";

    /// <summary>Gets or sets the input device's model number.</summary>
    public string DeviceModel { get; set; } = "";

    /// <summary>Gets or sets the input device's serial number.</summary>
    public string DeviceSerial { get; set; } = "";

    /// <summary>Gets or sets the film format, such as "Academy".</summary>
    public string FilmFormat { get; set; } = "";

    /// <summary>Gets or sets the film edge code prefix, or <see langword="null"/> when undefined.</summary>
    public uint? FilmPrefix { get; set; }

    /// <summary>Gets or sets the film edge code count, or <see langword="null"/> when undefined.</summary>
    public uint? FilmCount { get; set; }

    /// <summary>Gets or sets the frame's position in the sequence, or <see langword="null"/> when undefined.</summary>
    public uint? FramePosition { get; set; }

    /// <summary>Gets or sets the frame rate, in frames a second, or <see langword="null"/> when undefined.</summary>
    public float? FrameRate { get; set; }

    /// <summary>Gets or sets the frame id.</summary>
    public string FrameId { get; set; } = "";

    /// <summary>Gets or sets the slate information.</summary>
    public string SlateInfo { get; set; } = "";

    /// <inheritdoc/>
    public override FormatDetails Copy() => (CineonDetails)MemberwiseClone();
}

/// <summary>Options for reading DPX and Cineon.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dpx-linearize"/>
/// </example>
public sealed record DpxReaderOptions : ReaderOptions
{
    private readonly int? _element;

    /// <summary>Gets the DPX image element Load returns, from 0, or <see langword="null"/> (the default) for the first.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int? Element
    {
        get => _element;
        init
        {
            if (value is int v)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(v);
            }

            _element = value;
        }
    }

    /// <summary>
    /// Gets whether printing-density (log) code values load as linear light, with Kodak's conversion (reference black 95,
    /// reference white 685, 0.002 density a code value, negative gamma 0.6): black is 0, white 1, highlights above 1 and codes
    /// below black under 0, into <c>RgbF32</c>, <c>RgbaF32</c> or <c>GrayF32</c>. Defaults to <see langword="false"/>: the stored
    /// code values, scaled to the pixel's full range. Every Cineon file is log; DPX elements are when their transfer is
    /// printing density or logarithmic, and others load as stored.
    /// </summary>
    public bool Linearize { get; init; }
}

/// <summary>Options for writing DPX.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="dpx-save"/>
/// </example>
public sealed class DpxWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new DpxWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the bits a sample: 8, 10, 12 or 16, or <see langword="null"/> (the default) for those of a raster read from DPX or
    /// Cineon, and 10 otherwise.
    /// </summary>
    public int? BitsPerSample { get; init; }

    /// <summary>Gets whether to write big endian ("SDPX"). Defaults to <see langword="true"/>.</summary>
    public bool BigEndian { get; init; } = true;

    /// <summary>
    /// Gets the transfer characteristic and colorimetric code to write, or <see langword="null"/> (the default) for those of a
    /// raster read from DPX, printing density for one read from Cineon, and <see cref="DpxTransfer.UserDefined"/> otherwise.
    /// They only label the samples, which are written as they are.
    /// </summary>
    public DpxTransfer? Transfer { get; init; }
}
