using System.Buffers;

namespace Egami.Formats.Jpeg;

/// <content>
/// Decoding on several cores, with the same pixels as one thread. Each core gets a twin decoder: strips and row buffers of its
/// own, the coefficients and tables shared. A buffered file (progressive, 12-bit, or a scan per component) splits its last pass,
/// the IDCT, upsampling and color conversion of the held coefficients, by MCU rows. A sequential file with restart markers splits
/// its scan at the markers already in it: each core entropy-decodes, transforms and converts its own run of rows. Nothing is
/// written into a file to make this possible, and no index is read; a file without restart markers keeps the two-thread
/// pipeline.
/// </content>
internal sealed partial class JpegDecoder<TPixel>
{
    /// <summary>MCU rows each core should have at least, so the extra row a core transforms for its neighbors stays small.</summary>
    private const int MinRowsPerCore = 4;

    /// <summary>Restart intervals each core should have at least, so the one it may decode twice stays small.</summary>
    private const int MinIntervalsPerCore = 2;

    /// <summary>The most scan bytes read into memory for a restart split; larger scans take the pipeline.</summary>
    private const int MaxScanBytes = 1 << 30;

    private bool _speculative;
    private byte[]? _scanBytes;

    /// <summary>Creates a twin of <paramref name="parent"/>: its own strips and buffers, the coefficients and everything else shared.</summary>
    private JpegDecoder(JpegDecoder<TPixel> parent)
    {
        _header = parent._header;
        _options = parent._options;
        _raster = parent._raster;
        _planes = parent._planes;
        _warnings = parent._warnings;
        _fast = parent._fast;
        _wide = parent._wide;
        _maxH = parent._maxH;
        _maxV = parent._maxV;
        _mcusX = parent._mcusX;
        _mcusY = parent._mcusY;
        _mcuRows = parent._mcuRows;
        _blockSize = parent._blockSize;
        _width = parent._width;
        _height = parent._height;
        _channels = parent._channels;
        _components = [.. parent._components.Select(c => c.Twin())];
        (_rowBuffer, _sampleBuffer, _sums) = RentBuffers();
    }

    /// <summary>Returns how many cores to use for <paramref name="units"/> pieces of work: none for small images or one thread.</summary>
    private int Cores(EgamiInput input, int units, int perCore)
    {
        int cores = Math.Min(input.Context.MaxParallelism, Environment.ProcessorCount);
        if (cores < 2 || (!ForcePipeline && (long)_width * _height < ParallelPixels))
        {
            return 1;
        }

        return Math.Max(1, Math.Min(cores, Math.Min(units / perCore, _mcusY / MinRowsPerCore)));
    }

    /// <summary>
    /// The last pass of a buffered file: every MCU row is transformed and its rows converted into the output, split by rows across
    /// cores when the image is large enough.
    /// </summary>
    private void FinishBuffered(EgamiInput input)
    {
        int cores = Cores(input, _mcusY, MinRowsPerCore);
        if (cores <= 1)
        {
            FinishRows(input, 0, _mcusY);
            return;
        }

        JpegDecoder<TPixel>[] twins = new JpegDecoder<TPixel>[cores];
        try
        {
            for (int i = 0; i < cores; i++)
            {
                twins[i] = new JpegDecoder<TPixel>(this);
            }

            ParallelOptions options = new() { MaxDegreeOfParallelism = cores, CancellationToken = input.CancellationToken };
            Parallel.For(0, cores, options, i => twins[i].FinishRows(input, Share(_mcusY, i, cores), Share(_mcusY, i + 1, cores)));
        }
        finally
        {
            foreach (JpegDecoder<TPixel>? twin in twins)
            {
                twin?.Dispose();
            }
        }
    }

    /// <summary>
    /// Transforms and converts MCU rows <paramref name="first"/> to <paramref name="end"/> (exclusive). The row above the first is
    /// transformed too, without being written, since upsampling looks at it; so is the row below the last.
    /// </summary>
    private void FinishRows(EgamiInput input, int first, int end)
    {
        for (int my = Math.Max(0, first - 1); my <= Math.Min(end, _mcusY - 1); my++)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            TransformBufferedRow(my);
            if (my - 1 >= first)
            {
                EmitRows(my - 1);
            }
        }

