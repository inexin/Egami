using System.Buffers.Binary;
using System.Text;

using Egami.Metadata;
using Egami.Operations;

namespace Egami.Formats.Psd;

/// <summary>
/// A PSD or PSB file's header and sections up to its flattened image, read in order from Adobe's Photoshop File Format
/// Specification: the header, the color mode data, the image resources (kept only as far as Egami maps them or the caller reads
/// metadata), and the layer and mask information, of which only the layer records and the sign of the layer count are kept;
/// the layers' pixels are skipped by their lengths without being held.
/// </summary>
internal sealed class PsdFile
{
    /// <summary>Image resources whose data Egami reads whether or not metadata is wanted, since they change the pixels.</summary>
    private const int VersionInfoId = 1057;
    private const int TransparentIndexId = 1047;
    private const int AlphaNamesId = 1006;
    private const int UnicodeAlphaNamesId = 1045;

    public int Version { get; private set; }

    public int Channels { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Depth { get; private set; }

    public PsdColorMode Mode { get; private set; }

    public byte[] ColorData { get; private set; } = [];

    public bool HasRealComposite { get; private set; } = true;

    public int? TransparentIndex { get; private set; }

    /// <summary>Gets the signed layer count: negative when the flattened image's first extra channel is its transparency.</summary>
    public int LayerCount { get; private set; }

    public List<PsdLayer> Layers { get; } = [];

    public List<string> AlphaNames { get; } = [];

    public List<(int Id, string Name, byte[] Data)> Resources { get; } = [];

    public PsdCompression Compression { get; private set; }

    /// <summary>Gets where the flattened image's channel data starts, after its compression code, from the start of the file.</summary>
    public long DataStart { get; private set; }

    public WarningList Warnings { get; } = new();

    public bool Big => Version == 2;

    /// <summary>Gets the channels that make the color: 1 for gray, bitmap, indexed, duotone and multichannel, 3 for RGB and Lab, 4 for CMYK.</summary>
    public int ColorChannels => Mode switch
    {
        PsdColorMode.Rgb or PsdColorMode.Lab => 3,
        PsdColorMode.Cmyk => 4,
        _ => 1,
    };

    /// <summary>Gets whether the channel after the color ones is the flattened image's transparency.</summary>
    public bool MergedAlpha => LayerCount < 0 && Channels > ColorChannels && Mode is not (PsdColorMode.Bitmap or PsdColorMode.Multichannel);

    /// <summary>Reads everything before the flattened image's data and leaves the input at its first byte.</summary>
    /// <param name="input">The data, at the start of the file.</param>
    /// <param name="options">The caller's options: metadata and layer records are kept only when it reads metadata.</param>
    public static PsdFile Read(EgamiInput input, ReaderOptions options)
    {
        PsdFile file = new();
        file.ReadHeader(input);
        long colorLength = input.ReadUInt32BigEndian();
        Remaining(input, colorLength, "color mode data");
        if (colorLength > 0)
        {
            input.CheckMetadataBytes(colorLength);
            file.ColorData = new byte[colorLength];
            input.ReadExactly(file.ColorData);
            if (file.Mode == PsdColorMode.Indexed && colorLength < 768)
            {
                throw input.Corrupt(26, $"An indexed file's color table is {colorLength} bytes; it must be 768.");
            }
        }
        else if (file.Mode == PsdColorMode.Indexed)
        {
            throw input.Corrupt(26, "An indexed file has no color table.");
        }

        file.ReadResources(input, options);
        file.ReadLayerSection(input, options);
        file.Compression = (PsdCompression)input.ReadUInt16BigEndian();
        if (file.Compression is not (PsdCompression.Raw or PsdCompression.PackBits or PsdCompression.Deflate or PsdCompression.DeflatePredicted))
        {
            throw input.Corrupt(input.Position - 2, $"The image data's compression code {(int)file.Compression} is unknown.");
        }

        file.DataStart = input.Position;
        return file;
    }

    /// <summary>Throws when a section's length passes the end of a stream whose length is known.</summary>
    private static void Remaining(EgamiInput input, long length, string section)
    {
        if (length < 0 || (input.Length is long total && length > total - input.Position))
        {
            throw new CorruptDataException(input.FormatName, input.Position, $"The {section} is {length} bytes; the file ends before that.") { IsTruncation = true };
        }
    }

    private void ReadHeader(EgamiInput input)
    {
        Span<byte> header = stackalloc byte[26];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("8BPS"u8))
        {
            throw input.Corrupt(0, "The PSD signature is wrong.");
        }

        Version = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);
        if (Version is not (1 or 2))
        {
            throw input.Corrupt(4, $"PSD version {Version} is unknown; 1 is PSD and 2 is PSB.");
        }

