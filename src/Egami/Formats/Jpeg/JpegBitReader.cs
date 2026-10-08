using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Reads entropy-coded data after a scan header: a 64-bit buffer with the next bits at the top, refilled a byte at a time with
/// byte stuffing (FF 00) removed. A marker stops the refill; from then on the buffer is padded with zero bits, and
/// <see cref="PastEnd"/> says whether any padding was consumed, which means the data ended early.
/// </summary>
internal sealed class JpegBitReader : IDisposable
{
    private const int ChunkSize = 16 * 1024;

    private readonly EgamiInput _input;
    private byte[] _chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
    private int _position;
    private int _length;
    private ulong _bits;
    private int _count;
    private int _padding;

    internal JpegBitReader(EgamiInput input)
    {
        _input = input;
    }

    /// <summary>Gets the marker that stopped the data (the byte after 0xFF), or 0 while none has; <see cref="EndOfFile"/> at the end of the file.</summary>
    internal int Marker { get; private set; }

    /// <summary>Gets the number (0 to 7) of the last restart marker <see cref="Restart"/> resumed at.</summary>
    internal int LastRestart { get; private set; } = -1;

    /// <summary>The <see cref="Marker"/> value when the file ended inside the data.</summary>
    internal const int EndOfFile = -1;

    /// <summary>Gets whether decoding has used padding bits past the end of the real data.</summary>
    internal bool PastEnd => _padding > 0 && _count < _padding;

    /// <summary>Gets whether the data stopped at a restart marker (RST0 to RST7), so it can resume at the next interval.</summary>
    internal bool AtRestartMarker => Marker is >= 0xD0 and <= 0xD7;

    /// <summary>Makes at least 32 bits available.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Ensure32()
    {
        if (_count < 32)
        {
            Fill();
        }
    }

    /// <summary>Returns the next <paramref name="n"/> bits (1 to 32) without consuming them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Peek(int n)
    {
        return (uint)(_bits >> (64 - n));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Skip(int n)
    {
        _bits <<= n;
        _count -= n;
    }

    /// <summary>Reads <paramref name="n"/> bits (0 to 16) as an unsigned number.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Read(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        if (_count < n)
        {
            Fill();
        }

        int value = (int)Peek(n);
        Skip(n);
        return value;
    }

    /// <summary>Reads <paramref name="n"/> bits and extends them to a signed coefficient (T.81 figure F.12).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ReceiveExtend(int n)
    {
        int value = Read(n);
        return n == 0 || value >= (1 << (n - 1)) ? value : value - (1 << n) + 1;
    }

    /// <summary>
    /// Handles a restart interval boundary: drops the bits left, reads up to the next marker and, if it is a restart marker,
    /// starts the data again. Returns whether it was the expected RSTn; a lenient caller carries on after any restart marker.
    /// </summary>
    internal bool Restart(int expected)
    {
        _bits = 0;
        _count = 0;
        _padding = 0;
        FindMarker();
        if (!AtRestartMarker)
        {
            return false;
        }

        // Any restart marker resumes the data; a different number means intervals were lost, which the caller reports.
        LastRestart = Marker - 0xD0;
        bool inOrder = Marker == 0xD0 + (expected & 7);
        Marker = 0;
        return inOrder;
    }

    /// <summary>Copies the bit buffer out, for a hot loop to keep in registers; <see cref="Store"/> puts it back.</summary>
    internal void Load(out ulong bits, out int count)
    {
        bits = _bits;
        count = _count;
    }

    /// <summary>Puts back a bit buffer taken with <see cref="Load"/>.</summary>
    internal void Store(ulong bits, int count)
    {
        _bits = bits;
        _count = count;
    }

