using System.Runtime.CompilerServices;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// A lossless JPEG decoder, written from ITU-T T.81 Annex H (process 14: Huffman coding, predictors 1 to 7, point transform,
/// restart intervals), as RAW files use it for sensor data. Samples come out row by row, the components of each column next to
/// each other, which is how DNG tiles and CR2 slices lay them. Components may be subsampled (CR2's sRAW): each MCU then holds
/// H × V samples of the first component before one of each other.
/// </summary>
internal sealed class LosslessJpeg
{
    [ThreadStatic]
    private static LosslessJpeg? t_reused;

    private readonly HuffmanTable?[] _tables = new HuffmanTable?[4];
    private readonly bool[] _defined = new bool[4];
    private byte[] _data;
    private int _start;
    private int _end;
    private HuffmanTable[] _slotTables = [];
    private int[] _slotComponents = [];
    private int _scanAt;
    private int _restartInterval;
    private int _predictor;
    private int _pointTransform;
    private int[] _componentIds = [];
    private int[] _h = [];
    private int[] _v = [];
    private int[] _tableOf = [];

    private LosslessJpeg(byte[] data, int start, int end)
    {
        _data = data;
        _start = start;
        _end = end;
    }

    /// <summary>Gets the sample precision in bits.</summary>
    public int Precision { get; private set; }

    /// <summary>Gets the frame's width in MCUs' columns, as the SOF3 header says.</summary>
    public int Width { get; private set; }

    /// <summary>Gets the frame's height in lines, as the SOF3 header says.</summary>
    public int Height { get; private set; }

    /// <summary>Gets the number of components.</summary>
    public int Components { get; private set; }

    /// <summary>Gets the horizontal sampling factor of the first component (1 unless subsampled).</summary>
    public int H0 => _h.Length > 0 ? _h[0] : 1;

    /// <summary>Gets the vertical sampling factor of the first component (1 unless subsampled).</summary>
    public int V0 => _v.Length > 0 ? _v[0] : 1;

    /// <summary>Gets the number of samples one MCU holds.</summary>
    public int SamplesPerMcu { get; private set; }

    /// <summary>Gets the number of samples the whole frame holds.</summary>
    public long SampleCount => (long)McusPerLine * McuLines * SamplesPerMcu;

    /// <summary>Gets the number of MCUs on a line.</summary>
    public int McusPerLine => (Width + H0 - 1) / H0;

    /// <summary>Gets the number of MCU lines.</summary>
    public int McuLines => (Height + V0 - 1) / V0;

    /// <summary>Reads the markers up to the first scan. Throws <see cref="InvalidDataException"/> for data that is not lossless JPEG.</summary>
    public static LosslessJpeg Open(byte[] data, int start, int length)
    {
        LosslessJpeg jpeg = new(data, start, start + length);
        jpeg.ReadMarkers();
        return jpeg;
    }

    /// <summary>
    /// Like <see cref="Open"/>, but reuses this thread's decoder and its tables, so decoding thousands of tiles does not allocate
    /// for each. The decoder is valid until the thread opens the next one.
    /// </summary>
    public static LosslessJpeg OpenReused(byte[] data, int start, int length)
    {
        LosslessJpeg jpeg = t_reused ??= new LosslessJpeg([], 0, 0);
        jpeg._data = data;
        jpeg._start = start;
        jpeg._end = start + length;
        jpeg._restartInterval = 0;
        Array.Clear(jpeg._defined);
        jpeg.ReadMarkers();
        return jpeg;
    }

    private void ReadMarkers()
    {
        int p = _start;
        if (_end - p < 4 || _data[p] != 0xFF || _data[p + 1] != 0xD8)
        {
            throw new InvalidDataException("The lossless JPEG data does not start with SOI.");
        }

        p += 2;
        bool frame = false;
        while (p + 4 <= _end)
        {
            if (_data[p] != 0xFF)
            {
                p++;
                continue;
            }

            int marker = _data[p + 1];
            if (marker == 0xFF)
            {
                p++;
                continue;
            }

            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                p += 2;
                continue;
            }

            int length = (_data[p + 2] << 8) | _data[p + 3];
            int body = p + 4;
            int next = p + 2 + length;
            if (length < 2 || next > _end)
            {
                throw new InvalidDataException("A lossless JPEG marker segment runs past the data.");
            }

            switch (marker)
            {
                case 0xC3:
                case 0xC7:
                    ReadFrame(body, next);
                    frame = true;
                    break;
                case 0xC0 or 0xC1 or 0xC2 or 0xC5 or 0xC6 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    throw new InvalidDataException("The JPEG data is not lossless (SOF3).");
                case 0xC4:
                    ReadTables(body, next);
                    break;
                case 0xDD:
                    _restartInterval = next - body >= 2 ? (_data[body] << 8) | _data[body + 1] : 0;
                    break;
                case 0xDA:
                    if (!frame)
                    {
                        throw new InvalidDataException("The lossless JPEG scan comes before its frame header.");
                    }

                    ReadScan(body, next);
                    _scanAt = next;
                    return;
                case 0xD9:
                    throw new InvalidDataException("The lossless JPEG data ends before its scan.");
            }

            p = next;
        }

