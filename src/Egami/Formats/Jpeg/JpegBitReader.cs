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

    private readonly EgamiInput? _input;
    private readonly bool _pooled = true;
    private byte[] _chunk;
    private int _position;
    private int _length;
    private ulong _bits;
    private int _count;
    private int _padding;
    private int _mark = -1;
    private long _searched;

    internal JpegBitReader(EgamiInput input)
    {
        _input = input;
        _chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
    }

    /// <summary>
    /// Creates a reader over bytes already in memory, from <paramref name="start"/> to <paramref name="end"/>: the end of the
    /// bytes is the end of the file. The array stays the caller's.
    /// </summary>
    internal JpegBitReader(byte[] data, int start, int end)
    {
        _chunk = data;
        _position = start;
        _length = end;
        _pooled = false;
    }

    /// <summary>Gets the marker that stopped the data (the byte after 0xFF), or 0 while none has; <see cref="EndOfFile"/> at the end of the file.</summary>
    internal int Marker { get; private set; }

    /// <summary>Gets where the next byte comes from in the bytes of a reader over memory.</summary>
    internal int Offset => _position;

    /// <summary>Gets the number (0 to 7) of the last restart marker <see cref="Restart"/> resumed at.</summary>
    internal int LastRestart { get; private set; } = -1;

    /// <summary>The <see cref="Marker"/> value when the file ended inside the data.</summary>
    internal const int EndOfFile = -1;

    /// <summary>Gets whether decoding has used padding bits past the end of the real data.</summary>
    internal bool PastEnd => _padding > 0 && _count < _padding;

    /// <summary>Gets whether a bit buffer held outside, with <paramref name="count"/> bits left, has used padding past the data.</summary>
    internal bool PastEndWith(int count) => _padding > 0 && count < _padding;

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

    /// <summary>
    /// Returns the next byte of arithmetic-coded data (T.81 D.2.6, Byte_in): a stuffed FF 00 is the byte 0xFF, and once a marker
    /// or the end of the file stops the data every byte is 0, which the decoder reads past the end as T.81 requires.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ArithmeticByte()
    {
        if (Marker == 0 && _position < _length && _chunk[_position] != 0xFF)
        {
            return _chunk[_position++];
        }

        return ArithmeticByteSlow();
    }

    private int ArithmeticByteSlow()
    {
        if (Marker != 0)
        {
            return 0;
        }

        int b = NextByte();
        if (b < 0)
        {
            Marker = EndOfFile;
            return 0;
        }

        return b == 0xFF && !ReadMarkerByte() ? 0 : b;
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
        if (chunk is not null && _pooled)
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Skips data bytes until a marker, unless one has stopped the data already.</summary>
    private void FindMarker()
    {
        while (Marker == 0)
        {
            // Jump to the next 0xFF in the buffered bytes; only it can start a marker.
            int next = _chunk.AsSpan(_position, _length - _position).IndexOf((byte)0xFF);
            _position = next < 0 ? _length : _position + next;
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
            if (_input is null)
            {
                return -1;
            }

            if (_mark >= 0)
            {
                // The bytes since the checkpoint stay, so the decode can go back to it.
                int kept = _length - _mark;
                if (kept + ChunkSize > _chunk.Length)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(_chunk.Length * 2, kept + ChunkSize));
                    _chunk.AsSpan(_mark, kept).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(_chunk);
                    _chunk = larger;
                }
                else
                {
                    _chunk.AsSpan(_mark, kept).CopyTo(_chunk);
                }

                _position = kept;
                _length = kept;
                _mark = 0;
            }
            else
            {
                _position = 0;
                _length = 0;
            }

            int read = _input.Read(_chunk.AsSpan(_length, ChunkSize));
            _length += read;
            if (read == 0)
            {
                if (_input.Incremental && !_input.AtEndOfInput)
                {
                    throw new Streaming.NeedsInputException();
                }

                return -1;
            }
        }

        return _chunk[_position++];
    }

    /// <summary>Where the reader is, so an incremental decode that runs out of bytes can go back.</summary>
    internal readonly record struct Checkpoint(int Position, ulong Bits, int Count, int Padding, int Marker, int LastRestart);

    /// <summary>Marks the current place: the bytes from here on are kept until the next mark.</summary>
    internal Checkpoint Mark()
    {
        _mark = _position;
        return new Checkpoint(0, _bits, _count, _padding, Marker, LastRestart);
    }

    /// <summary>Goes back to the place <see cref="Mark"/> returned, whose bytes were kept.</summary>
    internal void Rewind(Checkpoint checkpoint)
    {
        _position = _mark + checkpoint.Position;
        _bits = checkpoint.Bits;
        _count = checkpoint.Count;
        _padding = checkpoint.Padding;
        Marker = checkpoint.Marker;
        LastRestart = checkpoint.LastRestart;
    }

    /// <summary>
    /// Returns whether the rest of the current scan has arrived: a marker other than a restart marker follows in the bytes
    /// received, so decoding the scan cannot run dry. Always true outside an incremental decode or once the input has ended.
    /// </summary>
    internal bool ScanArrived()
    {
        if (_input is null || !_input.Incremental || _input.AtEndOfInput)
        {
            return true;
        }

        // Bytes held here first, then those the input has not handed over yet; the search resumes where the last one stopped.
        int held = _length - _position;
        long total = held + _input.Available;
        for (long at = Math.Max(0, _searched - 1); at + 1 < total; at++)
        {
            byte first = at < held ? _chunk[_position + (int)at] : InputByte(at - held);
            if (first != 0xFF)
            {
                continue;
            }

            byte second = at + 1 < held ? _chunk[_position + (int)at + 1] : InputByte(at + 1 - held);
            if (second is not (0x00 or 0xFF) and not (>= 0xD0 and <= 0xD7))
            {
                _searched = 0;
                return true;
            }
        }

        _searched = Math.Max(0, total - 1);
        return false;
    }

    /// <summary>Returns a byte the input has not handed over yet, <paramref name="offset"/> bytes ahead.</summary>
    private byte InputByte(long offset)
    {
        Span<byte> one = stackalloc byte[1];
        _input!.TryPeekAt(offset, one);
        return one[0];
    }

    /// <summary>Forgets how far <see cref="ScanArrived"/> looked, at the start of a scan.</summary>
    internal void ResetSearch()
    {
        _searched = 0;
    }
}

