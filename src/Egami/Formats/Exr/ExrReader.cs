using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Formats.Tiff;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Exr;

/// <summary>
/// Reads OpenEXR, written from the OpenEXR file layout and technical introduction, with OpenEXR's own library read for the
/// compressions and the luminance-chroma conversion (BSD-3-Clause; see THIRD-PARTY-NOTICES). Scanline and tiled parts, single
/// and multi-part files, half, float and uint channels; the top level of mip and rip maps; deep parts are refused.
/// The chosen channels are decoded into planes, block by block (in parallel when the context allows), then composed into rows.
/// </summary>
internal sealed class ExrReader : IFormatReader, IMultiImageReader
{
    internal static readonly ExrReader Instance = new();

    /// <summary>Returns whether the data starts with the OpenEXR magic number 76 2F 31 01.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header) == ExrHeaderReader.Magic;
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        using ExrFile file = ExrFile.Open(input);
        ExrPartHeader part = file.Part(options);
        Selection selection = Selection.Choose(part, options, input);
        Rect window = Window(part, options);
        PixelLayoutInfo layout = selection.Layout;
        return new RasterInfo
        {
            Format = ExrFormat.Info,
            Width = window.Width,
            Height = window.Height,
            Layout = layout,
            FrameCount = 1,
            ImageCount = file.Parts.Count(p => !p.Deep),
            HasMetadata = true,
            EstimatedBytes = (long)window.Width * window.Height * layout.BytesPerPixel,
        };
    }

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        using ExrFile file = ExrFile.Open(input);
        ExrReaderOptions exr = options as ExrReaderOptions ?? new ExrReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
            Format = options.Format,
        };
        List<ReaderOptions> list = [];
        for (int i = 0; i < file.Parts.Count; i++)
        {
            if (!file.Parts[i].Deep)
            {
                list.Add(exr with { Part = i });
            }
        }

        return list.Count > 0 ? list : [exr];
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        using ExrFile file = ExrFile.Open(input);
        ExrPartHeader part = file.Part(options);
        Selection selection = Selection.Choose(part, options, input);
        return selection.Layout.ChannelType == PixelChannelType.Float16 ? Decode<RgbaF16>(input, options, file, part, selection)
            : selection.Layout.ChannelOrder == PixelChannelOrder.Gray ? Decode<GrayF32>(input, options, file, part, selection)
            : Decode<RgbaF32>(input, options, file, part, selection);
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using ExrFile file = ExrFile.Open(input);
        ExrPartHeader part = file.Part(options);
        return Decode<TPixel>(input, options, file, part, Selection.Choose(part, options, input));
    }

    private static Rect Window(ExrPartHeader part, ReaderOptions options)
    {
        if (options is ExrReaderOptions { Channel: string name } && Array.Find(part.Channels, c => c.Name == name) is ExrChannelInfo channel)
        {
            // One channel loads at its stored resolution.
            return new Rect(part.MinX, part.MinY, ExrBlock.Sampled(part.MinX, part.MaxX, channel.XSampling), ExrBlock.Sampled(part.MinY, part.MaxY, channel.YSampling));
        }

        return options is ExrReaderOptions { Window: ExrWindow.Display } ? part.Info.DisplayWindow : part.Info.DataWindow;
    }

    private static Raster<TPixel> Decode<TPixel>(EgamiInput input, ReaderOptions options, ExrFile file, ExrPartHeader part, Selection selection)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rect window = Window(part, options);
        if (window.Width < 1 || window.Height < 1)
        {
            throw input.Corrupt(0, "The display window is empty.");
        }

        using ExrPlanes planes = ExrPlanes.Decode(input, file, part, selection.Channels, options.Recovery == RecoveryMode.Strict, out List<(string Id, string Message)> problems);
        Raster<TPixel> raster = input.CreateRaster<TPixel>(window.Width, window.Height, ClearMode.None);
        try
        {
            RasterMetadata metadata = raster.Metadata;
            metadata.FormatName = ExrFormat.Info.Name;
            metadata.FormatDetails = new ExrDetails { Parts = file.Infos, Part = file.Parts.IndexOf(part), Layer = selection.Layer, Window = window };
            metadata.ColorSpace = ColorSpaceHint.Linear;
            if (part.Info.Chromaticities is ColorPrimaries primaries && primaries.IsValid)
            {
                metadata.Primaries = primaries;
            }

            if (file.Rebuilt)
            {
                metadata.WarningList.Add("Exr.ChunkTableRebuilt", "The chunk table was incomplete, as a writer that did not finish leaves it; it was rebuilt from the chunks.");
            }

            foreach ((string id, string message) in problems)
            {
                metadata.WarningList.Add(id, message);
            }

            float peak = ExrCompose.Compose(planes, part, selection, window, raster, input.CancellationToken);
            if (selection.Kind != SelectionKind.Channel && peak > 0 && float.IsFinite(peak))
            {
                metadata.Hdr = new HdrMetadata { MaxContentLightLevel = peak * 203.0 };
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }
}