    /// <summary>
    /// Tops up a bit buffer held in registers to more than 32 bits. When the next 8 bytes hold no 0xFF (so no stuffing and no
    /// marker) they are added in one step; otherwise byte by byte.
    /// </summary>
    internal void Refill(ref ulong bits, ref int count)
    {
        if (Marker == 0 && _length - _position >= 8)
        {
            ulong raw = BinaryPrimitives.ReadUInt64BigEndian(_chunk.AsSpan(_position, 8));
            ulong inverted = ~raw;
            if (((inverted - 0x0101010101010101UL) & ~inverted & 0x8080808080808080UL) == 0)
            {
                // Whole bytes that fit below the bits held; the buffer's unused low bits are zero.
                int take = (64 - count) >> 3;
                ulong kept = take == 8 ? raw : raw & ~(ulong.MaxValue >> (take * 8));
                bits |= kept >> count;
                count += take * 8;
                _position += take;
                return;
            }
        }

        _bits = bits;
        _count = count;
        Fill();
        bits = _bits;
        count = _count;
    }

    /// <summary>Starts the entropy-coded data of a scan whose header has just been read.</summary>
    internal void BeginScan()
    {
        _bits = 0;
        _count = 0;
        _padding = 0;
        Marker = 0;
        LastRestart = -1;
    }

    /// <summary>
    /// Ends a scan: drops the bits left and reads on to the marker after its data, passing over restart markers and any bytes
    /// the scan did not use. Returns that marker, or <see cref="EndOfFile"/>.
    /// </summary>
    internal int EndScan()
    {
        _bits = 0;
        _count = 0;
        _padding = 0;
        while (true)
        {
            FindMarker();
            if (!AtRestartMarker)
            {
                return Marker;
            }

            Marker = 0;
        }
    }

    /// <summary>
    /// Reads the next marker between segments, skipping fill bytes. Returns it, or <see cref="EndOfFile"/>; <paramref name="junk"/>
    /// says whether other bytes came first, which a strict read reports.
    /// </summary>
    internal int NextMarker(out bool junk)
    {
        junk = false;
        Marker = 0;
        while (Marker == 0)
        {
            int b = NextByte();
            if (b < 0)
            {
                Marker = EndOfFile;
            }
            else if (b != 0xFF || ReadMarkerByte())
            {
                junk = true;
            }
        }

        return Marker;
    }

    /// <summary>Reads one byte of a segment between scans, or returns -1 at the end of the file.</summary>
    internal int ReadSegmentByte()
    {
        return NextByte();
    }

    public void Dispose()
    {
        byte[] chunk = Interlocked.Exchange(ref _chunk, null!);
        if (chunk is not null)
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Skips data bytes until a marker, unless one has stopped the data already.</summary>
    private void FindMarker()
    {
        while (Marker == 0)
        {
            int b = NextByte();
            if (b < 0)
            {
                Marker = EndOfFile;
                break;
            }

            if (b == 0xFF)
            {
                ReadMarkerByte();
            }
        }
    }

    /// <summary>Tops the buffer up to at least 57 bits, padding with zeros once a marker or the end of the file is reached.</summary>
    private void Fill()
    {
        while (_count <= 56)
        {
            int b;
            if (Marker != 0)
            {
                b = 0;
                _padding += 8;
            }
            else
            {
                b = NextByte();
                if (b < 0)
                {
                    Marker = EndOfFile;
                    continue;
                }

                if (b == 0xFF && !ReadMarkerByte())
                {
                    continue;
                }
            }

            _bits |= (ulong)b << (56 - _count);
            _count += 8;
        }
    }

    /// <summary>Reads what follows a 0xFF: a stuffed zero (returns true, the 0xFF is data) or a marker (returns false and records it).</summary>
    private bool ReadMarkerByte()
    {
        while (true)
        {
            int next = NextByte();
            if (next < 0)
            {
                Marker = EndOfFile;
                return false;
            }

            if (next == 0)
            {
                return true;
            }

            if (next != 0xFF)
            {
                Marker = next;
                return false;
            }
        }
    }

    private int NextByte()
    {
        if (_position == _length)
        {
            _length = _input.Read(_chunk.AsSpan(0, ChunkSize));
            _position = 0;
            if (_length == 0)
            {
                return -1;
            }
        }

        return _chunk[_position++];
    }
}

/// <summary>
/// A Huffman table ready for decoding (T.81 annex C and F.2.2.3): a 9-bit lookahead table that resolves most codes in one step,
/// and the canonical code ranges per length for the rest.
/// </summary>
internal sealed class JpegHuffmanTable
{
    private const int LookBits = 9;

