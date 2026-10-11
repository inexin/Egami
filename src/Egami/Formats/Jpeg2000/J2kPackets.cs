using System.Numerics;

namespace Egami.Formats.Jpeg2000;

/// <summary>Reads packet header bits (T.800 B.10.1): most significant first, with a 0 bit stuffed after every 0xFF byte.</summary>
internal ref struct J2kBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;
    private int _byte;
    private int _bits;
    private bool _afterFF;

    internal J2kBitReader(ReadOnlySpan<byte> data, int position)
    {
        _data = data;
        _position = position;
    }

    /// <summary>Gets the offset of the next whole byte.</summary>
    internal readonly int Position => _position;

    /// <summary>Gets whether a bit was asked for past the end of the data.</summary>
    internal bool Overrun { get; private set; }

    internal int Bit()
    {
        if (_bits == 0)
        {
            int b;
            if (_position < _data.Length)
            {
                b = _data[_position];
            }
            else
            {
                b = 0;
                Overrun = true;
            }

            _position++;
            _bits = _afterFF ? 7 : 8;
            _afterFF = b == 0xFF;
            _byte = b;
        }

        _bits--;
        return (_byte >> _bits) & 1;
    }

    internal int Bits(int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            value = (value << 1) | Bit();
        }

        return value;
    }

    /// <summary>Skips to the byte boundary that ends a packet header, taking the byte after a final 0xFF, which holds its stuffed bit.</summary>
    internal void Align()
    {
        _bits = 0;
        if (_afterFF)
        {
            _position++;
            _afterFF = false;
        }
    }
}

/// <summary>
/// Tier 2 of the JPEG 2000 decoder (T.800 Annex B): builds a tile's resolutions, subbands, precincts and code-blocks, walks its
/// packets in progression order and hands each code-block the bytes of its coding passes.
/// </summary>
internal sealed class J2kTier2
{
    private readonly J2kCodestream _stream;
    private readonly J2kTileData _tile;
    private readonly J2kTileComponent[] _components;
    private readonly int _keepLayers;
    private readonly int _tx0;
    private readonly int _ty0;
    private readonly int _tx1;
    private readonly int _ty1;

    private ReadOnlyMemory<byte> _data;
    private int _position;
    private ReadOnlyMemory<byte> _headers;
    private int _headerPosition;
    private bool _packed;

    // Per packet: each included code-block's new segment pieces, as block, segment, passes and length.
    private readonly List<(J2kCodeBlock Block, int Segment, int Passes, int Length)> _pending = [];

    internal J2kTier2(J2kCodestream stream, J2kTileData tile, int keepLayers)
    {
        _stream = stream;
        _tile = tile;
        _keepLayers = keepLayers;
        (_tx0, _ty0, _tx1, _ty1) = stream.TileRect(tile.Index);
        _components = new J2kTileComponent[stream.Components.Length];
        for (int c = 0; c < _components.Length; c++)
        {
            _components[c] = Build(c);
        }
    }

    internal J2kTileComponent[] Components => _components;

    /// <summary>Gets whether the tile's data ended before its last packet.</summary>
    internal bool Truncated { get; private set; }

    /// <summary>Gets the number of packets read.</summary>
    internal int PacketCount { get; private set; }

