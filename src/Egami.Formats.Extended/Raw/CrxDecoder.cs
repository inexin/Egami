using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// A CR3 raw track's CMP1 box: the CRX codec's frame, tile and sample layout (CRX spec, section 3). Reading never throws, so a
/// file whose raw data Egami cannot decode still opens for its previews; <see cref="Check"/> runs when the data is decoded.
/// </summary>
internal sealed record CrxHeader(int Version, int FrameWidth, int FrameHeight, int TileWidth, int TileHeight, int Bits, int Planes,
    int CfaLayout, int EncType, int Levels, int TileBits, int HeaderSize, int MedianBits)
{
    /// <summary>Gets the bit depth of the decoded samples: the median bits of a roll frame's colour transform, else the data's.</summary>
    public int OutputBits => EncType == 3 ? MedianBits : Bits;

    /// <summary>Gets the CFA pattern the planes make (CRX spec, section 3): plane 0 is red, 1 and 2 green, 3 blue.</summary>
    public string Pattern => CfaLayout switch
    {
        1 => "GRBG",
        2 => "GBRG",
        3 => "BGGR",
        _ => "RGGB",
    };

    /// <summary>Reads CMP1's contents (after its box header); fields past the box's end read as 0.</summary>
    public static CrxHeader Read(ReadOnlySpan<byte> content)
    {
        byte[] c = new byte[85];
        content[..Math.Min(content.Length, c.Length)].CopyTo(c);
        int U32(int at) => (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(at)), int.MaxValue);
        int planes = c[25] >> 4, bits = c[24];
        bool median = (c[32] & 0x80) != 0 && planes == 4 && (c[56] & 0x40) != 0;
        return new CrxHeader(BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(4)), U32(8), U32(12), U32(16), U32(20), bits, planes, c[25] & 15,
            c[26] >> 4, c[26] & 15, c[27], U32(28), median ? c[84] : bits);
    }

    /// <summary>Throws when the header is not one the CRX decoder takes (CRX spec, sections 3 and 10).</summary>
    public void Check()
    {
        if (Planes == 1 || EncType == 1)
        {
            throw new NotSupportedException(Planes == 1
                ? "Egami does not read the CR3's 8-bit single-plane CRX data; no camera is known to write it."
                : "Egami does not read CRX data of encoding type 1; no camera is known to write it.");
        }

        bool valid = Version is 0x100 or 0x200 && HeaderSize > 0 && EncType is 0 or 3 && Bits is > 0 and <= 14 && Bits != 8
            && Planes == 4 && FrameWidth % 2 == 0 && FrameHeight % 2 == 0 && TileWidth % 2 == 0 && TileHeight % 2 == 0
            && CfaLayout <= 3 && TileWidth > 0 && TileHeight > 0 && TileWidth <= FrameWidth && TileHeight <= FrameHeight && Levels <= 3
            && (TileBits & 0x3F) == 0 && MedianBits is > 0 and <= 16;
        int pw = FrameWidth / 2, ph = FrameHeight / 2, tw = TileWidth / 2, th = TileHeight / 2;
        if (!valid || tw < 22 || th < 22 || pw > 32767 || ph > 32767)
        {
            throw new InvalidDataException("The CR3's CMP1 codec header is not valid.");
        }

        int columns = (pw + tw - 1) / tw, rows = (ph + th - 1) / th;
        if (columns > 255 || rows > 255 || pw - (tw * (columns - 1)) < 22 || ph - (th * (rows - 1)) < 22)
        {
            throw new InvalidDataException("The CR3's CRX tiles do not fit its frame.");
        }
    }
}