/// <summary>What a load composes: RGBA from a layer, luminance with chroma, gray, or one channel.</summary>
internal enum SelectionKind
{
    Rgba,
    Yca,
    Gray,
    Channel,
}

/// <summary>The channels a load reads, by role: R, G, B, A (or Y, RY, BY, A); a channel index of -1 is absent.</summary>
internal sealed class Selection
{
    public SelectionKind Kind { get; init; }

    public string Layer { get; init; } = string.Empty;

    /// <summary>Gets the indices of the channels for R, G, B and A, or Y, RY, BY and A, or the one channel first.</summary>
    public int[] Roles { get; init; } = [-1, -1, -1, -1];

    /// <summary>Gets the distinct channels to decode.</summary>
    public int[] Channels => [.. Roles.Where(r => r >= 0).Distinct()];

    public required PixelLayoutInfo Layout { get; init; }

    /// <summary>Chooses the channels of the part to load, as <see cref="ExrReaderOptions"/> say.</summary>
    public static Selection Choose(ExrPartHeader part, ReaderOptions options, EgamiInput input)
    {
        if (part.Deep)
        {
            throw new UnsupportedFormatException("OpenEXR deep data (a varying number of samples a pixel) is not supported.");
        }

        ExrChannelInfo[] channels = part.Channels;
        if (options is ExrReaderOptions { Channel: string wanted })
        {
            int index = Array.FindIndex(channels, c => c.Name == wanted);
            if (index < 0)
            {
                throw new ArgumentException($"The part has no channel '{wanted}'; it has {string.Join(", ", channels.Select(c => c.Name))}.", nameof(options));
            }

            return new Selection { Kind = SelectionKind.Channel, Roles = [index, -1, -1, -1], Layout = GrayF32.Layout };
        }

        string? layer = (options as ExrReaderOptions)?.Layer;
        if (layer is not null && layer.Length > 0 && !part.Info.Layers.Contains(layer))
        {
            throw new ArgumentException($"The part has no layer '{layer}'; it has {(part.Info.Layers.Count == 0 ? "none" : string.Join(", ", part.Info.Layers))}.", nameof(options));
        }

        if (layer is null && !HasColor(channels, string.Empty))
        {
            layer = part.Info.Layers.FirstOrDefault(l => HasColor(channels, l));
        }

        layer ??= string.Empty;
        string prefix = layer.Length == 0 ? string.Empty : layer + ".";
        int Find(string name) => Array.FindIndex(channels, c => c.Name == prefix + name);
        int r = Find("R"), g = Find("G"), b = Find("B"), a = Find("A"), y = Find("Y"), ry = Find("RY"), by = Find("BY");
        if (r >= 0 || g >= 0 || b >= 0)
        {
            int[] roles = [r, g, b, a];
            return new Selection { Kind = SelectionKind.Rgba, Layer = layer, Roles = roles, Layout = AllHalf(channels, roles) ? RgbaF16.Layout : RgbaF32.Layout };
        }

        if (y >= 0 && (ry >= 0 || by >= 0) && channels[y].Type == ExrPixelType.Half)
        {
            return new Selection { Kind = SelectionKind.Yca, Layer = layer, Roles = [y, ry, by, a], Layout = RgbaF16.Layout };
        }

        if (y >= 0)
        {
            return a >= 0
                ? new Selection { Kind = SelectionKind.Rgba, Layer = layer, Roles = [y, y, y, a], Layout = AllHalf(channels, [y, a]) ? RgbaF16.Layout : RgbaF32.Layout }
                : new Selection { Kind = SelectionKind.Gray, Layer = layer, Roles = [y, -1, -1, -1], Layout = GrayF32.Layout };
        }

        if (a >= 0)
        {
            return new Selection { Kind = SelectionKind.Rgba, Layer = layer, Roles = [-1, -1, -1, a], Layout = AllHalf(channels, [a]) ? RgbaF16.Layout : RgbaF32.Layout };
        }

        // No color at all, such as a depth part: its first channel, as gray.
        return new Selection { Kind = SelectionKind.Gray, Layer = layer, Roles = [0, -1, -1, -1], Layout = GrayF32.Layout };
    }

