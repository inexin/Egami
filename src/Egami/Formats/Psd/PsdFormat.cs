using Egami.Metadata;
using Egami.Operations;

namespace Egami.Formats.Psd;

/// <summary>
/// Adobe Photoshop's PSD and large-document PSB formats, read only, registered in <see cref="EgamiContext.Default"/>. Load returns
/// the flattened image Photoshop stores after the layers; the layers themselves are listed in <see cref="PsdDetails.Layers"/>
/// without their pixels.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-load"/>
/// </example>
public static class PsdFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extensions, signature and reader. It has no writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "PSD",
        MimeTypes = ["image/vnd.adobe.photoshop"],
        Extensions = ["psd", "psb"],
        SignatureLength = 6,
        Matches = header => header.Length >= 6 && header[..4].SequenceEqual("8BPS"u8) && header[4] == 0 && header[5] is 1 or 2,
        Reader = PsdReader.Instance,
    };
}

/// <summary>A PSD color mode, by its code in the file header.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-details"/>
/// </example>
public enum PsdColorMode
{
    /// <summary>1-bit black and white (0), loaded as <see cref="Pixels.Gray8"/>.</summary>
    Bitmap = 0,

    /// <summary>Gray (1).</summary>
    Grayscale = 1,

    /// <summary>Indices into a 256-color table (2), loaded as RGB.</summary>
    Indexed = 2,

    /// <summary>Red, green, blue (3).</summary>
    Rgb = 3,

    /// <summary>Cyan, magenta, yellow, black (4).</summary>
    Cmyk = 4,

    /// <summary>Several inks with no fixed meaning (7); the first is loaded as gray.</summary>
    Multichannel = 7,

    /// <summary>Gray printed with up to four inks (8); loaded as gray, the inks kept in <see cref="PsdDetails.ColorModeData"/>.</summary>
    Duotone = 8,

    /// <summary>CIE L*a*b* (9), converted to sRGB on load.</summary>
    Lab = 9,
}

/// <summary>How the flattened image's channels are compressed, by the code before them.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-details"/>
/// </example>
public enum PsdCompression
{
    /// <summary>Uncompressed rows (0).</summary>
    Raw = 0,

    /// <summary>PackBits rows, each row's byte count listed first (1). What Photoshop writes.</summary>
    PackBits = 1,

    /// <summary>Deflate (2).</summary>
    Deflate = 2,

    /// <summary>Deflate of each row's differences from its left neighbor (3).</summary>
    DeflatePredicted = 3,
}

/// <summary>What a layer record is: a layer, or one end of a group.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-layers"/>
/// </example>
public enum PsdLayerKind
{
    /// <summary>A layer with pixels, or an adjustment, text or shape layer.</summary>
    Layer,

    /// <summary>A group, open in Photoshop's layers panel; the layers above it in the list down to its <see cref="GroupEnd"/> are inside it.</summary>
    OpenGroup,

    /// <summary>A group, closed in Photoshop's layers panel.</summary>
    ClosedGroup,

    /// <summary>The hidden record that marks where a group's layers start, below them.</summary>
    GroupEnd,
}

/// <summary>One layer record of a PSD file, read as metadata: the layer's pixels are not loaded.</summary>
/// <param name="Name">The layer's name: its Unicode name when the file has one, else its Pascal name.</param>
/// <param name="Bounds">Where the layer's pixels lie in the image; empty for groups and layers without pixels.</param>
/// <param name="Opacity">The opacity, 0 (transparent) to 1, as DrawImage takes it; the file stores it in 256 steps.</param>
/// <param name="BlendModeKey">The blend mode as Photoshop names it, such as "norm", "mul " or "pass" (pass through, for groups).</param>
/// <param name="BlendMode">The same blend mode in Egami's terms, or <see langword="null"/> when Egami has none like it.</param>
/// <param name="IsVisible">Whether the layer is shown.</param>
/// <param name="IsClipped">Whether the layer is clipped to the layer below it (a clipping mask).</param>
/// <param name="Kind">Whether the record is a layer or one end of a group.</param>
/// <param name="Level">How many groups the record is inside; a group's own records are at the level outside it.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-layers"/>
/// </example>
public sealed record PsdLayer(string Name, Rect Bounds, float Opacity, string BlendModeKey, BlendMode? BlendMode, bool IsVisible, bool IsClipped, PsdLayerKind Kind, int Level)
{
    /// <summary>
    /// Gets the fill opacity, 0 to 1: like <see cref="Opacity"/>, but it leaves layer effects alone. 1 when the file names
    /// none; the file stores it in 256 steps.
    /// </summary>
    public float FillOpacity { get; init; } = 1;

