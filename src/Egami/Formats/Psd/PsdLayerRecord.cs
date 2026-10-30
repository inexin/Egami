namespace Egami.Formats.Psd;

/// <summary>
/// Everything a layer record holds that ReadLayers and the flattener use, read from Adobe's Photoshop File Format Specification:
/// the public <see cref="PsdLayer"/>, the channel list, the masks, the blending ranges and the additional layer information
/// blocks Egami interprets. Blocks Egami does not interpret are listed by key only.
/// </summary>
internal sealed class PsdLayerRecord
{
    /// <summary>The block keys of adjustment layers; the first five are the ones the flattener applies.</summary>
    internal static readonly string[] AdjustmentKeys =
        ["levl", "curv", "hue2", "brit", "nvrt", "CgEd", "hue ", "blnc", "selc", "mixr", "grdm", "thrs", "post", "phfl", "expA", "vibA", "clrL", "blwh"];

    /// <summary>The keys whose data the flattener reads; the data of other blocks is skipped.</summary>
    private static readonly string[] KeptKeys =
        ["SoCo", "vmsk", "vsms", "levl", "curv", "hue2", "brit", "CgEd", "lfx2", "lmfx"];

    public required PsdLayer Info { get; set; }

    public int Top { get; init; }

    public int Left { get; init; }

    public int Bottom { get; init; }

    public int Right { get; init; }

    /// <summary>Gets the channels in the order the file stores their data: id (0 up for color, −1 transparency, −2 the user mask, −3 the real user mask) and length.</summary>
    public PsdChannel[] Channels { get; set; } = [];

    /// <summary>Gets whether bit 4 of the flags says the pixels do not change how the document looks.</summary>
    public bool PixelsIrrelevant { get; init; }

    /// <summary>Gets the user mask (channel −2), or <see langword="null"/>.</summary>
    public PsdMaskRecord? Mask { get; init; }

    /// <summary>Gets the real user mask (channel −3), when the layer has both a user mask and a vector mask.</summary>
    public PsdMaskRecord? RealMask { get; init; }

    /// <summary>Gets the user mask's density, 0 to 255; 255 when the file names none.</summary>
    public int UserMaskDensity { get; init; } = 255;

    /// <summary>Gets the vector mask's density, 0 to 255.</summary>
    public int VectorMaskDensity { get; init; } = 255;

    /// <summary>Gets the user mask's feather radius in pixels; 0 when none.</summary>
    public double UserMaskFeather { get; init; }

    /// <summary>Gets the vector mask's feather radius in pixels.</summary>
    public double VectorMaskFeather { get; init; }

    /// <summary>
    /// Gets the blending ranges ("blend if"), two a channel, the composite gray's first: this layer's range, then the underlying
    /// one. Each packs black low, black high, white low and white high, one byte each from the top.
    /// </summary>
    public uint[] BlendRanges { get; init; } = [];

    /// <summary>Gets whether clipped layers blend as a group before they go onto the base (clbl); on unless the file says off.</summary>
    public bool BlendClippedAsGroup { get; init; } = true;

    /// <summary>Gets the knockout setting (knko): 0 none, 1 shallow, 2 deep.</summary>
    public int Knockout { get; init; }

    /// <summary>Gets the blend key a group's section divider names, which is the group's real mode; <see langword="null"/> when none.</summary>
    public string? SectionBlendKey { get; init; }

    /// <summary>Gets every additional layer information key the record holds, in file order.</summary>
    public List<string> Keys { get; } = [];

    /// <summary>Gets the data of the blocks the flattener reads, by key.</summary>
    public Dictionary<string, byte[]> Blocks { get; } = [];

    /// <summary>Gets the layer's width and height from its bounds; 0 when the bounds are empty or inverted.</summary>
    public (int Width, int Height) Size => Right > Left && Bottom > Top ? ((int)Math.Min((long)Right - Left, int.MaxValue), (int)Math.Min((long)Bottom - Top, int.MaxValue)) : (0, 0);

    /// <summary>Returns whether the flattener reads the data of the block with this key.</summary>
    public static bool Keeps(ReadOnlySpan<byte> key)
    {
        foreach (string kept in KeptKeys)
        {
            if (key.Length == 4 && key[0] == kept[0] && key[1] == kept[1] && key[2] == kept[2] && key[3] == kept[3])
            {
                return true;
            }
        }

        return false;
    }

    public bool Has(string key) => Keys.Contains(key);

    /// <summary>Returns what kind of content the layer has, from its section divider and blocks.</summary>
    public static PsdLayerType TypeOf(PsdLayerKind kind, List<string> keys)
    {
        if (kind != PsdLayerKind.Layer)
        {
            return PsdLayerType.Group;
        }

        if (keys.Exists(k => Array.IndexOf(AdjustmentKeys, k) >= 0))
        {
            return PsdLayerType.Adjustment;
        }

        if (keys.Contains("TySh") || keys.Contains("tySh"))
        {
            return PsdLayerType.Text;
        }

        if (keys.Contains("SoLd") || keys.Contains("PlLd") || keys.Contains("SoLE"))
        {
            return PsdLayerType.SmartObject;
        }

        bool fill = keys.Contains("SoCo") || keys.Contains("GdFl") || keys.Contains("PtFl");
        if (keys.Contains("vscg") || (fill && (keys.Contains("vmsk") || keys.Contains("vsms"))))
        {
            return PsdLayerType.Shape;
        }

        return fill ? PsdLayerType.Fill : PsdLayerType.Pixel;
    }
}

/// <summary>One channel of a layer: its id, its stored length (with the 2-byte compression code) and where its data starts in the file.</summary>
internal readonly record struct PsdChannel(short Id, long Length, long Offset);

/// <summary>A mask's rectangle, the value outside it and its flags, as a layer record stores them.</summary>
internal sealed record PsdMaskRecord(int Top, int Left, int Bottom, int Right, int DefaultColor, int Flags)
{
    /// <summary>Gets whether the mask is turned off in Photoshop.</summary>
    public bool Disabled => (Flags & 0x02) != 0;

    /// <summary>Gets whether the mask is inverted when blending (obsolete, but honored).</summary>
    public bool Inverted => (Flags & 0x04) != 0;

    public (int Width, int Height) Size => Right > Left && Bottom > Top ? ((int)Math.Min((long)Right - Left, int.MaxValue), (int)Math.Min((long)Bottom - Top, int.MaxValue)) : (0, 0);
}
