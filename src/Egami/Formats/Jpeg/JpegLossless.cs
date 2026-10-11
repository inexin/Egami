using System.Buffers;
using System.Globalization;

using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Decodes lossless JPEG (T.81 annex H; SOF3 with Huffman coding, SOF11 with arithmetic coding) at 2 to 16 bits: each sample is
/// a prediction from its reconstructed neighbors plus a coded difference, modulo 2^16, then scaled up by the point transform.
/// Every scan is decoded into a plane per component before any row is converted, with the upsampling and color conversion of the
/// 12-bit path. There is no DCT, so no reduced decode: a smaller size comes from the generic reduction after the full decode.
/// </summary>
internal sealed class JpegLosslessDecoder : IDisposable
{
    private readonly JpegHeader _header;
    private readonly ReaderOptions _options;
    private readonly WarningList _warnings;
    private readonly bool _fast;
    private readonly Plane[] _planes;
    private readonly int _maxH;
    private readonly int _maxV;
    private readonly int _mcusX;
    private readonly int _mcusY;
    private readonly List<(JpegHuffmanSpec Spec, JpegHuffmanTable Table)> _tables = [];
    private ushort[] _row;
    private ushort[] _samples;
    private readonly int _shiftWidth;
    private ushort[] _shifted;
    private int[] _sums;
    private bool _warned;

    // Pull mode: the scans decode one at a time as their bytes arrive.
    private EgamiInput? _input;
    private JpegBitReader? _reader;
    private JpegArithmeticDecoder? _arithmetic;
    private JpegScan? _scan;
    private int _scans;
    private bool _scanDecoded;
    private bool _scansDone;

    internal JpegLosslessDecoder(JpegHeader header, ReaderOptions options, WarningList warnings, bool fastUpsampling)
    {
        _header = header;
        _options = options;
        _warnings = warnings;
        _fast = fastUpsampling;
        bool single = header.Components.Length == 1;
        _maxH = single ? 1 : header.MaxH;
        _maxV = single ? 1 : header.MaxV;
        _mcusX = (header.Width + _maxH - 1) / _maxH;
        _mcusY = (header.Height + _maxV - 1) / _maxV;
        _planes = new Plane[header.Components.Length];
        for (int i = 0; i < _planes.Length; i++)
        {
            JpegFrameComponent c = header.Components[i];
            int h = single ? 1 : c.H;
            int v = single ? 1 : c.V;
            if (_maxH % h != 0 || _maxV % v != 0)
            {
                throw new UnsupportedFormatException($"Fractional chroma sampling ({c.H}×{c.V} against {_maxH}×{_maxV}) is not supported.");
            }

            // The plane covers whole MCUs; the component's own samples are the top-left part.
            _planes[i] = new Plane(
                h, v, _mcusX * h, _mcusY * v,
                (int)((((long)header.Width * h) + _maxH - 1) / _maxH), (int)((((long)header.Height * v) + _maxV - 1) / _maxV));
        }

        _row = ArrayPool<ushort>.Shared.Rent(header.Width * 4);
        _samples = ArrayPool<ushort>.Shared.Rent(header.Width * _planes.Length);
        _shiftWidth = _mcusX * _maxH;
        _shifted = ArrayPool<ushort>.Shared.Rent(2 * _planes.Length * _shiftWidth);
        _sums = ArrayPool<int>.Shared.Rent(header.Width + 2);
    }

    /// <summary>Returns the bytes a full decode holds besides the output: every component's plane and the row buffers.</summary>
    internal static long WorkingBytes(JpegHeader header)
    {
        bool single = header.Components.Length == 1;
        int maxH = single ? 1 : header.MaxH;
        int maxV = single ? 1 : header.MaxV;
        long mcusX = (header.Width + maxH - 1) / maxH;
        long mcusY = (header.Height + maxV - 1) / maxV;
        long bytes = header.Width * (4L + (3 * header.Components.Length)) * sizeof(ushort);
        foreach (JpegFrameComponent c in header.Components)
        {
            bytes += mcusX * (single ? 1 : c.H) * mcusY * (single ? 1 : c.V) * sizeof(ushort);
        }

        return bytes;
    }

