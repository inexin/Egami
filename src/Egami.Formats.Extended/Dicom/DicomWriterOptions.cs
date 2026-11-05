using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Egami.Formats.Extended.Dicom;

/// <summary>Where encapsulated pixel data says each frame starts (PS3.5 section A.4).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-transcode"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public enum DicomOffsetTable
{
    /// <summary>
    /// The Basic Offset Table, the first item of the pixel data (the default). Pixel data past 4 GiB, which its 32-bit offsets
    /// cannot reach, gets the Extended Offset Table instead.
    /// </summary>
    Basic,

    /// <summary>The Extended Offset Table (7FE0,0001) and its lengths (7FE0,0002), 64-bit; the Basic Offset Table is left empty.</summary>
    Extended,

    /// <summary>No table: the Basic Offset Table is left empty, and readers find frames by their fragments, one a frame.</summary>
    None,
}

/// <summary>
/// Options for writing DICOM: the transfer syntax, the quality of lossy ones, and the attributes of a Secondary Capture image.
/// Pass them to <see cref="Raster.Save(Stream, FormatInfo, WriterOptions?)"/> with <see cref="DicomFormat.Info"/>, or to
/// <see cref="DicomFile.Save(Stream, DicomWriterOptions?, EgamiContext?)"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-save"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public sealed class DicomWriterOptions : WriterOptions
{
    private readonly int _quality = 90;
    private readonly int _near = 2;

    /// <summary>Gets the default options: Explicit VR Little Endian, quality 90, the Basic Offset Table.</summary>
    public static new DicomWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the transfer syntax to write. Native: <see cref="DicomTransferSyntax.ExplicitVRLittleEndian"/> (the default),
    /// <see cref="DicomTransferSyntax.ImplicitVRLittleEndian"/> and <see cref="DicomTransferSyntax.DeflatedExplicitVRLittleEndian"/>;
    /// lossless: <see cref="DicomTransferSyntax.RleLossless"/>, <see cref="DicomTransferSyntax.JpegLossless"/>,
    /// <see cref="DicomTransferSyntax.JpegLsLossless"/> and <see cref="DicomTransferSyntax.JpegXlLossless"/>; lossy:
    /// <see cref="DicomTransferSyntax.JpegBaseline"/>, <see cref="DicomTransferSyntax.JpegExtended"/>,
    /// <see cref="DicomTransferSyntax.JpegLsNearLossless"/> and <see cref="DicomTransferSyntax.JpegXl"/>.
    /// </summary>
    public DicomTransferSyntax TransferSyntax { get; init; } = DicomTransferSyntax.ExplicitVRLittleEndian;

    /// <summary>
    /// Gets the quality of <see cref="DicomTransferSyntax.JpegBaseline"/>, <see cref="DicomTransferSyntax.JpegExtended"/> and
    /// <see cref="DicomTransferSyntax.JpegXl"/>, 1 (smallest) to 100 (best), as their own writers take it. Defaults to 90.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 100.</exception>
    public int Quality
    {
        get => _quality;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            _quality = value;
        }
    }

    /// <summary>
    /// Gets the largest error per sample of <see cref="DicomTransferSyntax.JpegLsNearLossless"/> (the JPEG-LS NEAR parameter),
    /// 1 to 255. Defaults to 2.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 255.</exception>
    public int NearLossless
    {
        get => _near;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255);
            _near = value;
        }
    }

    /// <summary>Gets where encapsulated pixel data says each frame starts. Defaults to <see cref="DicomOffsetTable.Basic"/>.</summary>
    public DicomOffsetTable OffsetTable { get; init; }

    /// <summary>
    /// Gets attributes to give a Secondary Capture image written from a raster, by PS3.6 keyword, such as <c>PatientName</c>,
    /// <c>PatientID</c> or <c>StudyInstanceUID</c>, each value as text (several values separated by <c>\</c>). The VR comes
    /// from PS3.6; text is written in UTF-8 (Specific Character Set <c>ISO_IR 192</c>) when it is not ASCII. Ignored by
    /// <see cref="DicomFile.Save(Stream, DicomWriterOptions?, EgamiContext?)"/>, which keeps the file's own.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = new Dictionary<string, string>();
}

/// <summary>DICOM unique identifiers (PS3.5 chapter 9).</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-save"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public static class DicomUid
{
    /// <summary>Gets the Implementation Class UID Egami writes in the file meta information (0002,0012).</summary>
    public static string ImplementationClassUid { get; } = "2.25.291407207116497368567570526339573279772";

    /// <summary>Gets the Implementation Version Name Egami writes (0002,0013), such as <c>EGAMI_1.3.0</c>.</summary>
    internal static string ImplementationVersion { get; } = Version();

    /// <summary>
    /// Returns a new UID made from a random UUID, as PS3.5 section B.2 gives: <c>2.25.</c> and the UUID's 128 bits as a decimal
    /// number, such as <c>2.25.329800735698586629295641978511506172918</c>. Up to 44 characters, under the 64 a UID may have.
    /// </summary>
    /// <returns>The UID.</returns>
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid.NewGuid().TryWriteBytes(bytes, bigEndian: true, out _);
        BigInteger value = new(bytes, isUnsigned: true, isBigEndian: true);
        return "2.25." + value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Version()
    {
        Version? version = typeof(DicomUid).Assembly.GetName().Version;
        string name = version is null ? "EGAMI" : $"EGAMI_{version.Major}.{version.Minor}.{version.Build}";
        return name.Length <= 16 ? name : name[..16];
    }
}
