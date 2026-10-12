using System.Buffers.Binary;

using Egami.Metadata;

namespace Egami.Formats.Jpeg;

/// <summary>What an image of a Multi-Picture Format file is, from its MP entry (CIPA DC-007, and the gain map type CIPA added later).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="mpf"/>
/// </example>
public enum MpfImageType
{
    /// <summary>No type given.</summary>
    Undefined = 0,

    /// <summary>A large thumbnail of about VGA size.</summary>
    LargeThumbnailVga = 0x010001,

    /// <summary>A large thumbnail of about full HD size.</summary>
    LargeThumbnailFullHd = 0x010002,

    /// <summary>A large thumbnail of about 4K size.</summary>
    LargeThumbnail4K = 0x010003,

    /// <summary>A large thumbnail of about 8K size.</summary>
    LargeThumbnail8K = 0x010004,

    /// <summary>A large thumbnail of about 16K size.</summary>
    LargeThumbnail16K = 0x010005,

    /// <summary>A frame of a panorama.</summary>
    Panorama = 0x020001,

    /// <summary>A view of a stereo or multi-view disparity set.</summary>
    Disparity = 0x020002,

    /// <summary>A view of a multi-angle set.</summary>
    MultiAngle = 0x020003,

    /// <summary>The primary image, which the file opens with.</summary>
    BaselinePrimary = 0x030000,

    /// <summary>An original kept beside an edited primary.</summary>
    OriginalPreservation = 0x040000,

    /// <summary>A gain map, which turns the primary into an HDR image.</summary>
    GainMap = 0x050000,
}

/// <summary>One image of a Multi-Picture Format file: its MP entry, with the offset resolved to the start of the file.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="mpf"/>
/// </example>
public sealed record MpfImage
{
    /// <summary>Gets the image's type. Values Egami does not name are kept as they are.</summary>
    public MpfImageType Type { get; init; }

    /// <summary>Gets where the image's SOI marker is, in bytes from the start of the file's own SOI.</summary>
    public long Offset { get; init; }

    /// <summary>Gets the image's length in bytes.</summary>
    public long Length { get; init; }

    /// <summary>Gets the image data format from the entry: 0 for JPEG, the only one the standard defines.</summary>
    public int DataFormat { get; init; }

    /// <summary>Gets whether the image is the parent of dependent images.</summary>
    public bool IsDependentParent { get; init; }

    /// <summary>Gets whether the image depends on a parent image.</summary>
    public bool IsDependentChild { get; init; }

    /// <summary>Gets whether the image represents the set, such as the image a viewer shows first.</summary>
    public bool IsRepresentative { get; init; }

    /// <summary>Gets the entry number (from 1) of the first dependent image, or 0 for none.</summary>
    public int DependentImage1 { get; init; }

    /// <summary>Gets the entry number (from 1) of the second dependent image, or 0 for none.</summary>
    public int DependentImage2 { get; init; }
}

/// <summary>
/// A JPEG file's Multi-Picture Format index (CIPA DC-007): the images the file holds after its primary image, such as large
/// previews, stereo views or an HDR gain map. <see cref="Raster.LoadAll(Stream, EgamiContext?, ReaderOptions?)"/> loads each
/// JPEG image it lists.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="mpf"/>
/// </example>
public sealed record MpfData
{
    /// <summary>Gets the MPF version, such as "0100".</summary>
    public string Version { get; init; } = "0100";

    /// <summary>Gets the images, in the index's order; the first is the primary image.</summary>
    public IReadOnlyList<MpfImage> Images { get; init; } = [];
}

/// <summary>Parses the MP index of an APP2 "MPF\0" segment.</summary>
internal static class Mpf
{
    /// <summary>The most entries Egami reads from one index; real files have a handful.</summary>
    internal const int MaxImages = 4096;

    private const ushort VersionTag = 0xB000;
    private const ushort NumberOfImagesTag = 0xB001;
    private const ushort EntryTag = 0xB002;

    internal static ReadOnlySpan<byte> Id => "MPF\0"u8;