    private static bool HasColor(ExrChannelInfo[] channels, string layer)
    {
        string prefix = layer.Length == 0 ? string.Empty : layer + ".";
        return channels.Any(c => c.Name.StartsWith(prefix, StringComparison.Ordinal) && c.Name.IndexOf('.', prefix.Length) < 0
            && c.Name[prefix.Length..] is "R" or "G" or "B" or "Y" or "A");
    }

    private static bool AllHalf(ExrChannelInfo[] channels, int[] roles)
    {
        return roles.All(i => i < 0 || (channels[i].Type == ExrPixelType.Half && channels[i].XSampling == 1 && channels[i].YSampling == 1));
    }
}

/// <summary>An OpenEXR file's bytes and parsed headers.</summary>
internal sealed class ExrFile : IDisposable
{
    private readonly TiffSource _source;
    private readonly byte[]? _headerCopy;

    private ExrFile(TiffSource source, List<ExrPartHeader> parts, bool multipart, byte[]? headerCopy)
    {
        _source = source;
        Parts = parts;
        Multipart = multipart;
        _headerCopy = headerCopy;
        Infos = [.. parts.Select(p => p.Info)];
    }

    public List<ExrPartHeader> Parts { get; }

    /// <summary>Gets whether the chunk table was incomplete and rebuilt from the chunks themselves.</summary>
    public bool Rebuilt { get; private set; }

    public IReadOnlyList<ExrPart> Infos { get; }

    public bool Multipart { get; }

    public TiffSource Source => _source;

