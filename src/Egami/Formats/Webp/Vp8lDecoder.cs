using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Webp;

/// <summary>
/// Decodes a VP8L (lossless WebP) bit stream, written from RFC 9649 section by section: the header, the transforms (each at most
/// once, undone in reverse order a row at a time), the color cache, meta prefix codes and the entropy-coded pixels. Rows come out
/// top-down as they finish. The entropy-coded pixels are kept in a window that covers the longest backward reference VP8L allows,
/// so a large image never needs a full copy; an image smaller than the window is held whole.
/// </summary>
internal sealed class Vp8lDecoder : IDisposable
{
    /// <summary>The longest backward distance: distance code 2^20 less the 120 plane codes.</summary>
    internal const int MaxDistance = (1 << 20) - 120;

    private const int LengthCodes = 24;
    private const int DistanceCodes = 40;
    private const int Predictor = 0;
    private const int CrossColor = 1;
    private const int SubtractGreen = 2;
    private const int ColorIndexing = 3;

    private readonly EgamiInput _input;
    private readonly Vp8lBitReader _reader;
    private readonly bool _strict;
    private readonly List<IMemoryOwner<byte>> _owned = [];
    private readonly Transform[] _transforms = new Transform[4];
    private int _transformCount;

    // The main entropy-coded image.
    private int _xsize;
    private int _cacheBits;
    private uint[]? _cache;
    private int _metaBits;
    private int _metaWidth;
    private Memory<byte> _meta;
    private CodeGroups? _groups;

    // The window of entropy-coded pixels: _window[i] is pixel _base + i.
    private Memory<byte> _windowMemory;
    private long _base;
    private long _decoded;
    private long _total;
    private int _windowRows;

    // Rows out.
    private uint[]? _row;
    private uint[]? _scratch;
    private uint[]? _previous;
    private int _nextRow;
    private bool _headerRead;
    private int _failedAt = -1;
    private bool _disposed;

    /// <summary>Starts decoding a VP8L stream whose first byte is the signature, or, when <paramref name="width"/> is given, the headerless stream of ALPH data.</summary>
    internal Vp8lDecoder(EgamiInput input, Vp8lBitReader reader, bool strict, int width = 0, int height = 0)
    {
        _input = input;
        _reader = reader;
        _strict = strict;
        if (width > 0)
        {
            // ALPH data: no signature or size, no alpha hint or version.
            Width = width;
            Height = height;
            return;
        }

        if (_reader.Read(8) != Vp8lEncoder.Signature)
        {
            throw Corrupt("The VP8L signature is wrong.");
        }

        Width = (int)_reader.Read(14) + 1;
        Height = (int)_reader.Read(14) + 1;
        AlphaHint = _reader.Read(1) != 0;
        if (_reader.Read(3) != 0)
        {
            throw Corrupt("The VP8L version is not 0.");
        }
    }

    /// <summary>Gets the image width.</summary>
    internal int Width { get; }

    /// <summary>Gets the image height.</summary>
    internal int Height { get; }

    /// <summary>Gets the header's hint that some pixel is not opaque.</summary>
    internal bool AlphaHint { get; }

    /// <summary>Gets the row at which the data was damaged or ended in a lenient decode, or -1; that row and those below are transparent black.</summary>
    internal int FailedAt => _failedAt;

    /// <summary>Gets the warning a lenient decode recovered with, if any.</summary>
    internal string? Warning { get; private set; }

    /// <summary>Gets the next row <see cref="DecodeRow"/> will produce.</summary>
    internal int NextRow => _nextRow;

    /// <summary>Returns the bytes the decoder holds for an image of this size (before transforms): the window of coded pixels and three rows.</summary>
    internal static long WorkingBytes(int width, int height)
    {
        return ((long)Math.Min(height, WindowRows(width)) * width * 4) + ((long)width * 12);
    }

    /// <summary>
    /// Returns how many rows of coded pixels the window holds: those a backward reference can reach, the row being decoded and the
    /// longest copy past it, plus a quarter more so the window slides only now and then.
    /// </summary>
    private static int WindowRows(int width)
    {
        long reach = ((MaxDistance + 4096 + (long)width - 1) / width) + 2;
        return (int)Math.Min(int.MaxValue, reach + Math.Max(8, reach / 4));
    }

