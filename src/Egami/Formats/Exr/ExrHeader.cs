using System.Buffers.Binary;
using System.Text;

using Egami.Colors;

namespace Egami.Formats.Exr;

/// <summary>One channel as the decoder uses it.</summary>
internal sealed record ExrChannelInfo(string Name, ExrPixelType Type, int XSampling, int YSampling, bool Linear)
{
    /// <summary>Gets the bytes of one sample.</summary>
    public int Bytes => Type == ExrPixelType.Half ? 2 : 4;
}

/// <summary>A part's header, parsed from the OpenEXR file layout: attributes, then the chunk table that follows all headers.</summary>
internal sealed class ExrPartHeader
{
    public required ExrPart Info { get; init; }

    public required ExrChannelInfo[] Channels { get; init; }

    public int MinX { get; init; }

    public int MinY { get; init; }

    public int MaxX { get; init; }

    public int MaxY { get; init; }

    public int Width => MaxX - MinX + 1;

    public int Height => MaxY - MinY + 1;

    public ExrCompression Compression => Info.Compression;

    public bool Tiled => Info.Kind is ExrPartKind.Tiled or ExrPartKind.DeepTiled;

    public bool Deep => Info.Kind is ExrPartKind.DeepScanline or ExrPartKind.DeepTiled;

    public int TileWidth { get; init; }

    public int TileHeight { get; init; }

    public bool RoundUp { get; init; }

    /// <summary>Gets the chunk count the header gives (multi-part files), or -1.</summary>
    public int ChunkCountAttribute { get; init; } = -1;

    /// <summary>Gets or sets where the chunk tables end and the first chunk starts; set on the first part.</summary>
    public long TableEnd { get; set; }

    /// <summary>Gets or sets the chunk offsets, from the table after the headers.</summary>
    public long[] Offsets { get; set; } = [];

    /// <summary>Gets the scanlines in one block of this compression (OpenEXR file layout, "Compression").</summary>
    public int LinesPerBlock => Compression switch
    {
        ExrCompression.None or ExrCompression.Rle or ExrCompression.Zips or ExrCompression.Zstd => 1,
        ExrCompression.Zip or ExrCompression.Pxr24 => 16,
        ExrCompression.Piz or ExrCompression.B44 or ExrCompression.B44A or ExrCompression.Dwaa or ExrCompression.Htj2k32 => 32,
        _ => 256,
    };

    /// <summary>Gets the number of chunks: scanline blocks, or tiles over every level.</summary>
    public long ChunkCount
    {
        get
        {
            if (ChunkCountAttribute >= 0)
            {
                return ChunkCountAttribute;
            }

            if (!Tiled)
            {
                return ((long)Height + LinesPerBlock - 1) / LinesPerBlock;
            }

            long count = 0;
            int xLevels = Levels(Width, true), yLevels = Levels(Height, false);
            if (Info.LevelMode == ExrLevelMode.RipMap)
            {
                for (int ly = 0; ly < yLevels; ly++)
                {
                    for (int lx = 0; lx < xLevels; lx++)
                    {
                        count += TilesAcross(LevelSize(Width, lx), TileWidth) * TilesAcross(LevelSize(Height, ly), TileHeight);
                    }
                }
            }
            else
            {
                int levels = Info.LevelMode == ExrLevelMode.MipMap ? Levels(Math.Max(Width, Height), true) : 1;
                for (int l = 0; l < levels; l++)
                {
                    count += TilesAcross(LevelSize(Width, l), TileWidth) * TilesAcross(LevelSize(Height, l), TileHeight);
                }
            }

            return count;
        }
    }

    /// <summary>Gets the number of tiles across and down the top level.</summary>
    public (int Across, int Down) TopTiles => ((int)TilesAcross(Width, TileWidth), (int)TilesAcross(Height, TileHeight));

    private static long TilesAcross(int size, int tile) => ((long)size + tile - 1) / tile;

    private int Levels(int size, bool _)
    {
        if (Info.LevelMode == ExrLevelMode.OneLevel)
        {
            return 1;
        }

        // floor or ceiling of log2(size), plus one.
        int log = 0;
        if (RoundUp)
        {
            while ((1L << log) < size)
            {
                log++;
            }
        }
        else
        {
            while ((2L << log) <= size)
            {
                log++;
            }
        }

        return log + 1;
    }

    private int LevelSize(int size, int level)
    {
        long s = RoundUp ? ((long)size + (1L << level) - 1) >> level : (long)size >> level;
        return (int)Math.Max(1, s);
    }
}