/// <summary>
/// Canon's CRX codec, the sensor data of CR3 files, written from the clean-room specification a study-only session wrote
/// (CRX spec, sections 4 to 10): tiles of four half-size planes, one per CFA position, each coded on its own; lossless planes
/// code their samples with an adaptive Golomb-Rice code, a gradient predictor and runs, and C-RAW planes code up to three
/// levels of quantised 5/3 wavelet subbands the same way. (Tile, plane) jobs decode in parallel; each writes only its own pixels.
/// </summary>
internal static class CrxDecoder
{
    /// <summary>The run state's step and extra-bit tables (CRX spec, section 5.3).</summary>
    private static ReadOnlySpan<byte> Extra => [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>The quantiser step bases (CRX spec, section 8.1).</summary>
    private static ReadOnlySpan<int> StepBase => [40, 45, 51, 57, 64, 72];

    /// <summary>
    /// The overlap coefficients a subband carries across a shared tile edge (CRX spec, section 9.3): per level count and size
    /// mod 8, the high and low band's extra count for levels 1 (finest) to 3.
    /// </summary>
    private static ReadOnlySpan<byte> Overlaps =>
    [
        // L = 1
        1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0,
        // L = 2
        1, 1, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0, 1, 2, 2, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0, 1, 2, 2, 1, 0, 0, 1, 1, 1, 1, 0, 0,
        // L = 3
        1, 1, 1, 1, 1, 1, 1, 0, 1, 0, 1, 0, 1, 2, 2, 2, 2, 1, 1, 1, 1, 2, 2, 1, 1, 1, 1, 2, 2, 1, 1, 0, 1, 1, 1, 1, 1, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    ];

    /// <summary>One subband (or a lossless plane's only band): where its bytes are and how its values are scaled.</summary>
    private struct Band
    {
        public long Start;
        public int Size;
        public int Width;
        public int Height;
        public bool PerLineQ;
        public int Q;
        public bool Version2;
        public int QStepBase;
        public int QStepMult;
    }

    private sealed class Plane
    {
        public bool Partial;
        public int Rounding;
        public Band[] Bands = [];
    }

    /// <summary>One tile, in plane samples, with its neighbour flags and, for header version 2, its step tables per level.</summary>
    private sealed class Tile
    {
        public int X, Y, Width, Height;
        public bool Left, Right, Top, Bottom;
        public long DataStart;
        public bool Missing;
        public Plane[] Planes = [];
        public int GridWidth;
        public int[][]? Steps;
        public int[] StepRows = [];
    }

    /// <summary>Decodes the raw sample (<paramref name="offset"/>, <paramref name="count"/>) into <paramref name="pixels"/>.</summary>
    public static void Decode(byte[] file, long offset, long count, RawPixels pixels, CrxHeader h, ParallelOptions parallel, Action<int, string> damaged)
    {
        h.Check();
        if (pixels.Width != h.FrameWidth || pixels.Height != h.FrameHeight)
        {
            throw new InvalidDataException("The CR3's CMP1 frame size is not the raw image's.");
        }

        long end = offset + count;
        Tile[] tiles = Tiles(file, offset, end, h);
        int missing = tiles.Count(t => t.Missing);
        if (missing > 0)
        {
            damaged(0, $"{missing} of the CR3's {tiles.Length} CRX tiles run past the end of the data; they read as black.");
        }

        int pw = h.FrameWidth / 2, ph = h.FrameHeight / 2;
        short[]? planes = h.EncType == 3 ? ArrayPool<short>.Shared.Rent(pw * ph * 4) : null;
        try
        {
            if (planes is not null)
            {
                Array.Clear(planes, 0, pw * ph * 4);
            }

            int jobs = tiles.Length * 4;
            RawParallel.For(0, jobs, parallel, j =>
            {
                Tile tile = tiles[j >> 2];
                if (!tile.Missing)
                {
                    Output output = new(pixels, h, planes, pw, j & 3, tile);
                    if (h.Levels == 0)
                    {
                        Lossless(file, tile, tile.Planes[j & 3], output);
                    }
                    else
                    {
                        Wavelet(file, tile, tile.Planes[j & 3], h.Levels, output);
                    }
                }
            });

            if (planes is not null)
            {
                RollColours(planes, pw, ph, h, pixels, parallel);
            }
        }
        finally
        {
            if (planes is not null)
            {
                ArrayPool<short>.Shared.Return(planes);
            }
        }
    }

    /// <summary>Reads the sample's header block (CRX spec, section 4) and places every tile, plane and subband.</summary>
    private static Tile[] Tiles(byte[] file, long offset, long end, CrxHeader h)
    {
        int pw = h.FrameWidth / 2, ph = h.FrameHeight / 2, tw = h.TileWidth / 2, th = h.TileHeight / 2;
        int columns = (pw + tw - 1) / tw, rows = (ph + th - 1) / th;
        int subbands = (3 * h.Levels) + 1;
        if (h.HeaderSize > end - offset)
        {
            throw new InvalidDataException("The CR3's CRX header block runs past its sample.");
        }

        int at = (int)offset, headerEnd = (int)offset + h.HeaderSize;
        ReadOnlySpan<byte> Next(int size)
        {
            if (at + size > headerEnd)
            {
                throw new InvalidDataException("The CR3's CRX header block ends inside a header.");
            }

            ReadOnlySpan<byte> s = file.AsSpan(at, size);
            at += size;
            return s;
        }

        Tile[] tiles = new Tile[columns * rows];
        long data = offset + h.HeaderSize;
        for (int t = 0; t < tiles.Length; t++)
        {
            int col = t % columns, row = t / columns;
            Tile tile = new()
            {
                X = col * tw,
                Y = row * th,
                Width = col == columns - 1 ? pw - (tw * (columns - 1)) : tw,
                Height = row == rows - 1 ? ph - (th * (rows - 1)) : th,
                Left = col > 0,
                Right = col < columns - 1,
                Top = row > 0,
                Bottom = row < rows - 1,
                Planes = new Plane[4],
            };
            tiles[t] = tile;

            ReadOnlySpan<byte> head = Next(4);
            int marker = BinaryPrimitives.ReadUInt16BigEndian(head), size = BinaryPrimitives.ReadUInt16BigEndian(head[2..]);
            if (!(marker == 0xFF01 && size == 8) && !(marker == 0xFF11 && size is 8 or 16))
            {
                throw new InvalidDataException("The CR3's CRX tile header is not valid.");
            }

            ReadOnlySpan<byte> body = Next(size);
            long tileSize = BinaryPrimitives.ReadUInt32BigEndian(body);
            int qpSize = 0, extraSize = 0;
            if (BinaryPrimitives.ReadUInt16BigEndian(body[4..]) != t)
            {
                throw new InvalidDataException("The CR3's CRX tiles are out of order.");
            }

            if (size == 16)
            {
                qpSize = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[8..]), int.MaxValue);
                extraSize = BinaryPrimitives.ReadUInt16BigEndian(body[12..]);
            }

            tile.DataStart = data;
            long planeAt = data + qpSize + extraSize;
            tile.Missing = data + tileSize > end || planeAt > data + tileSize;
            data += tileSize;
            bool version2 = false;
            for (int p = 0; p < 4; p++)
            {
                ReadOnlySpan<byte> ph12 = Next(12);
                int pm = BinaryPrimitives.ReadUInt16BigEndian(ph12);
                byte flags = ph12[8];
                if (pm is not (0xFF02 or 0xFF12) || BinaryPrimitives.ReadUInt16BigEndian(ph12[2..]) != 8 || flags >> 4 != p || (flags & 1) != 0)
                {
                    throw new InvalidDataException("The CR3's CRX plane header is not valid.");
                }

                long planeSize = BinaryPrimitives.ReadUInt32BigEndian(ph12[4..]);
                int rounded = (flags >> 1) & 3;
                bool partial = (flags & 8) != 0;
                if (rounded != 0 && (h.Levels != 0 || !partial))
                {
                    throw new InvalidDataException("The CR3's CRX plane rounds a band it may not.");
                }

                Plane plane = new() { Partial = partial, Rounding = rounded == 0 ? 0 : 1 << (rounded - 1), Bands = new Band[subbands] };
                tile.Planes[p] = plane;
                long bandAt = planeAt;
                for (int s = 0; s < subbands; s++)
                {
                    ReadOnlySpan<byte> sh = Next(4);
                    int sm = BinaryPrimitives.ReadUInt16BigEndian(sh), ssize = BinaryPrimitives.ReadUInt16BigEndian(sh[2..]);
                    ref Band band = ref plane.Bands[s];
                    if (sm == 0xFF03 && ssize == 8)
                    {
                        ReadOnlySpan<byte> b = Next(8);
                        uint field = BinaryPrimitives.ReadUInt32BigEndian(b[4..]);
                        if (field >> 28 != s)
                        {
                            throw new InvalidDataException("The CR3's CRX subbands are out of order.");
                        }

                        band.Size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b), int.MaxValue);
                        band.PerLineQ = ((field >> 27) & 1) != 0;
                        band.Q = (int)((field >> 19) & 0xFF);
                    }
                    else if (sm == 0xFF13 && ssize == 16)
                    {
                        ReadOnlySpan<byte> b = Next(16);
                        if (BinaryPrimitives.ReadUInt16BigEndian(b[4..]) >> 12 != s)
                        {
                            throw new InvalidDataException("The CR3's CRX subbands are out of order.");
                        }

                        band.Size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(b), int.MaxValue);
                        band.Version2 = true;
                        band.QStepMult = BinaryPrimitives.ReadUInt16BigEndian(b[6..]);
                        band.QStepBase = BinaryPrimitives.ReadInt32BigEndian(b[8..]);
                        version2 = true;
                    }
                    else
                    {
                        throw new InvalidDataException("The CR3's CRX subband header is not valid.");
                    }