    private J2kTileComponent Build(int c)
    {
        J2kComponent component = _stream.Components[c];
        J2kCodingStyle style = _stream.CodingStyle(_tile, c);
        J2kQuantization quantization = _stream.Quantization(_tile, c);
        J2kTileComponent tc = new()
        {
            X0 = CeilDiv(_tx0, component.Dx),
            Y0 = CeilDiv(_ty0, component.Dy),
            X1 = CeilDiv(_tx1, component.Dx),
            Y1 = CeilDiv(_ty1, component.Dy),
            Style = style,
            Quantization = quantization,
            RoiShift = _stream.RoiShift(_tile, c),
        };
        int levels = style.Levels;
        if (quantization.Style != 1 && quantization.Bands.Length < 1 + (3 * levels))
        {
            throw new InvalidDataException($"The quantization of component {c} gives {quantization.Bands.Length} step sizes for {1 + (3 * levels)} subbands.");
        }

        tc.Resolutions = new J2kResolution[levels + 1];
        for (int r = 0; r <= levels; r++)
        {
            int nb = levels - r;
            J2kResolution res = new()
            {
                X0 = CeilShift(tc.X0, nb),
                Y0 = CeilShift(tc.Y0, nb),
                X1 = CeilShift(tc.X1, nb),
                Y1 = CeilShift(tc.Y1, nb),
            };
            (res.PrecinctWidth, res.PrecinctHeight) = style.PrecinctSizes[r];
            if (res.Width > 0 && res.Height > 0)
            {
                res.PrecinctsWide = CeilShift(res.X1, res.PrecinctWidth) - (res.X0 >> res.PrecinctWidth);
                res.PrecinctsHigh = CeilShift(res.Y1, res.PrecinctHeight) - (res.Y0 >> res.PrecinctHeight);
            }

            res.Precincts = new J2kPrecinctBand[]?[res.PrecinctCount];
            res.NextLayer = new int[res.PrecinctCount];
            if (r == 0)
            {
                res.Bands = [Band(tc, component, 0, levels, 0, levels)];
            }
            else
            {
                J2kResolution lower = tc.Resolutions[r - 1];
                res.Bands = new J2kBand[3];
                for (int o = 1; o <= 3; o++)
                {
                    J2kBand band = Band(tc, component, o, nb + 1, (3 * (r - 1)) + o, levels);
                    band.BufferX = (o & 1) != 0 ? lower.Width : 0;
                    band.BufferY = o >= 2 ? lower.Height : 0;
                    res.Bands[o - 1] = band;
                }
            }

            tc.Resolutions[r] = res;
        }

        return tc;
    }

    /// <summary>Builds subband <paramref name="orientation"/> at decomposition level <paramref name="level"/> (B-15).</summary>
    private static J2kBand Band(J2kTileComponent tc, J2kComponent component, int orientation, int level, int index, int levels)
    {
        int xob = orientation & 1;
        int yob = orientation >> 1;
        J2kBand band = new()
        {
            Orientation = orientation,
            Index = index,
        };
        if (orientation == 0)
        {
            band.X0 = CeilShift(tc.X0, level);
            band.Y0 = CeilShift(tc.Y0, level);
            band.X1 = CeilShift(tc.X1, level);
            band.Y1 = CeilShift(tc.Y1, level);
        }
        else
        {
            long half = 1L << (level - 1);
            band.X0 = (int)CeilDiv(tc.X0 - (half * xob), 1L << level);
            band.Y0 = (int)CeilDiv(tc.Y0 - (half * yob), 1L << level);
            band.X1 = (int)CeilDiv(tc.X1 - (half * xob), 1L << level);
            band.Y1 = (int)CeilDiv(tc.Y1 - (half * yob), 1L << level);
        }

        (int exponent, int mantissa) = tc.Quantization.Band(index, levels);
        band.MagnitudePlanes = tc.Quantization.GuardBits + exponent - 1;
        if (tc.Style.Reversible)
        {
            band.Step = 1;
        }
        else
        {
            int gain = orientation == 0 ? 0 : orientation == 3 ? 2 : 1;
            band.Step = (float)(Math.Pow(2, component.Precision + gain - exponent) * (1 + (mantissa / 2048.0)));
        }

        return band;
    }