/// <summary>Parses OpenEXR headers.</summary>
internal static class ExrHeaderReader
{
    internal const uint Magic = 20000630;

    private const int TiledFlag = 0x200;
    private const int LongNamesFlag = 0x400;
    private const int NonImageFlag = 0x800;
    private const int MultipartFlag = 0x1000;

    /// <summary>The most attributes Egami reads from one header.</summary>
    private const int MaxAttributes = 10_000;

    /// <summary>The most channels Egami reads from one part.</summary>
    private const int MaxChannels = 4096;

    /// <summary>The most parts Egami reads from one file.</summary>
    private const int MaxParts = 10_000;

    /// <summary>Reads every header and the chunk table from the start of <paramref name="data"/>; returns the parts and where the table ended.</summary>
    internal static List<ExrPartHeader> Read(ReadOnlySpan<byte> data, EgamiInput input)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
        {
            throw input.Corrupt(0, "The OpenEXR magic number is wrong.");
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if ((version & 0xFF) != 2)
        {
            throw new UnsupportedFormatException($"OpenEXR file format version {version & 0xFF} is not supported; Egami reads version 2.");
        }

        bool multipart = (version & MultipartFlag) != 0;
        bool tiledFlag = (version & TiledFlag) != 0;
        bool deepFlag = (version & NonImageFlag) != 0;
        int pos = 8;
        List<ExrPartHeader> parts = [];
        do
        {
            if (multipart && pos < data.Length && data[pos] == 0)
            {
                pos++;
                break;
            }

            if (parts.Count >= MaxParts)
            {
                throw input.Corrupt(pos, $"The file has more than {MaxParts} parts.");
            }

            parts.Add(ReadPart(data, ref pos, input, multipart, tiledFlag, deepFlag));
        }
        while (multipart);

        if (parts.Count == 0)
        {
            throw input.Corrupt(pos, "The file has no parts.");
        }

        // The chunk tables: one 64-bit offset a chunk, part after part.
        foreach (ExrPartHeader part in parts)
        {
            long count = part.ChunkCount;
            if (count < 0 || count > (data.Length - pos) / 8)
            {
                throw input.Corrupt(pos, $"The chunk table of {count} entries does not fit in the file.");
            }

            long[] offsets = new long[count];
            for (int i = 0; i < count; i++)
            {
                offsets[i] = BinaryPrimitives.ReadInt64LittleEndian(data[pos..]);
                pos += 8;
            }

            part.Offsets = offsets;
        }

        parts[0].TableEnd = pos;
        return parts;
    }