    /// <summary>Gets what the layer holds: pixels, text, a shape, a smart object, a fill, an adjustment, or a group.</summary>
    public PsdLayerType LayerType { get; init; }
}

/// <summary>What a layer holds, from the blocks its record carries.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-layers"/>
/// </example>
public enum PsdLayerType
{
    /// <summary>Pixels painted in Photoshop.</summary>
    Pixel,

    /// <summary>Text; its pixels are Photoshop's rendering of it.</summary>
    Text,

    /// <summary>A shape: a fill bounded by a vector mask; its pixels are Photoshop's rendering of it.</summary>
    Shape,

    /// <summary>A smart object; its pixels are Photoshop's rendering of the embedded or linked file.</summary>
    SmartObject,

    /// <summary>A solid color, gradient or pattern fill.</summary>
    Fill,

    /// <summary>An adjustment layer, such as levels or curves, which changes the layers under it and has no pixels.</summary>
    Adjustment,

    /// <summary>A group record: one end of a group, see <see cref="PsdLayer.Kind"/>.</summary>
    Group,
}

/// <summary>An image resource Egami does not map to <see cref="RasterMetadata"/>, kept as it was read.</summary>
/// <param name="Id">The resource's number, such as 1036 for the thumbnail.</param>
/// <param name="Name">The resource's name, usually empty.</param>
/// <param name="Data">The resource's data.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-details"/>
/// </example>
public sealed record PsdResource(int Id, string Name, ReadOnlyMemory<byte> Data);

/// <summary>Details only PSD and PSB have, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Psd.cs" region="psd-details"/>
/// </example>
public sealed class PsdDetails : FormatDetails
{
    /// <summary>Gets or sets the version: 1 for PSD, 2 for PSB.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the color mode. Defaults to <see cref="PsdColorMode.Rgb"/>.</summary>
    public PsdColorMode ColorMode { get; set; } = PsdColorMode.Rgb;

    /// <summary>Gets or sets the bits of each channel: 1, 8, 16 or 32.</summary>
    public int BitsPerChannel { get; set; } = 8;

    /// <summary>Gets or sets the number of channels stored, color, transparency and extra channels together.</summary>
    public int ChannelCount { get; set; }

    /// <summary>Gets or sets how the flattened image is compressed.</summary>
    public PsdCompression Compression { get; set; }

    /// <summary>
    /// Gets or sets whether the file holds a real flattened image. A file saved without Photoshop's Maximize Compatibility has
    /// only a placeholder, which Load returns with a warning; Egami does not composite the layers itself.
    /// </summary>
    public bool HasRealComposite { get; set; } = true;

    /// <summary>Gets or sets the names of the channels beyond color and transparency (alpha and spot channels), which are not loaded.</summary>
    public IList<string> ExtraChannels { get; set; } = [];

    /// <summary>Gets or sets the layer records, bottom to top as the file lists them.</summary>
    public IList<PsdLayer> Layers { get; set; } = [];

    /// <summary>Gets or sets the color mode data: the duotone inks of a duotone file, empty for most files.</summary>
    public ReadOnlyMemory<byte> ColorModeData { get; set; }

    /// <summary>Gets the image resources Egami does not map, such as the thumbnail (1036), kept as they were read.</summary>
    public IList<PsdResource> OtherResources { get; } = [];

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        PsdDetails copy = new()
        {
            Version = Version,
            ColorMode = ColorMode,
            BitsPerChannel = BitsPerChannel,
            ChannelCount = ChannelCount,
            Compression = Compression,
            HasRealComposite = HasRealComposite,
            ExtraChannels = [.. ExtraChannels],
            Layers = [.. Layers],
            ColorModeData = ColorModeData,
        };
        foreach (PsdResource resource in OtherResources)
        {
            copy.OtherResources.Add(resource);
        }

        return copy;
    }
}
