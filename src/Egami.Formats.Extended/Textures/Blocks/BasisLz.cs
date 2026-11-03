using System.Buffers.Binary;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>Where one image's ETC1S slices lie in its mip level, from the BasisLZ global data's image descriptors.</summary>
internal readonly record struct BasisImage(bool IsPFrame, uint RgbOffset, uint RgbLength, uint AlphaOffset, uint AlphaLength);

/// <summary>
/// Decodes BasisLZ, the supercompression of ETC1S blocks in KTX 2, written from the KTX 2.0 Specification (Appendix C, the
/// global data, and Appendix D, the bitstream). The global data holds the endpoint and selector codebooks, the four Huffman
/// tables of the slices and one descriptor per image; each slice names an endpoint and a selector of the codebooks per block.
/// Slices decode to ETC1 blocks: an ETC1S block is a differential ETC1 block whose two halves share one colour and table.
/// </summary>
internal sealed class BasisLz
{
    /// <summary>The bytes of the global data's header and of one image descriptor.</summary>
    private const int HeaderBytes = 20;
    private const int ImageBytes = 20;

    private const int PredRepeatSymbol = 256;
    private const int PredMinRepeat = 3;
    private const int RleCountThreshold = 3;
    private const int RleCountTotal = 64;
    private const int MaxHistory = 64;

    private readonly uint[] _endpoints;
    private readonly uint[] _selectors;
    private readonly BasisHuffman _endpointPred;
    private readonly BasisHuffman _endpointDelta;
    private readonly BasisHuffman _selector;
    private readonly BasisHuffman _selectorRle;
    private readonly int _historySize;

    private BasisLz(uint[] endpoints, uint[] selectors, BasisHuffman[] tables, int historySize, BasisImage[] images)
    {
        _endpoints = endpoints;
        _selectors = selectors;
        _endpointPred = tables[0];
        _endpointDelta = tables[1];
        _selector = tables[2];
        _selectorRle = tables[3];
        _historySize = historySize;
        Images = images;
    }

    /// <summary>Gets the image descriptors, ordered by mip level, then layer, face and depth slice.</summary>
    public BasisImage[] Images { get; }

