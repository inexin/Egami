using System.Diagnostics.CodeAnalysis;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// A transfer syntax: how a file encodes its dataset (VR explicit or implicit, byte order, deflate) and its pixel data (native
/// or encapsulated in a codec's codestream), named by a UID of PS3.6 table A-1 (PS3.5 section 10).
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-load"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public sealed class DicomTransferSyntax
{
    private DicomTransferSyntax(string uid, bool explicitVR, bool bigEndian, bool deflated, bool encapsulated)
    {
        Uid = uid;
        IsExplicitVR = explicitVR;
        IsBigEndian = bigEndian;
        IsDeflated = deflated;
        IsEncapsulated = encapsulated;
        DicomUidEntry? entry = DicomDictionary.FindUid(uid);
        Name = entry is not null && entry.Type.Contains("Transfer Syntax", StringComparison.Ordinal) ? entry.Name : uid;
        IsKnown = entry is not null;
    }

    /// <summary>Gets Implicit VR Little Endian (1.2.840.10008.1.2), DICOM's default.</summary>
    public static DicomTransferSyntax ImplicitVRLittleEndian { get; } = new("1.2.840.10008.1.2", false, false, false, false);

    /// <summary>Gets Explicit VR Little Endian (1.2.840.10008.1.2.1).</summary>
    public static DicomTransferSyntax ExplicitVRLittleEndian { get; } = new("1.2.840.10008.1.2.1", true, false, false, false);

    /// <summary>Gets Deflated Explicit VR Little Endian (1.2.840.10008.1.2.1.99).</summary>
    public static DicomTransferSyntax DeflatedExplicitVRLittleEndian { get; } = new("1.2.840.10008.1.2.1.99", true, false, true, false);

    /// <summary>Gets Explicit VR Big Endian (1.2.840.10008.1.2.2), retired but still met.</summary>
    public static DicomTransferSyntax ExplicitVRBigEndian { get; } = new("1.2.840.10008.1.2.2", true, true, false, false);

    /// <summary>Gets RLE Lossless (1.2.840.10008.1.2.5): PackBits byte planes (PS3.5 annex G).</summary>
    public static DicomTransferSyntax RleLossless { get; } = new("1.2.840.10008.1.2.5", true, false, false, true);

    /// <summary>Gets JPEG Baseline (Process 1) (1.2.840.10008.1.2.4.50): lossy, 8 bits.</summary>
    public static DicomTransferSyntax JpegBaseline { get; } = new("1.2.840.10008.1.2.4.50", true, false, false, true);

    /// <summary>Gets JPEG Extended (Process 2 and 4) (1.2.840.10008.1.2.4.51): lossy, 8 or 12 bits.</summary>
    public static DicomTransferSyntax JpegExtended { get; } = new("1.2.840.10008.1.2.4.51", true, false, false, true);

    /// <summary>Gets JPEG Lossless, Non-Hierarchical, First-Order Prediction (Process 14, Selection Value 1) (1.2.840.10008.1.2.4.70).</summary>
    public static DicomTransferSyntax JpegLossless { get; } = new("1.2.840.10008.1.2.4.70", true, false, false, true);

    /// <summary>Gets JPEG-LS Lossless (1.2.840.10008.1.2.4.80).</summary>
    public static DicomTransferSyntax JpegLsLossless { get; } = new("1.2.840.10008.1.2.4.80", true, false, false, true);

    /// <summary>Gets JPEG-LS Lossy (Near-Lossless) (1.2.840.10008.1.2.4.81).</summary>
    public static DicomTransferSyntax JpegLsNearLossless { get; } = new("1.2.840.10008.1.2.4.81", true, false, false, true);

    /// <summary>Gets JPEG XL Lossless (1.2.840.10008.1.2.4.110).</summary>
    public static DicomTransferSyntax JpegXlLossless { get; } = new("1.2.840.10008.1.2.4.110", true, false, false, true);

    /// <summary>Gets JPEG XL (1.2.840.10008.1.2.4.112): lossy.</summary>
    public static DicomTransferSyntax JpegXl { get; } = new("1.2.840.10008.1.2.4.112", true, false, false, true);

    /// <summary>Gets the UID.</summary>
    public string Uid { get; }

    /// <summary>Gets the PS3.6 name, such as <c>JPEG 2000 Image Compression (Lossless Only)</c>, or the UID when PS3.6 does not list it.</summary>
    public string Name { get; }

    /// <summary>Gets whether each element states its VR.</summary>
    public bool IsExplicitVR { get; }

    /// <summary>Gets whether binary values are big-endian.</summary>
    public bool IsBigEndian { get; }

    /// <summary>Gets whether the dataset after the file meta information is deflated (RFC 1951).</summary>
    public bool IsDeflated { get; }

    /// <summary>Gets whether pixel data is encapsulated: compressed, in fragments (PS3.5 section A.4).</summary>
    public bool IsEncapsulated { get; }

    /// <summary>Gets whether PS3.6 lists the UID.</summary>
    public bool IsKnown { get; }

    /// <summary>
    /// Returns the transfer syntax a UID names. UIDs other than the four native ones are read as explicit VR little endian with
    /// encapsulated pixel data, as every compressed transfer syntax is.
    /// </summary>
    /// <param name="uid">The UID; trailing spaces and NUL padding are ignored.</param>
    /// <returns>The transfer syntax.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uid"/> is <see langword="null"/>.</exception>
    public static DicomTransferSyntax FromUid(string uid)
    {
        ArgumentNullException.ThrowIfNull(uid);
        uid = uid.Trim(' ', '\0');
        return uid switch
        {
            "1.2.840.10008.1.2" => ImplicitVRLittleEndian,
            "1.2.840.10008.1.2.1" => ExplicitVRLittleEndian,
            "1.2.840.10008.1.2.1.99" => DeflatedExplicitVRLittleEndian,
            "1.2.840.10008.1.2.2" => ExplicitVRBigEndian,
            _ => new DicomTransferSyntax(uid, true, false, false, true),
        };
    }

    /// <summary>Returns the name and UID.</summary>
    /// <returns>The transfer syntax as text.</returns>
    public override string ToString() => Name == Uid ? Uid : $"{Name} ({Uid})";
}