    /// <summary>
    /// Reads everything before the coded pixels: the transforms, the color cache size, the meta prefix codes and the prefix code
    /// groups. Called by the first <see cref="DecodeRow"/>; an incremental decode that runs out of bytes starts it over.
    /// </summary>
    internal void ReadHeader()
    {
        if (_headerRead)
        {
            return;
        }

        bool incremental = _input.Incremental;
        Vp8lBitReader.Checkpoint mark = incremental ? _reader.Mark() : default;
        try
        {
            int xsize = Width;
            _transformCount = 0;
            int seen = 0;
            while (_reader.Read(1) != 0)
            {
                int type = (int)_reader.Read(2);
                if ((seen & (1 << type)) != 0)
                {
                    throw Corrupt($"The VP8L transform {type} appears twice.");
                }

                seen |= 1 << type;
                xsize = ReadTransform(type, xsize);
            }

            _xsize = xsize;
            _cacheBits = ReadCacheBits();
            if (_reader.Read(1) != 0)
            {
                _metaBits = (int)_reader.Read(3) + 2;
                _metaWidth = SubSampleSize(xsize, _metaBits);
                int metaHeight = SubSampleSize(Height, _metaBits);
                _meta = Rent((long)_metaWidth * metaHeight);
                Span<uint> meta = MemoryMarshal.Cast<byte, uint>(_meta.Span)[..(_metaWidth * metaHeight)];
                DecodeImage(_metaWidth, metaHeight, meta);
                _groups = ReadGroups(meta, _cacheBits);
            }
            else
            {
                _metaBits = 0;
                _groups = ReadGroups(default, _cacheBits);
            }

            _cache = _cacheBits > 0 ? new uint[1 << _cacheBits] : null;
            _total = (long)_xsize * Height;
            _windowRows = (int)Math.Min(Height, WindowRows(_xsize));
            _windowMemory = Rent((long)_windowRows * _xsize);
            _row = ArrayPool<uint>.Shared.Rent(Width);
            _scratch = ArrayPool<uint>.Shared.Rent(Width);
            _previous = ArrayPool<uint>.Shared.Rent(Width);
            _headerRead = true;
        }
        catch (Streaming.NeedsInputException)
        {
            _reader.Rewind(mark);
            ReleaseHeader();
            throw;
        }
        finally
        {
            if (incremental)
            {
                _reader.Unmark();
            }
        }
    }

    /// <summary>
    /// Decodes the next row into <paramref name="argb"/> (A &lt;&lt; 24 | R &lt;&lt; 16 | G &lt;&lt; 8 | B, <see cref="Width"/>
    /// long). Damaged or missing data throws in a strict decode; in a lenient one that row and every one after it are transparent
    /// black and <see cref="Warning"/> says why.
    /// </summary>
    internal void DecodeRow(Span<uint> argb)
    {
        ReadHeader();
        int y = _nextRow;
        if (_failedAt >= 0)
        {
            argb[..Width].Clear();
            _nextRow++;
            return;
        }

        try
        {
            DecodeCoded(y);
        }
        catch (CorruptDataException e) when (!_strict && !e.IsTruncation)
        {
            Fail(y, e.Message);
        }

        if (_failedAt >= 0)
        {
            argb[..Width].Clear();
            _nextRow++;
            return;
        }

        Span<uint> window = MemoryMarshal.Cast<byte, uint>(_windowMemory.Span);
        Span<uint> row = _row.AsSpan(0, Width);
        window.Slice((int)(((long)y * _xsize) - _base), _xsize).CopyTo(row);
        Untransform(row, y);
        row.CopyTo(argb);
        _nextRow++;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseHeader();
        foreach (uint[]? array in (ReadOnlySpan<uint[]?>)[_row, _scratch, _previous])
        {
            if (array is not null)
            {
                ArrayPool<uint>.Shared.Return(array);
            }
        }

        _row = _scratch = _previous = null;
    }

    private void ReleaseHeader()
    {
        foreach (IMemoryOwner<byte> owned in _owned)
        {
            owned.Dispose();
        }

        _owned.Clear();
        _groups = null;
    }

    private void Fail(int y, string problem)
    {
        _failedAt = y;
        Warning = $"{problem} Rows {y} to {Height - 1} are left transparent black.";
    }