    public static ExrFile Open(EgamiInput input)
    {
        TiffSource source = TiffSource.Open(input);
        try
        {
            Span<byte> head = stackalloc byte[8];
            if (source.Read(0, head) < 8)
            {
                throw input.Corrupt(0, "The file is shorter than the OpenEXR header.");
            }

            bool multipart = (BinaryPrimitives.ReadInt32LittleEndian(head[4..]) & 0x1000) != 0;
            if (source.InMemory)
            {
                ReadOnlySpan<byte> all = source.Slice(0, (int)Math.Min(source.Length, int.MaxValue), []);
                return new ExrFile(source, ExrHeaderReader.Read(all, input), multipart, null).CheckTable();
            }

            // Read the headers and chunk tables from a growing prefix of the file.
            int size = (int)Math.Min(source.Length, 256 * 1024);
            while (true)
            {
                byte[] prefix = new byte[size];
                int n = source.Read(0, prefix);
                try
                {
                    return new ExrFile(source, ExrHeaderReader.Read(prefix.AsSpan(0, n), input), multipart, null).CheckTable();
                }
                catch (CorruptDataException) when (size < source.Length)
                {
                    size = (int)Math.Min(source.Length, Math.Min((long)size * 4, int.MaxValue));
                }
            }
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rebuilds the chunk tables when an entry points outside the file, as OpenEXR does for files whose writer did not finish
    /// them: the chunks are walked from the end of the tables, each one's header saying where it belongs.
    /// </summary>
    private ExrFile CheckTable()
    {
        long length = _source.Length;
        if (Parts.All(p => p.Deep || p.Offsets.All(o => o > 0 && o < length)))
        {
            return this;
        }

        Rebuilt = true;
        foreach (ExrPartHeader part in Parts)
        {
            Array.Fill(part.Offsets, 0L);
        }

        long pos = Parts[0].TableEnd;
        Span<byte> head = stackalloc byte[28];
        while (pos < length)
        {
            if (_source.Read(pos, head) < 12)
            {
                break;
            }

            int at = 0;
            int partIndex = 0;
            if (Multipart)
            {
                partIndex = BinaryPrimitives.ReadInt32LittleEndian(head);
                at = 4;
            }

            if (partIndex < 0 || partIndex >= Parts.Count || Parts[partIndex].Deep)
            {
                break;
            }

            ExrPartHeader part = Parts[partIndex];
            long index;
            int headerBytes;
            if (part.Tiled)
            {
                int tx = BinaryPrimitives.ReadInt32LittleEndian(head[at..]), ty = BinaryPrimitives.ReadInt32LittleEndian(head[(at + 4)..]);
                int lx = BinaryPrimitives.ReadInt32LittleEndian(head[(at + 8)..]), ly = BinaryPrimitives.ReadInt32LittleEndian(head[(at + 12)..]);
                (int across, int down) = part.TopTiles;
                index = lx == 0 && ly == 0 && tx >= 0 && ty >= 0 && tx < across && ty < down ? ((long)ty * across) + tx : -1;
                headerBytes = at + 20;
            }
            else
            {
                int y = BinaryPrimitives.ReadInt32LittleEndian(head[at..]);
                index = y >= part.MinY && y <= part.MaxY && (y - part.MinY) % part.LinesPerBlock == 0 ? (y - part.MinY) / part.LinesPerBlock : -2;
                headerBytes = at + 8;
            }

            if (index == -2)
            {
                break;
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(head[(headerBytes - 4)..]);
            if (size < 0 || size > length - pos - headerBytes)
            {
                break;
            }

            if (index >= 0 && index < part.Offsets.Length && part.Offsets[index] == 0)
            {
                part.Offsets[index] = pos;
            }

            pos += headerBytes + size;
        }

        return this;
    }

    /// <summary>Returns the part the options pick.</summary>
    public ExrPartHeader Part(ReaderOptions options)
    {
        int index = (options as ExrReaderOptions)?.Part ?? 0;
        if (index < 0 || index >= Parts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(options), index, $"The file has {Parts.Count} part(s).");
        }

        return Parts[index];
    }

    public void Dispose()
    {
        _ = _headerCopy;
        _source.Dispose();
    }
}

/// <summary>The decoded samples of the chosen channels, each at its sampled size, in its stored type.</summary>
internal sealed class ExrPlanes : IDisposable
{
    private readonly IMemoryOwner<byte>?[] _owners;

    private ExrPlanes(int count)
    {
        _owners = new IMemoryOwner<byte>?[count];
        Widths = new int[count];
        Heights = new int[count];
        FirstX = new int[count];
        FirstY = new int[count];
    }

    public int[] Widths { get; }

    public int[] Heights { get; }

    /// <summary>Gets the first sampled x of each plane.</summary>
    public int[] FirstX { get; }

    /// <summary>Gets the first sampled y of each plane.</summary>
    public int[] FirstY { get; }

    /// <summary>Returns a channel's plane, or an empty span when the channel was not decoded.</summary>
    public Span<byte> Plane(int channel) => _owners[channel] is { } owner ? owner.Memory.Span : [];

    public bool Has(int channel) => _owners[channel] is not null;

    public static ExrPlanes Decode(EgamiInput input, ExrFile file, ExrPartHeader part, int[] wanted, bool strict, out List<(string Id, string Message)> problems)
    {
        problems = [];
        ExrPlanes planes = new(part.Channels.Length);
        try
        {
            foreach (int c in wanted)
            {
                ExrChannelInfo channel = part.Channels[c];
                int w = ExrBlock.Sampled(part.MinX, part.MaxX, channel.XSampling);
                int h = ExrBlock.Sampled(part.MinY, part.MaxY, channel.YSampling);
                long bytes = (long)w * h * channel.Bytes;
                if (bytes > int.MaxValue)
                {
                    throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), int.MaxValue, bytes);
                }

                planes._owners[c] = input.RentScratch((int)bytes, ClearMode.None);
                planes.Widths[c] = w;
                planes.Heights[c] = h;
                planes.FirstX[c] = FirstMultiple(part.MinX, channel.XSampling);
                planes.FirstY[c] = FirstMultiple(part.MinY, channel.YSampling);
            }

            DecodeChunks(input, file, part, planes, strict, problems);
            return planes;
        }
        catch
        {
            planes.Dispose();
            throw;
        }
    }

