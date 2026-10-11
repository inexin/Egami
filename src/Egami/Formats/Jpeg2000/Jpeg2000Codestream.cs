using System.Buffers.Binary;
using System.Text;

namespace Egami.Formats.Jpeg2000;

/// <summary>One image component as SIZ describes it (T.800 A.5.1).</summary>
internal readonly record struct J2kComponent(int Precision, bool Signed, int Dx, int Dy);

/// <summary>The coding style of one component, from COD and COC (T.800 A.6.1 and A.6.2).</summary>
internal sealed class J2kCodingStyle
{
    internal bool Precincts { get; set; }

    internal bool Sop { get; set; }

    internal bool Eph { get; set; }

    internal int Progression { get; set; }

    internal int Layers { get; set; } = 1;

    internal int Mct { get; set; }

    internal int Levels { get; set; }

    /// <summary>Gets or sets the code-block width exponent, xcb (2 to 10).</summary>
    internal int BlockWidth { get; set; } = 6;

    /// <summary>Gets or sets the code-block height exponent, ycb.</summary>
    internal int BlockHeight { get; set; } = 6;

    internal int BlockStyle { get; set; }

    /// <summary>Gets or sets whether the 5/3 reversible wavelet is used; otherwise the 9/7 irreversible one.</summary>
    internal bool Reversible { get; set; }

    /// <summary>Gets or sets the precinct width and height exponents for each resolution, 15 when precincts are not defined.</summary>
    internal (int X, int Y)[] PrecinctSizes { get; set; } = [];

    internal J2kCodingStyle Copy() => (J2kCodingStyle)MemberwiseClone();
}

/// <summary>The quantization of one component, from QCD and QCC (T.800 A.6.4 and A.6.5).</summary>
internal sealed class J2kQuantization
{
    /// <summary>Gets or sets the style: 0 none (reversible), 1 scalar derived, 2 scalar expounded.</summary>
    internal int Style { get; set; }

    internal int GuardBits { get; set; }

    /// <summary>Gets or sets the exponent and mantissa of each subband in order (LL, then HL, LH, HH per level, coarsest first).</summary>
    internal (int Exponent, int Mantissa)[] Bands { get; set; } = [];

    /// <summary>Returns the exponent and mantissa of subband <paramref name="index"/> (0 the LL band), deriving them when the style says so.</summary>
    internal (int Exponent, int Mantissa) Band(int index, int levels)
    {
        if (Style == 1)
        {
            // Scalar derived (E.1.1.1): only the LL band is signalled; a band at decomposition level nb has exponent e0 - NL + nb.
            (int e0, int m0) = Bands.Length > 0 ? Bands[0] : (0, 0);
            int nb = index == 0 ? levels : levels - ((index - 1) / 3);
            return (e0 - levels + nb, m0);
        }

        return index < Bands.Length ? Bands[index] : Bands.Length > 0 ? Bands[^1] : (0, 0);
    }

    internal J2kQuantization Copy() => (J2kQuantization)MemberwiseClone();
}

/// <summary>One entry of a progression order change (T.800 A.6.6).</summary>
internal readonly record struct J2kProgressionChange(int ResolutionStart, int ComponentStart, int LayerEnd, int ResolutionEnd, int ComponentEnd, int Order);

/// <summary>A tile's markers and data: the tile-part headers' COD, COC, QCD, QCC, RGN, POC and PPT, and its tile-parts' bit streams.</summary>
internal sealed class J2kTileData
{
    internal int Index { get; init; }

    /// <summary>Gets or sets the tile's COD, or <see langword="null"/>.</summary>
    internal J2kCodingStyle? Cod { get; set; }

    /// <summary>Gets or sets the tile's COC for each component, or <see langword="null"/>.</summary>
    internal J2kCodingStyle?[]? Coc { get; set; }

    internal J2kQuantization? Qcd { get; set; }

    internal J2kQuantization?[]? Qcc { get; set; }

    internal int[]? RoiShifts { get; set; }