    /// <summary>Decodes every scan, from the first scan's data on.</summary>
    internal void Decode(EgamiInput input)
    {
        Begin(input);
    }

    /// <summary>Starts decoding: allocates the planes and decodes the scans that have arrived.</summary>
    internal void Begin(EgamiInput input)
    {
        _input = input;
        _reader = new JpegBitReader(input);
        _arithmetic = _header.Arithmetic ? new JpegArithmeticDecoder(_reader, _header) : null;
        foreach (Plane plane in _planes)
        {
            plane.Allocate(input.Memory);
        }

        _scan = _header.FirstScan!;
        Pump();
    }

    /// <summary>Converts the part of output row <paramref name="y"/> from column <paramref name="x"/> into <paramref name="target"/>, once every scan has been decoded.</summary>
    internal void ReadRow<TOut>(int y, int x, Span<TOut> target)
        where TOut : unmanaged, IPixelType<TOut>
    {
        Pump();
        int count = _planes.Length;
        int width = target.Length;
        ReadOnlySpan<ushort> a = SampleRow(0, y).Slice(x, width);
        ReadOnlySpan<ushort> b = count > 1 ? SampleRow(1, y).Slice(x, width) : default;
        ReadOnlySpan<ushort> c = count > 2 ? SampleRow(2, y).Slice(x, width) : default;
        ReadOnlySpan<ushort> k = count > 3 ? SampleRow(3, y).Slice(x, width) : default;
        JpegWide.ConvertRow(_header.Details.ColorSpace, _header.Precision, a, b, c, k, target, _row);
    }

    /// <summary>
    /// Copies every sample, after the point transform and without color conversion, into <paramref name="destination"/>,
    /// interleaved in component order. Every component must be sampled 1×1, as in DNG and TIFF lossless JPEG.
    /// </summary>
    internal void CopySamples(Span<ushort> destination)
    {
        Pump();
        int count = _planes.Length;
        int width = _header.Width;
        for (int y = 0; y < _header.Height; y++)
        {
            Span<ushort> target = destination.Slice(y * width * count, width * count);
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<ushort> row = PlaneRow(_planes[i], y, 2 * i);
                for (int x = 0; x < width; x++)
                {
                    target[(x * count) + i] = row[x];
                }
            }
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _reader = null;
        foreach (Plane plane in _planes)
        {
            plane.Dispose();
        }

        foreach ((_, JpegHuffmanTable table) in _tables)
        {
            table.Dispose();
        }

        _tables.Clear();
        ArrayPool<ushort>.Shared.Return(Interlocked.Exchange(ref _row, null!));
        ArrayPool<ushort>.Shared.Return(Interlocked.Exchange(ref _samples, null!));
        ArrayPool<ushort>.Shared.Return(Interlocked.Exchange(ref _shifted, null!));
        ArrayPool<int>.Shared.Return(Interlocked.Exchange(ref _sums, null!));
    }

    /// <summary>
    /// Decodes the scans one after another. An incremental decode decodes a scan once all of it has arrived and stops (with
    /// <see cref="Streaming.NeedsInputException"/>) at the first that has not, as the DCT decoder's buffered path does.
    /// </summary>
    private void Pump()
    {
        EgamiInput input = _input!;
        JpegBitReader reader = _reader!;
        while (!_scansDone)
        {
            if (!_scanDecoded)
            {
                if (++_scans > JpegDecoder<Gray8>.MaxScans)
                {
                    throw new CorruptDataException(JpegFormat.Info.Name, -1, string.Create(CultureInfo.InvariantCulture, $"The file has more than {JpegDecoder<Gray8>.MaxScans:N0} scans."));
                }

                input.CancellationToken.ThrowIfCancellationRequested();
                if (!reader.ScanArrived())
                {
                    _scans--;
                    throw new Streaming.NeedsInputException();
                }

                _scanDecoded = true;
                if (!DecodeScan(reader, _scan!))
                {
                    _scansDone = true;
                    break;
                }
            }

            JpegBitReader.Checkpoint mark = reader.Mark();
            int warnings = _warnings.Count;
            bool warned = _warned;
            JpegScan? next;
            try
            {
                next = JpegSegmentReader.NextScan(input, _header, reader, Problem);
            }
            catch (Streaming.NeedsInputException)
            {
                reader.Rewind(mark);
                _warnings.RemoveRange(warnings, _warnings.Count - warnings);
                _warned = warned;
                throw;
            }

            if (next is null)
            {
                _scansDone = true;
                break;
            }

            if (next.SpectralStart is < 1 or > 7)
            {
                throw new CorruptDataException(JpegFormat.Info.Name, -1, $"A lossless scan has an invalid predictor ({next.SpectralStart}).");
            }

            _scan = next;
            _scanDecoded = false;
            reader.ResetSearch();
        }
    }

