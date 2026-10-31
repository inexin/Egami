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

    /// <summary>
    /// The most cores a pipelined decode of scans uses. Its critical path is one chain of refinement scans, so more cores only add
    /// contention: on a 12-core machine with two threads per core, 8 decode the progressive benchmark photo fastest.
    /// </summary>
    private const int MaxPipelineCores = 8;

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
        if (cores < 2 || (!ForcePipeline && (long)_header.Width * _header.Height < ParallelPixels))
        {
            return 1;
        }

        int used = Math.Max(1, Math.Min(cores, Math.Min(units / perCore, _mcusY / MinRowsPerCore)));
        if (used > 1)
        {
            input.Emit(DiagnosticLevel.Debug, "Parallel.Bands", $"The JPEG decode splits {units} MCU rows or runs into {used} bands on {used} threads.");
        }

        return used;
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
    private void FinishRows(EgamiInput input, int first, int end, ScanFollower? follower = null)
    {
        for (int my = Math.Max(0, first - 1); my <= Math.Min(end, _mcusY - 1); my++)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            follower?.Wait(my);
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

    /// <summary>
    /// Decodes the scans of a buffered file on several cores at once, when the image is large and the rest of the
    /// file can be read into memory. Every scan is located first, with the tables it uses; then each core takes the next scan in
    /// file order, and a scan that refines coefficients an earlier one codes follows it block row by block row, so a chain of
    /// refinement scans runs as a pipeline. Returns false to decode one scan at a time as before; a parallel decode that met
    /// anything but clean data is undone, and <paramref name="reader"/> then reads the bytes already in memory. Without
    /// <paramref name="finish"/> the rows are not transformed: the decode stops with every coefficient in place, for pull mode.
    /// </summary>
    private bool DecodeScansTogether(EgamiInput input, ref JpegBitReader reader, JpegScan first, bool finish = true)
    {
        int cores = Math.Min(Math.Min(input.Context.MaxParallelism, Environment.ProcessorCount), MaxPipelineCores);
        if (cores < 2 || (!ForcePipeline && (long)_header.Width * _header.Height < ParallelPixels)
            || input.Incremental || input.Length is not long length || length - input.Position is <= 0 or > MaxScanBytes)
        {
            return false;
        }

        input.Emit(DiagnosticLevel.Debug, "Parallel.Bands", $"The JPEG's scans are decoded together, as a pipeline on up to {cores} threads.");
        int size = (int)(length - input.Position);
        input.CheckMemory(size);
        byte[] bytes = _scanBytes = ArrayPool<byte>.Shared.Rent(size);
        size = input.Read(bytes.AsSpan(0, size));
        reader.Dispose();
        reader = new JpegBitReader(bytes, 0, size);

        // The tables a later scan may replace, to go back to if the parallel decode is abandoned.
        JpegHuffmanSpec?[] dcTables = (JpegHuffmanSpec?[])_header.DcTables.Clone();
        JpegHuffmanSpec?[] acTables = (JpegHuffmanSpec?[])_header.AcTables.Clone();
        ushort[]?[] quantTables = (ushort[]?[])_header.QuantTables.Clone();
        byte[] lower = (byte[])_header.ArithmeticLower.Clone();
        byte[] upper = (byte[])_header.ArithmeticUpper.Clone();
        byte[] kx = (byte[])_header.ArithmeticKx.Clone();
        int restart = _header.RestartInterval;
        _speculative = true;
        try
        {
            List<PlannedScan> plan = Plan(input, bytes, size, first);
            for (int i = 0; i < _components.Length; i++)
            {
                // A component no scan reached is all zeros, mid-gray, and still needs a table.
                LatchQuant(i);
            }

            RunPlan(input, bytes, size, plan, cores, finish);
            _speculative = false;
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Damaged or unusual data: undo everything and decode one scan at a time, which reports it as it always has.
            _speculative = false;
            dcTables.CopyTo(_header.DcTables, 0);
            acTables.CopyTo(_header.AcTables, 0);
            quantTables.CopyTo(_header.QuantTables, 0);
            lower.CopyTo(_header.ArithmeticLower, 0);
            upper.CopyTo(_header.ArithmeticUpper, 0);
            kx.CopyTo(_header.ArithmeticKx, 0);
            _header.RestartInterval = restart;
            foreach (Component c in _components)
            {
                c.Quant = null;
                c.ClearCoefficients();
            }

            reader = new JpegBitReader(bytes, 0, size);
            if (_arithmetic is not null)
            {
                _arithmetic = new JpegArithmeticDecoder(reader, _header);
            }

            return false;
        }
    }

    /// <summary>
    /// Walks the scans from the first one's data on, as the one-thread decode reads them: each scan with the tables and restart
    /// interval it uses and where its data starts. Quantization tables latch in file order, and the decoding tables are built here,
    /// before any core needs them.
    /// </summary>
    private List<PlannedScan> Plan(EgamiInput input, byte[] bytes, int size, JpegScan first)
    {
        List<PlannedScan> plan = [];
        using JpegBitReader walker = new(bytes, 0, size);
        JpegScan? scan = first;
        int start = 0;
        while (scan is not null)
        {
            if (plan.Count == MaxScans)
            {
                throw new SpeculationFailedException();
            }

            bool progressive = _header.Progressive;
            int kind = !progressive ? 0 : scan.SpectralStart == 0 ? (scan.ApproxHigh == 0 ? 1 : 2) : (scan.ApproxHigh == 0 ? 3 : 4);
            foreach (JpegScanComponent sc in scan.Components)
            {
                LatchQuant(sc.ComponentIndex);
                if (kind is 0 or 1 && _header.DcTables[sc.DcTable] is JpegHuffmanSpec dc)
                {
                    _ = Table(dc);
                }

                if (kind is 0 or 3 or 4 && _header.AcTables[sc.AcTable] is JpegHuffmanSpec ac)
                {
                    _ = Table(ac).FastAc;
                }
            }

            plan.Add(new PlannedScan(
                scan, (JpegHuffmanSpec?[])_header.DcTables.Clone(), (JpegHuffmanSpec?[])_header.AcTables.Clone(), _header.RestartInterval, start,
                ((byte[])_header.ArithmeticLower.Clone(), (byte[])_header.ArithmeticUpper.Clone(), (byte[])_header.ArithmeticKx.Clone())));
            walker.BeginScan();
            scan = NextScan(input, walker);
            start = walker.Offset;
        }

        return plan;
    }

    /// <summary>
    /// Runs the planned scans on <paramref name="cores"/> cores, then the last pass in runs of MCU rows, each run following every
    /// scan so it converts rows as soon as the last scan has passed them. Each core takes the next piece of work in that order, so
    /// a piece only ever waits for pieces that have started already, and none waits for a later one: the pipeline cannot deadlock.
    /// Without <paramref name="finish"/> there is no last pass.
    /// </summary>
    private void RunPlan(EgamiInput input, byte[] bytes, int size, List<PlannedScan> plan, int cores, bool finish)
    {
        int runs = finish ? Math.Max(1, Math.Min(cores, _mcusY / MinRowsPerCore)) : 0;
        PipelineState state = new(input.CancellationToken);
        ScanFollower[] followers = new ScanFollower[plan.Count];
        for (int s = 0; s < plan.Count; s++)
        {
            JpegScan scan = plan[s].Scan;
            List<(ScanFollower Scan, int V)> waits = [];
            for (int t = 0; t < s; t++)
            {
                JpegScan earlier = plan[t].Scan;
                bool overlap = earlier.SpectralStart <= scan.SpectralEnd && scan.SpectralStart <= earlier.SpectralEnd;
                foreach (JpegScanComponent sc in scan.Components)
                {
                    if (overlap && earlier.Components.Any(e => e.ComponentIndex == sc.ComponentIndex))
                    {
                        waits.Add((followers[t], _components[sc.ComponentIndex].V));
                    }
                }
            }

            followers[s] = new ScanFollower(state, scan.Components.Length > 1, [.. waits]);
        }

        // The last pass follows every scan of every component, MCU row by MCU row.
        List<(ScanFollower Scan, int V)> all = [];
        for (int s = 0; s < plan.Count; s++)
        {
            foreach (JpegScanComponent sc in plan[s].Scan.Components)
            {
                all.Add((followers[s], _components[sc.ComponentIndex].V));
            }
        }

        ScanFollower last = new(state, interleaved: true, [.. all]);
        JpegDecoder<TPixel>[] twins = new JpegDecoder<TPixel>[runs];
        try
        {
            for (int i = 0; i < runs; i++)
            {
                twins[i] = new JpegDecoder<TPixel>(this);
            }

            int next = -1;
            int work = plan.Count + runs;
            ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Min(cores, work), CancellationToken = input.CancellationToken };
            Parallel.For(0, Math.Min(cores, work), options, _ =>
            {
                int item;
                while (!state.Failed && (item = Interlocked.Increment(ref next)) < work)
                {
                    try
                    {
                        if (item >= plan.Count)
                        {
                            int run = item - plan.Count;
                            twins[run].FinishRows(input, Share(_mcusY, run, runs), Share(_mcusY, run + 1, runs), last);
                            continue;
                        }

                        PlannedScan p = plan[item];
                        bool skip = _header.Progressive && p.Scan.SpectralStart > 0 && _components[p.Scan.Components[0].ComponentIndex].BlockSize == 1;
                        if (!skip)
                        {
                            using JpegBitReader scanReader = new(bytes, p.Start, size);
                            JpegArithmeticDecoder? arithmetic = _header.Arithmetic ? new JpegArithmeticDecoder(scanReader, _header, p.Conditioning) : null;
                            DecodeBufferedScan(scanReader, p.Scan, p.DcTables, p.AcTables, p.RestartInterval, followers[item], arithmetic);
                        }

                        followers[item].Finish();
                    }
                    catch
                    {
                        state.Failed = true;
                        throw;
                    }
                }
            });
        }
        finally
        {
            foreach (JpegDecoder<TPixel>? twin in twins)
            {
                twin?.Dispose();
            }
        }

        if (state.Failed)
        {
            throw new SpeculationFailedException();
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

    /// <summary>
    /// One scan of a pipelined decode: its header, the Huffman tables, restart interval and arithmetic conditioning in force for
    /// it, and where its data starts.
    /// </summary>
    private sealed record PlannedScan(
        JpegScan Scan, JpegHuffmanSpec?[] DcTables, JpegHuffmanSpec?[] AcTables, int RestartInterval, int Start, (byte[] Lower, byte[] Upper, byte[] Kx) Conditioning);

    /// <summary>What every scan of a pipelined decode shares: whether one failed, and the load's cancellation.</summary>
    private sealed class PipelineState(CancellationToken cancellationToken)
    {
        private volatile bool _failed;

        internal bool Failed
        {
            get => _failed;
            set => _failed = value;
        }

        internal CancellationToken CancellationToken => cancellationToken;
    }

    /// <summary>
    /// How far one scan of a pipelined decode has come, in its own rows (MCU rows when it interleaves components, block rows of
    /// its component otherwise), and the earlier scans it must stay behind: those that code some of the same coefficients. A
    /// scan that has to wait spins briefly, then sleeps until the earlier scan says it has moved on, so waiting costs no core.
    /// </summary>
    private sealed class ScanFollower(PipelineState state, bool interleaved, (ScanFollower Scan, int V)[] waits)
    {
        private readonly object _gate = new();
        private int _rows;
        private int _sleepers;

        /// <summary>Says that the scan has decoded <paramref name="rows"/> rows, waking any scan waiting for them.</summary>
        internal void Publish(int rows)
        {
            // A full fence between the write and the read, so a scan going to sleep either sees the rows or is seen.
            Interlocked.Exchange(ref _rows, rows);
            if (Volatile.Read(ref _sleepers) > 0)
            {
                lock (_gate)
                {
                    Monitor.PulseAll(_gate);
                }
            }
        }

        /// <summary>Says that the scan is done, so no later scan waits for it.</summary>
        internal void Finish()
        {
            Publish(int.MaxValue);
        }

        /// <summary>Waits until every earlier scan this one follows has decoded the block rows of row <paramref name="row"/>.</summary>
        internal void Wait(int row)
        {
            foreach ((ScanFollower earlier, int v) in waits)
            {
                long needed = interleaved ? (row + 1L) * v : row + 1L;
                if (earlier.BlockRows(v) < needed)
                {
                    earlier.WaitFor(needed, v, state);
                }
            }
        }

        private void WaitFor(long needed, int v, PipelineState state)
        {
            SpinWait spin = default;
            while (!spin.NextSpinWillYield)
            {
                spin.SpinOnce();
                if (BlockRows(v) >= needed)
                {
                    return;
                }
            }

            lock (_gate)
            {
                Interlocked.Increment(ref _sleepers);
                try
                {
                    while (BlockRows(v) < needed)
                    {
                        if (state.Failed)
                        {
                            throw new SpeculationFailedException();
                        }

                        state.CancellationToken.ThrowIfCancellationRequested();

                        // The timeout only bounds how late a failure elsewhere or a cancellation is noticed.
                        Monitor.Wait(_gate, 20);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _sleepers);
                }
            }
        }

        /// <summary>Returns the block rows of a component sampled <paramref name="v"/> rows per MCU that this scan has finished.</summary>
        private long BlockRows(int v)
        {
            int rows = Volatile.Read(ref _rows);
            return rows == int.MaxValue ? long.MaxValue : interleaved ? (long)rows * v : rows;
        }
    }
}