        Channels = BinaryPrimitives.ReadUInt16BigEndian(header[12..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[14..]);
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[18..]);
        Depth = BinaryPrimitives.ReadUInt16BigEndian(header[22..]);
        int mode = BinaryPrimitives.ReadUInt16BigEndian(header[24..]);
        if (Channels is < 1 or > 56)
        {
            throw input.Corrupt(12, $"The file has {Channels} channels; PSD allows 1 to 56.");
        }

        uint maxSide = Version == 1 ? 30_000u : 300_000u;
        if (width < 1 || height < 1 || width > maxSide || height > maxSide)
        {
            throw input.Corrupt(14, $"The image size {width}×{height} is outside 1 to {maxSide:N0} pixels a side.");
        }

        Width = (int)width;
        Height = (int)height;
        input.Limits.CheckFrameSize(Width, Height);
        if (Depth is not (1 or 8 or 16 or 32))
        {
            throw input.Corrupt(22, $"{Depth} bits a channel is not a PSD depth; it has 1, 8, 16 and 32.");
        }

        if (mode is not (0 or 1 or 2 or 3 or 4 or 7 or 8 or 9))
        {
            throw input.Corrupt(24, $"The color mode {mode} is unknown.");
        }

        Mode = (PsdColorMode)mode;
        bool fits = Mode switch
        {
            PsdColorMode.Bitmap => Depth == 1,
            PsdColorMode.Indexed => Depth == 8,
            PsdColorMode.Grayscale or PsdColorMode.Rgb => Depth is 8 or 16 or 32,
            _ => Depth is 8 or 16,
        };
        if (!fits)
        {
            throw new UnsupportedFormatException($"{Depth}-bit {Mode} PSD files are not supported; Photoshop does not make them.");
        }

