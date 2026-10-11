namespace Egami.Formats.Jpeg2000;

/// <summary>One codeword segment of a code-block: the passes it holds and its bytes, which may arrive over several packets.</summary>
internal struct J2kSegment
{
    /// <summary>The number of coding passes the segment holds so far.</summary>
    internal int Passes;

    /// <summary>The most passes the segment can hold, by the code-block style.</summary>
    internal int MaxPasses;

    /// <summary>The number of passes kept for decoding: those of the layers below the quality layer limit.</summary>
    internal int KeptPasses;

    /// <summary>The number of bytes kept for decoding.</summary>
    internal int Length;
}

/// <summary>A code-block (T.800 B.7): its rectangle in its subband, its tier-2 state and the codeword segments tier 2 found.</summary>
internal sealed class J2kCodeBlock
{
    internal int X0;
    internal int Y0;
    internal int X1;
    internal int Y1;

    /// <summary>Whether a packet has included the code-block yet.</summary>
    internal bool Included;

    /// <summary>The number of missing most significant bit-planes, from the zero bit-plane tag tree.</summary>
    internal int ZeroPlanes;

    /// <summary>The Lblock state of B.10.7.1.</summary>
    internal int Lblock = 3;

    /// <summary>The number of coding passes found so far.</summary>
    internal int Passes;

    /// <summary>The codeword segments, in order.</summary>
    internal List<J2kSegment> Segments = [];

    /// <summary>The bytes of every segment, in order, as slices of the codestream.</summary>
    internal List<ReadOnlyMemory<byte>> Chunks = [];

    /// <summary>The total number of bytes in <see cref="Chunks"/>.</summary>
    internal int Bytes;

    internal int Width => X1 - X0;

    internal int Height => Y1 - Y0;
}

/// <summary>The part of one subband a precinct covers: its code-blocks and their tag trees (T.800 B.6 and B.10.2).</summary>
internal sealed class J2kPrecinctBand
{
    internal J2kPrecinctBand(J2kCodeBlock[] blocks, int blocksWide, int blocksHigh)
    {
        Blocks = blocks;
        BlocksWide = blocksWide;
        BlocksHigh = blocksHigh;
        Inclusion = new J2kTagTree(blocksWide, blocksHigh);
        ZeroPlanes = new J2kTagTree(blocksWide, blocksHigh);
    }

    /// <summary>The code-blocks in raster order.</summary>
    internal J2kCodeBlock[] Blocks { get; }

    internal int BlocksWide { get; }

    internal int BlocksHigh { get; }

    internal J2kTagTree Inclusion { get; }

    internal J2kTagTree ZeroPlanes { get; }
}

/// <summary>A subband of one resolution of a tile-component (T.800 B.5).</summary>
internal sealed class J2kBand
{
    /// <summary>The orientation: 0 LL, 1 HL, 2 LH, 3 HH.</summary>
    internal int Orientation;

    /// <summary>The subband's index in the quantization step list: 0 for LL, then 3 per level, coarsest first.</summary>
    internal int Index;

    internal int X0;
    internal int Y0;
    internal int X1;
    internal int Y1;

    /// <summary>The number of magnitude bit-planes Mb = G + εb − 1 (E-2), before any ROI shift.</summary>
    internal int MagnitudePlanes;

    /// <summary>The quantization step Δb (1 for reversible bands).</summary>
    internal float Step;

    /// <summary>Where the band's samples start in the tile-component's coefficient buffer.</summary>
    internal int BufferX;

    internal int BufferY;

    internal int Width => X1 - X0;

    internal int Height => Y1 - Y0;
}

/// <summary>One resolution level of a tile-component (T.800 B.5).</summary>
internal sealed class J2kResolution
{
    internal int X0;
    internal int Y0;
    internal int X1;
    internal int Y1;

    /// <summary>The precinct width and height exponents, PPx and PPy.</summary>
    internal int PrecinctWidth;

    internal int PrecinctHeight;

    /// <summary>The number of precincts across and down (0 when the resolution is empty).</summary>
    internal int PrecinctsWide;

    internal int PrecinctsHigh;

    /// <summary>The subbands: LL alone at resolution 0, else HL, LH and HH.</summary>
    internal J2kBand[] Bands = [];

    /// <summary>Each precinct's part of each band, by precinct index then band; built when a packet first needs it.</summary>
    internal J2kPrecinctBand[]?[] Precincts = [];

    /// <summary>The next layer each precinct's packet carries, so a progression that passes a packet twice reads it once.</summary>
    internal int[] NextLayer = [];

    internal int Width => X1 - X0;

    internal int Height => Y1 - Y0;

    internal int PrecinctCount => PrecinctsWide * PrecinctsHigh;
}

/// <summary>A tile-component: its rectangle, coding style, resolutions and the coefficients tier 1 writes.</summary>
internal sealed class J2kTileComponent
{
    internal int X0;
    internal int Y0;
    internal int X1;
    internal int Y1;

    internal J2kCodingStyle Style = null!;

    internal J2kQuantization Quantization = null!;

    internal int RoiShift;

    /// <summary>The resolutions decoded, 0 to <see cref="J2kCodingStyle.Levels"/> minus the levels dropped.</summary>
    internal J2kResolution[] Resolutions = [];

    internal int Width => X1 - X0;

    internal int Height => Y1 - Y0;
}

/// <summary>A tag tree (T.800 B.10.2) over a precinct band's code-blocks.</summary>
internal sealed class J2kTagTree
{
    private readonly int[] _value;
    private readonly int[] _low;
    private readonly int[] _parent;
    private readonly int _leaves;

    internal J2kTagTree(int width, int height)
    {
        _leaves = width * height;
        int count = 0;
        int w = Math.Max(width, 1);
        int h = Math.Max(height, 1);
        while (true)
        {
            count += w * h;
            if (w == 1 && h == 1)
            {
                break;
            }

            w = (w + 1) >> 1;
            h = (h + 1) >> 1;
        }

        _value = new int[Math.Max(count, 1)];
        _low = new int[_value.Length];
        _parent = new int[_value.Length];
        Array.Fill(_value, int.MaxValue);
        Array.Fill(_parent, -1);

        // Link each level's nodes to the next level's.
        int start = 0;
        w = Math.Max(width, 1);
        h = Math.Max(height, 1);
        while (w * h > 1)
        {
            int nw = (w + 1) >> 1;
            int nh = (h + 1) >> 1;
            int next = start + (w * h);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    _parent[start + (y * w) + x] = next + ((y >> 1) * nw) + (x >> 1);
                }
            }

            start = next;
            w = nw;
            h = nh;
        }
    }

    /// <summary>
    /// Decodes leaf <paramref name="leaf"/> up to <paramref name="threshold"/> and returns whether its value is below it; the
    /// value is then <see cref="Value"/>.
    /// </summary>
    internal bool Decode(ref J2kBitReader bits, int leaf, int threshold)
    {
        Span<int> path = stackalloc int[32];
        int depth = 0;
        int node = leaf;
        while (node >= 0)
        {
            path[depth++] = node;
            node = _parent[node];
        }

        int low = 0;
        for (int i = depth - 1; i >= 0; i--)
        {
            int n = path[i];
            if (low > _low[n])
            {
                _low[n] = low;
            }
            else
            {
                low = _low[n];
            }

            while (low < threshold && low < _value[n])
            {
                if (bits.Bit() != 0)
                {
                    _value[n] = low;
                }
                else
                {
                    low++;
                }
            }

            _low[n] = low;
        }

        return _value[leaf] < threshold;
    }

    internal int Value(int leaf) => _value[leaf];

    internal int Leaves => _leaves;
}