        throw new InvalidDataException("The lossless JPEG data has no scan.");
    }

    private void ReadFrame(int p, int end)
    {
        if (end - p < 6)
        {
            throw new InvalidDataException("The lossless JPEG frame header is cut short.");
        }

        Precision = _data[p];
        Height = (_data[p + 1] << 8) | _data[p + 2];
        Width = (_data[p + 3] << 8) | _data[p + 4];
        Components = _data[p + 5];
        if (Precision is < 2 or > 16 || Width == 0 || Height == 0 || Components is < 1 or > 4 || end - p < 6 + (Components * 3))
        {
            throw new InvalidDataException("The lossless JPEG frame header is not valid.");
        }

        if (_componentIds.Length != Components)
        {
            _componentIds = new int[Components];
            _h = new int[Components];
            _v = new int[Components];
            _tableOf = new int[Components];
        }
        SamplesPerMcu = 0;
        for (int c = 0; c < Components; c++)
        {
            int q = p + 6 + (c * 3);
            _componentIds[c] = _data[q];
            _h[c] = Math.Max(1, _data[q + 1] >> 4);
            _v[c] = Math.Max(1, _data[q + 1] & 15);
            if (_h[c] > 4 || _v[c] > 4 || (c > 0 && (_h[c] != 1 || _v[c] != 1)))
            {
                throw new InvalidDataException("The lossless JPEG uses a sampling Egami does not read.");
            }

            SamplesPerMcu += _h[c] * _v[c];
        }
    }

    private void ReadTables(int p, int end)
    {
        while (p + 17 <= end)
        {
            int id = _data[p] & 15;
            if (id > 3 || (_data[p] >> 4) != 0)
            {
                throw new InvalidDataException("The lossless JPEG has a Huffman table it cannot use.");
            }

            int total = 0;
            for (int i = 0; i < 16; i++)
            {
                total += _data[p + 1 + i];
            }

            if (total > 256 || p + 17 + total > end)
            {
                throw new InvalidDataException("A lossless JPEG Huffman table is cut short.");
            }

            (_tables[id] ??= new HuffmanTable()).Build(_data.AsSpan(p + 1, 16), _data.AsSpan(p + 17, total));
            _defined[id] = true;
            p += 17 + total;
        }
    }

    private void ReadScan(int p, int end)
    {
        int n = _data[p];
        if (n != Components || end - p < 4 + (n * 2))
        {
            throw new InvalidDataException("The lossless JPEG scan does not cover every component.");
        }

        for (int i = 0; i < n; i++)
        {
            int id = _data[p + 1 + (i * 2)];
            int table = _data[p + 2 + (i * 2)] >> 4;
            int c = Array.IndexOf(_componentIds, id);
            if (c < 0 || table > 3 || !_defined[table])
            {
                throw new InvalidDataException("The lossless JPEG scan names a component or table that is not there.");
            }

            _tableOf[c] = table;
        }

        int q = p + 1 + (n * 2);
        _predictor = _data[q];
        _pointTransform = _data[q + 2] & 15;
        if (_predictor is < 1 or > 7)
        {
            throw new InvalidDataException($"The lossless JPEG predictor {_predictor} is not one T.81 defines.");
        }
    }

    /// <summary>
    /// Decodes every sample into <paramref name="output"/>, MCU after MCU: <see cref="SampleCount"/> values. Data that ends
    /// early leaves the rest as it was and returns <see langword="false"/>.
    /// </summary>
    public bool Decode(Span<ushort> output)
    {
        if (output.Length < SampleCount)
        {
            throw new ArgumentException("The output is too small for the frame.", nameof(output));
        }

        int perMcu = SamplesPerMcu;
        int lineSamples = McusPerLine * perMcu;
        if (_slotTables.Length != perMcu)
        {
            _slotTables = new HuffmanTable[perMcu];
            _slotComponents = new int[perMcu];
        }

        HuffmanTable[] tables = _slotTables;
        int[] componentOf = _slotComponents;
        for (int s = 0, c = 0; c < Components; c++)
        {
            for (int k = 0; k < _h[c] * _v[c]; k++, s++)
            {
                tables[s] = _tables[_tableOf[c]]!;
                componentOf[s] = c;
            }
        }

        BitReader bits = new(_data, _scanAt, _end);
        int initial = 1 << (Precision - _pointTransform - 1);
        int restart = _restartInterval;
        int untilRestart = restart;
        bool firstLine = true;
        bool reset = true;
        int predictor = _predictor;
        int pt = _pointTransform;
        int lines = McuLines;
        int mcus = McusPerLine;
        Span<int> left = stackalloc int[perMcu];
        for (int line = 0; line < lines; line++)
        {
            Span<ushort> row = output.Slice(line * lineSamples, lineSamples);
            ReadOnlySpan<ushort> above = line == 0 ? default : output.Slice((line - 1) * lineSamples, lineSamples);
            for (int m = 0; m < mcus; m++)
            {
                if (restart > 0)
                {
                    if (untilRestart == 0)
                    {
                        // A restart interval starts like a scan: from half range, then from the left for the rest of its line.
                        bits.Restart();
                        untilRestart = restart;
                        firstLine = true;
                        reset = true;
                    }

                    untilRestart--;
                }

                int at = m * perMcu;
                for (int s = 0; s < perMcu; s++)
                {
                    int diff = tables[s].Decode(ref bits);
                    if (bits.Ended)
                    {
                        return false;
                    }

                    int prediction;
                    if (reset)
                    {
                        prediction = initial;
                    }
                    else if (firstLine)
                    {
                        prediction = left[s];
                    }
                    else if (m == 0)
                    {
                        prediction = above[at + s] >> pt;
                    }
                    else
                    {
                        int a = left[s];
                        int b = above[at + s] >> pt;
                        int c = above[at + s - perMcu] >> pt;
                        prediction = predictor switch
                        {
                            1 => a,
                            2 => b,
                            3 => c,
                            4 => a + b - c,
                            5 => a + ((b - c) >> 1),
                            6 => b + ((a - c) >> 1),
                            _ => (a + b) >> 1,
                        };
                    }

                    int value = (prediction + diff) & 0xFFFF;
                    left[s] = value;
                    row[at + s] = (ushort)(value << pt);
                }

                reset = false;
            }

            firstLine = false;
        }

        return true;
    }

    /// <summary>
    /// Decodes a Canon sRAW or mRAW frame (CR2 spec, section 5): groups of H0 × V0 luma values then Cb and Cr, the luma values
    /// sharing one running predictor, Cb and Cr each predicted from the group before, and each frame row starting from the
    /// first group of the row above. Writes <see cref="SampleCount"/> values in decode order; returns <see langword="false"/>
    /// when the data ends early.
    /// </summary>
    public bool DecodeSraw(Span<ushort> output)
    {
        int luma = H0 * V0;
        int perMcu = SamplesPerMcu;
        if (Components != 3 || perMcu != luma + 2 || output.Length < SampleCount)
        {
            throw new InvalidDataException("The frame is not a Canon sRAW frame.");
        }

        HuffmanTable[] tables = [_tables[_tableOf[0]]!, _tables[_tableOf[1]]!, _tables[_tableOf[2]]!];
        BitReader bits = new(_data, _scanAt, _end);
        int initial = 1 << (Precision - 1);
        int lineSamples = McusPerLine * perMcu;
        int y = initial, cb = initial, cr = initial;
        for (int line = 0; line < McuLines; line++)
        {
            Span<ushort> row = output.Slice(line * lineSamples, lineSamples);
            if (line > 0)
            {
                ReadOnlySpan<ushort> above = output.Slice((line - 1) * lineSamples, perMcu);
                (y, cb, cr) = (above[0], above[luma], above[luma + 1]);
            }

            for (int at = 0; at < lineSamples; at += perMcu)
            {
                for (int k = 0; k < luma; k++)
                {
                    y = (y + tables[0].Decode(ref bits)) & 0xFFFF;
                    row[at + k] = (ushort)y;
                }

                cb = (cb + tables[1].Decode(ref bits)) & 0xFFFF;
                cr = (cr + tables[2].Decode(ref bits)) & 0xFFFF;
                row[at + luma] = (ushort)cb;
                row[at + luma + 1] = (ushort)cr;
                if (bits.Ended)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>A Huffman table: a 9-bit lookup for short codes and the canonical code ranges for the rest (T.81 Annex C).</summary>
    private sealed class HuffmanTable
    {
        private const int FastBits = 9;
        private readonly ushort[] _fast = new ushort[1 << FastBits];
        private readonly int[] _maxCode = new int[18];
        private readonly int[] _valPtr = new int[17];
        private readonly byte[] _values = new byte[256];
        private int _count;

        /// <summary>Builds the table from a DHT segment's 16 code counts and its symbols, reusing its arrays.</summary>
        public void Build(ReadOnlySpan<byte> counts, ReadOnlySpan<byte> values)
        {
            Array.Clear(_fast);
            values.CopyTo(_values);
            _count = values.Length;
            int code = 0;
            int k = 0;
            for (int length = 1; length <= 16; length++)
            {
                _valPtr[length] = k - code;
                for (int i = 0; i < counts[length - 1]; i++, k++, code++)
                {
                    if (code >= 1 << length)
                    {
                        // More codes of this length than the code space holds (T.81 C.2 never builds such a table).
                        throw new InvalidDataException("A lossless JPEG Huffman table has more codes than its lengths allow.");
                    }

                    if (length <= FastBits)
                    {
                        int shift = FastBits - length;
                        for (int j = 0; j < 1 << shift; j++)
                        {
                            _fast[(code << shift) | j] = (ushort)((length << 8) | values[k]);
                        }
                    }
                }

                _maxCode[length] = counts[length - 1] == 0 ? -1 : code - 1;
                code <<= 1;
            }

            _maxCode[17] = int.MaxValue;
        }

        /// <summary>Decodes one difference value: its category, then its extra bits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Decode(ref BitReader bits)
        {
            bits.Fill();
            int peek = (int)(bits.Peek(FastBits));
            int entry = _fast[peek];
            int category;
            if (entry != 0)
            {
                bits.Skip(entry >> 8);
                category = entry & 0xFF;
            }
            else
            {
                int length = FastBits + 1;
                int code = (int)bits.Peek(length);
                while (length <= 16 && code > _maxCode[length])
                {
                    length++;
                    code = (int)bits.Peek(length);
                }

                if (length > 16)
                {
                    bits.Fail();
                    return 0;
                }

                bits.Skip(length);
                int index = _valPtr[length] + code;
                if ((uint)index >= (uint)_count)
                {
                    bits.Fail();
                    return 0;
                }

                category = _values[index];
            }

            if (category == 0)
            {
                return 0;
            }

            if (category >= 16)
            {
                return 32768;
            }

            bits.Fill();
            int extra = (int)bits.Peek(category);
            bits.Skip(category);
            return extra < 1 << (category - 1) ? extra - (1 << category) + 1 : extra;
        }
    }

    /// <summary>The entropy-coded bits, with stuffed zero bytes removed; a marker ends them until <see cref="Restart"/>.</summary>
    private struct BitReader(byte[] data, int start, int end)
    {
        private readonly byte[] _data = data;
        private readonly int _end = end;
        private int _pos = start;
        private ulong _buffer;
        private int _count;
        private bool _marker;
        private int _zeros;

        /// <summary>Gets whether a read went past the data: more than 64 zero bits were fed after its end.</summary>
        public readonly bool Ended => _zeros > 64;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Fill()
        {
            while (_count <= 56)
            {
                int b = 0;
                if (!_marker && _pos < _end)
                {
                    b = _data[_pos];
                    if (b == 0xFF)
                    {
                        int next = _pos + 1 < _end ? _data[_pos + 1] : 0;
                        if (next == 0)
                        {
                            _pos += 2;
                        }
                        else
                        {
                            _marker = true;
                            b = 0;
                            _zeros += 8;
                        }
                    }
                    else
                    {
                        _pos++;
                    }
                }
                else
                {
                    _zeros += 8;
                }

                _buffer |= (ulong)b << (56 - _count);
                _count += 8;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly uint Peek(int n) => (uint)(_buffer >> (64 - n));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Skip(int n)
        {
            _buffer <<= n;
            _count -= n;
        }

        public void Fail() => _zeros = int.MaxValue / 2;

        /// <summary>Skips to after the next RST marker and starts on a byte boundary.</summary>
        public void Restart()
        {
            _buffer = 0;
            _count = 0;
            _zeros = 0;
            _marker = false;
            while (_pos + 1 < _end && !(_data[_pos] == 0xFF && _data[_pos + 1] is >= 0xD0 and <= 0xD7))
            {
                _pos++;
            }

            _pos = Math.Min(_end, _pos + 2);
        }
    }
}