    /// <summary>Builds precinct <paramref name="k"/>'s code-blocks in each band of a resolution (B.6 and B.7).</summary>
    private static J2kPrecinctBand[] Precinct(J2kTileComponent tc, int r, int k)
    {
        J2kResolution res = tc.Resolutions[r];
        int px = k % res.PrecinctsWide;
        int py = k / res.PrecinctsWide;
        int ppx = r == 0 ? res.PrecinctWidth : res.PrecinctWidth - 1;
        int ppy = r == 0 ? res.PrecinctHeight : res.PrecinctHeight - 1;
        int xcb = Math.Min(tc.Style.BlockWidth, ppx);
        int ycb = Math.Min(tc.Style.BlockHeight, ppy);
        long startX = ((long)(res.X0 >> res.PrecinctWidth) + px) << ppx;
        long startY = ((long)(res.Y0 >> res.PrecinctHeight) + py) << ppy;
        J2kPrecinctBand[] bands = new J2kPrecinctBand[res.Bands.Length];
        for (int b = 0; b < bands.Length; b++)
        {
            J2kBand band = res.Bands[b];
            long x0 = Math.Max(startX, band.X0);
            long y0 = Math.Max(startY, band.Y0);
            long x1 = Math.Min(startX + (1L << ppx), band.X1);
            long y1 = Math.Min(startY + (1L << ppy), band.Y1);
            if (x0 >= x1 || y0 >= y1)
            {
                bands[b] = new J2kPrecinctBand([], 0, 0);
                continue;
            }

            int cbx0 = (int)(x0 >> xcb);
            int cby0 = (int)(y0 >> ycb);
            int wide = (int)(((x1 - 1) >> xcb) - cbx0 + 1);
            int high = (int)(((y1 - 1) >> ycb) - cby0 + 1);
            J2kCodeBlock[] blocks = new J2kCodeBlock[wide * high];
            for (int j = 0; j < high; j++)
            {
                for (int i = 0; i < wide; i++)
                {
                    long bx = (long)(cbx0 + i) << xcb;
                    long by = (long)(cby0 + j) << ycb;
                    blocks[(j * wide) + i] = new J2kCodeBlock
                    {
                        X0 = (int)Math.Max(bx, x0),
                        Y0 = (int)Math.Max(by, y0),
                        X1 = (int)Math.Min(bx + (1L << xcb), x1),
                        Y1 = (int)Math.Min(by + (1L << ycb), y1),
                    };
                }
            }

            bands[b] = new J2kPrecinctBand(blocks, wide, high);
        }

        return bands;
    }

    /// <summary>Reads every packet of the tile in its progression order, then those any progression order change left out.</summary>
    internal void ReadPackets(CancellationToken cancellationToken)
    {
        _data = Join(_tile.Parts);
        _position = 0;
        _packed = _tile.PacketHeaders.Count > 0;
        if (_packed)
        {
            _headers = Join(_tile.PacketHeaders);
            _headerPosition = 0;
        }

        J2kCodingStyle cod = _components[0].Style;
        int layers = cod.Layers;
        int maxResolutions = 0;
        foreach (J2kTileComponent tc in _components)
        {
            maxResolutions = Math.Max(maxResolutions, tc.Resolutions.Length);
        }

        List<J2kProgressionChange> changes = _tile.ProgressionChanges.Count > 0 ? _tile.ProgressionChanges : _stream.ProgressionChanges;
        try
        {
            foreach (J2kProgressionChange change in changes)
            {
                Progress(change.Order, change.LayerEnd, change.ResolutionStart, change.ResolutionEnd, change.ComponentStart, change.ComponentEnd, cancellationToken);
            }

            Progress(cod.Progression, layers, 0, maxResolutions, 0, _components.Length, cancellationToken);
        }
        catch (EndOfDataException)
        {
            Truncated = true;
        }
    }

    private static ReadOnlyMemory<byte> Join(List<ReadOnlyMemory<byte>> parts)
    {
        if (parts.Count == 1)
        {
            return parts[0];
        }

        byte[] joined = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (ReadOnlyMemory<byte> part in parts)
        {
            part.Span.CopyTo(joined.AsSpan(at));
            at += part.Length;
        }

        return joined;
    }