    private static ExrPartHeader ReadPart(ReadOnlySpan<byte> data, ref int pos, EgamiInput input, bool multipart, bool tiledFlag, bool deepFlag)
    {
        List<ExrHeaderEntry> attributes = [];
        List<ExrChannelInfo>? channels = null;
        int[]? dataWindow = null, displayWindow = null;
        ExrCompression? compression = null;
        int lineOrder = 0;
        float aspect = 1;
        string name = string.Empty;
        string? type = null;
        int chunkCount = -1;
        int tileWidth = 0, tileHeight = 0, levelMode = 0;
        bool roundUp = false;
        ColorPrimaries? chromaticities = null;
        double? whiteLuminance = null, fps = null;
        string? interop = null, view = null;
        ulong? timeCode = null;
        while (true)
        {
            if (pos >= data.Length)
            {
                throw input.Corrupt(pos, "The file ends inside a header.");
            }

            if (data[pos] == 0)
            {
                pos++;
                break;
            }

            if (attributes.Count >= MaxAttributes)
            {
                throw input.Corrupt(pos, $"A header has more than {MaxAttributes} attributes.");
            }

            long at = pos;
            string attrName = ReadString(data, ref pos, input);
            string attrType = ReadString(data, ref pos, input);
            if (pos > data.Length - 4)
            {
                throw input.Corrupt(pos, "The file ends inside a header.");
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(data[pos..]);
            pos += 4;
            if (size < 0 || size > data.Length - pos)
            {
                throw input.Corrupt(at, $"Attribute '{attrName}' says it holds {size} bytes, past the end of the file.");
            }

            ReadOnlySpan<byte> value = data.Slice(pos, size);
            pos += size;
            input.Limits.CheckMetadataBytes(pos);
            attributes.Add(new ExrHeaderEntry(attrName, attrType, value.ToArray()));
            switch (attrName)
            {
                case "channels" when attrType == "chlist":
                    channels = ReadChannels(value, input, at);
                    break;
                case "compression" when attrType == "compression" && size >= 1:
                    compression = (ExrCompression)value[0];
                    break;
                case "dataWindow" when attrType == "box2i" && size >= 16:
                    dataWindow = Box(value);
                    break;
                case "displayWindow" when attrType == "box2i" && size >= 16:
                    displayWindow = Box(value);
                    break;
                case "lineOrder" when attrType == "lineOrder" && size >= 1:
                    lineOrder = value[0];
                    break;
                case "pixelAspectRatio" when attrType == "float" && size >= 4:
                    aspect = BinaryPrimitives.ReadSingleLittleEndian(value);
                    break;
                case "name" when attrType == "string":
                    name = Encoding.UTF8.GetString(value);
                    break;
                case "type" when attrType == "string":
                    type = Encoding.ASCII.GetString(value);
                    break;
                case "chunkCount" when attrType == "int" && size >= 4:
                    chunkCount = BinaryPrimitives.ReadInt32LittleEndian(value);
                    break;
                case "tiles" when attrType == "tiledesc" && size >= 9:
                    tileWidth = BinaryPrimitives.ReadInt32LittleEndian(value);
                    tileHeight = BinaryPrimitives.ReadInt32LittleEndian(value[4..]);
                    levelMode = value[8] & 0x0F;
                    roundUp = (value[8] >> 4) == 1;
                    break;
                case "chromaticities" when attrType == "chromaticities" && size >= 32:
                    chromaticities = new ColorPrimaries(
                        new(F(value, 0), F(value, 4)), new(F(value, 8), F(value, 12)), new(F(value, 16), F(value, 20)), new(F(value, 24), F(value, 28)));
                    break;
                case "whiteLuminance" when attrType == "float" && size >= 4:
                    whiteLuminance = F(value, 0);
                    break;
                case "colorInteropID" when attrType == "string":
                    interop = Encoding.UTF8.GetString(value);
                    break;
                case "view" when attrType == "string":
                    view = Encoding.UTF8.GetString(value);
                    break;
                case "framesPerSecond" when attrType == "rational" && size >= 8:
                    uint denominator = BinaryPrimitives.ReadUInt32LittleEndian(value[4..]);
                    fps = denominator == 0 ? null : (double)BinaryPrimitives.ReadInt32LittleEndian(value) / denominator;
                    break;
                case "timeCode" when attrType == "timecode" && size >= 8:
                    timeCode = BinaryPrimitives.ReadUInt64LittleEndian(value);
                    break;
            }
        }

        if (channels is null || compression is null || dataWindow is null || displayWindow is null)
        {
            string missing = channels is null ? "channels" : compression is null ? "compression" : dataWindow is null ? "dataWindow" : "displayWindow";
            throw input.Corrupt(pos, $"A header lacks the required '{missing}' attribute.");
        }

        ExrPartKind kind = type switch
        {
            "scanlineimage" => ExrPartKind.Scanline,
            "tiledimage" => ExrPartKind.Tiled,
            "deepscanline" => ExrPartKind.DeepScanline,
            "deeptile" => ExrPartKind.DeepTiled,
            null when deepFlag => tiledFlag ? ExrPartKind.DeepTiled : ExrPartKind.DeepScanline,
            null => tiledFlag ? ExrPartKind.Tiled : ExrPartKind.Scanline,
            _ => throw new UnsupportedFormatException($"OpenEXR part type '{type}' is not supported."),
        };
        if (multipart && chunkCount < 0)
        {
            throw input.Corrupt(pos, "A part of a multi-part file lacks its 'chunkCount' attribute.");
        }

        if (dataWindow[2] < dataWindow[0] || dataWindow[3] < dataWindow[1])
        {
            throw input.Corrupt(pos, "The data window is empty.");
        }

        long width = (long)dataWindow[2] - dataWindow[0] + 1, height = (long)dataWindow[3] - dataWindow[1] + 1;
        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw input.Corrupt(pos, $"The data window of {width} × {height} is too large.");
        }

        bool tiled = kind is ExrPartKind.Tiled or ExrPartKind.DeepTiled;
        if (tiled && (tileWidth < 1 || tileHeight < 1 || levelMode > 2))
        {
            throw input.Corrupt(pos, "A tiled part lacks a valid 'tiles' attribute.");
        }

        foreach (ExrChannelInfo channel in channels)
        {
            if (dataWindow[0] % channel.XSampling != 0 || dataWindow[1] % channel.YSampling != 0 || width % channel.XSampling != 0 || height % channel.YSampling != 0)
            {
                throw input.Corrupt(pos, $"Channel '{channel.Name}' is subsampled {channel.XSampling} × {channel.YSampling}, which the data window is not a multiple of.");
            }

            if (tiled && (channel.XSampling != 1 || channel.YSampling != 1))
            {
                throw input.Corrupt(pos, $"Channel '{channel.Name}' of a tiled part is subsampled.");
            }
        }

        SortedSet<string> layers = new(StringComparer.Ordinal);
        foreach (ExrChannelInfo channel in channels)
        {
            int dot = channel.Name.LastIndexOf('.');
            if (dot > 0)
            {
                layers.Add(channel.Name[..dot]);
            }
        }

        ExrPart info = new()
        {
            Name = name,
            Kind = kind,
            Compression = compression.Value,
            Channels = [.. channels.Select(c => new ExrChannel(c.Name, c.Type, c.XSampling, c.YSampling, c.Linear))],
            Layers = [.. layers],
            DataWindow = new Rect(dataWindow[0], dataWindow[1], (int)width, (int)height),
            DisplayWindow = new Rect(displayWindow[0], displayWindow[1], (int)Math.Clamp((long)displayWindow[2] - displayWindow[0] + 1, 0, int.MaxValue), (int)Math.Clamp((long)displayWindow[3] - displayWindow[1] + 1, 0, int.MaxValue)),
            DecreasingY = lineOrder == 1,
            PixelAspectRatio = aspect,
            TileSize = tiled ? new Size(tileWidth, tileHeight) : null,
            LevelMode = (ExrLevelMode)levelMode,
            Chromaticities = chromaticities,
            WhiteLuminance = whiteLuminance,
            ColorInteropId = interop,
            View = view,
            FramesPerSecond = fps,
            TimeCode = timeCode,
            Attributes = attributes,
        };
        return new ExrPartHeader
        {
            Info = info,
            Channels = [.. channels],
            MinX = dataWindow[0],
            MinY = dataWindow[1],
            MaxX = dataWindow[2],
            MaxY = dataWindow[3],
            TileWidth = tileWidth,
            TileHeight = tileHeight,
            RoundUp = roundUp,
            ChunkCountAttribute = chunkCount,
        };
    }

