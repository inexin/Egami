namespace Egami.Formats.Av1;

/// <summary>The loop restoration of one unit: its type and its filter (section 5.11.58).</summary>
internal struct Av1RestorationUnit
{
    /// <summary>The unit's type: 0 none, 1 Wiener, 2 self-guided.</summary>
    internal int Type;

    /// <summary>The Wiener filter's vertical taps 0 to 2 (the rest mirror them).</summary>
    internal int V0, V1, V2;

    /// <summary>The Wiener filter's horizontal taps 0 to 2.</summary>
    internal int H0, H1, H2;

    /// <summary>The self-guided filter's parameter set.</summary>
    internal int SgrSet;

    /// <summary>The self-guided filter's projection weights.</summary>
    internal int Xqd0, Xqd1;
}

/// <summary>A plane's loop restoration units.</summary>
internal sealed class Av1RestorationPlane
{
    /// <summary>Gets or sets the frame's restoration type for the plane.</summary>
    internal Av1RestorationType Type { get; init; }

    /// <summary>Gets or sets the unit size, in samples.</summary>
    internal int UnitSize { get; init; }

    /// <summary>Gets or sets the number of unit columns.</summary>
    internal int HorzUnits { get; init; }

    /// <summary>Gets or sets the number of unit rows.</summary>
    internal int VertUnits { get; init; }

    /// <summary>Gets the units, row by row.</summary>
    internal Av1RestorationUnit[] Units { get; init; } = [];
}

/// <summary>
/// The state of one frame being decoded: the mode info grid the tiles fill in, the maps later stages read (segments, transform
/// sizes and types, CDEF indexes, loop restoration units) and the tile data of the frame's tile groups.
/// </summary>
internal sealed class Av1Frame
{
    /// <summary>Creates the state of a frame.</summary>
    /// <param name="header">The frame header.</param>
    /// <param name="formatName">The format name errors name.</param>
    /// <param name="reconstruct">Whether to reconstruct samples, or only parse.</param>
    internal Av1Frame(Av1FrameHeader header, string formatName, bool reconstruct)
    {
        Header = header;
        Sequence = header.Sequence;
        FormatName = formatName;
        MiRows = header.MiRows;
        MiCols = header.MiCols;
        SbSize = Sequence.Use128x128Superblock ? Bs.B128x128 : Bs.B64x64;
        SbMiLog2 = Sequence.Use128x128Superblock ? 5 : 4;
        Grid = new Av1BlockInfo?[MiRows * MiCols];
        SegmentMap = new byte[MiRows * MiCols];
        TxTypeMap = new byte[MiRows * MiCols];
        TxSizeMap = new byte[MiRows * MiCols];
        CdefStride = (MiCols + 15) >> 4;
        CdefIndex = new sbyte[CdefStride * ((MiRows + 15) >> 4)];
        CdefIndex.AsSpan().Fill(-1);
        SbCols = (MiCols + (1 << SbMiLog2) - 1) >> SbMiLog2;
        SuperblockBits = new int[SbCols * ((MiRows + (1 << SbMiLog2) - 1) >> SbMiLog2)];
        Restoration = new Av1RestorationPlane[header.NumPlanes];
        for (int plane = 0; plane < header.NumPlanes; plane++)
        {
            int ssx = plane > 0 ? Sequence.SubsamplingX : 0, ssy = plane > 0 ? Sequence.SubsamplingY : 0;
            int size = header.LoopRestorationSize[plane];
            int horz = Math.Max((((header.UpscaledWidth + ssx) >> ssx) + (size >> 1)) / size, 1);
            int vert = Math.Max((((header.FrameHeight + ssy) >> ssy) + (size >> 1)) / size, 1);
            Av1RestorationType type = header.UsesLr ? header.LrType[plane] : Av1RestorationType.None;
            Restoration[plane] = new Av1RestorationPlane
            {
                Type = type,
                UnitSize = size,
                HorzUnits = horz,
                VertUnits = vert,
                Units = type == Av1RestorationType.None ? [] : new Av1RestorationUnit[horz * vert],
            };
        }

        Context = Av1FrameContext.CreateDefault(header.BaseQIdx);
        if (reconstruct)
        {
            // Superblock-aligned planes: transform blocks reach past the frame's edge as far as their superblock.
            int sbPixels = 4 << SbMiLog2;
            int width = ((MiCols * 4) + sbPixels - 1) & ~(sbPixels - 1), height = ((MiRows * 4) + sbPixels - 1) & ~(sbPixels - 1);
            Planes = new ushort[header.NumPlanes][];
            Strides = new int[header.NumPlanes];
            for (int plane = 0; plane < header.NumPlanes; plane++)
            {
                int ssx = plane > 0 ? Sequence.SubsamplingX : 0, ssy = plane > 0 ? Sequence.SubsamplingY : 0;
                Strides[plane] = width >> ssx;
                Planes[plane] = new ushort[(width >> ssx) * (height >> ssy)];
            }
        }
    }

    /// <summary>Gets the reconstructed planes (Y, U, V), or null when only parsing.</summary>
    internal ushort[][]? Planes { get; }