    /// <summary>Walks one progression (B.12) over layers below <paramref name="le"/>, resolutions and components in range.</summary>
    private void Progress(int order, int le, int rs, int re, int cs, int ce, CancellationToken cancellationToken)
    {
        int layers = _components[0].Style.Layers;
        le = Math.Min(le, layers);
        ce = Math.Min(ce, _components.Length);
        int maxResolutions = 0;
        foreach (J2kTileComponent tc in _components)
        {
            maxResolutions = Math.Max(maxResolutions, tc.Resolutions.Length);
        }

        re = Math.Min(re, maxResolutions);
        switch (order)
        {
            case 0:
                // Layer, resolution, component, position.
                for (int l = 0; l < le; l++)
                {
                    for (int r = rs; r < re; r++)
                    {
                        for (int c = cs; c < ce; c++)
                        {
                            AllPrecincts(l, c, r);
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                break;
            case 1:
                // Resolution, layer, component, position.
                for (int r = rs; r < re; r++)
                {
                    for (int l = 0; l < le; l++)
                    {
                        for (int c = cs; c < ce; c++)
                        {
                            AllPrecincts(l, c, r);
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                break;
            case 2:
                // Resolution, position, component, layer.
                for (int r = rs; r < re; r++)
                {
                    List<(int C, int R)> pairs = [];
                    for (int c = cs; c < ce; c++)
                    {
                        pairs.Add((c, r));
                    }

                    foreach (long y in Positions(pairs, vertical: true))
                    {
                        foreach (long x in Positions(pairs, vertical: false))
                        {
                            for (int c = cs; c < ce; c++)
                            {
                                if (PrecinctAt(c, r, x, y, out int k))
                                {
                                    Layers(c, r, k, le);
                                }
                            }
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                break;
            case 3:
                // Position, component, resolution, layer.
                {
                    List<(int C, int R)> pairs = [];
                    for (int c = cs; c < ce; c++)
                    {
                        for (int r = rs; r < Math.Min(re, _components[c].Resolutions.Length); r++)
                        {
                            pairs.Add((c, r));
                        }
                    }

                    foreach (long y in Positions(pairs, vertical: true))
                    {
                        foreach (long x in Positions(pairs, vertical: false))
                        {
                            for (int c = cs; c < ce; c++)
                            {
                                for (int r = rs; r < re; r++)
                                {
                                    if (PrecinctAt(c, r, x, y, out int k))
                                    {
                                        Layers(c, r, k, le);
                                    }
                                }
                            }
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }

                break;
            case 4:
                // Component, position, resolution, layer.
                for (int c = cs; c < ce; c++)
                {
                    List<(int C, int R)> pairs = [];
                    for (int r = rs; r < Math.Min(re, _components[c].Resolutions.Length); r++)
                    {
                        pairs.Add((c, r));
                    }

                    foreach (long y in Positions(pairs, vertical: true))
                    {
                        foreach (long x in Positions(pairs, vertical: false))
                        {
                            for (int r = rs; r < re; r++)
                            {
                                if (PrecinctAt(c, r, x, y, out int k))
                                {
                                    Layers(c, r, k, le);
                                }
                            }
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                break;
        }
    }

    private void AllPrecincts(int l, int c, int r)
    {
        J2kTileComponent tc = _components[c];
        if (r >= tc.Resolutions.Length)
        {
            return;
        }

        J2kResolution res = tc.Resolutions[r];
        for (int k = 0; k < res.PrecinctCount; k++)
        {
            Packet(l, tc, r, k);
        }
    }

    private void Layers(int c, int r, int k, int le)
    {
        J2kTileComponent tc = _components[c];
        for (int l = tc.Resolutions[r].NextLayer[k]; l < le; l++)
        {
            Packet(l, tc, r, k);
        }
    }

    /// <summary>
    /// Returns the reference grid positions, from the tile's corner, where a precinct of any of the component and resolution pairs
    /// starts (B.12.1.3), in order.
    /// </summary>
    private List<long> Positions(List<(int C, int R)> pairs, bool vertical)
    {
        long start = vertical ? _ty0 : _tx0;
        long end = vertical ? _ty1 : _tx1;
        SortedSet<long> positions = [start];
        foreach ((int c, int r) in pairs)
        {
            J2kTileComponent tc = _components[c];
            if (r >= tc.Resolutions.Length || tc.Resolutions[r].PrecinctCount == 0)
            {
                continue;
            }

            J2kResolution res = tc.Resolutions[r];
            int levels = tc.Resolutions.Length - 1;
            int shift = (vertical ? res.PrecinctHeight : res.PrecinctWidth) + levels - r;
            long step = (long)(vertical ? _stream.Components[c].Dy : _stream.Components[c].Dx) << Math.Min(shift, 40);
            for (long p = ((start / step) + 1) * step; p < end; p += step)
            {
                positions.Add(p);
            }
        }

        return [.. positions];
    }

    /// <summary>Returns whether a precinct of component <paramref name="c"/> at resolution <paramref name="r"/> starts at (x, y), and which.</summary>
    private bool PrecinctAt(int c, int r, long x, long y, out int k)
    {
        k = 0;
        J2kTileComponent tc = _components[c];
        if (r >= tc.Resolutions.Length)
        {
            return false;
        }

        J2kResolution res = tc.Resolutions[r];
        if (res.PrecinctCount == 0)
        {
            return false;
        }

        J2kComponent component = _stream.Components[c];
        int levels = tc.Resolutions.Length - 1;
        int down = levels - r;
        int shiftX = Math.Min(res.PrecinctWidth + down, 40);
        int shiftY = Math.Min(res.PrecinctHeight + down, 40);
        bool atX = x % ((long)component.Dx << shiftX) == 0 || (x == _tx0 && (((long)res.X0 << down) & ((1L << shiftX) - 1)) != 0);
        bool atY = y % ((long)component.Dy << shiftY) == 0 || (y == _ty0 && (((long)res.Y0 << down) & ((1L << shiftY) - 1)) != 0);
        if (!atX || !atY)
        {
            return false;
        }

        long rx = CeilDiv(x, (long)component.Dx << down);
        long ry = CeilDiv(y, (long)component.Dy << down);
        long px = (rx >> res.PrecinctWidth) - (res.X0 >> res.PrecinctWidth);
        long py = (ry >> res.PrecinctHeight) - (res.Y0 >> res.PrecinctHeight);
        if (px < 0 || py < 0 || px >= res.PrecinctsWide || py >= res.PrecinctsHigh)
        {
            return false;
        }

        k = (int)(px + (py * res.PrecinctsWide));
        return true;
    }

    /// <summary>Reads packet (l, r, k) of a tile-component (B.9 and B.10), unless an earlier progression read it.</summary>
    private void Packet(int l, J2kTileComponent tc, int r, int k)
    {
        J2kResolution res = tc.Resolutions[r];
        if (res.NextLayer[k] != l)
        {
            return;
        }

        res.NextLayer[k] = l + 1;
        PacketCount++;
        ReadOnlySpan<byte> data = _data.Span;
        J2kCodingStyle style = tc.Style;
        if (style.Sop && _position + 6 <= data.Length && data[_position] == 0xFF && data[_position + 1] == 0x91)
        {
            _position += 6;
        }

        if (!_packed && _position >= data.Length)
        {
            throw new EndOfDataException();
        }

        J2kPrecinctBand[] precinct = res.Precincts[k] ??= Precinct(tc, r, k);
        ReadOnlySpan<byte> headers = _packed ? _headers.Span : data;
        int start = _packed ? _headerPosition : _position;
        J2kBitReader bits = new(headers, start);
        _pending.Clear();
        if (bits.Bit() != 0)
        {
            foreach (J2kPrecinctBand band in precinct)
            {
                for (int i = 0; i < band.Blocks.Length; i++)
                {
                    J2kCodeBlock block = band.Blocks[i];
                    bool included = block.Included ? bits.Bit() != 0 : band.Inclusion.Decode(ref bits, i, l + 1);
                    if (!included)
                    {
                        continue;
                    }

                    if (!block.Included)
                    {
                        int threshold = 1;
                        while (!band.ZeroPlanes.Decode(ref bits, i, threshold))
                        {
                            if (++threshold > 80 || bits.Overrun)
                            {
                                throw new InvalidDataException("A code-block's zero bit-plane count is impossibly large.");
                            }
                        }

                        block.ZeroPlanes = band.ZeroPlanes.Value(i);
                        block.Included = true;
                    }

                    int passes = PassCount(ref bits);
                    while (bits.Bit() != 0)
                    {
                        if (++block.Lblock > 32 || bits.Overrun)
                        {
                            throw new InvalidDataException("A code-block's length indicator grows past 32 bits.");
                        }
                    }

                    while (passes > 0)
                    {
                        List<J2kSegment> segments = block.Segments;
                        if (segments.Count == 0 || segments[^1].Passes == segments[^1].MaxPasses)
                        {
                            segments.Add(new J2kSegment { MaxPasses = MaxPasses(style.BlockStyle, segments.Count) });
                        }

                        J2kSegment segment = segments[^1];
                        int n = Math.Min(passes, segment.MaxPasses - segment.Passes);
                        int lengthBits = block.Lblock + BitOperations.Log2((uint)n);
                        if (lengthBits > 31)
                        {
                            throw new InvalidDataException("A code-block segment length takes more than 31 bits.");
                        }

                        int length = bits.Bits(lengthBits);
                        segment.Passes += n;
                        segments[^1] = segment;
                        block.Passes += n;
                        _pending.Add((block, segments.Count - 1, n, length));
                        passes -= n;
                    }

                    if (bits.Overrun)
                    {
                        throw new EndOfDataException();
                    }
                }
            }
        }

        bits.Align();
        if (bits.Overrun)
        {
            throw new EndOfDataException();
        }

        int end = bits.Position;
        if (style.Eph && end + 2 <= headers.Length && headers[end] == 0xFF && headers[end + 1] == 0x92)
        {
            end += 2;
        }

        if (_packed)
        {
            _headerPosition = end;
        }
        else
        {
            _position = end;
        }

        bool keep = l < _keepLayers;
        foreach ((J2kCodeBlock block, int segment, int passes, int length) in _pending)
        {
            int take = Math.Min(length, Math.Max(0, data.Length - _position));
            if (keep && take > 0)
            {
                block.Chunks.Add(_data.Slice(_position, take));
                block.Bytes += take;
                J2kSegment s = block.Segments[segment];
                s.Length += take;
                s.KeptPasses += passes;
                block.Segments[segment] = s;
            }
            else if (keep)
            {
                J2kSegment s = block.Segments[segment];
                s.KeptPasses += passes;
                block.Segments[segment] = s;
            }

            _position += take;
            if (take < length)
            {
                Truncated = true;
            }
        }

        if (Truncated)
        {
            throw new EndOfDataException();
        }
    }

    /// <summary>Reads a number of coding passes (Table B.4).</summary>
    private static int PassCount(ref J2kBitReader bits)
    {
        if (bits.Bit() == 0)
        {
            return 1;
        }

        if (bits.Bit() == 0)
        {
            return 2;
        }

        int v = bits.Bits(2);
        if (v != 3)
        {
            return 3 + v;
        }

        v = bits.Bits(5);
        return v != 31 ? 6 + v : 37 + bits.Bits(7);
    }

    /// <summary>
    /// Returns the most coding passes segment <paramref name="index"/> holds (D.4.1): one with termination on each pass; with
    /// arithmetic bypass, the first ten, then two raw passes and one arithmetic pass in turn; otherwise all of them.
    /// </summary>
    internal static int MaxPasses(int blockStyle, int index)
    {
        if ((blockStyle & 4) != 0)
        {
            return 1;
        }

        if ((blockStyle & 1) != 0)
        {
            return index == 0 ? 10 : (index & 1) != 0 ? 2 : 1;
        }

        return int.MaxValue;
    }

    internal static int CeilDiv(int a, int b) => (int)CeilDiv((long)a, b);

    internal static long CeilDiv(long a, long b) => a >= 0 ? (a + b - 1) / b : -(-a / b);

    internal static int CeilShift(int a, int shift) => shift >= 31 ? (a > 0 ? 1 : 0) : (int)(((long)a + (1L << shift) - 1) >> shift);

    /// <summary>Thrown inside tier 2 when the tile's data ends; the packets read so far still decode.</summary>
    private sealed class EndOfDataException : Exception
    {
    }
}