    private readonly ushort[] _lookup = new ushort[1 << LookBits];
    private readonly int[] _maxCode = new int[18];
    private readonly int[] _valueOffset = new int[17];
    private readonly byte[] _symbols;

    /// <summary>Builds the table; <paramref name="fastAc"/> also builds <see cref="FastAc"/>, which only sequential AC tables use.</summary>
    internal JpegHuffmanTable(JpegHuffmanSpec spec, bool fastAc = false)
    {
        _symbols = spec.Symbols;
        int code = 0;
        int k = 0;
        for (int length = 1; length <= 16; length++)
        {
            int count = spec.Counts[length - 1];
            _valueOffset[length] = k - code;
            for (int i = 0; i < count; i++, k++, code++)
            {
                if (length <= LookBits)
                {
                    // Every 9-bit value that starts with this code resolves to it.
                    int first = code << (LookBits - length);
                    int span = 1 << (LookBits - length);
                    for (int j = 0; j < span; j++)
                    {
                        _lookup[first + j] = (ushort)((length << 8) | _symbols[k]);
                    }
                }
            }

            _maxCode[length] = count > 0 ? code - 1 : -1;
            if (code > (1 << length))
            {
                throw new CorruptDataException(JpegFormat.Info.Name, -1, "A Huffman table has more codes than its lengths allow.");
            }

            code <<= 1;
        }

        _maxCode[17] = int.MaxValue;

        // Where a code and its value bits fit in the lookahead together, the AC coefficient is decoded in one step.
        FastAc = fastAc ? new int[1 << LookBits] : [];
        for (int index = 0; index < FastAc.Length; index++)
        {
            int entry = _lookup[index];
            int length = entry >> 8;
            int size = entry & 15;
            if (entry != 0 && size != 0 && length + size <= LookBits)
            {
                int raw = (index >> (LookBits - length - size)) & ((1 << size) - 1);
                int value = raw < (1 << (size - 1)) ? raw - (1 << size) + 1 : raw;
                FastAc[index] = (value << 16) | ((entry & 0xF0) << 4) | (length + size);
            }
        }
    }

    /// <summary>
    /// Gets the one-step AC lookup by the next 9 bits: the coefficient value in the top 16 bits, the zero run in bits 8 to 11 and
    /// the bits used in the low byte, or 0 where the code or its value is longer.
    /// </summary>
    internal int[] FastAc { get; }

    /// <summary>Decodes one symbol. Codes longer than 16 bits (only in damaged data) decode as symbol 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Decode(JpegBitReader reader)
    {
        reader.Ensure32();
        int entry = _lookup[reader.Peek(LookBits)];
        if (entry != 0)
        {
            reader.Skip(entry >> 8);
            return entry & 0xFF;
        }

        return DecodeLong(reader);
    }

    /// <summary>Decodes one symbol from a bit buffer held in registers, which must hold at least 16 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Decode(ref ulong bits, ref int count)
    {
        int entry = _lookup[(int)(bits >> (64 - LookBits))];
        if (entry != 0)
        {
            int length = entry >> 8;
            bits <<= length;
            count -= length;
            return entry & 0xFF;
        }

        return DecodeLong(ref bits, ref count);
    }

    private int DecodeLong(ref ulong bits, ref int count)
    {
        for (int length = LookBits + 1; length <= 16; length++)
        {
            int code = (int)(bits >> (64 - length));
            if (code <= _maxCode[length])
            {
                bits <<= length;
                count -= length;
                int index = code + _valueOffset[length];
                return (uint)index < (uint)_symbols.Length ? _symbols[index] : 0;
            }
        }

        bits <<= 16;
        count -= 16;
        return 0;
    }

    private int DecodeLong(JpegBitReader reader)
    {
        for (int length = LookBits + 1; length <= 16; length++)
        {
            int code = (int)reader.Peek(length);
            if (code <= _maxCode[length])
            {
                reader.Skip(length);
                int index = code + _valueOffset[length];
                return (uint)index < (uint)_symbols.Length ? _symbols[index] : 0;
            }
        }

        reader.Skip(16);
        return 0;
    }
}