    /// <summary>Decodes coded pixels until row <paramref name="y"/> is complete, sliding the window first if it is full.</summary>
    private void DecodeCoded(int y)
    {
        long end = ((long)y + 1) * _xsize;
        if (_decoded >= end)
        {
            return;
        }

        Span<uint> window = MemoryMarshal.Cast<byte, uint>(_windowMemory.Span);
        long capacity = (long)_windowRows * _xsize;

        // A copy can run up to 4,096 pixels past the row; keep room for it, sliding only at row starts so a rewind stays valid.
        if (end + 4096 - _base > capacity && _base + capacity < _total)
        {
            long keep = Math.Max(0, ((long)y * _xsize) - MaxDistance);
            keep -= keep % _xsize;
            int move = (int)(keep - _base);
            if (move > 0)
            {
                window[move..(int)(_decoded - _base)].CopyTo(window);
                _base = keep;
            }
        }

        bool incremental = _input.Incremental;
        Vp8lBitReader.Checkpoint mark = default;
        long decoded = _decoded;
        uint[]? cache = null;
        if (incremental)
        {
            mark = _reader.Mark();
            if (_cache is not null)
            {
                cache = ArrayPool<uint>.Shared.Rent(_cache.Length);
                _cache.CopyTo(cache, 0);
            }
        }

        try
        {
            _decoded = DecodePixels(window, _base, _decoded, Math.Min(_total, end), _total, _xsize, _groups!, _cache, _cacheBits, MemoryMarshal.Cast<byte, uint>(_meta.Span), _metaBits, _metaWidth, capacity);
            if (_reader.PastEnd)
            {
                if (_strict)
                {
                    throw new CorruptDataException(_input.FormatName, _input.Position, "The lossless image data ends unexpectedly.") { IsTruncation = true };
                }

                Fail(y, "The lossless image data ends early.");
            }
        }
        catch (Streaming.NeedsInputException)
        {
            _reader.Rewind(mark);
            _decoded = decoded;
            cache?.CopyTo(_cache!, 0);
            throw;
        }
        finally
        {
            if (incremental)
            {
                _reader.Unmark();
                if (cache is not null)
                {
                    ArrayPool<uint>.Shared.Return(cache);
                }
            }
        }
    }

    /// <summary>Reads one transform (section 4) and returns the coded width after it.</summary>
    private int ReadTransform(int type, int xsize)
    {
        ref Transform t = ref _transforms[_transformCount++];
        t = new Transform { Type = type, XSize = xsize };
        switch (type)
        {
            case Predictor:
            case CrossColor:
                t.Bits = (int)_reader.Read(3) + 2;
                int w = SubSampleSize(xsize, t.Bits);
                int h = SubSampleSize(Height, t.Bits);
                t.Data = Rent((long)w * h);
                DecodeImage(w, h, MemoryMarshal.Cast<byte, uint>(t.Data.Span)[..(w * h)]);
                return xsize;
            case ColorIndexing:
                int colors = (int)_reader.Read(8) + 1;
                t.Bits = colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;
                t.Colors = colors;
                t.Data = Rent(256);
                Span<uint> palette = MemoryMarshal.Cast<byte, uint>(t.Data.Span)[..256];
                palette.Clear();
                DecodeImage(colors, 1, palette[..colors]);
                for (int i = 1; i < colors; i++)
                {
                    palette[i] = AddPixels(palette[i], palette[i - 1]);
                }

                return SubSampleSize(xsize, t.Bits);
            default:
                return xsize;
        }
    }

    private int ReadCacheBits()
    {
        if (_reader.Read(1) == 0)
        {
            return 0;
        }

        int bits = (int)_reader.Read(4);
        if (bits is < 1 or > 11)
        {
            throw Corrupt($"The color cache size {bits} is outside 1 to 11 bits.");
        }

        return bits;
    }

    /// <summary>Decodes a sub-image (transform data, the entropy image or the palette) whole: its own cache size and one group of codes.</summary>
    private void DecodeImage(int width, int height, Span<uint> pixels)
    {
        int cacheBits = ReadCacheBits();
        CodeGroups groups = ReadGroups(default, cacheBits);
        uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        long total = (long)width * height;
        DecodePixels(pixels, 0, 0, total, total, width, groups, cache, cacheBits, default, 0, 0, total);
        if (_reader.PastEnd)
        {
            throw new CorruptDataException(_input.FormatName, _input.Position, "The lossless image data ends unexpectedly.") { IsTruncation = true };
        }
    }