        if (end == _mcusY)
        {
            EmitRows(_mcusY - 1);
        }
    }

    /// <summary>
    /// Decodes a sequential scan split at its restart markers, one run of MCU rows per core, when the file has markers, the image
    /// is large and the scan's bytes can be read into memory. Returns false to decode as before; a split that met damaged data is
    /// undone, and <paramref name="reader"/> then reads the bytes already in memory.
    /// </summary>
    private bool SplitByRestarts(EgamiInput input, ref JpegBitReader reader, JpegScan scan, JpegHuffmanTable[] dc, JpegHuffmanTable[] ac, int[] firstBlock, int blocks)
    {
        int restart = _header.RestartInterval;
        long mcus = (long)_mcusX * _mcusY;
        if (restart == 0 || input.Incremental || input.Length is not long length || length - input.Position is <= 0 or > MaxScanBytes)
        {
            return false;
        }

        int intervals = (int)((mcus + restart - 1) / restart);
        int cores = Cores(input, intervals, MinIntervalsPerCore);
        if (cores <= 1)
        {
            return false;
        }

        // The rest of the file, read once; each core reads its own part of it.
        int size = (int)(length - input.Position);
        input.CheckMemory(size);
        byte[] bytes = _scanBytes = ArrayPool<byte>.Shared.Rent(size);
        size = input.Read(bytes.AsSpan(0, size));
        reader.Dispose();
        reader = new JpegBitReader(bytes, 0, size);
        if (_arithmetic is not null)
        {
            _arithmetic = new JpegArithmeticDecoder(reader, _header);
        }

        int[]? starts = FindIntervals(bytes.AsSpan(0, size), intervals);
        if (starts is null)
        {
            return false;
        }

        foreach (JpegHuffmanTable? table in ac)
        {
            // Built once here, not by several cores at once.
            _ = table?.FastAc;
        }

        JpegDecoder<TPixel>[] twins = new JpegDecoder<TPixel>[cores];
        try
        {
            for (int i = 0; i < cores; i++)
            {
                twins[i] = new JpegDecoder<TPixel>(this) { _speculative = true };
            }

            ParallelOptions options = new() { MaxDegreeOfParallelism = cores, CancellationToken = input.CancellationToken };
            Parallel.For(0, cores, options, i => twins[i].DecodeRun(input, bytes, size, starts, scan, dc, ac, firstBlock, blocks, Share(_mcusY, i, cores), Share(_mcusY, i + 1, cores)));
            return true;
        }
        catch (AggregateException e) when (e.InnerExceptions.All(x => x is SpeculationFailedException))
        {
            // Damaged data: the one-thread decode reports it as it always has.
            return false;
        }
        finally
        {
            foreach (JpegDecoder<TPixel>? twin in twins)
            {
                twin?.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns where each restart interval's data starts in <paramref name="scan"/>, the scan's entropy-coded bytes, or null when
    /// the markers are not exactly RST0, RST1 and so on, one fewer than <paramref name="intervals"/>, before the marker that ends
    /// the scan. Only an 0xFF can start a marker, and the search for one is vectorized.
    /// </summary>
    internal static int[]? FindIntervals(ReadOnlySpan<byte> scan, int intervals)
    {
        int[] starts = new int[intervals];
        int found = 1;
        int at = 0;
        while (true)
        {
            int next = scan[at..].IndexOf((byte)0xFF);
            if (next < 0)
            {
                // The file ends without a marker after the scan.
                return found == intervals ? starts : null;
            }

            at += next + 1;
            while (at < scan.Length && scan[at] == 0xFF)
            {
                at++;
            }

            if (at == scan.Length)
            {
                return null;
            }

            byte marker = scan[at++];
            if (marker == 0)
            {
                continue;
            }

            if (marker is < 0xD0 or > 0xD7)
            {
                return found == intervals ? starts : null;
            }

            if (found == intervals || marker != 0xD0 + ((found - 1) & 7))
            {
                return null;
            }

            starts[found++] = at;
        }
    }

    /// <summary>
    /// One core's part of a restart split: MCU rows <paramref name="first"/> to <paramref name="end"/> (exclusive), decoded from
    /// the start of the interval that holds the row above the first, which is transformed for the upsampling, as is the row
    /// below the last.
    /// </summary>
    private void DecodeRun(
        EgamiInput input, byte[] bytes, int size, int[] starts, JpegScan scan, JpegHuffmanTable[] dc, JpegHuffmanTable[] ac, int[] firstBlock, int blocks, int first, int end)
    {
        int restart = _header.RestartInterval;
        int from = Math.Max(0, first - 1);
        int interval = (int)((long)from * _mcusX / restart);
        using JpegBitReader reader = new(bytes, starts[interval], size);
        JpegArithmeticDecoder? arithmetic = _header.Arithmetic ? new JpegArithmeticDecoder(reader, _header) : null;
        arithmetic?.BeginScan(scan);
        Component[] order = [.. scan.Components.Select(sc => _components[sc.ComponentIndex])];
        EntropyState state = new(order.Length)
        {
            Mcu = interval * restart,
            NextRestart = interval & 7,

            // The marker before this interval was passed already: the data starts after it.
            Resynced = interval > 0,
        };

        short[] coefficients = ArrayPool<short>.Shared.Rent(blocks * 64);
        byte[] hasAc = ArrayPool<byte>.Shared.Rent(blocks);
        try
        {
            RowSlot slot = new(coefficients, 0, hasAc, 0, firstBlock);
            int row = state.Mcu / _mcusX;
            if (state.Mcu % _mcusX != 0)
            {
                // The interval starts inside a row nobody here needs: decode on to the next row.
                DecodeRow(reader, arithmetic, dc, ac, order, state, slot, state.Mcu % _mcusX);
                row++;
            }

            int last = Math.Min(end, _mcusY - 1);
            for (int my = row; my <= last; my++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                DecodeRow(reader, arithmetic, dc, ac, order, state, slot);
                if (my >= from)
                {
                    TransformRow(my, order, slot);
                    if (my - 1 >= first)
                    {
                        EmitRows(my - 1);
                    }
                }
            }

            if (end == _mcusY)
            {
                EmitRows(_mcusY - 1);
            }
        }
        finally
        {
            ArrayPool<short>.Shared.Return(coefficients);
            ArrayPool<byte>.Shared.Return(hasAc);
        }
    }

    /// <summary>Returns the start of part <paramref name="index"/> of <paramref name="count"/> even parts of <paramref name="total"/>.</summary>
    private static int Share(int total, int index, int count)
    {
        return (int)((long)total * index / count);
    }

    private void DisposeParallel()
    {
        if (_scanBytes is not null)
        {
            ArrayPool<byte>.Shared.Return(_scanBytes);
            _scanBytes = null;
        }
    }

    /// <summary>Thrown inside a parallel decode that met damaged data, to start over on one thread.</summary>
    private sealed class SpeculationFailedException : Exception
    {
    }
}