    private static int FirstMultiple(int lo, int s) => (int)(Math.Ceiling(lo / (double)s) * s);

    /// <summary>Lists the blocks of the top level, reads their bytes and decompresses them into the planes.</summary>
    private static void DecodeChunks(EgamiInput input, ExrFile file, ExrPartHeader part, ExrPlanes planes, bool strict, List<(string Id, string Message)> problems)
    {
        int count;
        if (part.Tiled)
        {
            (int across, int down) = part.TopTiles;
            count = (int)Math.Min((long)across * down, part.Offsets.Length);
        }
        else
        {
            count = part.Offsets.Length;
        }

        int partIndex = file.Parts.IndexOf(part);
        int failed = 0;
        string? firstProblem = null;
        object gate = new();
        int threads = Math.Max(1, input.Context.MaxParallelism);
        int maxUnpacked = MaxUnpacked(part);
        void Run(int i, byte[] packedBuffer, byte[] unpacked, byte[] scratch)
        {
            string? problem = DecodeChunk(file, part, partIndex, planes, i, packedBuffer, unpacked, scratch);
            if (problem is not null)
            {
                // The planes are not cleared up front; a block that failed is.
                ClearBlock(part, planes, i);
                lock (gate)
                {
                    failed++;
                    firstProblem ??= problem;
                }
            }
        }

        if (threads == 1 || count < 4 || !file.Source.InMemory)
        {
            byte[] unpacked = ArrayPool<byte>.Shared.Rent(maxUnpacked);
            byte[] scratch = ArrayPool<byte>.Shared.Rent(ExrCodec.ScratchBytes(maxUnpacked));
            byte[] packed = ArrayPool<byte>.Shared.Rent(maxUnpacked + 64);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    Run(i, packed, unpacked, scratch);
                    if (strict && failed > 0)
                    {
                        break;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(unpacked);
                ArrayPool<byte>.Shared.Return(scratch);
                ArrayPool<byte>.Shared.Return(packed);
            }
        }
        else
        {
            Parallel.For(
                0,
                count,
                new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = input.CancellationToken },
                () => (ArrayPool<byte>.Shared.Rent(maxUnpacked + 64), ArrayPool<byte>.Shared.Rent(maxUnpacked), ArrayPool<byte>.Shared.Rent(ExrCodec.ScratchBytes(maxUnpacked))),
                (i, _, buffers) =>
                {
                    Run(i, buffers.Item1, buffers.Item2, buffers.Item3);
                    return buffers;
                },
                buffers =>
                {
                    ArrayPool<byte>.Shared.Return(buffers.Item1);
                    ArrayPool<byte>.Shared.Return(buffers.Item2);
                    ArrayPool<byte>.Shared.Return(buffers.Item3);
                });
        }