    /// <summary>Gets or sets the effective coding style of each component, once worked out.</summary>
    internal J2kCodingStyle[]? Styles { get; set; }

    internal List<J2kProgressionChange> ProgressionChanges { get; } = [];

    /// <summary>Gets the packed packet headers of PPT markers, in tile-part order.</summary>
    internal List<ReadOnlyMemory<byte>> PacketHeaders { get; } = [];

    /// <summary>Gets the tile-parts' data (after SOD), in order.</summary>
    internal List<ReadOnlyMemory<byte>> Parts { get; } = [];

    internal int PartsSeen { get; set; }

    internal int? PartCount { get; set; }
}

/// <summary>
/// A JPEG 2000 codestream (T.800 Annex A): the main header's markers and each tile's tile-parts. Written from ITU-T T.800 and
/// checked against OpenJPEG's decodes; Part 2 coding tools and High-Throughput code-blocks are refused, naming the feature.
/// </summary>
internal sealed class J2kCodestream
{
    internal const ushort Soc = 0xFF4F;
    internal const ushort Cap = 0xFF50;
    internal const ushort Siz = 0xFF51;
    internal const ushort Cod = 0xFF52;
    internal const ushort Coc = 0xFF53;
    internal const ushort Tlm = 0xFF55;
    internal const ushort Plm = 0xFF57;
    internal const ushort Plt = 0xFF58;
    internal const ushort Cpf = 0xFF59;
    internal const ushort Qcd = 0xFF5C;
    internal const ushort Qcc = 0xFF5D;
    internal const ushort Rgn = 0xFF5E;
    internal const ushort Poc = 0xFF5F;
    internal const ushort Ppm = 0xFF60;
    internal const ushort Ppt = 0xFF61;
    internal const ushort Crg = 0xFF63;
    internal const ushort Com = 0xFF64;
    internal const ushort Sot = 0xFF90;
    internal const ushort Sop = 0xFF91;
    internal const ushort Eph = 0xFF92;
    internal const ushort Sod = 0xFF93;
    internal const ushort Eoc = 0xFFD9;

    private J2kCodestream()
    {
    }

    internal ushort Profile { get; private set; }

    internal int Width { get; private set; }

    internal int Height { get; private set; }

    internal int OffsetX { get; private set; }

    internal int OffsetY { get; private set; }

    internal int TileWidth { get; private set; }

    internal int TileHeight { get; private set; }

    internal int TileOffsetX { get; private set; }

    internal int TileOffsetY { get; private set; }

    internal int TilesX { get; private set; }

    internal int TilesY { get; private set; }

    internal J2kComponent[] Components { get; private set; } = [];

    /// <summary>Gets the main header's COD.</summary>
    internal J2kCodingStyle MainCod { get; private set; } = new();

    /// <summary>Gets the main header's COC for each component, or <see langword="null"/>.</summary>
    internal J2kCodingStyle?[] MainCoc { get; private set; } = [];

    internal J2kQuantization MainQcd { get; private set; } = new();

    internal J2kQuantization?[] MainQcc { get; private set; } = [];

    internal int[] RoiShifts { get; private set; } = [];

    internal List<J2kProgressionChange> ProgressionChanges { get; } = [];

    /// <summary>Gets the PPM packed packet headers, in tile-part order across the codestream.</summary>
    internal List<ReadOnlyMemory<byte>> PackedHeaders { get; } = [];

    internal List<string> Comments { get; } = [];

    /// <summary>Gets the tile each tile-part belongs to, in codestream order, which PPM's packed headers follow.</summary>
    internal List<int> PartTiles { get; } = [];

    internal J2kTileData?[] Tiles { get; private set; } = [];

    /// <summary>Gets whether the codestream ended before its EOC, inside a tile-part or between them.</summary>
    internal bool Truncated { get; private set; }

    internal int TileCount => TilesX * TilesY;

