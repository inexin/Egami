namespace Egami.Formats.Av1;

/// <summary>
/// The superblock rows of one tile as the encoder's search decides them, a row at a time on any thread: a row waits until the row
/// above has decided the superblock above and to the right of the one it starts (two ahead, which covers the above-right samples,
/// the reference vector scan and the above contexts), so rows run as a wavefront. Every rate the search weighs comes from CDFs a
/// fixed rule gives, the same at any thread count: an intra frame starts each row from the CDFs the row above had after its second
/// superblock and blends each later superblock's, three parts the row's own and one part the row above's after the superblock above
/// and to the right (libaom's row-based multi-threading and av1_avg_cdf_symbols, SVT-AV1's avg_cdf_symbols; BSD); an inter frame
/// weighs every symbol at the frame's starting CDFs, which stay as they are through the frame (SVT-AV1's frame-level rate
/// estimation at its middle presets). The writer then codes the decisions in order, its CDFs adapting as the syntax says.
/// </summary>
internal sealed class Av1TileRows
{
    private readonly int[] _done;
    private readonly ushort[]?[] _saved;
    private readonly object _gate = new();

    internal Av1TileRows(Av1Frame frame, int tileRow, int tileCol, bool sequential = false)
    {
        Av1FrameHeader header = frame.Header;
        int sbMi = 1 << frame.SbMiLog2;
        MiRowStart = header.MiRowStarts[tileRow];
        int miRowEnd = Math.Min(header.MiRowStarts[tileRow + 1], frame.MiRows);
        int miColStart = header.MiColStarts[tileCol], miColEnd = Math.Min(header.MiColStarts[tileCol + 1], frame.MiCols);
        Rows = (miRowEnd - MiRowStart + sbMi - 1) / sbMi;
        Columns = (miColEnd - miColStart + sbMi - 1) / sbMi;
        _done = new int[Rows];
        Sequential = sequential;
        _saved = new ushort[]?[3 * Columns];
        (TileRow, TileCol) = (tileRow, tileCol);
        FrameCdf = frame.Context.Cdf;
    }

    /// <summary>
    /// Gets whether the rows are searched in raster order by one worker, each carrying the CDFs and quantizer of the superblock before
    /// it, as a still picture is (its sizes stay those the AVIF gates were tuned on); a stream's frames take the wavefront.
    /// </summary>
    internal bool Sequential { get; }

    internal int TileRow { get; }

    internal int TileCol { get; }

    /// <summary>Gets the tile's first row, in 4x4 units.</summary>
    internal int MiRowStart { get; }

    /// <summary>Gets the tile's superblock rows.</summary>
    internal int Rows { get; }

    /// <summary>Gets the tile's superblock columns.</summary>
    internal int Columns { get; }

    /// <summary>Gets the CDFs the frame starts from.</summary>
    internal ushort[] FrameCdf { get; }

    /// <summary>Gets the rate each row's chosen superblocks cost as their symbols adapted the CDFs, in 1/512 bits.</summary>
    internal long Rate => Interlocked.Read(ref _rate);

    /// <summary>Gets the rate the search estimated for each row's chosen superblocks, in 1/512 bits.</summary>
    internal long EstimatedRate => Interlocked.Read(ref _estimated);

    private long _rate, _estimated;

    internal void AddRates(long rate, long estimated)
    {
        Interlocked.Add(ref _rate, rate);
        Interlocked.Add(ref _estimated, estimated);
    }