    /// <summary>
    /// Parses <paramref name="payload"/>, an APP2 segment's data after its length, whose TIFF header starts <paramref name="headerOffset"/>
    /// bytes after the start of the file. Returns <see langword="null"/>, with a warning, when the index is damaged.
    /// </summary>
    internal static MpfData? Parse(ReadOnlySpan<byte> payload, long headerOffset, WarningList warnings)
    {
        ReadOnlySpan<byte> tiff = payload[Id.Length..];
        string? problem = null;
        MpfData? data = Parse(tiff, headerOffset, ref problem);
        if (data is null)
        {
            warnings.Add("Jpeg.MpfDamaged", $"The Multi-Picture Format index is damaged ({problem}); the images after the first were not listed.", headerOffset);
        }

        return data;
    }

    private static MpfData? Parse(ReadOnlySpan<byte> tiff, long headerOffset, ref string? problem)
    {
        if (tiff.Length < 8)
        {
            problem = "the header is cut short";
            return null;
        }

        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I')
        {
            little = true;
        }
        else if (tiff[0] == 'M' && tiff[1] == 'M')
        {
            little = false;
        }
        else
        {
            problem = "the byte order is neither II nor MM";
            return null;
        }

        if (U16(tiff, 2, little) != 42)
        {
            problem = "the header's magic number is not 42";
            return null;
        }

        long ifd = U32(tiff, 4, little);
        if (ifd < 8 || ifd > tiff.Length - 2)
        {
            problem = $"the index IFD at {ifd} lies outside the segment";
            return null;
        }

        int count = U16(tiff, (int)ifd, little);
        if ((long)ifd + 2 + (count * 12L) > tiff.Length)
        {
            problem = "the index IFD runs past the segment";
            return null;
        }

        string version = "0100";
        long images = -1;
        long entryOffset = -1, entryBytes = 0;
        for (int i = 0; i < count; i++)
        {
            int at = (int)ifd + 2 + (i * 12);
            ushort tag = U16(tiff, at, little);
            uint n = U32(tiff, at + 4, little);
            switch (tag)
            {
                case VersionTag when n == 4:
                    version = new string([(char)tiff[at + 8], (char)tiff[at + 9], (char)tiff[at + 10], (char)tiff[at + 11]]);
                    break;
                case NumberOfImagesTag:
                    images = U32(tiff, at + 8, little);
                    break;
                case EntryTag:
                    entryBytes = n;
                    entryOffset = n <= 4 ? at + 8 : U32(tiff, at + 8, little);
                    break;
            }
        }

        if (images < 1 || images > MaxImages)
        {
            problem = images < 0 ? "it gives no number of images" : $"it lists {images} images";
            return null;
        }

        if (entryOffset < 0 || entryBytes < images * 16 || entryOffset > tiff.Length - (images * 16))
        {
            problem = "the MP entries are missing or lie outside the segment";
            return null;
        }

        List<MpfImage> list = new((int)images);
        for (int i = 0; i < images; i++)
        {
            int at = (int)entryOffset + (i * 16);
            uint attribute = U32(tiff, at, little);
            uint length = U32(tiff, at + 4, little);
            uint offset = U32(tiff, at + 8, little);
            list.Add(new MpfImage
            {
                Type = (MpfImageType)(attribute & 0x00FF_FFFF),
                DataFormat = (int)((attribute >> 24) & 7),
                IsDependentParent = (attribute & 0x8000_0000) != 0,
                IsDependentChild = (attribute & 0x4000_0000) != 0,
                IsRepresentative = (attribute & 0x2000_0000) != 0,
                Length = length,

                // The first image's offset is 0: it starts the file. The others count from the MP header.
                Offset = offset == 0 ? 0 : headerOffset + offset,
                DependentImage1 = U16(tiff, at + 12, little),
                DependentImage2 = U16(tiff, at + 14, little),
            });
        }

        return new MpfData { Version = version, Images = list };
    }

    private static ushort U16(ReadOnlySpan<byte> data, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(data[at..]) : BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    private static uint U32(ReadOnlySpan<byte> data, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(data[at..]) : BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
}