    /// <summary>
    /// Decodes one scan into the planes (T.81 H.2). Returns false if the data ended early, so no scan can follow; the samples
    /// decoded so far are kept.
    /// </summary>
    private bool DecodeScan(JpegBitReader reader, JpegScan scan)
    {
        int count = scan.Components.Length;
        bool single = count == 1;
        Unit[] units = new Unit[count];
        JpegArithmeticDecoder? arithmetic = _arithmetic;
        for (int i = 0; i < count; i++)
        {
            JpegScanComponent sc = scan.Components[i];
            Plane plane = _planes[sc.ComponentIndex];
            int h = single ? 1 : plane.H;
            int v = single ? 1 : plane.V;
            JpegHuffmanTable? table = arithmetic is null ? Table(_header.DcTables[sc.DcTable] ?? throw Missing(sc.DcTable)) : null;
            int[]? differences = arithmetic is null ? null : new int[(v + 1) * plane.Width];
            units[i] = new Unit(plane, h, v, sc.DcTable, table, differences);
            plane.Shift = scan.ApproxLow;
        }

        reader.BeginScan();
        arithmetic?.BeginScan(scan);
        int predictor = scan.SpectralStart;
        int initial = 1 << (_header.Precision - scan.ApproxLow - 1);
        int unitsX = single ? units[0].Plane.ComponentWidth : _mcusX;
        int unitsY = single ? units[0].Plane.ComponentHeight : _mcusY;
        int restart = _header.RestartInterval;
        int nextRestart = 0;
        long mcu = 0;
        for (int uy = 0; uy < unitsY; uy++)
        {
            for (int ux = 0; ux < unitsX; ux++, mcu++)
            {
                if (restart > 0 && mcu > 0 && mcu % restart == 0)
                {
                    if (!RestartInterval(reader, ref nextRestart))
                    {
                        return false;
                    }

                    arithmetic?.Restart();
                    foreach (Unit unit in units)
                    {
                        // The prediction starts over: the next sample is the first of a scan, its line the first line.
                        unit.ResetRow = uy * unit.V;
                        unit.ResetX = ux * unit.H;
                    }
                }

                foreach (Unit unit in units)
                {
                    for (int v = 0; v < unit.V; v++)
                    {
                        for (int h = 0; h < unit.H; h++)
                        {
                            DecodeSample(reader, arithmetic, unit, (ux * unit.H) + h, (uy * unit.V) + v, predictor, initial);
                        }
                    }
                }

                if (arithmetic is not null ? arithmetic.Failed : reader.PastEnd && !reader.AtRestartMarker)
                {
                    Problem("Jpeg.DataEnded", "The image data ends early or is damaged. The rest of the image is black.");
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Predicts sample (<paramref name="x"/>, <paramref name="y"/>) of a plane from its neighbors (T.81 H.1.2.1) and adds its decoded difference.</summary>
    private static void DecodeSample(JpegBitReader reader, JpegArithmeticDecoder? arithmetic, Unit unit, int x, int y, int predictor, int initial)
    {
        Span<ushort> row = unit.Plane.Row(y);
        int prediction;
        bool firstLine = y == unit.ResetRow;
        if (firstLine)
        {
            // The first line after the start or a restart predicts from the left, its first sample from the default value.
            prediction = x <= unit.ResetX ? initial : row[x - 1];
        }
        else
        {
            ReadOnlySpan<ushort> above = unit.Plane.Row(y - 1);
            if (x == 0)
            {
                prediction = above[0];
            }
            else
            {
                int a = row[x - 1];
                int b = above[x];
                int c = above[x - 1];
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
        }

        int difference;
        if (arithmetic is not null)
        {
            // Conditioning on the differences coded to the left and above, zero at the start of a line and on the first line.
            int[] differences = unit.Differences!;
            int width = unit.Plane.Width;
            int ring = unit.V + 1;
            int left = x == 0 || x <= unit.ResetX && firstLine ? 0 : differences[((y % ring) * width) + x - 1];
            int above = firstLine ? 0 : differences[(((y - 1) % ring) * width) + x];
            difference = arithmetic.DecodeDifference(unit.TableIndex, left, above);
            differences[((y % ring) * width) + x] = difference;
        }
        else
        {
            // T.81 table H.2: category 16 is the difference 32768, with no extra bits.
            int s = unit.Table!.Decode(reader);
            difference = s >= 16 ? 32768 : reader.ReceiveExtend(s);
        }

        row[x] = (ushort)(prediction + difference);
    }

    /// <summary>
    /// Returns one full-width row of component <paramref name="index"/> for output row <paramref name="y"/>, scaled up by the
    /// point transform and upsampled as the 12-bit path does.
    /// </summary>
    private ReadOnlySpan<ushort> SampleRow(int index, int y)
    {
        Plane plane = _planes[index];
        int hr = _maxH / plane.H;
        int vr = _maxV / plane.V;
        int width = _header.Width;
        int row = y / vr;
        ReadOnlySpan<ushort> cur = PlaneRow(plane, row, 2 * index);
        if (hr == 1 && vr == 1)
        {
            return cur[..width];
        }

        Span<ushort> output = _samples.AsSpan(index * width, width);
        if (_fast || (hr != 1 && hr != 2) || (vr != 1 && vr != 2))
        {
            JpegWide.Replicate(cur, hr, output);
            return output;
        }

        int n = plane.ComponentWidth;
        Span<int> sums = _sums.AsSpan(0, n + 2);
        if (vr == 2)
        {
            bool upper = (y & 1) == 0;
            ReadOnlySpan<ushort> near = PlaneRow(plane, Math.Clamp(upper ? row - 1 : row + 1, 0, plane.ComponentHeight - 1), (2 * index) + 1);
            if (hr == 1)
            {
                JpegWide.Vertical(cur[..width], near[..width], output, upper ? 1 : 2);
                return output;
            }

            JpegWide.ColumnSums(cur, near, n, sums);
            JpegWide.Double(sums, n, output, 8, 7, 4);
            return output;
        }

        JpegWide.Widen(cur, n, sums);
        JpegWide.Double(sums, n, output, 1, 2, 2);
        return output;
    }

    /// <summary>Returns a plane row with its point transform applied, in buffer <paramref name="slot"/>, or the row itself when there is none.</summary>
    private ReadOnlySpan<ushort> PlaneRow(Plane plane, int row, int slot)
    {
        Span<ushort> raw = plane.Row(row);
        int shift = plane.Shift;
        if (shift == 0)
        {
            return raw;
        }

        int max = (1 << _header.Precision) - 1;
        Span<ushort> shifted = _shifted.AsSpan(slot * _shiftWidth, raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            shifted[i] = (ushort)((raw[i] << shift) & max);
        }

        return shifted;
    }

    /// <summary>Handles a restart interval boundary. Returns false if the data ended instead.</summary>
    private bool RestartInterval(JpegBitReader reader, ref int nextRestart)
    {
        bool ended = false;
        if (!reader.Restart(nextRestart))
        {
            Problem("Jpeg.RestartResync", "A restart marker is missing or out of order; the data was resynchronized at the next one.");
            ended = reader.Marker != 0;
        }

        nextRestart = reader.LastRestart >= 0 ? reader.LastRestart + 1 : nextRestart + 1;
        return !ended;
    }

    private JpegHuffmanTable Table(JpegHuffmanSpec spec)
    {
        foreach ((JpegHuffmanSpec known, JpegHuffmanTable table) in _tables)
        {
            if (ReferenceEquals(known, spec))
            {
                return table;
            }
        }

        JpegHuffmanTable built = new(spec);
        _tables.Add((spec, built));
        return built;
    }

    private static CorruptDataException Missing(int slot)
    {
        return new CorruptDataException(JpegFormat.Info.Name, -1, $"The scan uses DC Huffman table {slot}, which is missing.");
    }

    private void Problem(string id, string message)
    {
        if (_options.Recovery == RecoveryMode.Strict)
        {
            throw new CorruptDataException(JpegFormat.Info.Name, -1, message);
        }

        if (!_warned)
        {
            _warnings.Add(id, message);
            _warned = true;
        }
    }

    /// <summary>One component of a scan: its plane, samples per MCU, coding table and where its prediction last started over.</summary>
    private sealed class Unit(Plane plane, int h, int v, int tableIndex, JpegHuffmanTable? table, int[]? differences)
    {
        internal Plane Plane => plane;

        internal int H => h;

        internal int V => v;

        internal int TableIndex => tableIndex;

        internal JpegHuffmanTable? Table => table;

        /// <summary>Gets the differences coded for the last <see cref="V"/> + 1 rows, for arithmetic conditioning.</summary>
        internal int[]? Differences => differences;

        internal int ResetRow { get; set; }

        internal int ResetX { get; set; }
    }

    /// <summary>One component's samples over whole MCUs, in chunks of whole rows that each fit one memory block.</summary>
    private sealed unsafe class Plane(int h, int v, int width, int height, int componentWidth, int componentHeight) : IDisposable
    {
        private IMemoryOwner<byte>?[] _chunks = [];
        private MemoryHandle[] _pins = [];
        private int _rowsPerChunk = 1;

        internal int H => h;

        internal int V => v;

        /// <summary>Gets or sets the point transform of the scan that coded the plane: its samples are shifted up by this many bits.</summary>
        internal int Shift { get; set; }

        /// <summary>Gets the plane's width in samples: whole MCUs.</summary>
        internal int Width => width;

        /// <summary>Gets the component's real width in samples.</summary>
        internal int ComponentWidth => componentWidth;

        /// <summary>Gets the component's real height in samples.</summary>
        internal int ComponentHeight => componentHeight;

        internal void Allocate(BudgetedMemorySource memory)
        {
            if (_chunks.Length > 0)
            {
                return;
            }

            int rowBytes = width * sizeof(ushort);
            memory.Check((long)rowBytes * height);
            _rowsPerChunk = Math.Clamp(memory.MaxBlockBytes / rowBytes, 1, height);
            _chunks = new IMemoryOwner<byte>?[(height + _rowsPerChunk - 1) / _rowsPerChunk];
            _pins = new MemoryHandle[_chunks.Length];
            for (int i = 0; i < _chunks.Length; i++)
            {
                int rows = Math.Min(_rowsPerChunk, height - (i * _rowsPerChunk));
                _chunks[i] = memory.Allocate(rows * rowBytes, ClearMode.Zero);
                _pins[i] = _chunks[i]!.Memory.Pin();
            }
        }

        internal Span<ushort> Row(int y)
        {
            return new Span<ushort>((ushort*)_pins[y / _rowsPerChunk].Pointer + ((long)(y % _rowsPerChunk) * width), width);
        }

        public void Dispose()
        {
            foreach (MemoryHandle pin in _pins)
            {
                pin.Dispose();
            }

            foreach (IMemoryOwner<byte>? chunk in _chunks)
            {
                chunk?.Dispose();
            }

            _pins = [];
            _chunks = [];
        }
    }
}