        if (failed > 0)
        {
            string message = $"{failed} of {count} blocks could not be decoded ({firstProblem}); their pixels are left at 0.";
            if (strict)
            {
                throw new CorruptDataException(ExrFormat.Info.Name, -1, message);
            }

            problems.Add(("Exr.BlocksDamaged", message));
        }
    }

    /// <summary>Gets the largest decompressed block of the part.</summary>
    private static int MaxUnpacked(ExrPartHeader part)
    {
        int lines = part.Tiled ? part.TileHeight : part.LinesPerBlock;
        int width = part.Tiled ? Math.Min(part.TileWidth, part.Width) : part.Width;
        long bytes = 0;
        foreach (ExrChannelInfo c in part.Channels)
        {
            bytes += ((long)width + c.XSampling) / c.XSampling * (((long)lines + c.YSampling) / c.YSampling) * c.Bytes;
        }

        return (int)Math.Min(bytes, int.MaxValue - 1024);
    }

    /// <summary>Clears the samples block <paramref name="i"/> of the top level would have filled.</summary>
    private static void ClearBlock(ExrPartHeader part, ExrPlanes planes, int i)
    {
        int x0, y0, x1, y1;
        if (part.Tiled)
        {
            int across = part.TopTiles.Across;
            x0 = part.MinX + (i % across * part.TileWidth);
            y0 = part.MinY + (i / across * part.TileHeight);
            x1 = (int)Math.Min((long)x0 + part.TileWidth - 1, part.MaxX);
            y1 = (int)Math.Min((long)y0 + part.TileHeight - 1, part.MaxY);
        }
        else
        {
            x0 = part.MinX;
            x1 = part.MaxX;
            y0 = (int)Math.Min(part.MinY + ((long)i * part.LinesPerBlock), part.MaxY);
            y1 = (int)Math.Min((long)y0 + part.LinesPerBlock - 1, part.MaxY);
        }

        for (int c = 0; c < part.Channels.Length; c++)
        {
            if (!planes.Has(c))
            {
                continue;
            }

            ExrChannelInfo channel = part.Channels[c];
            int first = FirstSampled(x0, channel.XSampling), count = ExrBlock.Sampled(x0, x1, channel.XSampling);
            int col = (first - planes.FirstX[c]) / channel.XSampling;
            for (int y = y0; y <= y1; y++)
            {
                if (y % channel.YSampling == 0 && count > 0)
                {
                    int row = (y - planes.FirstY[c]) / channel.YSampling;
                    planes.Plane(c).Slice(((row * planes.Widths[c]) + col) * channel.Bytes, count * channel.Bytes).Clear();
                }
            }
        }
    }

    /// <summary>Decodes block <paramref name="i"/>; returns what was wrong, or null.</summary>
    private static string? DecodeChunk(ExrFile file, ExrPartHeader part, int partIndex, ExrPlanes planes, int i, byte[] packedBuffer, byte[] unpackedBuffer, byte[] scratch)
    {
        long offset = part.Offsets[i];
        int headerBytes = (file.Multipart ? 4 : 0) + (part.Tiled ? 20 : 8);
        Span<byte> head = stackalloc byte[24];
        if (offset <= 0 || file.Source.Read(offset, head[..headerBytes]) < headerBytes)
        {
            return $"block {i} lies outside the file";
        }

        int at = 0;
        if (file.Multipart)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(head) != partIndex)
            {
                return $"block {i} belongs to another part";
            }

            at = 4;
        }

        int x0, y0, x1, y1;
        if (part.Tiled)
        {
            int tx = BinaryPrimitives.ReadInt32LittleEndian(head[at..]);
            int ty = BinaryPrimitives.ReadInt32LittleEndian(head[(at + 4)..]);
            int lx = BinaryPrimitives.ReadInt32LittleEndian(head[(at + 8)..]);
            int ly = BinaryPrimitives.ReadInt32LittleEndian(head[(at + 12)..]);
            (int across, int down) = part.TopTiles;
            if (lx != 0 || ly != 0 || tx < 0 || ty < 0 || tx >= across || ty >= down)
            {
                return $"block {i} is tile ({tx}, {ty}) at level ({lx}, {ly}), not one of the top level";
            }

            x0 = part.MinX + (tx * part.TileWidth);
            y0 = part.MinY + (ty * part.TileHeight);
            x1 = (int)Math.Min((long)x0 + part.TileWidth - 1, part.MaxX);
            y1 = (int)Math.Min((long)y0 + part.TileHeight - 1, part.MaxY);
        }
        else
        {
            y0 = BinaryPrimitives.ReadInt32LittleEndian(head[at..]);
            if (y0 < part.MinY || y0 > part.MaxY || (y0 - part.MinY) % part.LinesPerBlock != 0)
            {
                return $"block {i} starts at line {y0}";
            }

            x0 = part.MinX;
            x1 = part.MaxX;
            y1 = (int)Math.Min((long)y0 + part.LinesPerBlock - 1, part.MaxY);
        }

        int packedSize = BinaryPrimitives.ReadInt32LittleEndian(head[(headerBytes - 4)..]);
        ExrBlock block = new(part, x0, y0, x1, y1);
        int unpackedSize = block.UnpackedBytes;
        if (packedSize < 0 || packedSize > file.Source.Length - offset - headerBytes)
        {
            return $"block {i} says it holds {packedSize} bytes";
        }

        byte[]? larger = null;
        try
        {
            Span<byte> packedScratch = packedSize <= packedBuffer.Length ? packedBuffer : (larger = ArrayPool<byte>.Shared.Rent(packedSize));
            ReadOnlySpan<byte> packed = file.Source.Slice(offset + headerBytes, packedSize, packedScratch);
            if (packed.Length != packedSize)
            {
                return $"block {i} is cut short";
            }

            Span<byte> unpacked = unpackedBuffer.AsSpan(0, unpackedSize);
            if (!ExrCodec.Decompress(block, packed, unpacked, scratch))
            {
                return $"block {i} is damaged";
            }

            Scatter(block, unpacked, planes);
            return null;
        }
        finally
        {
            if (larger is not null)
            {
                ArrayPool<byte>.Shared.Return(larger);
            }
        }
    }

    /// <summary>Copies each decoded line's samples of the chosen channels into their planes.</summary>
    private static void Scatter(in ExrBlock block, ReadOnlySpan<byte> unpacked, ExrPlanes planes)
    {
        ExrChannelInfo[] channels = block.Part.Channels;
        int pos = 0;
        for (int y = block.Y0; y <= block.Y1; y++)
        {
            for (int c = 0; c < channels.Length; c++)
            {
                ExrChannelInfo channel = channels[c];
                if (y % channel.YSampling != 0)
                {
                    continue;
                }

                int n = ExrBlock.Sampled(block.X0, block.X1, channel.XSampling);
                int bytes = n * channel.Bytes;
                if (planes.Has(c))
                {
                    int row = (y - planes.FirstY[c]) / channel.YSampling;
                    int col = (FirstSampled(block.X0, channel.XSampling) - planes.FirstX[c]) / channel.XSampling;
                    unpacked.Slice(pos, bytes).CopyTo(planes.Plane(c).Slice(((row * planes.Widths[c]) + col) * channel.Bytes, bytes));
                }

                pos += bytes;
            }
        }
    }

    private static int FirstSampled(int lo, int s) => s == 1 ? lo : (int)(Math.Ceiling(lo / (double)s) * s);

    public void Dispose()
    {
        foreach (IMemoryOwner<byte>? owner in _owners)
        {
            owner?.Dispose();
        }
    }
}