        if (Channels < ColorChannels)
        {
            throw input.Corrupt(12, $"A {Mode} file needs {ColorChannels} channels; it has {Channels}.");
        }
    }

    private void ReadResources(EgamiInput input, ReaderOptions options)
    {
        long length = input.ReadUInt32BigEndian();
        Remaining(input, length, "image resource section");
        long end = input.Position + length;
        long kept = 0;
        Span<byte> head = stackalloc byte[6];
        Span<byte> nameBuffer = stackalloc byte[256];
        while (input.Position + 12 <= end)
        {
            long at = input.Position;
            input.ReadExactly(head);
            if (!head[..4].SequenceEqual("8BIM"u8) && !head[..4].SequenceEqual("MeSa"u8) && !head[..4].SequenceEqual("PHUT"u8)
                && !head[..4].SequenceEqual("AgHg"u8) && !head[..4].SequenceEqual("DCSR"u8))
            {
                Warnings.Add("Psd.ResourcesDamaged", "An image resource has no signature; the rest of the resources were skipped.", at);
                break;
            }

            int id = BinaryPrimitives.ReadUInt16BigEndian(head[4..]);
            int nameLength = input.ReadByte();
            Span<byte> nameBytes = nameBuffer[..(nameLength + ((nameLength + 1) % 2))];
            input.ReadExactly(nameBytes);
            string name = Encoding.Latin1.GetString(nameBytes[..nameLength]);
            long size = input.ReadUInt32BigEndian();
            long padded = size + (size % 2);
            if (input.Position + size > end)
            {
                Warnings.Add("Psd.ResourcesDamaged", $"Image resource {id} is longer than its section; the rest of the resources were skipped.", at);
                break;
            }

            bool needed = id is VersionInfoId or TransparentIndexId or AlphaNamesId or UnicodeAlphaNamesId;
            if (needed || options.ReadMetadata)
            {
                kept += size;
                input.CheckMetadataBytes(kept);
                byte[] data = new byte[size];
                input.ReadExactly(data);
                input.Skip(Math.Min(padded - size, end - input.Position));
                Use(id, name, data);
            }
            else
            {
                input.Skip(Math.Min(padded, end - input.Position));
            }
        }

        input.Skip(Math.Max(0, end - input.Position));
    }

    private void Use(int id, string name, byte[] data)
    {
        switch (id)
        {
            case VersionInfoId:
                if (data.Length >= 5)
                {
                    HasRealComposite = data[4] != 0;
                }

                return;
            case TransparentIndexId:
                if (data.Length >= 2)
                {
                    TransparentIndex = BinaryPrimitives.ReadUInt16BigEndian(data);
                }

                return;
            case AlphaNamesId:
                for (int at = 0; at < data.Length;)
                {
                    int n = Math.Min(data[at], data.Length - at - 1);
                    AlphaNames.Add(Encoding.Latin1.GetString(data, at + 1, n));
                    at += 1 + n;
                }

                return;
            case UnicodeAlphaNamesId:
                List<string> names = [];
                for (int at = 0; at + 4 <= data.Length;)
                {
                    long count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
                    at += 4;
                    int bytes = (int)Math.Min(count * 2, data.Length - at);
                    names.Add(Encoding.BigEndianUnicode.GetString(data, at, bytes).TrimEnd('\0'));
                    at += bytes;
                }

                AlphaNames.Clear();
                AlphaNames.AddRange(names);
                return;
            default:
                Resources.Add((id, name, data));
                return;
        }
    }

    private long ReadLength(EgamiInput input) => Big ? (long)Math.Min(ReadUInt64(input), long.MaxValue) : input.ReadUInt32BigEndian();

    private static ulong ReadUInt64(EgamiInput input)
    {
        Span<byte> bytes = stackalloc byte[8];
        input.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    /// <summary>Gets whether an additional layer information block's length is 8 bytes in a PSB file.</summary>
    private static bool LongKey(ReadOnlySpan<byte> key)
    {
        return key.SequenceEqual("LMsk"u8) || key.SequenceEqual("Lr16"u8) || key.SequenceEqual("Lr32"u8) || key.SequenceEqual("Layr"u8)
            || key.SequenceEqual("Mt16"u8) || key.SequenceEqual("Mt32"u8) || key.SequenceEqual("Mtrn"u8) || key.SequenceEqual("Alph"u8)
            || key.SequenceEqual("FMsk"u8) || key.SequenceEqual("lnk2"u8) || key.SequenceEqual("FEid"u8) || key.SequenceEqual("FXid"u8)
            || key.SequenceEqual("PxSD"u8);
    }

    /// <summary>
    /// Reads the layer and mask information: the layer count and records of the layer info, or of an Lr16, Lr32 or Layr block
    /// where 16 and 32-bit files keep them. Damage in the records is a warning: the flattened image does not depend on them.
    /// </summary>
    private void ReadLayerSection(EgamiInput input, ReaderOptions options)
    {
        long length = ReadLength(input);
        Remaining(input, length, "layer and mask section");
        long end = input.Position + length;
        if (length == 0)
        {
            return;
        }

        bool strict = options.Recovery == RecoveryMode.Strict;
        try
        {
            long infoLength = ReadLength(input);
            if (input.Position + infoLength > end)
            {
                throw input.Corrupt(input.Position, "The layer info is longer than its section.");
            }

            long infoEnd = input.Position + infoLength;
            if (infoLength >= 2)
            {
                ReadLayerInfo(input, infoEnd, options);
            }

            input.Skip(infoEnd - input.Position);
            if (input.Position + 4 <= end)
            {
                long mask = input.ReadUInt32BigEndian();
                input.Skip(Math.Min(mask, end - input.Position));
            }

            Span<byte> head = stackalloc byte[8];
            while (input.Position + 12 <= end)
            {
                input.ReadExactly(head);
                if (!head[..4].SequenceEqual("8BIM"u8) && !head[..4].SequenceEqual("8B64"u8))
                {
                    break;
                }

                long blockLength = Big && LongKey(head[4..]) ? ReadLength(input) : input.ReadUInt32BigEndian();
                if (input.Position + blockLength > end)
                {
                    break;
                }

                long blockEnd = input.Position + blockLength;
                if (head[4..].SequenceEqual("Lr16"u8) || head[4..].SequenceEqual("Lr32"u8) || head[4..].SequenceEqual("Layr"u8))
                {
                    if (blockLength >= 2 && Layers.Count == 0 && LayerCount == 0)
                    {
                        ReadLayerInfo(input, blockEnd, options);
                    }
                }

                input.Skip(blockEnd - input.Position);
                if (blockLength % 2 == 1 && input.Position < end)
                {
                    input.Skip(1);
                }
            }
        }
        catch (CorruptDataException e) when (!strict && !e.IsTruncation)
        {
            Warnings.Add("Psd.LayersDamaged", $"{e.Message} The layer records after the damage were not read.", e);
        }

        if (input.Position > end)
        {
            throw input.Corrupt(end, "The layer and mask section's contents run past its end.");
        }

        input.Skip(end - input.Position);
    }

    private void ReadLayerInfo(EgamiInput input, long end, ReaderOptions options)
    {
        LayerCount = (short)input.ReadUInt16BigEndian();
        if (!options.ReadMetadata)
        {
            return;
        }

        int count = Math.Abs(LayerCount);
        int level = 0;
        long kept = 0;
        for (int i = 0; i < count; i++)
        {
            if (input.Position + 34 > end)
            {
                throw input.Corrupt(input.Position, $"The layer info ends after {i} of {count} layer records.");
            }

            long start = input.Position;
            PsdLayer layer = ReadRecord(input, end, ref level);
            kept += input.Position - start + (layer.Name.Length * 2L);
            input.CheckMetadataBytes(kept);
            Layers.Add(layer);
        }
    }

    private PsdLayer ReadRecord(EgamiInput input, long end, ref int level)
    {
        int top = (int)input.ReadUInt32BigEndian();
        int left = (int)input.ReadUInt32BigEndian();
        int bottom = (int)input.ReadUInt32BigEndian();
        int right = (int)input.ReadUInt32BigEndian();
        int channels = input.ReadUInt16BigEndian();
        if (channels > 56)
        {
            throw input.Corrupt(input.Position - 2, $"A layer has {channels} channels; PSD allows 56.");
        }

        input.Skip(channels * (Big ? 10L : 6L));
        Span<byte> blend = stackalloc byte[12];
        input.ReadExactly(blend);
        if (!blend[..4].SequenceEqual("8BIM"u8))
        {
            throw input.Corrupt(input.Position - 12, "A layer record's blend mode signature is wrong.");
        }

        string key = Encoding.Latin1.GetString(blend.Slice(4, 4));
        int opacity = blend[8];
        bool clipped = blend[9] != 0;
        bool visible = (blend[10] & 0x02) == 0;
        long extra = input.ReadUInt32BigEndian();
        long extraEnd = input.Position + extra;
        if (extraEnd > end)
        {
            throw input.Corrupt(input.Position - 4, "A layer record's extra data is longer than the layer info.");
        }

        long maskLength = input.ReadUInt32BigEndian();
        input.Skip(Math.Min(maskLength, Math.Max(0, extraEnd - input.Position)));
        long rangesLength = Math.Min(input.ReadUInt32BigEndian(), Math.Max(0, extraEnd - input.Position));
        input.Skip(rangesLength);
        int nameLength = input.ReadByte();
        Span<byte> nameBytes = stackalloc byte[nameLength];
        input.ReadExactly(nameBytes);
        string name = Encoding.Latin1.GetString(nameBytes);
        input.Skip((4 - ((nameLength + 1) % 4)) % 4);

        PsdLayerKind kind = PsdLayerKind.Layer;
        Span<byte> head = stackalloc byte[8];
        while (input.Position + 12 <= extraEnd)
        {
            input.ReadExactly(head);
            if (!head[..4].SequenceEqual("8BIM"u8) && !head[..4].SequenceEqual("8B64"u8))
            {
                break;
            }

            long blockLength = Big && LongKey(head[4..]) ? ReadLength(input) : input.ReadUInt32BigEndian();
            long blockEnd = input.Position + blockLength;
            if (blockEnd > extraEnd)
            {
                break;
            }

            if (head[4..].SequenceEqual("luni"u8) && blockLength >= 4)
            {
                long units = input.ReadUInt32BigEndian();
                int bytes = (int)Math.Min(units * 2, blockEnd - input.Position);
                byte[] text = new byte[bytes & ~1];
                input.ReadExactly(text);
                name = Encoding.BigEndianUnicode.GetString(text).TrimEnd('\0');
            }
            else if ((head[4..].SequenceEqual("lsct"u8) || head[4..].SequenceEqual("lsdk"u8)) && blockLength >= 4)
            {
                kind = input.ReadUInt32BigEndian() switch
                {
                    1 => PsdLayerKind.OpenGroup,
                    2 => PsdLayerKind.ClosedGroup,
                    3 => PsdLayerKind.GroupEnd,
                    _ => PsdLayerKind.Layer,
                };
            }

            input.Skip(blockEnd - input.Position);
        }

        input.Skip(Math.Max(0, extraEnd - input.Position));
        int at = level;
        if (kind == PsdLayerKind.GroupEnd)
        {
            level++;
        }
        else if (kind is PsdLayerKind.OpenGroup or PsdLayerKind.ClosedGroup)
        {
            at = level = Math.Max(0, level - 1);
        }

        Rect bounds = right > left && bottom > top ? new Rect(left, top, (int)Math.Min((long)right - left, int.MaxValue), (int)Math.Min((long)bottom - top, int.MaxValue)) : default;
        return new PsdLayer(name, bounds, opacity, key, BlendModeOf(key), visible, clipped, kind, at);
    }

    /// <summary>Returns Egami's blend mode for a Photoshop blend key, or <see langword="null"/> when Egami has none like it.</summary>
    internal static BlendMode? BlendModeOf(string key) => key switch
    {
        "norm" => BlendMode.SourceOver,
        "mul " => BlendMode.Multiply,
        "scrn" => BlendMode.Screen,
        "over" => BlendMode.Overlay,
        "dark" => BlendMode.Darken,
        "lite" => BlendMode.Lighten,
        "div " => BlendMode.ColorDodge,
        "idiv" => BlendMode.ColorBurn,
        "hLit" => BlendMode.HardLight,
        "sLit" => BlendMode.SoftLight,
        "diff" => BlendMode.Difference,
        "smud" => BlendMode.Exclusion,
        "hue " => BlendMode.Hue,
        "sat " => BlendMode.Saturation,
        "colr" => BlendMode.Color,
        "lum " => BlendMode.Luminosity,
        _ => null,
    };
}