    /// <summary>Returns whether data starts with SOC then SIZ.</summary>
    internal static bool Matches(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51;

    /// <summary>
    /// Reads the main header and, unless <paramref name="headerOnly"/>, every tile-part. Malformed data throws
    /// <see cref="InvalidDataException"/>; data that ends early throws <see cref="EndOfStreamException"/> in the main header and
    /// marks <see cref="Truncated"/> after it.
    /// </summary>
    internal static J2kCodestream Parse(ReadOnlyMemory<byte> data, SafetyLimits limits, bool headerOnly = false)
    {
        J2kCodestream stream = new();
        stream.Read(data, limits, headerOnly);
        return stream;
    }

    private void Read(ReadOnlyMemory<byte> memory, SafetyLimits limits, bool headerOnly)
    {
        ReadOnlySpan<byte> data = memory.Span;
        if (!Matches(data))
        {
            throw new InvalidDataException("The codestream does not start with SOC and SIZ.");
        }

        int at = 2;
        bool sizSeen = false;
        bool codSeen = false;
        bool qcdSeen = false;
        while (true)
        {
            ushort marker = Marker(data, at);
            if (marker == Sot)
            {
                break;
            }

            if (marker is >= 0xFF30 and <= 0xFF3F)
            {
                // Reserved markers without parameters (Table A.1); skipped.
                at += 2;
                continue;
            }

            int length = Length(data, at);
            ReadOnlySpan<byte> body = data.Slice(at + 4, length - 2);
            switch (marker)
            {
                case Siz:
                    ReadSiz(body, limits);
                    sizSeen = true;
                    break;
                case Cod:
                    Require(sizSeen);
                    MainCod = ReadCod(body);
                    codSeen = true;
                    break;
                case Coc:
                    Require(sizSeen);
                    (int coc, J2kCodingStyle cocStyle) = ReadCoc(body);
                    MainCoc[coc] = cocStyle;
                    break;
                case Qcd:
                    Require(sizSeen);
                    MainQcd = ReadQuantization(body);
                    qcdSeen = true;
                    break;
                case Qcc:
                    Require(sizSeen);
                    int qcc = ComponentIndex(body);
                    MainQcc[qcc] = ReadQuantization(body[ComponentBytes..]);
                    break;
                case Rgn:
                    Require(sizSeen);
                    (int rc, int shift) = ReadRgn(body);
                    RoiShifts[rc] = shift;
                    break;
                case Poc:
                    Require(sizSeen);
                    ReadPoc(body, ProgressionChanges);
                    break;
                case Ppm:
                    if (body.Length >= 1)
                    {
                        ReadPpm(memory.Slice(at + 5, length - 3));
                    }

                    break;
                case Com:
                    ReadComment(body);
                    break;
                case Cap:
                    throw new NotSupportedException("The codestream uses High-Throughput JPEG 2000 (Part 15, the CAP marker), which Egami does not read yet.");
                case Tlm or Plm or Crg or Cpf:
                    break;
                default:
                    if (marker is >= 0xFF70 and <= 0xFF7F)
                    {
                        throw new NotSupportedException($"The codestream uses JPEG 2000 Part 2 coding tools ({PartTwoName(marker)}), which Egami does not read.");
                    }

                    if ((marker & 0xFF00) != 0xFF00)
                    {
                        throw new InvalidDataException($"A main header marker is malformed (0x{marker:X4}).");
                    }

                    break;
            }

            at += 2 + length;
        }

        if (!sizSeen || !codSeen || !qcdSeen)
        {
            throw new InvalidDataException("The main header lacks SIZ, COD or QCD.");
        }

        Tiles = new J2kTileData?[TileCount];
        if (!headerOnly)
        {
            ReadTileParts(memory, at, limits);
        }
    }

    private static void Require(bool siz)
    {
        if (!siz)
        {
            throw new InvalidDataException("A marker comes before SIZ.");
        }
    }

    /// <summary>Gets the bytes a component index takes in COC, QCC, RGN and POC: 1, or 2 with more than 256 components.</summary>
    private int ComponentBytes => Components.Length < 257 ? 1 : 2;

    private int ComponentIndex(ReadOnlySpan<byte> body)
    {
        if (body.Length < ComponentBytes)
        {
            throw new InvalidDataException("A marker segment is too short for its component index.");
        }

        int c = ComponentBytes == 1 ? body[0] : BinaryPrimitives.ReadUInt16BigEndian(body);
        return c < Components.Length ? c : throw new InvalidDataException($"A marker names component {c}, which SIZ does not have.");
    }

    private void ReadSiz(ReadOnlySpan<byte> body, SafetyLimits limits)
    {
        if (body.Length < 36)
        {
            throw new InvalidDataException("SIZ is too short.");
        }

        Profile = BinaryPrimitives.ReadUInt16BigEndian(body);
        if ((Profile & 0x8000) != 0)
        {
            throw new NotSupportedException("The codestream uses JPEG 2000 Part 2 extensions (SIZ Rsiz bit 15), which Egami does not read.");
        }

        if ((Profile & 0x4000) != 0)
        {
            throw new NotSupportedException("The codestream uses High-Throughput JPEG 2000 (Part 15, SIZ Rsiz bit 14), which Egami does not read yet.");
        }

        uint xsiz = BinaryPrimitives.ReadUInt32BigEndian(body[2..]);
        uint ysiz = BinaryPrimitives.ReadUInt32BigEndian(body[6..]);
        uint xosiz = BinaryPrimitives.ReadUInt32BigEndian(body[10..]);
        uint yosiz = BinaryPrimitives.ReadUInt32BigEndian(body[14..]);
        uint xtsiz = BinaryPrimitives.ReadUInt32BigEndian(body[18..]);
        uint ytsiz = BinaryPrimitives.ReadUInt32BigEndian(body[22..]);
        uint xtosiz = BinaryPrimitives.ReadUInt32BigEndian(body[26..]);
        uint ytosiz = BinaryPrimitives.ReadUInt32BigEndian(body[30..]);
        int csiz = BinaryPrimitives.ReadUInt16BigEndian(body[34..]);
        if (xsiz > int.MaxValue || ysiz > int.MaxValue || xosiz >= xsiz || yosiz >= ysiz || xtsiz == 0 || ytsiz == 0
            || xtosiz > xosiz || ytosiz > yosiz || (ulong)xtosiz + xtsiz <= xosiz || (ulong)ytosiz + ytsiz <= yosiz)
        {
            throw new InvalidDataException("SIZ gives an impossible image or tile grid.");
        }

        if (csiz is 0 or > 16384 || body.Length < 36 + (3 * csiz))
        {
            throw new InvalidDataException("SIZ gives an impossible number of components.");
        }

        Width = (int)xsiz;
        Height = (int)ysiz;
        OffsetX = (int)xosiz;
        OffsetY = (int)yosiz;
        TileWidth = (int)Math.Min(xtsiz, int.MaxValue);
        TileHeight = (int)Math.Min(ytsiz, int.MaxValue);
        TileOffsetX = (int)xtosiz;
        TileOffsetY = (int)ytosiz;
        long tilesX = ((long)Width - TileOffsetX + TileWidth - 1) / TileWidth;
        long tilesY = ((long)Height - TileOffsetY + TileHeight - 1) / TileHeight;
        if (tilesX * tilesY > 65535)
        {
            throw new InvalidDataException("SIZ gives more than 65,535 tiles, the most Isot can name.");
        }

        TilesX = (int)tilesX;
        TilesY = (int)tilesY;
        limits.CheckFrameSize((long)Width - OffsetX, (long)Height - OffsetY);
        Components = new J2kComponent[csiz];
        for (int c = 0; c < csiz; c++)
        {
            byte ssiz = body[36 + (3 * c)];
            int dx = body[37 + (3 * c)];
            int dy = body[38 + (3 * c)];
            int precision = (ssiz & 0x7F) + 1;
            if (precision > 38 || dx == 0 || dy == 0)
            {
                throw new InvalidDataException($"Component {c} has precision {precision} or subsampling {dx}×{dy}, which T.800 does not allow.");
            }

            Components[c] = new J2kComponent(precision, (ssiz & 0x80) != 0, dx, dy);
        }

        MainCoc = new J2kCodingStyle?[csiz];
        MainQcc = new J2kQuantization?[csiz];
        RoiShifts = new int[csiz];
    }

    private static J2kCodingStyle ReadCod(ReadOnlySpan<byte> body)
    {
        if (body.Length < 10)
        {
            throw new InvalidDataException("COD is too short.");
        }

        byte scod = body[0];
        J2kCodingStyle style = new()
        {
            Precincts = (scod & 1) != 0,
            Sop = (scod & 2) != 0,
            Eph = (scod & 4) != 0,
            Progression = body[1],
            Layers = BinaryPrimitives.ReadUInt16BigEndian(body[2..]),
            Mct = body[4],
        };
        ReadSpcod(body[5..], style);
        if (style.Progression > 4 || style.Layers == 0)
        {
            throw new InvalidDataException("COD gives an unknown progression order or no layers.");
        }

        return style;
    }

    private (int Component, J2kCodingStyle Style) ReadCoc(ReadOnlySpan<byte> body)
    {
        int c = ComponentIndex(body);
        ReadOnlySpan<byte> rest = body[ComponentBytes..];
        if (rest.Length < 6)
        {
            throw new InvalidDataException("COC is too short.");
        }

        J2kCodingStyle style = new();
        style.Precincts = (rest[0] & 1) != 0;
        ReadSpcod(rest[1..], style);
        return (c, style);
    }

    /// <summary>Merges a COC's fields into a component's style: everything but SOP, EPH, progression, layers and MCT.</summary>
    private static J2kCodingStyle Merge(J2kCodingStyle main, J2kCodingStyle coc)
    {
        J2kCodingStyle merged = coc.Copy();
        merged.Sop = main.Sop;
        merged.Eph = main.Eph;
        merged.Progression = main.Progression;
        merged.Layers = main.Layers;
        merged.Mct = main.Mct;
        return merged;
    }

    /// <summary>Reads SPcod or SPcoc (T.800 Table A.15).</summary>
    private static void ReadSpcod(ReadOnlySpan<byte> sp, J2kCodingStyle style)
    {
        int levels = sp[0];
        int xcb = (sp[1] & 0x0F) + 2;
        int ycb = (sp[2] & 0x0F) + 2;
        int blockStyle = sp[3];
        int transform = sp[4];
        if (levels > 32 || xcb > 10 || ycb > 10 || xcb + ycb > 12)
        {
            throw new InvalidDataException("COD or COC gives more than 32 decomposition levels or code-blocks over 4,096 samples.");
        }

        if ((blockStyle & 0x40) != 0)
        {
            throw new NotSupportedException("The codestream uses High-Throughput code-blocks (Part 15), which Egami does not read yet.");
        }

        if (transform > 1)
        {
            throw new NotSupportedException("The codestream uses an arbitrary wavelet (JPEG 2000 Part 2), which Egami does not read.");
        }

        style.Levels = levels;
        style.BlockWidth = xcb;
        style.BlockHeight = ycb;
        style.BlockStyle = blockStyle;
        style.Reversible = transform == 1;
        (int, int)[] sizes = new (int, int)[levels + 1];
        for (int r = 0; r <= levels; r++)
        {
            if (style.Precincts)
            {
                if (sp.Length < 5 + levels + 1)
                {
                    throw new InvalidDataException("COD or COC is too short for its precinct sizes.");
                }

                byte p = sp[5 + r];
                sizes[r] = (p & 0x0F, p >> 4);
                if (r > 0 && (sizes[r].Item1 == 0 || sizes[r].Item2 == 0))
                {
                    throw new InvalidDataException("A precinct size of 1 is allowed only at resolution 0.");
                }
            }
            else
            {
                sizes[r] = (15, 15);
            }
        }

        style.PrecinctSizes = sizes;
    }

    private static J2kQuantization ReadQuantization(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1)
        {
            throw new InvalidDataException("QCD or QCC is too short.");
        }

        int style = body[0] & 0x1F;
        J2kQuantization q = new() { Style = style, GuardBits = body[0] >> 5 };
        ReadOnlySpan<byte> sp = body[1..];
        if (style == 0)
        {
            q.Bands = new (int, int)[sp.Length];
            for (int i = 0; i < sp.Length; i++)
            {
                q.Bands[i] = (sp[i] >> 3, 0);
            }
        }
        else if (style is 1 or 2)
        {
            q.Bands = new (int, int)[sp.Length / 2];
            for (int i = 0; i < q.Bands.Length; i++)
            {
                ushort v = BinaryPrimitives.ReadUInt16BigEndian(sp[(2 * i)..]);
                q.Bands[i] = (v >> 11, v & 0x7FF);
            }
        }
        else
        {
            throw new NotSupportedException("The codestream uses trellis-coded or other Part 2 quantization, which Egami does not read.");
        }

        if (q.Bands.Length == 0)
        {
            throw new InvalidDataException("QCD or QCC gives no step sizes.");
        }

        return q;
    }

