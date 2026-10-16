using System.Buffers.Binary;

using Egami.Colors;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Avif;

/// <summary>The AVIF format (AV1 Image File Format), registered in <see cref="EgamiContext.Default"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="probe"/>
/// </example>
public static class AvifFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature and reader.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "AVIF",
        MimeTypes = ["image/avif"],
        Extensions = ["avif"],
        SignatureLength = 64,
        Matches = Matches,
        Reader = AvifReader.Instance,
    };

    /// <summary>
    /// Gets whether the data starts with an ftyp box whose major or compatible brands include avif or avis. A HEIF file without
    /// those brands, such as HEIC, is left to other readers.
    /// </summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(header[4..]) != FourCC.Ftyp)
        {
            return false;
        }

        uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
        int end = (int)Math.Min(size < 16 ? 12 : size, (uint)header.Length);
        for (int at = 8; at + 4 <= end; at += 4)
        {
            // Byte 12 holds the minor version, not a brand.
            uint brand = BinaryPrimitives.ReadUInt32BigEndian(header[at..]);
            if (at != 12 && (brand == AvifFile.Avif || brand == AvifFile.Avis))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Details only AVIF has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Avif.cs" region="probe"/>
/// </example>
public sealed class AvifDetails : FormatDetails
{
    /// <summary>Gets or sets the ftyp box's major brand, such as avif or avis.</summary>
    public string MajorBrand { get; set; } = "avif";

    /// <summary>Gets or sets the ftyp box's compatible brands, such as mif1, miaf and MA1B.</summary>
    public IReadOnlyList<string> CompatibleBrands { get; set; } = [];

    /// <summary>Gets or sets the AV1 profile: 0 (Main), 1 (High, 4:4:4) or 2 (Professional, 4:2:2 and 12 bits).</summary>
    public int Profile { get; set; }

    /// <summary>Gets or sets the AV1 level, as seq_level_idx: 0 is level 2.0, 1 is 2.1, and so on; 31 means no level.</summary>
    public int Level { get; set; }

    /// <summary>Gets or sets the AV1 tier: 0 (Main) or 1 (High).</summary>
    public int Tier { get; set; }

    /// <summary>Gets or sets the bits a sample has: 8, 10 or 12.</summary>
    public int BitDepth { get; set; } = 8;

    /// <summary>Gets or sets the chroma format: 4:0:0 (monochrome), 4:2:0, 4:2:2 or 4:4:4.</summary>
    public ChromaFormat Chroma { get; set; } = ChromaFormat.Yuv420;

    /// <summary>
    /// Gets or sets where 4:2:0 chroma sits, as the AV1 sequence header says: <see cref="ChromaSiting.Left"/> (vertical) or
    /// <see cref="ChromaSiting.TopLeft"/> (colocated); <see langword="null"/> when it is unknown or the image is not 4:2:0.
    /// </summary>
    public ChromaSiting? ChromaSiting { get; set; }

    /// <summary>
    /// Gets or sets the color code points (ITU-T H.273) and range: the nclx colr property's when there is one, otherwise the AV1
    /// sequence header's. With an ICC profile too, the matrix and range still come from here (design question 8).
    /// </summary>
    public ColorCodePoints CodePoints { get; set; } = new(2, 2, 2, false);

    /// <summary>Gets or sets whether the image has an alpha item.</summary>
    public bool HasAlpha { get; set; }

    /// <summary>Gets or sets whether the color is premultiplied by the alpha (a prem item reference). Egami loads straight alpha.</summary>
    public bool AlphaPremultiplied { get; set; }

    /// <summary>Gets or sets the rows of a grid image, or 1.</summary>
    public int GridRows { get; set; } = 1;

    /// <summary>Gets or sets the columns of a grid image, or 1.</summary>
    public int GridColumns { get; set; } = 1;

    /// <summary>Gets or sets whether the image is layered for progressive display; Egami decodes the best layer (design question 15).</summary>
    public bool IsProgressive { get; set; }

    /// <summary>Gets or sets whether the file is an image sequence (the avis brand).</summary>
    public bool IsSequence { get; set; }

    /// <summary>Gets or sets whether the AV1 stream carries film grain parameters.</summary>
    public bool HasFilmGrain { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new AvifDetails
        {
            MajorBrand = MajorBrand,
            CompatibleBrands = CompatibleBrands,
            Profile = Profile,
            Level = Level,
            Tier = Tier,
            BitDepth = BitDepth,
            Chroma = Chroma,
            ChromaSiting = ChromaSiting,
            CodePoints = CodePoints,
            HasAlpha = HasAlpha,
            AlphaPremultiplied = AlphaPremultiplied,
            GridRows = GridRows,
            GridColumns = GridColumns,
            IsProgressive = IsProgressive,
            IsSequence = IsSequence,
            HasFilmGrain = HasFilmGrain,
        };
    }

    /// <summary>Gets the details of a file whose structure has been read.</summary>
    internal static AvifDetails From(AvifFile file)
    {
        return new AvifDetails
        {
            MajorBrand = FourCC.ToString(file.Heif.MajorBrand),
            CompatibleBrands = [.. file.Heif.CompatibleBrands.Select(FourCC.ToString)],
            Profile = file.Config.Profile,
            Level = file.Config.Level,
            Tier = file.Config.Tier,
            BitDepth = file.Config.BitDepth,
            Chroma = file.Config.Chroma,
            ChromaSiting = file.Config.Chroma != ChromaFormat.Yuv420 ? null : file.Sequence.ChromaSamplePosition switch
            {
                1 => Pixels.ChromaSiting.Left,
                2 => Pixels.ChromaSiting.TopLeft,
                _ => null,
            },
            CodePoints = file.CodePoints,
            HasAlpha = file.Alpha is not null,
            AlphaPremultiplied = file.AlphaPremultiplied,
            GridRows = file.GridSize.Rows,
            GridColumns = file.GridSize.Columns,
            IsProgressive = file.Progressive,
            IsSequence = file.Heif.HasBrand(AvifFile.Avis),
            HasFilmGrain = file.Sequence.FilmGrainParamsPresent,
        };
    }
}