    private static List<ExrChannelInfo> ReadChannels(ReadOnlySpan<byte> value, EgamiInput input, long at)
    {
        List<ExrChannelInfo> channels = [];
        int pos = 0;
        while (pos < value.Length && value[pos] != 0)
        {
            if (channels.Count >= MaxChannels)
            {
                throw input.Corrupt(at, $"The channel list holds more than {MaxChannels} channels.");
            }

            string name = ReadString(value, ref pos, input);
            if (pos > value.Length - 16)
            {
                throw input.Corrupt(at, "The channel list is cut short.");
            }

            int type = BinaryPrimitives.ReadInt32LittleEndian(value[pos..]);
            bool linear = value[pos + 4] != 0;
            int xs = BinaryPrimitives.ReadInt32LittleEndian(value[(pos + 8)..]);
            int ys = BinaryPrimitives.ReadInt32LittleEndian(value[(pos + 12)..]);
            pos += 16;
            if (type is < 0 or > 2)
            {
                throw input.Corrupt(at, $"Channel '{name}' has pixel type {type}, which is not uint (0), half (1) or float (2).");
            }

            if (xs < 1 || ys < 1 || xs > 1 << 16 || ys > 1 << 16)
            {
                throw input.Corrupt(at, $"Channel '{name}' has sampling {xs} × {ys}.");
            }

            channels.Add(new ExrChannelInfo(name, (ExrPixelType)type, xs, ys, linear));
        }

        if (channels.Count == 0)
        {
            throw input.Corrupt(at, "The channel list is empty.");
        }

        return channels;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int pos, EgamiInput input)
    {
        int end = data[pos..].IndexOf((byte)0);
        if (end < 0 || end > 255)
        {
            throw input.Corrupt(pos, "A header name is not terminated within 255 bytes.");
        }

        string text = Encoding.UTF8.GetString(data.Slice(pos, end));
        pos += end + 1;
        return text;
    }

    private static int[] Box(ReadOnlySpan<byte> v) =>
        [BinaryPrimitives.ReadInt32LittleEndian(v), BinaryPrimitives.ReadInt32LittleEndian(v[4..]), BinaryPrimitives.ReadInt32LittleEndian(v[8..]), BinaryPrimitives.ReadInt32LittleEndian(v[12..])];

    private static float F(ReadOnlySpan<byte> v, int at) => BinaryPrimitives.ReadSingleLittleEndian(v[at..]);
}