    private (int Component, int Shift) ReadRgn(ReadOnlySpan<byte> body)
    {
        int c = ComponentIndex(body);
        ReadOnlySpan<byte> rest = body[ComponentBytes..];
        if (rest.Length < 2)
        {
            throw new InvalidDataException("RGN is too short.");
        }

        if (rest[0] != 0)
        {
            throw new NotSupportedException("The codestream uses a region of interest style other than max-shift (Part 2), which Egami does not read.");
        }

        return (c, rest[1]);
    }

    private void ReadPoc(ReadOnlySpan<byte> body, List<J2kProgressionChange> changes)
    {
        int cb = ComponentBytes;
        int entry = 5 + (2 * cb);
        for (int at = 0; at + entry <= body.Length; at += entry)
        {
            int rs = body[at];
            int cs = cb == 1 ? body[at + 1] : BinaryPrimitives.ReadUInt16BigEndian(body[(at + 1)..]);
            int lye = BinaryPrimitives.ReadUInt16BigEndian(body[(at + 1 + cb)..]);
            int re = body[at + 3 + cb];
            int ce = cb == 1 ? body[at + 4 + cb] : BinaryPrimitives.ReadUInt16BigEndian(body[(at + 4 + cb)..]);
            int order = body[at + 4 + (2 * cb)];
            if (ce == 0)
            {
                ce = cb == 1 ? 256 : 16384;
            }

            if (order > 4)
            {
                throw new InvalidDataException("POC gives an unknown progression order.");
            }

            changes.Add(new J2kProgressionChange(rs, cs, lye, re, ce, order));
        }
    }

