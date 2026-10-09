using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Png;

/// <summary>Where a kept chunk sat in the file, so it can be written back in the same place.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="png-writer"/>
/// </example>
public enum PngChunkPosition
{
    /// <summary>After IHDR, before PLTE.</summary>
    BeforePalette,

    /// <summary>After PLTE (if any), before the first IDAT.</summary>
    BeforeImageData,

    /// <summary>After the last IDAT, before IEND.</summary>
    AfterImageData,
}

/// <summary>A chunk kept unchanged for round trips.</summary>
/// <param name="Type">The four-letter chunk type, such as "bKGD".</param>
/// <param name="Data">The chunk data.</param>
/// <param name="Position">Where the chunk sat.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="png-writer"/>
/// </example>
public sealed record PngChunk(string Type, ReadOnlyMemory<byte> Data, PngChunkPosition Position);

/// <summary>Which chunk a text entry came from.</summary>
/// <param name="ChunkType">"tEXt", "zTXt" or "iTXt".</param>
/// <param name="Compressed">Whether the text was compressed.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="format-details"/>
/// </example>
public sealed record PngTextSource(string ChunkType, bool Compressed);

/// <summary>Details only PNG has, kept in <see cref="RasterMetadata.FormatDetails"/> so a PNG can be written back as it was.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/ApiExamples.cs" region="format-details"/>
/// </example>
public sealed class PngDetails : FormatDetails
{
    /// <summary>Gets or sets the original color type.</summary>
    public PngColorType ColorType { get; set; }

    /// <summary>Gets or sets the original bit depth.</summary>
    public int BitDepth { get; set; }

    /// <summary>Gets or sets a value indicating whether the file was Adam7 interlaced.</summary>
    public bool Interlaced { get; set; }

    /// <summary>Gets or sets the palette (PLTE), with alpha from tRNS. Empty if the file had none.</summary>
    public ReadOnlyMemory<Rgba8> Palette { get; set; }

    /// <summary>Gets or sets the raw tRNS data. Empty if the file had none.</summary>
    public ReadOnlyMemory<byte> Transparency { get; set; }

    /// <summary>Gets or sets the gAMA value as stored: the file gamma times 100,000.</summary>
    public uint? FileGamma { get; set; }

    /// <summary>Gets or sets the raw cHRM data (32 bytes). Empty if the file had none.</summary>
    public ReadOnlyMemory<byte> Chromaticities { get; set; }

    /// <summary>Gets or sets the sRGB rendering intent, 0 to 3, if the file had an sRGB chunk.</summary>
    public byte? SrgbRenderingIntent { get; set; }

    /// <summary>Gets or sets the raw cICP data (4 bytes). Empty if the file had none.</summary>
    public ReadOnlyMemory<byte> CodingIndependentCodePoints { get; set; }

    /// <summary>Gets or sets the ICC profile name from iCCP.</summary>
    public string? IccProfileName { get; set; }

    /// <summary>Gets or sets a value indicating whether pHYs gave a unit (meter) rather than only an aspect ratio.</summary>
    public bool? ResolutionHasUnit { get; set; }

    /// <summary>Gets or sets whether the file is an APNG (has a valid acTL chunk).</summary>
    public bool Animated { get; set; }

    /// <summary>
    /// Gets or sets whether an APNG's default image (IDAT) is its first frame. When it is not, it is a still for viewers without
    /// APNG support, and the reader skips it.
    /// </summary>
    public bool DefaultImageIsFrame { get; set; } = true;

    /// <summary>Gets the chunk each entry of <see cref="RasterMetadata.Text"/> came from, in the same order.</summary>
    public IList<PngTextSource> TextSources { get; } = [];

    /// <summary>Gets the other chunks kept unchanged, such as bKGD and tIME, in file order.</summary>
    public IList<PngChunk> OtherChunks { get; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        PngDetails copy = new()
        {
            ColorType = ColorType,
            BitDepth = BitDepth,
            Interlaced = Interlaced,
            Palette = Palette.ToArray(),
            Transparency = Transparency.ToArray(),
            FileGamma = FileGamma,
            Chromaticities = Chromaticities.ToArray(),
            SrgbRenderingIntent = SrgbRenderingIntent,
            CodingIndependentCodePoints = CodingIndependentCodePoints.ToArray(),
            IccProfileName = IccProfileName,
            ResolutionHasUnit = ResolutionHasUnit,
            Animated = Animated,
            DefaultImageIsFrame = DefaultImageIsFrame,
        };
        foreach (PngTextSource source in TextSources)
        {
            copy.TextSources.Add(source);
        }

        foreach (PngChunk chunk in OtherChunks)
        {
            copy.OtherChunks.Add(chunk with { Data = chunk.Data.ToArray() });
        }

        return copy;
    }
}