/// <summary>
/// A Huffman table ready for decoding (T.81 annex C and F.2.2.3): a 9-bit lookahead table that resolves most codes in one step,
/// and the canonical code ranges per length for the rest.
/// </summary>
internal sealed class JpegHuffmanTable : IDisposable
{
    private const int LookBits = 9;

    // Pooled: a progressive file often defines new tables for every scan.
    private readonly ushort[] _lookup = ArrayPool<ushort>.Shared.Rent(1 << LookBits);
    private readonly int[] _maxCode = ArrayPool<int>.Shared.Rent(18);
    private readonly int[] _valueOffset = ArrayPool<int>.Shared.Rent(17);
    private readonly byte[] _symbols;
    private int[]? _fastAc;

    internal JpegHuffmanTable(JpegHuffmanSpec spec)
    {
        Array.Clear(_lookup, 0, 1 << LookBits);
        _symbols = spec.Symbols;
        int code = 0;
        int k = 0;
        for (int length = 1; length <= 16; length++)
        {
            int count = spec.Counts[length - 1];
            if (code + count > (1 << length))
            {
                // Checked before the codes are placed: an over-full length would write past the lookahead table.
                throw new CorruptDataException(JpegFormat.Info.Name, -1, "A Huffman table has more codes than its lengths allow.");
            }

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
            code <<= 1;
        }

        _maxCode[17] = int.MaxValue;
    }

    /// <summary>
    /// Gets the one-step AC lookup by the next 9 bits: the coefficient value in the top 16 bits, the zero run in bits 8 to 11 and
    /// the bits used in the low byte, or 0 where the code or its value is longer. Built on first use, since DC tables never need it.
    /// </summary>
    internal int[] FastAc => _fastAc ??= BuildFastAc();

    public void Dispose()
    {
        ArrayPool<ushort>.Shared.Return(_lookup);
        ArrayPool<int>.Shared.Return(_maxCode);
        ArrayPool<int>.Shared.Return(_valueOffset);
        if (_fastAc is not null)
        {
            ArrayPool<int>.Shared.Return(_fastAc);
            _fastAc = null;
        }
    }

    private int[] BuildFastAc()
    {
        // Where a code and its value bits fit in the lookahead together, the AC coefficient is decoded in one step.
        int[] fast = ArrayPool<int>.Shared.Rent(1 << LookBits);
        Array.Clear(fast, 0, 1 << LookBits);
        for (int index = 0; index < 1 << LookBits; index++)
        {
            int entry = _lookup[index];
            int length = entry >> 8;
            int size = entry & 15;
            if (entry != 0 && size != 0 && length + size <= LookBits)
            {
                int raw = (index >> (LookBits - length - size)) & ((1 << size) - 1);
                int value = raw < (1 << (size - 1)) ? raw - (1 << size) + 1 : raw;
                fast[index] = (value << 16) | ((entry & 0xF0) << 4) | (length + size);
            }
        }

        return fast;
    }

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