    private void ReadPpm(ReadOnlyMemory<byte> body)
    {
        // Zppm orders the segments; PPM bodies hold Nppm and Ippm pairs, which may run across segments, so keep the bytes and
        // split them when tiles ask (by concatenating every PPM body in order).
        PackedHeaders.Add(body);
    }

    private void ReadComment(ReadOnlySpan<byte> body)
    {
        if (body.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(body) == 1)
        {
            Comments.Add(Encoding.Latin1.GetString(body[2..]).TrimEnd('\0'));
        }
    }

    private static string PartTwoName(ushort marker) => marker switch
    {
        0xFF72 => "DFS, arbitrary decomposition",
        0xFF73 => "ADS, arbitrary decomposition styles",
        0xFF74 => "MCT, multiple component transformation",
        0xFF75 => "MCC, multiple component collection",
        0xFF76 => "NLT, non-linearity point transformation",
        0xFF77 => "MCO, multiple component transformation ordering",
        0xFF78 => "CBD, component bit depth",
        0xFF79 => "ATK, arbitrary transformation kernels",
        _ => $"marker 0x{marker:X4}",
    };

    /// <summary>Reads the tile-parts, from the first SOT to EOC or the end of the data.</summary>
    private void ReadTileParts(ReadOnlyMemory<byte> memory, int at, SafetyLimits limits)
    {
        ReadOnlySpan<byte> data = memory.Span;
        while (at + 2 <= data.Length)
        {
            ushort marker = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            if (marker == Eoc)
            {
                return;
            }

            if (marker != Sot)
            {
                // Bytes after the last tile-part that are not EOC; some writers pad. Stop here.
                Truncated = true;
                return;
            }

            if (at + 12 > data.Length)
            {
                Truncated = true;
                return;
            }

            int tile = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 4)..]);
            uint psot = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 6)..]);
            int tpsot = data[at + 10];
            int tnsot = data[at + 11];
            if (tile >= TileCount)
            {
                throw new InvalidDataException($"A tile-part names tile {tile}, past the {TileCount} tiles SIZ gives.");
            }

            long end = psot == 0 ? data.Length : at + (long)psot;
            if (end > data.Length)
            {
                Truncated = true;
                end = data.Length;
            }

            if (psot != 0 && psot < 14)
            {
                throw new InvalidDataException("A tile-part is shorter than its SOT.");
            }

            J2kTileData tileData = Tiles[tile] ??= new J2kTileData { Index = tile };
            if (tnsot != 0)
            {
                tileData.PartCount = tnsot;
            }

            tileData.PartsSeen++;
            int p = at + 12;
            bool first = tpsot == 0;
            while (true)
            {
                if (p + 2 > end)
                {
                    Truncated = true;
                    return;
                }

                ushort m = BinaryPrimitives.ReadUInt16BigEndian(data[p..]);
                if (m == Sod)
                {
                    p += 2;
                    break;
                }

                if (m is >= 0xFF30 and <= 0xFF3F)
                {
                    p += 2;
                    continue;
                }

                if (p + 4 > end)
                {
                    Truncated = true;
                    return;
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(data[(p + 2)..]);
                if (length < 2 || p + 2 + length > end)
                {
                    throw new InvalidDataException("A tile-part header marker segment runs past its tile-part.");
                }

                ReadOnlySpan<byte> body = data.Slice(p + 4, length - 2);
                switch (m)
                {
                    case Cod when first:
                        tileData.Cod = ReadCod(body);
                        break;
                    case Coc when first:
                        tileData.Coc ??= new J2kCodingStyle?[Components.Length];
                        (int cc, J2kCodingStyle cs) = ReadCoc(body);
                        tileData.Coc[cc] = cs;
                        break;
                    case Qcd when first:
                        tileData.Qcd = ReadQuantization(body);
                        break;
                    case Qcc when first:
                        tileData.Qcc ??= new J2kQuantization?[Components.Length];
                        int qc = ComponentIndex(body);
                        tileData.Qcc[qc] = ReadQuantization(body[ComponentBytes..]);
                        break;
                    case Rgn when first:
                        tileData.RoiShifts ??= (int[])RoiShifts.Clone();
                        (int rc, int shift) = ReadRgn(body);
                        tileData.RoiShifts[rc] = shift;
                        break;
                    case Poc:
                        ReadPoc(body, tileData.ProgressionChanges);
                        break;
                    case Ppt:
                        if (body.Length >= 1)
                        {
                            tileData.PacketHeaders.Add(memory.Slice(p + 5, length - 3));
                        }

                        break;
                    case Com:
                        ReadComment(body);
                        break;
                    case Plt or Cod or Coc or Qcd or Qcc or Rgn:
                        break;
                    default:
                        if (m is >= 0xFF70 and <= 0xFF7F)
                        {
                            throw new NotSupportedException($"The codestream uses JPEG 2000 Part 2 coding tools ({PartTwoName(m)}), which Egami does not read.");
                        }

                        break;
                }

                p += 2 + length;
            }

            tileData.Parts.Add(memory[p..(int)end]);
            PartTiles.Add(tile);
            limits.CheckFrameCount(tileData.Parts.Count);
            at = (int)end;
            if (psot == 0)
            {
                return;
            }
        }

        Truncated = true;
    }

    private static ushort Marker(ReadOnlySpan<byte> data, int at)
    {
        if (at + 2 > data.Length)
        {
            throw new EndOfStreamException("The codestream ends inside its main header.");
        }

        return BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
    }

    private static int Length(ReadOnlySpan<byte> data, int at)
    {
        if (at + 4 > data.Length)
        {
            throw new EndOfStreamException("The codestream ends inside its main header.");
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
        if (length < 2)
        {
            throw new InvalidDataException("A marker segment has a length below 2.");
        }

        if (at + 2 + length > data.Length)
        {
            throw new EndOfStreamException("The codestream ends inside its main header.");
        }

        return length;
    }

    /// <summary>
    /// Hands each tile its packed packet headers from the main header's PPM markers (A.7.4): the PPM bodies, joined in order,
    /// hold a length (Nppm) and that many header bytes (Ippm) for each tile-part in codestream order.
    /// </summary>
    internal void SplitPackedHeaders()
    {
        if (PackedHeaders.Count == 0)
        {
            return;
        }

        byte[] joined = new byte[PackedHeaders.Sum(h => h.Length)];
        int at = 0;
        foreach (ReadOnlyMemory<byte> header in PackedHeaders)
        {
            header.Span.CopyTo(joined.AsSpan(at));
            at += header.Length;
        }

        int position = 0;
        foreach (int tile in PartTiles)
        {
            if (position + 4 > joined.Length)
            {
                break;
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(joined.AsSpan(position));
            position += 4;
            int take = (int)Math.Min(length, (uint)(joined.Length - position));
            Tiles[tile]!.PacketHeaders.Add(joined.AsMemory(position, take));
            position += take;
        }
    }

    /// <summary>
    /// Returns the coding style tile <paramref name="tile"/> uses for component <paramref name="c"/>: the component's fields from
    /// the first of the tile's COC, the tile's COD, the main COC and the main COD (A.6.1), the tile-wide fields (SOP, EPH,
    /// progression, layers, MCT) from the tile's COD or the main COD.
    /// </summary>
    internal J2kCodingStyle CodingStyle(J2kTileData? tile, int c)
    {
        if (tile?.Styles is { } styles)
        {
            return styles[c];
        }

        J2kCodingStyle cod = tile?.Cod ?? MainCod;
        J2kCodingStyle? coc = tile?.Coc?[c] ?? (tile?.Cod is null ? MainCoc[c] : null);
        return coc is null ? cod : Merge(cod, coc);
    }

    /// <summary>Works out every component's coding style for a tile once.</summary>
    internal void Resolve(J2kTileData tile)
    {
        if (tile.Styles is not null)
        {
            return;
        }

        J2kCodingStyle[] styles = new J2kCodingStyle[Components.Length];
        for (int c = 0; c < styles.Length; c++)
        {
            styles[c] = CodingStyle(tile, c);
        }

        tile.Styles = styles;
    }

    /// <summary>Returns the quantization of a component: the tile's QCC, the tile's QCD, the main QCC, then the main QCD (A.6.4).</summary>
    internal J2kQuantization Quantization(J2kTileData? tile, int c) => tile?.Qcc?[c] ?? tile?.Qcd ?? MainQcc[c] ?? MainQcd;

    internal int RoiShift(J2kTileData? tile, int c) => tile?.RoiShifts?[c] ?? RoiShifts[c];

    /// <summary>Returns tile <paramref name="index"/>'s rectangle on the reference grid (B.3).</summary>
    internal (int X0, int Y0, int X1, int Y1) TileRect(int index)
    {
        int p = index % TilesX;
        int q = index / TilesX;
        int x0 = (int)Math.Max(TileOffsetX + ((long)p * TileWidth), OffsetX);
        int y0 = (int)Math.Max(TileOffsetY + ((long)q * TileHeight), OffsetY);
        int x1 = (int)Math.Min(TileOffsetX + ((long)(p + 1) * TileWidth), Width);
        int y1 = (int)Math.Min(TileOffsetY + ((long)(q + 1) * TileHeight), Height);
        return (x0, y0, x1, y1);
    }
}