    /// <summary>
    /// Reads the global data of a texture with <paramref name="imageCount"/> images; throws <see cref="InvalidDataException"/>
    /// when it is malformed.
    /// </summary>
    public static BasisLz Read(ReadOnlySpan<byte> data, long imageCount)
    {
        if (data.Length < HeaderBytes)
        {
            throw new InvalidDataException("The BasisLZ global data is too short for its header.");
        }

        int endpointCount = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int selectorCount = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        uint endpointBytes = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        uint selectorBytes = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        uint tableBytes = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        uint extendedBytes = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        long total = HeaderBytes + (imageCount * ImageBytes) + endpointBytes + selectorBytes + tableBytes + extendedBytes;
        if (total != data.Length)
        {
            throw new InvalidDataException($"The BasisLZ global data is {data.Length} bytes; its header and {imageCount} images give {total}.");
        }

        if (endpointCount == 0 || selectorCount == 0)
        {
            throw new InvalidDataException("The BasisLZ codebooks are empty.");
        }

        BasisImage[] images = new BasisImage[imageCount];
        for (int i = 0; i < images.Length; i++)
        {
            ReadOnlySpan<byte> d = data[(HeaderBytes + (i * ImageBytes))..];
            images[i] = new BasisImage(
                (BinaryPrimitives.ReadUInt32LittleEndian(d) & 2) != 0,
                BinaryPrimitives.ReadUInt32LittleEndian(d[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(d[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(d[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(d[16..]));
        }

        int at = HeaderBytes + (images.Length * ImageBytes);
        uint[] endpoints = Endpoints(data.Slice(at, (int)endpointBytes), endpointCount);
        at += (int)endpointBytes;
        uint[] selectors = Selectors(data.Slice(at, (int)selectorBytes), selectorCount);
        at += (int)selectorBytes;
        BasisBits bits = new(data.Slice(at, (int)tableBytes));
        BasisHuffman[] tables = [BasisHuffman.Read(ref bits), BasisHuffman.Read(ref bits), BasisHuffman.Read(ref bits), BasisHuffman.Read(ref bits)];
        int history = (int)bits.Read(13);
        if (history > MaxHistory)
        {
            throw new InvalidDataException($"The selector history buffer holds {history} entries; at most {MaxHistory} are allowed.");
        }

        return new BasisLz(endpoints, selectors, tables, history, images);
    }

    /// <summary>
    /// Decodes the endpoint codebook (Appendix D.2) into the top 32 bits of a differential ETC1 block: the colour with zero
    /// deltas, the intensity table in both halves and the differential bit.
    /// </summary>
    private static uint[] Endpoints(ReadOnlySpan<byte> data, int count)
    {
        BasisBits bits = new(data);
        BasisHuffman[] color = [BasisHuffman.Read(ref bits), BasisHuffman.Read(ref bits), BasisHuffman.Read(ref bits)];
        BasisHuffman inten = BasisHuffman.Read(ref bits);
        bool grayscale = bits.Read(1) != 0;
        uint[] endpoints = new uint[count];
        int prevInten = 0;
        Span<int> prev = [16, 16, 16];
        for (int i = 0; i < count; i++)
        {
            prevInten = (prevInten + inten.Decode(ref bits)) & 7;
            for (int c = 0; c < (grayscale ? 1 : 3); c++)
            {
                int p = prev[c];
                BasisHuffman model = color[p <= 9 ? 0 : p <= 21 ? 1 : 2];
                prev[c] = (p + model.Decode(ref bits)) & 31;
            }

            if (grayscale)
            {
                prev[1] = prev[2] = prev[0];
            }

            endpoints[i] = Etc1High(prev[0], prev[1], prev[2], prevInten);
        }

        return endpoints;
    }

    /// <summary>Returns the top 32 bits of the differential ETC1 block of one ETC1S colour (5 bits a channel) and table.</summary>
    internal static uint Etc1High(int r, int g, int b, int inten) => ((uint)r << 27) | ((uint)g << 19) | ((uint)b << 11) | ((uint)inten << 5) | ((uint)inten << 2) | 2;

    /// <summary>Decodes the selector codebook (Appendix D.3) into the bottom 32 bits of an ETC1 block: its pixel index bits.</summary>
    private static uint[] Selectors(ReadOnlySpan<byte> data, int count)
    {
        BasisBits bits = new(data);
        if (bits.Read(2) != 0)
        {
            throw new InvalidDataException("The BasisLZ selector codebook's reserved bits are set.");
        }

        bool raw = bits.Read(1) != 0;
        BasisHuffman? delta = raw ? null : BasisHuffman.Read(ref bits);
        uint[] selectors = new uint[count];
        uint rows = 0;
        for (int i = 0; i < count; i++)
        {
            uint next = 0;
            for (int j = 0; j < 4; j++)
            {
                uint row = raw || i == 0 ? bits.Read(8) : (uint)delta!.Decode(ref bits) ^ ((rows >> (j * 8)) & 0xFF);
                next |= (row & 0xFF) << (j * 8);
            }

            rows = next;
            selectors[i] = Etc1Low(rows);
        }

        return selectors;
    }

    /// <summary>
    /// Returns the pixel index bits of an ETC1 block from four rows of ETC1S selectors (the left texel in each byte's low bits):
    /// selector 0 is −b, 1 −a, 2 +a and 3 +b, which ETC1 numbers 3, 2, 0 and 1.
    /// </summary>
    internal static uint Etc1Low(uint rows)
    {
        uint low = 0;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                uint s = (rows >> ((y * 8) + (x * 2))) & 3;
                int k = (x * 4) + y;
                low |= (s < 2 ? 1u : 0) << (16 + k);
                low |= (s is 0 or 3 ? 1u : 0) << k;
            }
        }

        return low;
    }

    /// <summary>
    /// Decodes one slice (Appendix D.5) of <paramref name="blocksX"/> × <paramref name="blocksY"/> blocks into ETC1 blocks,
    /// <paramref name="stride"/> bytes apart in <paramref name="output"/>. A texture video passes <paramref name="previous"/>,
    /// the endpoint and selector of each block of the previous frame (selector in the top 16 bits), which a P-frame reuses
    /// and every frame updates.
    /// </summary>
    public void DecodeSlice(ReadOnlySpan<byte> slice, int blocksX, int blocksY, bool pFrame, uint[]? previous, Span<byte> output, int stride)
    {
        BasisBits bits = new(slice);
        int endpointCount = _endpoints.Length;
        int selectorCount = _selectors.Length;
        int rleSymbol = selectorCount + _historySize;
        Span<ushort> preds = blocksX <= 1024 ? stackalloc ushort[blocksX * 2] : new ushort[blocksX * 2];
        Span<byte> predBits = blocksX <= 2048 ? stackalloc byte[(blocksX + 1) >> 1] : new byte[(blocksX + 1) >> 1];
        Span<int> history = stackalloc int[Math.Max(_historySize, 1)];
        history.Clear();
        int rover = _historySize / 2;
        int rleCount = 0;
        int curPredBits = 0;
        int prevPredSymbol = 0;
        int predRepeat = 0;
        int prevEndpoint = 0;
        for (int by = 0; by < blocksY; by++)
        {
            int row = by & 1;
            for (int bx = 0; bx < blocksX; bx++)
            {
                if ((bx & 1) == 0)
                {
                    if (row == 0)
                    {
                        if (predRepeat > 0)
                        {
                            predRepeat--;
                            curPredBits = prevPredSymbol;
                        }
                        else
                        {
                            curPredBits = _endpointPred.Decode(ref bits);
                            if (curPredBits == PredRepeatSymbol)
                            {
                                predRepeat = (int)Math.Min(bits.Vlc(4) + PredMinRepeat - 1, int.MaxValue);
                                curPredBits = prevPredSymbol;
                            }
                            else if (curPredBits > PredRepeatSymbol)
                            {
                                throw new InvalidDataException("An ETC1S endpoint prediction symbol is out of range.");
                            }
                            else
                            {
                                prevPredSymbol = curPredBits;
                            }
                        }

                        predBits[bx >> 1] = (byte)(curPredBits >> 4);
                    }
                    else
                    {
                        curPredBits = predBits[bx >> 1];
                    }
                }

                int pred = curPredBits & 3;
                curPredBits >>= 2;
                int endpoint;
                int selector = 0;
                bool reuse = false;
                switch (pred)
                {
                    case 0:
                        if (bx == 0)
                        {
                            throw new InvalidDataException("An ETC1S block in the first column predicts from the left.");
                        }

                        endpoint = prevEndpoint;
                        break;
                    case 1:
                        if (by == 0)
                        {
                            throw new InvalidDataException("An ETC1S block in the first row predicts from above.");
                        }

                        endpoint = preds[((row ^ 1) * blocksX) + bx];
                        break;
                    case 2:
                        if (pFrame)
                        {
                            uint both = previous![(by * blocksX) + bx];
                            endpoint = (int)(both & 0xFFFF);
                            selector = (int)(both >> 16);
                            reuse = true;
                        }
                        else
                        {
                            if (bx == 0 || by == 0)
                            {
                                throw new InvalidDataException("An ETC1S block on the top or left edge predicts from the upper left.");
                            }

                            endpoint = preds[((row ^ 1) * blocksX) + bx - 1];
                        }

                        break;
                    default:
                        endpoint = _endpointDelta.Decode(ref bits) + prevEndpoint;
                        if (endpoint >= endpointCount)
                        {
                            endpoint -= endpointCount;
                        }

                        break;
                }

                if ((uint)endpoint >= (uint)endpointCount)
                {
                    throw new InvalidDataException("An ETC1S block's endpoint is outside the codebook.");
                }

                preds[(row * blocksX) + bx] = (ushort)endpoint;
                prevEndpoint = endpoint;
                if (!reuse)
                {
                    int symbol;
                    if (rleCount > 0)
                    {
                        rleCount--;
                        symbol = selectorCount;
                    }
                    else
                    {
                        symbol = _selector.Decode(ref bits);
                        if (symbol == rleSymbol)
                        {
                            int run = _selectorRle.Decode(ref bits);
                            long count = run == RleCountTotal - 1 ? bits.Vlc(7) + RleCountThreshold : run + RleCountThreshold;
                            if (count > (long)blocksX * blocksY)
                            {
                                throw new InvalidDataException("An ETC1S selector run is longer than the slice.");
                            }

                            rleCount = (int)count - 1;
                            symbol = selectorCount;
                        }
                    }

                    if (symbol >= selectorCount)
                    {
                        int index = symbol - selectorCount;
                        if (index >= _historySize)
                        {
                            throw new InvalidDataException("An ETC1S selector refers past the history buffer.");
                        }

                        selector = history[index];
                        if (index != 0)
                        {
                            (history[index / 2], history[index]) = (history[index], history[index / 2]);
                        }
                    }
                    else
                    {
                        selector = symbol;
                        if (_historySize > 0)
                        {
                            history[rover++] = selector;
                            if (rover == _historySize)
                            {
                                rover = _historySize / 2;
                            }
                        }
                    }
                }

                if ((uint)selector >= (uint)selectorCount)
                {
                    throw new InvalidDataException("An ETC1S block's selector is outside the codebook.");
                }

                if (previous is not null)
                {
                    previous[(by * blocksX) + bx] = (uint)endpoint | ((uint)selector << 16);
                }

                ulong block = ((ulong)_endpoints[endpoint] << 32) | _selectors[selector];
                BinaryPrimitives.WriteUInt64BigEndian(output.Slice(((by * blocksX) + bx) * stride, 8), block);
            }
        }
    }
}

/// <summary>Reads BasisLZ's bits, least significant first; past the end it reads zeros.</summary>
internal ref struct BasisBits(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _next;
    private ulong _buffer;
    private int _count;

    /// <summary>Returns the next <paramref name="n"/> bits (at most 32) without consuming them.</summary>
    public uint Peek(int n)
    {
        Fill();
        return (uint)(_buffer & ((1UL << n) - 1));
    }

    /// <summary>Consumes <paramref name="n"/> bits.</summary>
    public void Skip(int n)
    {
        _buffer >>= n;
        _count -= n;
    }

    /// <summary>Reads <paramref name="n"/> bits, at most 32.</summary>
    public uint Read(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        uint value = Peek(n);
        Skip(n);
        return value;
    }

    /// <summary>Reads a variable-length value in chunks of <paramref name="bits"/> + 1 bits, the top bit of each saying another follows.</summary>
    public long Vlc(int bits)
    {
        long value = 0;
        for (int shift = 0; shift < 32; shift += bits)
        {
            uint chunk = Read(bits + 1);
            value |= (long)(chunk & ((1u << bits) - 1)) << shift;
            if ((chunk >> bits) == 0)
            {
                break;
            }
        }

        return value;
    }

    private void Fill()
    {
        while (_count <= 56)
        {
            ulong b = _next < _data.Length ? _data[_next] : 0UL;
            _next++;
            _buffer |= b << _count;
            _count += 8;
        }
    }
}

/// <summary>A canonical Huffman table of BasisLZ (Appendix D.1), coded as Deflate codes are, with bits read least significant first.</summary>
internal sealed class BasisHuffman
{
    private const int FastBits = 10;
    private const int MaxCodeSize = 16;

    /// <summary>The order the code length code sizes are sent in.</summary>
    private static ReadOnlySpan<byte> CodeLengthOrder => [17, 18, 19, 20, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15, 16];

    private readonly int[] _fast;
    private readonly ushort[] _counts;
    private readonly ushort[] _symbols;
    private readonly int _single = -1;

    private BasisHuffman(ReadOnlySpan<byte> sizes)
    {
        _fast = new int[1 << FastBits];
        _counts = new ushort[MaxCodeSize + 1];
        int used = 0;
        foreach (byte size in sizes)
        {
            if (size > MaxCodeSize)
            {
                throw new InvalidDataException("A BasisLZ Huffman code is longer than 16 bits.");
            }

            _counts[size]++;
            used += size > 0 ? 1 : 0;
        }

        _counts[0] = 0;
        _symbols = new ushort[used];
        if (used == 0)
        {
            return;
        }

        // A complete prefix code, or a single symbol, which then decodes from any bits.
        long left = 1;
        for (int len = 1; len <= MaxCodeSize; len++)
        {
            left = (left << 1) - _counts[len];
            if (left < 0)
            {
                throw new InvalidDataException("A BasisLZ Huffman table's code sizes over-subscribe the code space.");
            }
        }

        if (used == 1)
        {
            for (int s = 0; s < sizes.Length; s++)
            {
                if (sizes[s] != 0)
                {
                    _single = s;
                    Size = sizes[s];
                }
            }

            return;
        }

        if (left != 0)
        {
            throw new InvalidDataException("A BasisLZ Huffman table's code sizes leave the code incomplete.");
        }

        Span<int> offsets = stackalloc int[MaxCodeSize + 2];
        for (int len = 1; len <= MaxCodeSize; len++)
        {
            offsets[len + 1] = offsets[len] + _counts[len];
        }

        // Canonical codes as Deflate assigns them: shorter codes first, then by symbol.
        Span<int> next = stackalloc int[MaxCodeSize + 2];
        int code = 0;
        for (int len = 1; len <= MaxCodeSize; len++)
        {
            next[len] = code;
            code = (code + _counts[len]) << 1;
        }

        for (int s = 0; s < sizes.Length; s++)
        {
            int len = sizes[s];
            if (len == 0)
            {
                continue;
            }

            _symbols[offsets[len]++] = (ushort)s;
            int c = next[len]++;
            if (len <= FastBits)
            {
                int reversed = 0;
                for (int i = 0; i < len; i++)
                {
                    reversed = (reversed << 1) | ((c >> i) & 1);
                }

                for (int r = reversed; r < _fast.Length; r += 1 << len)
                {
                    _fast[r] = (len << 16) | s;
                }
            }
        }
    }

    /// <summary>Gets the code size of a single-symbol table.</summary>
    private int Size { get; }

    /// <summary>Reads a table (its code sizes, themselves run-length and Huffman coded) from the bits.</summary>
    public static BasisHuffman Read(ref BasisBits bits)
    {
        int total = (int)bits.Read(14);
        if (total == 0)
        {
            return new BasisHuffman([]);
        }

        int codes = (int)bits.Read(5);
        if (codes is < 1 or > 21)
        {
            throw new InvalidDataException("A BasisLZ Huffman table has a code length code count out of range.");
        }

        Span<byte> lengthSizes = stackalloc byte[21];
        lengthSizes.Clear();
        for (int i = 0; i < codes; i++)
        {
            lengthSizes[CodeLengthOrder[i]] = (byte)bits.Read(3);
        }

        BasisHuffman lengths = new(lengthSizes);
        byte[] sizes = new byte[total];
        int cur = 0;
        while (cur < total)
        {
            int c = lengths.Decode(ref bits);
            if (c <= 16)
            {
                sizes[cur++] = (byte)c;
            }
            else if (c is 17 or 18)
            {
                cur += c == 17 ? (int)bits.Read(3) + 3 : (int)bits.Read(7) + 11;
            }
            else
            {
                if (cur == 0 || sizes[cur - 1] == 0)
                {
                    throw new InvalidDataException("A BasisLZ Huffman table repeats a code size that is not there.");
                }

                int run = c == 19 ? (int)bits.Read(2) + 3 : (int)bits.Read(7) + 7;
                if (cur + run > total)
                {
                    break;
                }

                sizes.AsSpan(cur, run).Fill(sizes[cur - 1]);
                cur += run;
            }
        }

        if (cur != total)
        {
            throw new InvalidDataException("A BasisLZ Huffman table's code sizes do not add up to its symbols.");
        }

        return new BasisHuffman(sizes);
    }

    /// <summary>Decodes one symbol.</summary>
    public int Decode(ref BasisBits bits)
    {
        if (_single >= 0)
        {
            bits.Skip(Size);
            return _single;
        }

        if (_symbols.Length == 0)
        {
            throw new InvalidDataException("A BasisLZ symbol is read from an empty Huffman table.");
        }

        uint peek = bits.Peek(MaxCodeSize);
        int entry = _fast[peek & ((1 << FastBits) - 1)];
        if (entry != 0)
        {
            bits.Skip(entry >> 16);
            return entry & 0xFFFF;
        }

        // A longer code: walk the canonical code one bit at a time.
        int code = 0;
        int first = 0;
        int index = 0;
        for (int len = 1; len <= MaxCodeSize; len++)
        {
            code |= (int)((peek >> (len - 1)) & 1);
            int count = _counts[len];
            if (code - count < first)
            {
                bits.Skip(len);
                return _symbols[index + (code - first)];
            }

            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }

        throw new InvalidDataException("A BasisLZ Huffman code is not in its table.");
    }
}