    /// <summary>
    /// Reads the prefix code groups (section 3.7.2.2): one group, or as many as the entropy image names (the largest value plus
    /// one). Groups the image never uses are read and dropped, so a small hostile file cannot make large tables.
    /// </summary>
    private CodeGroups ReadGroups(Span<uint> meta, int cacheBits)
    {
        int count = 1;
        int kept = 1;
        bool[]? used = null;
        if (!meta.IsEmpty)
        {
            int largest = 0;
            for (int i = 0; i < meta.Length; i++)
            {
                int group = (int)((meta[i] >> 8) & 0xFFFF);
                meta[i] = (uint)group;
                largest = Math.Max(largest, group);
            }

            count = largest + 1;
            used = new bool[count];
            foreach (uint group in meta)
            {
                used[group] = true;
            }

            kept = 0;
            foreach (bool use in used)
            {
                kept += use ? 1 : 0;
            }
        }

        int green = 256 + LengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0);
        ReadOnlySpan<int> sizes = [green, 256, 256, 256, DistanceCodes];
        CodeGroups groups = new(count);
        byte[] lengths = ArrayPool<byte>.Shared.Rent(green);
        uint[] scratch = ArrayPool<uint>.Shared.Rent(1 << 15);
        try
        {
            List<uint> tables = new(kept * 5 * 256);
            for (int g = 0; g < count; g++)
            {
                bool keep = used is null || used[g];
                for (int c = 0; c < 5; c++)
                {
                    Span<byte> code = lengths.AsSpan(0, sizes[c]);
                    ReadCodeLengths(code);
                    int size = Vp8lHuffman.Size(code);
                    if (size < 0)
                    {
                        throw Corrupt("A prefix code is incomplete or over-subscribed.");
                    }

                    if (!keep)
                    {
                        continue;
                    }

                    Span<uint> table = size <= scratch.Length ? scratch.AsSpan(0, size) : new uint[size];
                    Vp8lHuffman.Build(code, table);
                    groups.Offsets[(g * 5) + c] = tables.Count;
                    if (c is >= 1 and <= 3)
                    {
                        // Red, blue and alpha codes of one symbol take no bits: the literal path can use the symbol directly.
                        groups.Single[(g * 5) + c] = Vp8lPrefixCode.IsSingle(code) ? FirstSymbol(code) : -1;
                    }

                    foreach (uint entry in table)
                    {
                        tables.Add(entry);
                    }
                }
            }

            _input.CheckMemory((long)tables.Count * 4);
            groups.Tables = [.. tables];
            return groups;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lengths);
            ArrayPool<uint>.Shared.Return(scratch);
        }
    }

    private static int FirstSymbol(ReadOnlySpan<byte> lengths)
    {
        for (int s = 0; s < lengths.Length; s++)
        {
            if (lengths[s] != 0)
            {
                return s;
            }
        }

        return 0;
    }

    /// <summary>Reads one prefix code's lengths (section 3.7.2.1): the simple form for one or two symbols, or the normal form.</summary>
    private void ReadCodeLengths(Span<byte> lengths)
    {
        lengths.Clear();
        if (_reader.Read(1) != 0)
        {
            int symbols = (int)_reader.Read(1) + 1;
            int first = (int)_reader.Read(_reader.Read(1) != 0 ? 8 : 1);
            if (first >= lengths.Length)
            {
                throw Corrupt("A simple prefix code names a symbol outside its alphabet.");
            }

            lengths[first] = 1;
            if (symbols == 2)
            {
                int second = (int)_reader.Read(8);
                if (second >= lengths.Length)
                {
                    throw Corrupt("A simple prefix code names a symbol outside its alphabet.");
                }

                lengths[second] = 1;
            }

            return;
        }

        Span<byte> codeLengths = stackalloc byte[Vp8lPrefixCode.CodeLengthSymbols];
        codeLengths.Clear();
        int count = (int)_reader.Read(4) + 4;
        for (int i = 0; i < count; i++)
        {
            codeLengths[Vp8lPrefixCode.CodeLengthOrder[i]] = (byte)_reader.Read(3);
        }

        int size = Vp8lHuffman.Size(codeLengths);
        if (size < 0)
        {
            throw Corrupt("The code-length code is incomplete or over-subscribed.");
        }

        Span<uint> table = stackalloc uint[size];
        Vp8lHuffman.Build(codeLengths, table);
        int maxSymbol = lengths.Length;
        if (_reader.Read(1) != 0)
        {
            int bits = 2 + (2 * (int)_reader.Read(3));
            maxSymbol = 2 + (int)_reader.Read(bits);
            if (maxSymbol > lengths.Length)
            {
                throw Corrupt("A prefix code has more code lengths than its alphabet.");
            }
        }

        int symbol = 0;
        int previous = 8;
        while (symbol < lengths.Length)
        {
            if (maxSymbol-- == 0)
            {
                break;
            }

            _reader.Fill();
            (ulong bits, int available) = _reader.Save();
            int code = Vp8lHuffman.Decode(table, ref bits, ref available);
            _reader.Restore(bits, available);
            if (code < 16)
            {
                lengths[symbol++] = (byte)code;
                if (code != 0)
                {
                    previous = code;
                }

                continue;
            }

            int repeat = code switch
            {
                16 => 3 + (int)_reader.Read(2),
                17 => 3 + (int)_reader.Read(3),
                _ => 11 + (int)_reader.Read(7),
            };
            if (symbol + repeat > lengths.Length)
            {
                throw Corrupt("A prefix code's repeated lengths run past its alphabet.");
            }

            lengths.Slice(symbol, repeat).Fill(code == 16 ? (byte)previous : (byte)0);
            symbol += repeat;
        }
    }

    /// <summary>
    /// Decodes coded pixels <paramref name="from"/> up to at least <paramref name="to"/> into <paramref name="pixels"/>, whose first
    /// element is pixel <paramref name="baseIndex"/>; a backward reference may run past <paramref name="to"/>, never past
    /// <paramref name="total"/>. Returns where decoding stopped. Damage throws <see cref="CorruptDataException"/>.
    /// </summary>
    private long DecodePixels(
        Span<uint> pixels, long baseIndex, long from, long to, long total, int width, CodeGroups groups, uint[]? cache, int cacheBits,
        ReadOnlySpan<uint> meta, int metaBits, int metaWidth, long capacity)
    {
        ReadOnlySpan<uint> tables = groups.Tables;
        ReadOnlySpan<int> offsets = groups.Offsets;
        ReadOnlySpan<int> singles = groups.Single;
        int mask = metaBits == 0 ? -1 : (1 << metaBits) - 1;
        int cacheShift = 32 - cacheBits;
        long pos = from;
        int x = (int)(pos % width);
        int y = (int)(pos / width);
        int group = 0;
        if (metaBits > 0)
        {
            group = (int)meta[((y >> metaBits) * metaWidth) + (x >> metaBits)] * 5;
        }

        (ulong bits, int count) = _reader.Save();
        try
        {
            while (pos < to)
            {
                if ((x & mask) == 0 && metaBits > 0)
                {
                    group = (int)meta[((y >> metaBits) * metaWidth) + (x >> metaBits)] * 5;
                }

                _reader.Fill(ref bits, ref count);
                int green = Vp8lHuffman.Decode(tables[offsets[group]..], ref bits, ref count);
                if (green < 256)
                {
                    int red = singles[group + 1];
                    int blue = singles[group + 2];
                    int alpha = singles[group + 3];
                    if (red < 0 || blue < 0 || alpha < 0)
                    {
                        _reader.Fill(ref bits, ref count);
                        if (red < 0)
                        {
                            red = Vp8lHuffman.Decode(tables[offsets[group + 1]..], ref bits, ref count);
                        }

                        if (blue < 0)
                        {
                            blue = Vp8lHuffman.Decode(tables[offsets[group + 2]..], ref bits, ref count);
                        }

                        _reader.Fill(ref bits, ref count);
                        if (alpha < 0)
                        {
                            alpha = Vp8lHuffman.Decode(tables[offsets[group + 3]..], ref bits, ref count);
                        }
                    }

                    uint argb = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
                    pixels[(int)(pos - baseIndex)] = argb;
                    if (cache is not null)
                    {
                        cache[(int)((argb * 0x1E35A7BDu) >> cacheShift)] = argb;
                    }

                    pos++;
                    if (++x == width)
                    {
                        x = 0;
                        y++;
                    }

                    continue;
                }

                if (green < 256 + LengthCodes)
                {
                    _reader.Restore(bits, count);
                    int length = CopyValue(green - 256);
                    _reader.Fill();
                    (bits, count) = _reader.Save();
                    int distanceSymbol = Vp8lHuffman.Decode(tables[offsets[group + 4]..], ref bits, ref count);
                    _reader.Restore(bits, count);
                    int distance = PlaneToDistance(width, CopyValue(distanceSymbol));
                    (bits, count) = _reader.Save();
                    if (distance > pos)
                    {
                        throw Corrupt("A backward reference reaches before the first pixel.");
                    }

                    if (pos + length > total)
                    {
                        throw Corrupt("A backward reference runs past the last pixel.");
                    }

                    if (pos + length - baseIndex > capacity || pos - distance < baseIndex)
                    {
                        throw Corrupt("A backward reference reaches outside the decoding window.");
                    }

                    int at = (int)(pos - baseIndex);
                    Span<uint> target = pixels.Slice(at, length);
                    if (distance >= length)
                    {
                        pixels.Slice(at - distance, length).CopyTo(target);
                    }
                    else
                    {
                        // Overlapping: the run repeats the last `distance` pixels.
                        for (int i = 0; i < length; i++)
                        {
                            pixels[at + i] = pixels[at + i - distance];
                        }
                    }

                    if (cache is not null)
                    {
                        foreach (uint argb in target)
                        {
                            cache[(int)((argb * 0x1E35A7BDu) >> cacheShift)] = argb;
                        }
                    }

                    pos += length;
                    x += length;
                    while (x >= width)
                    {
                        x -= width;
                        y++;
                    }

                    if (metaBits > 0 && pos < total)
                    {
                        group = (int)meta[((y >> metaBits) * metaWidth) + (x >> metaBits)] * 5;
                    }

                    continue;
                }

                // A color cache index: the alphabet only has as many symbols as the cache has slots.
                uint cached = cache![green - 256 - LengthCodes];
                pixels[(int)(pos - baseIndex)] = cached;
                pos++;
                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
        }
        finally
        {
            _reader.Restore(bits, count);
        }

        return pos;
    }

    /// <summary>Returns a length or distance from its prefix symbol and extra bits (section 5.2.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CopyValue(int symbol)
    {
        if (symbol < 4)
        {
            return symbol + 1;
        }

        int extraBits = (symbol - 2) >> 1;
        int offset = (2 + (symbol & 1)) << extraBits;
        return offset + (int)_reader.Read(extraBits) + 1;
    }

    /// <summary>Maps a distance code to a distance in pixels: codes 1 to 120 are nearby (x, y) offsets, larger codes are the distance plus 120.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int PlaneToDistance(int width, int code)
    {
        if (code > 120)
        {
            return code - 120;
        }

        int dx = PlaneX[code - 1];
        int dy = PlaneY[code - 1];
        return Math.Max(1, dx + (dy * width));
    }

    /// <summary>Gets the x offsets of the 120 plane codes (RFC 9649 section 5.2.2): how many pixels to the left.</summary>
    internal static ReadOnlySpan<sbyte> PlaneX =>
    [
        0, 1, 1, -1, 0, 2, 1, -1, 2, -2, 2, -2, 0, 3, 1, -1, 3, -3, 2, -2, 3, -3, 0, 4, 1, -1, 4, -4, 3, -3, 2, -2, 4, -4, 0, 3, -3, 4,
        -4, 5, 1, -1, 5, -5, 2, -2, 5, -5, 4, -4, 3, -3, 5, -5, 0, 6, 1, -1, 6, -6, 2, -2, 6, -6, 4, -4, 5, -5, 3, -3, 6, -6, 0, 7, 1, -1,
        5, -5, 7, -7, 4, -4, 6, -6, 2, -2, 7, -7, 3, -3, 7, -7, 5, -5, 6, -6, 8, 4, -4, 7, -7, 8, 8, 6, -6, 8, 5, -5, 7, -7, 8, 6, -6, 7,
        -7, 8, 7, -7, 8, 8,
    ];

    /// <summary>Gets the y offsets of the 120 plane codes: how many rows up.</summary>
    internal static ReadOnlySpan<sbyte> PlaneY =>
    [
        1, 0, 1, 1, 2, 0, 2, 2, 1, 1, 2, 2, 3, 0, 3, 3, 1, 1, 3, 3, 2, 2, 4, 0, 4, 4, 1, 1, 3, 3, 4, 4, 2, 2, 5, 4, 4, 3, 3, 0, 5, 5, 1,
        1, 5, 5, 2, 2, 4, 4, 5, 5, 3, 3, 6, 0, 6, 6, 1, 1, 6, 6, 2, 2, 5, 5, 4, 4, 6, 6, 3, 3, 7, 0, 7, 7, 5, 5, 1, 1, 6, 6, 4, 4, 7, 7,
        2, 2, 7, 7, 3, 3, 6, 6, 5, 5, 0, 7, 7, 4, 4, 1, 2, 6, 6, 3, 7, 7, 5, 5, 4, 7, 7, 6, 6, 5, 7, 7, 6, 7,
    ];

    /// <summary>Undoes the transforms on one coded row, last read first, leaving the image's ARGB row.</summary>
    private void Untransform(Span<uint> row, int y)
    {
        for (int i = _transformCount - 1; i >= 0; i--)
        {
            ref Transform t = ref _transforms[i];
            switch (t.Type)
            {
                case Predictor:
                    UndoPredictor(row[..t.XSize], y, ref t);
                    break;
                case CrossColor:
                    UndoCrossColor(row[..t.XSize], y, ref t);
                    break;
                case SubtractGreen:
                    UndoSubtractGreen(row[..t.XSize]);
                    break;
                case ColorIndexing:
                    UndoColorIndexing(row, ref t);
                    break;
            }
        }
    }

    private void UndoPredictor(Span<uint> row, int y, ref Transform t)
    {
        Span<uint> above = _previous.AsSpan(0, row.Length);
        if (y == 0)
        {
            row[0] = AddPixels(row[0], 0xFF000000);
            for (int x = 1; x < row.Length; x++)
            {
                row[x] = AddPixels(row[x], row[x - 1]);
            }
        }
        else
        {
            ReadOnlySpan<uint> modes = MemoryMarshal.Cast<byte, uint>(t.Data.Span).Slice((y >> t.Bits) * SubSampleSize(t.XSize, t.Bits));
            row[0] = AddPixels(row[0], above[0]);
            int blockWidth = 1 << t.Bits;
            for (int x = 1; x < row.Length;)
            {
                int end = Math.Min(row.Length, ((x >> t.Bits) + 1) * blockWidth);
                int mode = (int)((modes[x >> t.Bits] >> 8) & 0xF);
                Vp8lPredict.UndoRun(mode, row, above, x, end);
                x = end;
            }
        }

        row.CopyTo(above);
    }

    private static void UndoCrossColor(Span<uint> row, int y, ref Transform t)
    {
        ReadOnlySpan<uint> elements = MemoryMarshal.Cast<byte, uint>(t.Data.Span).Slice((y >> t.Bits) * SubSampleSize(t.XSize, t.Bits));
        for (int x = 0; x < row.Length; x++)
        {
            uint e = elements[x >> t.Bits];
            sbyte greenToRed = (sbyte)e;
            sbyte greenToBlue = (sbyte)(e >> 8);
            sbyte redToBlue = (sbyte)(e >> 16);
            uint p = row[x];
            sbyte green = (sbyte)(p >> 8);
            int red = (int)(p >> 16) + ((greenToRed * green) >> 5);
            int blue = (int)p + ((greenToBlue * green) >> 5) + ((redToBlue * (sbyte)red) >> 5);
            row[x] = (p & 0xFF00FF00) | (((uint)red & 0xFF) << 16) | ((uint)blue & 0xFF);
        }
    }

    private static void UndoSubtractGreen(Span<uint> row)
    {
        for (int x = 0; x < row.Length; x++)
        {
            uint p = row[x];
            uint green = (p >> 8) & 0xFF;
            uint redBlue = (p & 0x00FF00FF) + ((green << 16) | green);
            row[x] = (p & 0xFF00FF00) | (redBlue & 0x00FF00FF);
        }
    }

    private void UndoColorIndexing(Span<uint> row, ref Transform t)
    {
        ReadOnlySpan<uint> palette = MemoryMarshal.Cast<byte, uint>(t.Data.Span)[..256];
        int colors = t.Colors;
        int width = t.XSize;
        if (t.Bits == 0)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (int)((row[x] >> 8) & 0xFF);
                row[x] = index < colors ? palette[index] : 0;
            }

            return;
        }

        // Bundled: the packed row is shorter, so expand from the end backwards in place... through the scratch row to be safe.
        int packedWidth = SubSampleSize(width, t.Bits);
        Span<uint> packed = _scratch.AsSpan(0, packedWidth);
        row[..packedWidth].CopyTo(packed);
        int bitsPerPixel = 8 >> t.Bits;
        int perByte = 1 << t.Bits;
        int indexMask = (1 << bitsPerPixel) - 1;
        for (int x = 0; x < width; x++)
        {
            int value = (int)((packed[x >> t.Bits] >> 8) & 0xFF);
            int index = (value >> ((x & (perByte - 1)) * bitsPerPixel)) & indexMask;
            row[x] = index < colors ? palette[index] : 0;
        }
    }

    /// <summary>Adds two pixels channel by channel, each wrapping around.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint AddPixels(uint a, uint b)
    {
        uint alphaGreen = (a & 0xFF00FF00) + (b & 0xFF00FF00);
        uint redBlue = (a & 0x00FF00FF) + (b & 0x00FF00FF);
        return (alphaGreen & 0xFF00FF00) | (redBlue & 0x00FF00FF);
    }

    /// <summary>Returns ceil(size / 2^bits).</summary>
    internal static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    private Memory<byte> Rent(long pixels)
    {
        if (pixels * 4 > int.MaxValue)
        {
            throw new LimitExceededException("MaxTotalBytes", int.MaxValue, pixels * 4);
        }

        IMemoryOwner<byte> owner = _input.RentScratch((int)(pixels * 4));
        _owned.Add(owner);
        return owner.Memory[..(int)(pixels * 4)];
    }

    private CorruptDataException Corrupt(string problem) => _input.Corrupt(_input.Position, problem);

    /// <summary>One transform as read: its type, the coded width it applies to, and its data.</summary>
    private struct Transform
    {
        public int Type;
        public int XSize;
        public int Bits;
        public int Colors;
        public Memory<byte> Data;
    }

    /// <summary>The prefix code groups: per group and code (green, red, blue, alpha, distance) the table's offset, and the red, blue and alpha symbol when that code has one.</summary>
    private sealed class CodeGroups(int count)
    {
        public int[] Offsets { get; } = new int[count * 5];

        public int[] Single { get; } = new int[count * 5];

        public uint[] Tables { get; set; } = [];
    }
}