    /// <summary>Gets each plane's stride.</summary>
    internal int[] Strides { get; } = [];

    /// <summary>Gets the frame header.</summary>
    internal Av1FrameHeader Header { get; }

    /// <summary>Gets the sequence header.</summary>
    internal Av1SequenceHeader Sequence { get; }

    /// <summary>Gets the format name errors name.</summary>
    internal string FormatName { get; }

    /// <summary>Gets the frame's height in 4x4 units.</summary>
    internal int MiRows { get; }

    /// <summary>Gets the frame's width in 4x4 units, also the stride of the grids.</summary>
    internal int MiCols { get; }

    /// <summary>Gets the superblock size (<see cref="Bs"/>).</summary>
    internal int SbSize { get; }

    /// <summary>Gets the log2 of the superblock size in 4x4 units.</summary>
    internal int SbMiLog2 { get; }

    /// <summary>Gets the number of superblock columns.</summary>
    internal int SbCols { get; }

    /// <summary>Gets each 4x4 unit's block.</summary>
    internal Av1BlockInfo?[] Grid { get; }

    /// <summary>Gets each 4x4 unit's segment.</summary>
    internal byte[] SegmentMap { get; }

    /// <summary>Gets the luma transform type at each transform block's top left 4x4 unit.</summary>
    internal byte[] TxTypeMap { get; }

    /// <summary>Gets each 4x4 unit's luma transform size.</summary>
    internal byte[] TxSizeMap { get; }

    /// <summary>Gets the stride of <see cref="CdefIndex"/>.</summary>
    internal int CdefStride { get; }

    /// <summary>Gets each 64x64 unit's CDEF strength index, -1 where none was coded.</summary>
    internal sbyte[] CdefIndex { get; }

    /// <summary>Gets each plane's loop restoration.</summary>
    internal Av1RestorationPlane[] Restoration { get; }

    /// <summary>Gets the bits each superblock's symbols took, for tests against libaom.</summary>
    internal int[] SuperblockBits { get; }

    /// <summary>Gets the frame's CDFs as the frame starts.</summary>
    internal Av1FrameContext Context { get; }

    /// <summary>Gets or sets the CDFs the frame saves (the context_update_tile_id tile's, at its end).</summary>
    internal Av1FrameContext? SavedContext { get; set; }

    /// <summary>Gets or sets whether a tile's data ended badly (overflow or wrong trailing bits); decoding goes on, as dav1d's does.</summary>
    internal bool TileDataDamaged { get; set; }

    /// <summary>Lists a tile group OBU's tiles (section 5.11.1): each one's number, offset and size.</summary>
    internal List<(int Tile, int Offset, int Size)> TileGroupTiles(byte[] data, int start, int length)
    {
        int numTiles = Header.TileCols * Header.TileRows;
        Av1BitReader r = new(data.AsSpan(start, length), FormatName);
        int tgStart = 0, tgEnd = numTiles - 1;
        if (numTiles > 1 && r.Flag())
        {
            int bits = Header.TileColsLog2 + Header.TileRowsLog2;
            tgStart = (int)r.Bits(bits);
            tgEnd = (int)r.Bits(bits);
        }

        r.ByteAlign();
        if (tgEnd < tgStart || tgEnd >= numTiles)
        {
            throw new CorruptDataException(FormatName, start, "An AV1 tile group's tile range is out of the frame's tiles.");
        }

        int at = start + (r.Position >> 3), end = start + length;
        List<(int, int, int)> tiles = [];
        for (int tile = tgStart; tile <= tgEnd; tile++)
        {
            int size;
            if (tile == tgEnd)
            {
                size = end - at;
            }
            else
            {
                if (end - at < Header.TileSizeBytes)
                {
                    throw new CorruptDataException(FormatName, at, "An AV1 tile group ends inside a tile size.") { IsTruncation = true };
                }

                size = 1;
                for (int i = 0; i < Header.TileSizeBytes; i++)
                {
                    size += data[at + i] << (8 * i);
                }

                at += Header.TileSizeBytes;
                if (size > end - at)
                {
                    throw new CorruptDataException(FormatName, at, "An AV1 tile is larger than its tile group.") { IsTruncation = true };
                }
            }

            if (size <= 0)
            {
                throw new CorruptDataException(FormatName, at, "An AV1 tile has no data.");
            }

            tiles.Add((tile, at, size));
            at += size;
        }

        return tiles;
    }

    /// <summary>Decodes one tile.</summary>
    internal void DecodeTile(int tile, byte[] data, int offset, int size)
    {
        int row = tile / Header.TileCols, col = tile % Header.TileCols;
        Av1TileParser parser = new(this, row, col, data, offset, size, Planes is null ? null : new Av1Reconstructor(this));
        parser.Parse();
        if (tile == Header.ContextUpdateTileId && !Header.DisableFrameEndUpdateCdf)
        {
            Av1FrameContext saved = parser.Context.Clone();
            saved.ResetCounters();
            SavedContext = saved;
        }
    }
}