                    band.Start = bandAt;
                    bandAt += band.Size;
                }

                if (bandAt - planeAt > planeSize)
                {
                    throw new InvalidDataException("The CR3's CRX subbands do not fit their plane.");
                }

                planeAt += planeSize;
            }

            tile.Missing |= planeAt > data;
            Sizes(tile, h.Levels);
            if (h.Levels > 0 && version2)
            {
                if (qpSize == 0)
                {
                    throw new InvalidDataException("The CR3's C-RAW tile has version 2 subbands but no quantiser data.");
                }

                if (!tile.Missing)
                {
                    StepTables(file, tile, qpSize, h.Levels);
                }
            }
        }

        return tiles;
    }

    /// <summary>Gives every band of a tile its size (CRX spec, sections 9.1 and 9.3), the overlap coefficients included.</summary>
    private static void Sizes(Tile tile, int levels)
    {
        foreach (Plane plane in tile.Planes)
        {
            if (levels == 0)
            {
                plane.Bands[0].Width = tile.Width;
                plane.Bands[0].Height = tile.Height;
                continue;
            }

            Geometry g = new(tile, levels);
            for (int l = 1; l <= levels; l++)
            {
                int d = levels - l;
                ref Band hl = ref plane.Bands[(3 * d) + 1];
                ref Band lh = ref plane.Bands[(3 * d) + 2];
                ref Band hh = ref plane.Bands[(3 * d) + 3];
                (hl.Width, hl.Height) = (g.HighWidth(l), g.LowHeight(l));
                (lh.Width, lh.Height) = (g.LowWidth(l), g.HighHeight(l));
                (hh.Width, hh.Height) = (g.HighWidth(l), g.HighHeight(l));
            }

            plane.Bands[0].Width = g.LowWidth(levels);
            plane.Bands[0].Height = g.LowHeight(levels);
        }
    }

    /// <summary>A tile's wavelet sizes per level l, 1 the finest (CRX spec, sections 9.1 and 9.3).</summary>
    private readonly struct Geometry
    {
        private readonly Tile _tile;
        private readonly int _levels;

        public Geometry(Tile tile, int levels)
        {
            _tile = tile;
            _levels = levels;
        }

        public int HighWidth(int l) => (Size(_tile.Width, l - 1) / 2) + (_tile.Right ? Overlap(_tile.Width, l, 0) : 0) + (_tile.Left ? 1 : 0);

        public int LowWidth(int l) => Size(_tile.Width, l) + (_tile.Right ? Overlap(_tile.Width, l, 1) : 0);

        public int HighHeight(int l) => (Size(_tile.Height, l - 1) / 2) + (_tile.Bottom ? Overlap(_tile.Height, l, 0) : 0) + (_tile.Top ? 1 : 0);

        public int LowHeight(int l) => Size(_tile.Height, l) + (_tile.Bottom ? Overlap(_tile.Height, l, 1) : 0);

        /// <summary>The width level l's synthesis outputs: the tile's at the finest level, else the next finer level's low band.</summary>
        public int OutWidth(int l) => l == 1 ? _tile.Width : LowWidth(l - 1);

        public int OutHeight(int l) => l == 1 ? _tile.Height : LowHeight(l - 1);

        private static int Size(int n, int l)
        {
            for (int i = 0; i < l; i++)
            {
                n = (n + 1) / 2;
            }

            return n;
        }

        private int Overlap(int size, int l, int which) => Overlaps[((_levels - 1) * 48) + ((size & 7) * 6) + ((l - 1) * 2) + which];
    }

    /// <summary>
    /// Decodes a version 2 tile's quantiser grid (CRX spec, section 8.2) and builds its step tables: one per decode level, each
    /// a row of grid columns per subband line.
    /// </summary>
    private static void StepTables(byte[] file, Tile tile, int qpSize, int levels)
    {
        int gw = (tile.Width + 7) / 8, gh = (tile.Height + 1) / 2;
        int[] grid = new int[gw * gh];
        int[] lines = new int[(gw + 2) * 2];
        Span<int> prev = lines.AsSpan(0, gw + 2), cur = lines.AsSpan(gw + 2, gw + 2);
        Bits bits = new(file.AsSpan((int)tile.DataStart, qpSize));
        int k = 0;
        for (int r = 0; r < gh; r++)
        {
            cur[0] = r == 0 ? 0 : prev[1];
            for (int x = 0; x < gw; x++)
            {
                uint v = bits.QCode(k);
                if (r == 0)
                {
                    cur[x + 1] = cur[x] + Unzig(v);
                    k = Adapt(k, v, 7);
                }
                else
                {
                    cur[x + 1] = Predict(cur[x], prev[x + 1], prev[x]) + Unzig(v);
                    k = x < gw - 1 ? Adapt(k, (v + (2 * (uint)Math.Abs(prev[x + 2] - prev[x + 1]))) >> 1, 7) : Adapt(k, v, 7);
                }
            }

            cur[gw + 1] = cur[gw] + 1;
            for (int x = 0; x < gw; x++)
            {
                grid[(r * gw) + x] = cur[x + 1] + 4;
            }

            Span<int> swap = prev;
            prev = cur;
            cur = swap;
        }

        bits.CheckEnd();
        tile.GridWidth = gw;
        tile.Steps = new int[levels][];
        tile.StepRows = new int[levels];
        for (int d = 0; d < levels; d++)
        {
            // Decode level d of L averages 2^(L-1-d) grid rows: 4 for the coarsest of three levels, 1 for the finest.
            int group = 1 << (levels - 1 - d);
            int rows = (gh + group - 1) / group;
            int[] table = new int[rows * gw];
            for (int r = 0; r < rows; r++)
            {
                for (int x = 0; x < gw; x++)
                {
                    int sum = 0;
                    for (int i = 0; i < group; i++)
                    {
                        sum += grid[(Math.Min((r * group) + i, gh - 1) * gw) + x];
                    }

                    table[(r * gw) + x] = Step(sum / group);
                }
            }

            tile.Steps[d] = table;
            tile.StepRows[d] = rows;
        }
    }

    /// <summary>The quantiser step of q (CRX spec, sections 8.1 and 8.2), C's truncating division, saturated to 32 bits.</summary>
    private static int Step(int q)
    {
        int div = q / 6, mod = q % 6;
        if (div >= 6)
        {
            long s = (long)StepBase[mod] << Math.Min(div - 6, 40);
            return (int)Math.Min(s, int.MaxValue);
        }

        int shift = 6 - div;
        return shift >= 31 ? 0 : StepBase[Math.Max(0, mod)] >> shift;
    }

    /// <summary>Where one (tile, plane) job writes its decoded lines.</summary>
    private readonly struct Output(RawPixels pixels, CrxHeader h, short[]? planes, int planeWidth, int plane, Tile tile)
    {
        private readonly int _dy = Cell(h.CfaLayout, plane) >> 1, _dx = Cell(h.CfaLayout, plane) & 1;
        private readonly int _middle = 1 << (h.Bits - 1), _top = (1 << h.Bits) - 1;
        private readonly int _plane = plane;

        public Tile Tile { get; } = tile;

        /// <summary>Writes the tile's line <paramref name="y"/>, <see cref="Tile"/>'s width of plane values.</summary>
        public void Line(int y, ReadOnlySpan<int> values)
        {
            if (planes is not null)
            {
                Span<short> row = planes.AsSpan((((_plane * (pixels.Height / 2)) + Tile.Y + y) * planeWidth) + Tile.X, values.Length);
                for (int x = 0; x < values.Length; x++)
                {
                    row[x] = (short)values[x];
                }

                return;
            }

            Span<ushort> cfa = pixels.U16Row((2 * (Tile.Y + y)) + _dy)[((2 * Tile.X) + _dx)..];
            for (int x = 0; x < values.Length; x++)
            {
                cfa[2 * x] = (ushort)Math.Clamp(_middle + values[x], 0, _top);
            }
        }

        /// <summary>The 2 × 2 cell (row × 2 + column) where plane <paramref name="p"/> sits (CRX spec, section 3).</summary>
        public static int Cell(int layout, int p) => p ^ layout;
    }

    /// <summary>A lossless plane of a tile: one band, decoded and written line by line (CRX spec, section 7).</summary>
    private static void Lossless(byte[] file, Tile tile, Plane plane, Output output)
    {
        Band band = plane.Bands[0];
        int w = tile.Width;
        int[] buffer = ArrayPool<int>.Shared.Rent((3 * w) + 5);
        try
        {
            Span<int> prev = buffer.AsSpan(0, w + 2), cur = buffer.AsSpan(w + 2, w + 2), k = buffer.AsSpan((2 * w) + 4, w + 1);
            prev.Clear();
            if (band.Size == 0)
            {
                // A plane with no data (CRX spec, open point 4): its values are all 0, the middle of the range.
                cur.Clear();
                for (int y = 0; y < tile.Height; y++)
                {
                    output.Line(y, cur.Slice(1, w));
                }

                return;
            }

            Mode mode = plane.Partial ? (plane.Rounding > 0 ? Mode.Rounded : Mode.Predicted) : Mode.Coefficient;
            BandReader reader = new(file.AsSpan((int)band.Start, band.Size), w, plane.Rounding);
            for (int y = 0; y < tile.Height; y++)
            {
                reader.Line(mode, y == 0, cur, prev, k);
                output.Line(y, cur.Slice(1, w));
                Span<int> swap = prev;
                prev = cur;
                cur = swap;
            }

            reader.CheckEnd();
        }
        finally
        {
            ArrayPool<int>.Shared.Return(buffer);
        }
    }

    private enum Mode
    {
        Predicted,
        Rounded,
        Coefficient,
    }

    /// <summary>
    /// A C-RAW plane of a tile (CRX spec, sections 8 and 9): every subband decoded and scaled by its quantiser step, then each
    /// level's 5/3 synthesis, horizontal then vertical, from the coarsest; the finest level's rows go to the output.
    /// </summary>
    private static void Wavelet(byte[] file, Tile tile, Plane plane, int levels, Output output)
    {
        Geometry g = new(tile, levels);
        int bandTotal = 0, widest = 0;
        foreach (Band b in plane.Bands)
        {
            bandTotal += b.Width * b.Height;
            widest = Math.Max(widest, b.Width);
        }

        int work = 0, low = 0;
        for (int l = 1; l <= levels; l++)
        {
            work = Math.Max(work, g.OutWidth(l) * (g.LowHeight(l) + g.HighHeight(l)));
            low = Math.Max(low, l > 1 ? g.OutWidth(l) * g.OutHeight(l) : 0);
            widest = Math.Max(widest, g.OutWidth(l));
        }

        int lineLength = (3 * widest) + 5;
        int[] buffer = ArrayPool<int>.Shared.Rent(bandTotal + work + low + lineLength);
        try
        {
            Span<int> all = buffer;
            Span<int> lines = all.Slice(bandTotal + work + low, lineLength);
            int at = 0;
            for (int s = 0; s < plane.Bands.Length; s++)
            {
                Band b = plane.Bands[s];
                Span<int> values = all.Slice(at, b.Width * b.Height);
                at += values.Length;
                int level = s == 0 ? 0 : (s - 1) / 3;
                // The step table's column shift: 0 for the coarsest of three levels, 2 for the finest (CRX spec, section 8.2).
                int shift = s == 0 ? 3 - levels : 3 - (levels - level);
                // HL and HH carry a left neighbour's extra column first, LH and HH an upper neighbour's extra row.
                bool highX = s > 0 && (s - 1) % 3 != 1, highY = s > 0 && (s - 1) % 3 != 0;
                Subband(file, tile, b, s == 0 && plane.Partial ? Mode.Predicted : Mode.Coefficient, level, shift, highX && tile.Left, highY && tile.Top, values, lines);
            }

            Span<int> work1 = all.Slice(bandTotal, work);
            Span<int> lowOut = all.Slice(bandTotal + work, low);
            ReadOnlySpan<int> input = all[..(plane.Bands[0].Width * plane.Bands[0].Height)];
            int offset = 0;
            Span<int> bandsAt = stackalloc int[plane.Bands.Length];
            for (int s = 0; s < plane.Bands.Length; s++)
            {
                bandsAt[s] = offset;
                offset += plane.Bands[s].Width * plane.Bands[s].Height;
            }

            for (int l = levels; l >= 1; l--)
            {
                int d = levels - l;
                Band hl = plane.Bands[(3 * d) + 1], lh = plane.Bands[(3 * d) + 2], hh = plane.Bands[(3 * d) + 3];
                int outW = g.OutWidth(l), outH = g.OutHeight(l), loH = g.LowHeight(l), hiH = g.HighHeight(l), loW = g.LowWidth(l);
                Span<int> lowRows = work1[..(outW * loH)], highRows = work1.Slice(outW * loH, outW * hiH);
                ReadOnlySpan<int> hlv = all.Slice(bandsAt[(3 * d) + 1], hl.Width * hl.Height);
                ReadOnlySpan<int> lhv = all.Slice(bandsAt[(3 * d) + 2], lh.Width * lh.Height);
                ReadOnlySpan<int> hhv = all.Slice(bandsAt[(3 * d) + 3], hh.Width * hh.Height);
                for (int r = 0; r < loH; r++)
                {
                    Synthesize(input.Slice(r * loW, loW), hlv.Slice(r * hl.Width, hl.Width), tile.Left, lowRows.Slice(r * outW, outW));
                }

                for (int r = 0; r < hiH; r++)
                {
                    Synthesize(lhv.Slice(r * lh.Width, lh.Width), hhv.Slice(r * hh.Width, hh.Width), tile.Left, highRows.Slice(r * outW, outW));
                }

                Vertical(lowRows, highRows, outW, loH, hiH, outH, tile.Top);
                ReadOnlySpan<int> high = tile.Top ? highRows[outW..] : highRows;
                if (l == 1)
                {
                    for (int y = 0; y < outH; y++)
                    {
                        output.Line(y, ((y & 1) == 0 ? lowRows : high).Slice((y >> 1) * outW, outW));
                    }
                }
                else
                {
                    Span<int> next = lowOut[..(outW * outH)];
                    for (int y = 0; y < outH; y++)
                    {
                        ((y & 1) == 0 ? lowRows : high).Slice((y >> 1) * outW, outW).CopyTo(next.Slice(y * outW, outW));
                    }

                    input = next;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(buffer);
        }
    }

    /// <summary>Decodes one subband into <paramref name="values"/>, scaling each line by its quantiser step (CRX spec, section 8).</summary>
    private static void Subband(byte[] file, Tile tile, Band band, Mode mode, int level, int shift, bool leftExtra, bool topExtra, Span<int> values, Span<int> lines)
    {
        int w = band.Width;
        if (band.Size == 0 || w == 0 || band.Height == 0)
        {
            values.Clear();
            return;
        }

        Span<int> prev = lines[..(w + 2)], cur = lines.Slice(w + 2, w + 2), k = lines.Slice((2 * w) + 4, w + 1);
        prev.Clear();
        BandReader reader = new(file.AsSpan((int)band.Start, band.Size), w, 0);
        int q = band.Q, kq = 0;
        int step = band.Version2 ? 0 : QStep(q);
        int[]? table = band.Version2 ? tile.Steps![level] : null;
        int tableRows = band.Version2 ? tile.StepRows[level] : 0;
        for (int y = 0; y < band.Height; y++)
        {
            if (band.PerLineQ && !band.Version2)
            {
                uint v = reader.QCode(kq);
                q += Unzig(v);
                kq = Adapt(kq, v, 0);
                if (kq > 7 || q < 0 || q > 255)
                {
                    throw new InvalidDataException("The CR3's C-RAW quantiser runs out of range.");
                }

                step = QStep(q);
            }

            reader.Line(mode, y == 0, cur, prev, k);
            Span<int> row = values.Slice(y * w, w);
            ReadOnlySpan<int> line = cur.Slice(1, w);
            if (table is null)
            {
                for (int x = 0; x < w; x++)
                {
                    row[x] = line[x] * step;
                }
            }
            else
            {
                // Overlap rows and columns at a tile's top and left map to the table's first row and column (CRX spec, open point 3).
                int r = Math.Clamp(y - (topExtra ? 1 : 0), 0, tableRows - 1) * tile.GridWidth;
                int gw = tile.GridWidth;
                int left = leftExtra ? 1 : 0;
                for (int x = 0; x < w; x++)
                {
                    int column = Math.Clamp((x - left) >> shift, 0, gw - 1);
                    uint scaled = unchecked((uint)table[r + column] * (uint)band.QStepMult) >> 3;
                    int s = Math.Clamp(band.QStepBase + (int)Math.Min(scaled, 0x168000u), 1, 0x168000);
                    row[x] = line[x] * s;
                }
            }

            Span<int> swap = prev;
            prev = cur;
            cur = swap;
        }

        reader.CheckEnd();
    }

    /// <summary>Version 1's step for q (CRX spec, section 8.1).</summary>
    private static int QStep(int q) => Step(q);

    /// <summary>
    /// One-dimensional reversible 5/3 synthesis (CRX spec, section 9.2; ITU-T T.800 Annex F): a low and a high sequence into
    /// <paramref name="x"/>. With a left neighbour the high band's first value is the one before the tile (Hi[-1]); values past
    /// the tile's end come from overlap coefficients, and only an edge without data mirrors.
    /// </summary>
    private static void Synthesize(ReadOnlySpan<int> lo, ReadOnlySpan<int> hiAll, bool left, Span<int> x)
    {
        int n = x.Length;
        ReadOnlySpan<int> hi = left && hiAll.Length > 0 ? hiAll[1..] : hiAll;
        int lenLo = lo.Length, lenHi = hi.Length;
        if (lenHi == 0)
        {
            if (n > 0)
            {
                x[0] = lo[0];
            }

            return;
        }

        int before = left && hiAll.Length > 0 ? hiAll[0] : hi[0];
        int evens = (n + 1) >> 1;
        if (evens > lenLo)
        {
            throw new InvalidDataException("The CR3's C-RAW subbands are too small for their tile.");
        }

        int prevHi = before;
        for (int i = 0; i < evens; i++)
        {
            int h = i < lenHi ? hi[i] : hi[lenHi - 1];
            x[2 * i] = lo[i] - ((prevHi + h + 2) >> 2);
            prevHi = h;
        }

        int odds = n >> 1;
        if (odds > lenHi)
        {
            throw new InvalidDataException("The CR3's C-RAW subbands are too small for their tile.");
        }

        for (int i = 0; i < odds; i++)
        {
            int next;
            if ((2 * i) + 2 < n)
            {
                next = x[(2 * i) + 2];
            }
            else if (i + 1 < lenLo)
            {
                // One even value past the output, from the overlap coefficients.
                int h = i + 1 < lenHi ? hi[i + 1] : hi[lenHi - 1];
                next = lo[i + 1] - ((hi[i] + h + 2) >> 2);
            }
            else
            {
                next = x[2 * i];
            }

            x[(2 * i) + 1] = hi[i] + ((x[2 * i] + next) >> 1);
        }
    }

    /// <summary>
    /// The vertical 5/3 synthesis on whole rows, in place: low row i becomes output row 2i and high row i output row 2i + 1. With
    /// a neighbour above, high row 0 is the row before the tile.
    /// </summary>
    private static void Vertical(Span<int> lowRows, Span<int> highRowsAll, int w, int loH, int hiHAll, int outH, bool top)
    {
        Span<int> high = top && hiHAll > 0 ? highRowsAll[w..] : highRowsAll;
        int hiH = top && hiHAll > 0 ? hiHAll - 1 : hiHAll;
        if (hiH == 0)
        {
            return;
        }

        int evens = (outH + 1) >> 1, odds = outH >> 1;
        if (evens > loH || odds > hiH)
        {
            throw new InvalidDataException("The CR3's C-RAW subbands are too small for their tile.");
        }

        // Even rows first, then odd rows: each even row needs the high rows around it, each odd row the even rows around it.
        for (int i = evens - 1; i >= 0; i--)
        {
            ReadOnlySpan<int> before = i == 0 ? (top && hiHAll > 0 ? highRowsAll[..w] : high[..w]) : high.Slice((i - 1) * w, w);
            ReadOnlySpan<int> here = high.Slice(Math.Min(i, hiH - 1) * w, w);
            Lift(lowRows.Slice(i * w, w), before, here);
        }

        for (int i = 0; i < odds; i++)
        {
            Span<int> h = high.Slice(i * w, w);
            ReadOnlySpan<int> even = lowRows.Slice(i * w, w);
            if ((2 * i) + 2 < outH)
            {
                Odd(h, even, lowRows.Slice((i + 1) * w, w));
            }
            else if (i + 1 < loH)
            {
                // The even row past the output: computed on the fly from the overlap rows, without storing it.
                ReadOnlySpan<int> lo = lowRows.Slice((i + 1) * w, w);
                ReadOnlySpan<int> hn = high.Slice(Math.Min(i + 1, hiH - 1) * w, w);
                for (int x = 0; x < w; x++)
                {
                    int next = lo[x] - ((h[x] + hn[x] + 2) >> 2);
                    h[x] += (even[x] + next) >> 1;
                }
            }
            else
            {
                Odd(h, even, even);
            }
        }
    }

    /// <summary>Even rows: <c>row -= (before + here + 2) &gt;&gt; 2</c>.</summary>
    private static void Lift(Span<int> row, ReadOnlySpan<int> before, ReadOnlySpan<int> here)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated && row.Length >= Vector<int>.Count)
        {
            Vector<int> two = new(2);
            for (; x <= row.Length - Vector<int>.Count; x += Vector<int>.Count)
            {
                Vector<int> v = new Vector<int>(row[x..]) - Vector.ShiftRightArithmetic(new Vector<int>(before[x..]) + new Vector<int>(here[x..]) + two, 2);
                v.CopyTo(row[x..]);
            }
        }

        for (; x < row.Length; x++)
        {
            row[x] -= (before[x] + here[x] + 2) >> 2;
        }
    }

    /// <summary>Odd rows: <c>high += (even + next) &gt;&gt; 1</c>.</summary>
    private static void Odd(Span<int> high, ReadOnlySpan<int> even, ReadOnlySpan<int> next)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated && high.Length >= Vector<int>.Count)
        {
            for (; x <= high.Length - Vector<int>.Count; x += Vector<int>.Count)
            {
                Vector<int> v = new Vector<int>(high[x..]) + Vector.ShiftRightArithmetic(new Vector<int>(even[x..]) + new Vector<int>(next[x..]), 1);
                v.CopyTo(high[x..]);
            }
        }

        for (; x < high.Length; x++)
        {
            high[x] += (even[x] + next[x]) >> 1;
        }
    }

    /// <summary>A roll frame's colour transform (CRX spec, section 10.2), from the four decoded planes to the CFA.</summary>
    private static void RollColours(short[] planes, int pw, int ph, CrxHeader h, RawPixels pixels, ParallelOptions parallel)
    {
        int median = 1 << (h.MedianBits - 1), top = (1 << h.MedianBits) - 1;
        long m = (long)median * 1024;
        int planeSize = pw * ph;
        RawParallel.For(0, ph, parallel, y =>
        {
            ReadOnlySpan<short> p0 = planes.AsSpan(y * pw, pw), p1 = planes.AsSpan(planeSize + (y * pw), pw);
            ReadOnlySpan<short> p2 = planes.AsSpan((2 * planeSize) + (y * pw), pw), p3 = planes.AsSpan((3 * planeSize) + (y * pw), pw);
            Span<ushort> row0 = pixels.U16Row(2 * y), row1 = pixels.U16Row((2 * y) + 1);
            for (int x = 0; x < pw; x++)
            {
                long b0 = m + (p0[x] * 1024L);
                long g = b0 - (168L * p1[x]) - (585L * p3[x]);
                long gr = Math.Sign(g) * (((Math.Abs(g) + 512) >> 9) & ~1L);
                Span<int> rgb = [(int)((b0 + (1510L * p3[x]) + 512) >> 10), (int)((p2[x] + gr + 1) >> 1), (int)((gr - p2[x] + 1) >> 1), (int)((b0 + (1927L * p1[x]) + 512) >> 10)];
                for (int p = 0; p < 4; p++)
                {
                    int cell = Output.Cell(h.CfaLayout, p);
                    ((cell >> 1) == 0 ? row0 : row1)[(2 * x) + (cell & 1)] = (ushort)Math.Clamp(rgb[p], 0, top);
                }
            }
        });
    }

    /// <summary>The predictor of the line above's gradient (CRX spec, section 6.1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Predict(int a, int b, int c)
    {
        int g = b - c;
        bool s = g < 0;
        int i = ((c < a) ^ s ? 2 : 0) + ((a < b) ^ s ? 1 : 0);
        return i switch
        {
            0 or 1 => a + g,
            2 => a,
            _ => b,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Unzig(uint v) => (v & 1) == 0 ? (int)(v >> 1) : -(int)((v + 1) >> 1);

    /// <summary>The Rice parameter's update (CRX spec, section 5.2); a cap of 0 is none.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Adapt(int k, uint v, int cap)
    {
        int next = k;
        if (v < (uint)((1 << k) >> 1))
        {
            next--;
        }

        uint high = v >> k;
        if (high > 2)
        {
            next++;
        }

        if (high > 5)
        {
            next++;
        }

        return cap > 0 && next > cap ? cap : next;
    }

    /// <summary>
    /// An MSB-first reader of one band's bytes, no stuffing (CRX spec, section 5). Past the end it reads zeros, and
    /// <see cref="CheckEnd"/> or a long run of them throws.
    /// </summary>
    private ref struct Bits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;
        private ulong _buffer;
        private int _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Read(int n)
        {
            if (_count < n)
            {
                Fill();
            }

            uint v = (uint)(_buffer >> (64 - n));
            _buffer <<= n;
            _count -= n;
            return v;
        }

        /// <summary>Counts zero bits up to the next one bit and reads both.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Zeros()
        {
            if (_count < 32)
            {
                Fill();
            }

            int lead = BitOperations.LeadingZeroCount(_buffer);
            if (lead < _count)
            {
                _buffer <<= lead + 1;
                _count -= lead + 1;
                return lead;
            }

            return LongZeros();
        }

        private int LongZeros()
        {
            int zeros = 0;
            while (true)
            {
                int lead = BitOperations.LeadingZeroCount(_buffer);
                if (lead < _count)
                {
                    _buffer <<= lead + 1;
                    _count -= lead + 1;
                    return zeros + lead;
                }

                zeros += _count;
                _buffer = 0;
                _count = 0;
                if (_pos > _data.Length + 16)
                {
                    throw new InvalidDataException("The CR3's CRX data ends inside a code.");
                }

                Fill();
            }
        }

        /// <summary>A coded value with parameter k (CRX spec, section 5.1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Code(int k)
        {
            int q = Zeros();
            return q >= 41 ? Read(21) : k > 0 ? ((uint)q << k) | Read(k) : (uint)q;
        }

        /// <summary>A quantiser value (CRX spec, section 5.1): escape at 23 zeros, then 8 bits.</summary>
        public uint QCode(int k)
        {
            int q = Zeros();
            return q >= 23 ? Read(8) : k > 0 ? ((uint)q << k) | Read(k) : (uint)q;
        }

        public readonly void CheckEnd()
        {
            if (((long)_pos * 8) - _count > (long)_data.Length * 8)
            {
                throw new InvalidDataException("The CR3's CRX data reads past the end of its band.");
            }
        }

        private void Fill()
        {
            if (_pos + 8 <= _data.Length)
            {
                ulong word = BinaryPrimitives.ReadUInt64BigEndian(_data[_pos..]);
                _buffer |= word >> _count;
                _pos += (63 - _count) >> 3;
                _count |= 56;
                return;
            }

            while (_count <= 56)
            {
                ulong b = _pos < _data.Length ? _data[_pos] : 0UL;
                _pos++;
                _buffer |= b << (56 - _count);
                _count += 8;
            }
        }
    }

    /// <summary>One band's line decoder: the bit reader, the Rice parameter and the run state (CRX spec, sections 5 to 7).</summary>
    private ref struct BandReader(ReadOnlySpan<byte> data, int width, int rounding)
    {
        private Bits _bits = new(data);
        private readonly int _w = width;
        private readonly int _m = rounding;
        private int _k;
        private int _s;

        public uint QCode(int k) => _bits.QCode(k);

        public readonly void CheckEnd() => _bits.CheckEnd();

        /// <summary>Decodes one line into <paramref name="cur"/> (value x at index x + 1); <paramref name="prev"/> is the line above.</summary>
        public void Line(Mode mode, bool first, Span<int> cur, Span<int> prev, Span<int> k)
        {
            switch (mode)
            {
                case Mode.Predicted when first:
                    PredictedFirst(cur);
                    break;
                case Mode.Predicted:
                    PredictedOther(cur, prev);
                    break;
                case Mode.Rounded when first:
                    RoundedFirst(cur);
                    break;
                case Mode.Rounded:
                    RoundedOther(cur, prev);
                    break;
                case Mode.Coefficient when first:
                    CoefficientFirst(cur, k);
                    break;
                default:
                    CoefficientOther(cur, prev, k);
                    break;
            }

            cur[_w + 1] = cur[_w] + 1;
        }

        /// <summary>The first line of a predicted band (CRX spec, section 7.1).</summary>
        private void PredictedFirst(Span<int> cur)
        {
            int w = _w;
            cur[0] = 0;
            for (int x = 0; x < w; x++)
            {
                int a = cur[x], pred = a;
                if (x < w - 1 && a == 0)
                {
                    int n = Run(w - x);
                    cur.Slice(x + 1, n).Clear();
                    x += n;
                    if (x == w)
                    {
                        break;
                    }

                    pred = 0;
                }

                uint v = _bits.Code(_k);
                cur[x + 1] = pred + Unzig(v);
                _k = Adapt(_k, v, 15);
            }
        }

        /// <summary>The other lines of a predicted band (CRX spec, section 7.2).</summary>
        private void PredictedOther(Span<int> cur, Span<int> prev)
        {
            int w = _w;
            cur[0] = prev[1];
            for (int x = 0; x < w; x++)
            {
                int a = cur[x], b = prev[x + 1], c = prev[x], d = prev[x + 2];
                if (x == w - 1)
                {
                    uint v = _bits.Code(_k);
                    cur[x + 1] = Predict(a, b, c) + Unzig(v);
                    _k = Adapt(_k, v, 15);
                }
                else if (a != b || a != d)
                {
                    uint v = _bits.Code(_k);
                    cur[x + 1] = Predict(a, b, c) + Unzig(v);
                    _k = Adapt(_k, (v + (2 * (uint)Math.Abs(d - b))) >> 1, 15);
                }
                else
                {
                    int n = Run(w - x);
                    cur.Slice(x + 1, n).Fill(a);
                    x += n;
                    if (x == w)
                    {
                        break;
                    }

                    b = prev[x + 1];
                    uint v = _bits.Code(_k);
                    cur[x + 1] = b + Unzig(v);
                    _k = x < w - 1 ? Adapt(_k, (v + (2 * (uint)Math.Abs(prev[x + 2] - b))) >> 1, 15) : Adapt(_k, v, 15);
                }
            }
        }

        /// <summary>The first line of a rounded lossless band (CRX spec, section 7.3).</summary>
        private void RoundedFirst(Span<int> cur)
        {
            int w = _w, m = _m;
            cur[0] = 0;
            for (int x = 0; x < w; x++)
            {
                int a = cur[x], pred;
                if (x == w - 1)
                {
                    pred = 0;
                }
                else if (Math.Abs(a) > m)
                {
                    pred = a;
                }
                else
                {
                    int n = Run(w - x);
                    cur.Slice(x + 1, n).Fill(a);
                    x += n;
                    if (x == w)
                    {
                        break;
                    }

                    pred = 0;
                }

                uint v = _bits.Code(_k);
                cur[x + 1] = Recon(pred, v);
                _k = Adapt(_k, v, 15);
            }
        }

        /// <summary>The other lines of a rounded lossless band (CRX spec, section 7.4).</summary>
        private void RoundedOther(Span<int> cur, Span<int> prev)
        {
            int w = _w, m = _m;
            cur[0] = prev[1];
            prev[0] = prev[1];
            bool f = false;
            int x = 0;
            while (x < w - 1)
            {
                int a = cur[x], b = prev[x + 1], c = prev[x], d = prev[x + 2];
                bool full;
                if (Math.Abs(d - b) > m)
                {
                    full = true;
                    f = true;
                }
                else if (f || Math.Abs(c - a) > m)
                {
                    full = true;
                    f = false;
                }
                else
                {
                    full = false;
                }

                if (full)
                {
                    uint v = _bits.Code(_k);
                    cur[x + 1] = Recon(Predict(a, b, c), v);
                    _k = RoundedAhead(_k, v, d - b);
                    x++;
                    continue;
                }

                int n = Run(w - x);
                cur.Slice(x + 1, n).Fill(a);
                x += n;
                if (w - x > 1)
                {
                    b = prev[x + 1];
                    uint v = _bits.Code(_k);
                    cur[x + 1] = Recon(b, v);
                    _k = RoundedAhead(_k, v, prev[x + 2] - b);
                    f = Math.Abs(prev[x + 2] - prev[x + 1]) > m;
                    x++;
                }
                else
                {
                    if (w - x == 1)
                    {
                        uint v = _bits.Code(_k);
                        cur[x + 1] = Recon(prev[x + 1], v);
                        _k = Adapt(_k, v, 15);
                    }

                    return;
                }
            }

            if (x == w - 1)
            {
                uint v = _bits.Code(_k);
                cur[x + 1] = Recon(Predict(cur[x], prev[x + 1], prev[x]), v);
                _k = Adapt(_k, v, 15);
            }
        }

        private readonly int Recon(int pred, uint v)
        {
            int u = Unzig(v);
            return pred + (2 * _m * u) - (u < 0 ? 1 : 0);
        }

        private readonly int RoundedAhead(int k, uint v, int e)
        {
            int r = BitOperations.Log2((uint)_m) + 1;
            int t = e > 0 ? (e + _m - 1) >> r : -((-e + _m) >> r);
            return Adapt(k, (v + (2 * (uint)Math.Abs(t))) >> 1, 15);
        }

        /// <summary>The first line of a coefficient band (CRX spec, section 7.5).</summary>
        private void CoefficientFirst(Span<int> cur, Span<int> k)
        {
            int w = _w;
            cur[0] = 0;
            for (int x = 0; x < w; x++)
            {
                if (x == w - 1 || cur[x] != 0)
                {
                    uint v = _bits.Code(_k);
                    cur[x + 1] = Unzig(v);
                    _k = Adapt(_k, v, 15);
                }
                else
                {
                    int n = Run(w - x);
                    cur.Slice(x + 1, n).Clear();
                    k.Slice(x, n).Clear();
                    x += n;
                    if (x == w)
                    {
                        break;
                    }

                    uint v = _bits.Code(_k);
                    cur[x + 1] = Unzig(v + 1);
                    _k = Adapt(_k, v, 15);
                }

                k[x] = _k;
            }
        }

        /// <summary>The other lines of a coefficient band (CRX spec, section 7.6).</summary>
        private void CoefficientOther(Span<int> cur, Span<int> prev, Span<int> k)
        {
            int w = _w;
            cur[0] = 0;
            int x = 0;
            while (x < w - 1)
            {
                if (prev[x + 1] != 0 || prev[x + 2] != 0 || cur[x] != 0)
                {
                    uint v = _bits.Code(_k);
                    cur[x + 1] = Unzig(v);
                    _k = Bump(Adapt(_k, v, 0), k[x + 1]);
                }
                else
                {
                    int n = Run(w - x);
                    cur.Slice(x + 1, n).Clear();
                    k.Slice(x, n).Clear();
                    x += n;
                    if (x == w)
                    {
                        return;
                    }

                    uint v = _bits.Code(_k);
                    cur[x + 1] = Unzig(v + 1);
                    if (x == w - 1)
                    {
                        _k = Adapt(_k, v, 15);
                        k[x] = _k;
                        return;
                    }

                    _k = Bump(Adapt(_k, v, 0), k[x + 1]);
                }

                k[x] = _k;
                x++;
            }

            if (x == w - 1)
            {
                uint v = _bits.Code(_k);
                cur[x + 1] = Unzig(v);
                _k = Adapt(_k, v, 15);
                k[x] = _k;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Bump(int k, int up) => up - k > 1 ? k + 1 : Math.Min(k, 15);

        /// <summary>A run of unchanged positions (CRX spec, section 5.3); <paramref name="remaining"/> counts the current one.</summary>
        private int Run(int remaining)
        {
            if (_bits.Read(1) == 0)
            {
                return 0;
            }

            int n = 1;
            while (_bits.Read(1) != 0)
            {
                n += 1 << Extra[_s];
                if (n > remaining)
                {
                    return remaining;
                }

                if (_s < 31)
                {
                    _s++;
                }

                if (n == remaining)
                {
                    return n;
                }
            }

            int extra = Extra[_s];
            if (extra > 0)
            {
                n += (int)_bits.Read(extra);
            }

            if (_s > 0)
            {
                _s--;
            }

            if (n > remaining)
            {
                throw new InvalidDataException("The CR3's CRX run passes the end of its line.");
            }

            return n;
        }
    }
}