    /// <summary>Waits until row <paramref name="row"/> has decided its first <paramref name="count"/> superblocks (or all of them).</summary>
    internal void WaitFor(int row, int count, CancellationToken cancellationToken)
    {
        if (row < 0)
        {
            return;
        }

        count = Math.Min(count, Columns);
        if (Volatile.Read(ref _done[row]) >= count)
        {
            return;
        }

        lock (_gate)
        {
            while (_done[row] < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, 50);
            }
        }
    }

    /// <summary>Marks a row's superblock decided.</summary>
    internal void Done(int row, int column)
    {
        lock (_gate)
        {
            Volatile.Write(ref _done[row], column + 1);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Keeps the CDFs a row of an intra frame has after one of its superblocks, for the row below.</summary>
    internal void Save(int row, int column, ushort[] cdf)
    {
        ref ushort[]? slot = ref _saved[((row % 3) * Columns) + column];
        slot ??= new ushort[cdf.Length];
        cdf.CopyTo(slot, 0);
    }

    /// <summary>Gets the CDFs <see cref="Save"/> kept for a superblock of the row above.</summary>
    internal ushort[] Saved(int row, int column) => _saved[((row % 3) * Columns) + Math.Min(column, Columns - 1)]!;
}

/// <summary>The row by row search of <see cref="Av1TileEncoder"/> (<see cref="Av1TileRows"/>).</summary>
internal sealed partial class Av1TileEncoder
{
    /// <summary>Decides and reconstructs one superblock row of the tile, the rows above it deciding as it goes.</summary>
    internal void EncodeRow(Av1TileRows rows, int row)
    {
        int miRow = MiRowStart + (row * _sbMi);
        bool intra = _header.FrameIsIntra;
        _rowWavefront = !rows.Sequential;
        if (rows.Sequential)
        {
            // A still picture's rows in raster order, the CDFs and quantizer carried from each superblock to the next.
            ResetLeftContext();
            if (row == 0)
            {
                ResetSearchMemory();
            }
        }
        else
        {
            ResetRow();
            ResetSearchMemory();
        }

        if (!intra)
        {
            rows.FrameCdf.CopyTo(_cdf, 0);
            _costs.Fill(_cdf);
        }

        long rate = 0, estimated = 0;
        int column = 0;
        for (int miCol = MiColStart; miCol < MiColEnd; miCol += _sbMi, column++)
        {
            _frame.CancellationToken.ThrowIfCancellationRequested();
            rows.WaitFor(row - 1, column + 2, _frame.CancellationToken);
            if (intra && rows.Sequential)
            {
                if (row == 0 && column == 0)
                {
                    rows.FrameCdf.CopyTo(_cdf, 0);
                }

                _costs.Fill(_cdf);
            }
            else if (intra)
            {
                if (column == 0)
                {
                    (row == 0 ? rows.FrameCdf : rows.Saved(row - 1, 1)).CopyTo(_cdf, 0);
                }
                else if (row > 0)
                {
                    Blend(_cdf, rows.Saved(row - 1, column + 1));
                }

                _costs.Fill(_cdf);
            }

            // The superblock's quantizer (Variance Boost), a whole number of steps from the frame's.
            int[]? sbQ = _source.SuperblockQIndices;
            _sbQIndex = sbQ is null ? _header.BaseQIdx
                : Av1VarianceBoost.Adjust(_header.DeltaQRes, rows.Sequential ? _currentQIndex : _header.BaseQIdx, sbQ[((miRow / _sbMi) * _source.SuperblockColumns) + (miCol / _sbMi)]);
            _frameRdmult = _settings.RdMultiplier(Math.Max(_sbQIndex, 1), _sequence.BitDepth);
            Snapshot start = Take(miRow, miCol, _frame.SbSize, pixels: false);

            SearchPartition(miRow, miCol, _frame.SbSize, long.MaxValue);

            // Code the chosen superblock from its starting contexts: first as the search saw it, then, in an intra frame, adapting
            // the CDFs as the writer will.
            start.RestoreContexts(this);
            Av1CostSink estimate = new(false);
            CodePartition(ref estimate, miRow, miCol, _frame.SbSize);
            estimated += estimate.Cost;
            if (intra)
            {
                start.RestoreContexts(this);
                Av1CostSink adapt = new(true);
                CodePartition(ref adapt, miRow, miCol, _frame.SbSize);
                rate += adapt.Cost;
                if (row + 1 < rows.Rows)
                {
                    rows.Save(row, column, _cdf);
                }
            }

            Give(start);
            rows.Done(row, column);
        }

        rows.AddRates(rate, estimated);
    }

    /// <summary>Blends CDFs with the row above's: three parts these, one part those.</summary>
    private static void Blend(ushort[] cdf, ushort[] above)
    {
        for (int i = 0; i < cdf.Length; i++)
        {
            cdf[i] = (ushort)(((3 * cdf[i]) + above[i] + 2) >> 2);
        }
    }

    /// <summary>
    /// Gives each superblock coded whole without residual the quantizer of the one before it in the tile, which is the quantizer the
    /// decoder keeps for it, once every row is decided (a row's search starts from the frame's).
    /// </summary>
    internal void SettleQuantizers()
    {
        if (!_header.DeltaQPresent)
        {
            return;
        }

        int current = _header.BaseQIdx;
        for (int miRow = MiRowStart; miRow < MiRowEnd; miRow += _sbMi)
        {
            for (int miCol = MiColStart; miCol < MiColEnd; miCol += _sbMi)
            {
                Av1BlockInfo b = _frame.Grid[(miRow * _miCols) + miCol]!;
                if (b.Skip && b.Size == _frame.SbSize)
                {
                    b.QIndex = current;
                }
                else
                {
                    current = b.QIndex;
                }
            }
        }
    }
}