/// <summary>Undoing the predictor transform for a run of pixels that share a mode.</summary>
internal static class Vp8lPredict
{
    /// <summary>Adds mode <paramref name="mode"/>'s prediction to <paramref name="row"/>[x..end], in order (the left pixel is the one just reconstructed).</summary>
    internal static void UndoRun(int mode, Span<uint> row, ReadOnlySpan<uint> above, int x, int end)
    {
        // The top-right of the last pixel is the first pixel of the current row (which is already reconstructed), as the format says.
        switch (mode)
        {
            case 0:
                for (; x < end; x++)
                {
                    row[x] = Vp8lDecoder.AddPixels(row[x], 0xFF000000);
                }

                break;
            case 1:
                for (; x < end; x++)
                {
                    row[x] = Vp8lDecoder.AddPixels(row[x], row[x - 1]);
                }

                break;
            case 2:
                for (; x < end; x++)
                {
                    row[x] = Vp8lDecoder.AddPixels(row[x], above[x]);
                }

                break;
            default:
                for (; x < end; x++)
                {
                    uint tr = x + 1 < row.Length ? above[x + 1] : row[0];
                    row[x] = Vp8lDecoder.AddPixels(row[x], Vp8lPredictors.Predict(mode, row[x - 1], above[x], above[x - 1], tr));
                }

                break;
        }
    }
}
