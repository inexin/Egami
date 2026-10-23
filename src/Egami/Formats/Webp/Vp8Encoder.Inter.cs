using System.Buffers;

namespace Egami.Formats.Webp;

/// <summary>
/// Inter frames (RFC 6386 sections 9.7 to 9.11 and 16 to 19): each macroblock searches the last, golden and alternate references
/// for a vector, prices ZERO, NEAREST, NEAR, NEW and SPLIT by rate and distortion against the intra modes, and the frame header
/// carries the reference flags and the mode and vector probability updates that save bits. Predictions come from the decoder's
/// own predictor, so the encoder's references are the decoder's.
/// </summary>
internal sealed partial class Vp8Encoder
{
    private const int InterSlot = 4;

    // The largest vector difference a frame codes, in quarter pixels, and the cost table of one component.
    private const int MvLimit = 1023;
    private const int MvCostSize = (2 * MvLimit) + 1;
    private const int Impossible = 1 << 28;

    // The rate weight against the squared error: λ = q² × LambdaScale / 256 per bit, q the luma AC step.
    private const int LambdaScale = 4;

    // How much the best 16 × 16 vector's prediction error may pass q² before the split layouts are tried.
    private const int SplitGate = 2;

    private static readonly int[] NoSpectrum = new int[16];

    private static byte[][] BuildCandidateBuffers()
    {
        byte[][] buffers = new byte[12][];
        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i] = new byte[16 * S];
        }

        return buffers;
    }
    private static readonly ushort[] InterIntra4Costs = BuildInterIntra4Costs();

    private Vp8InterFrame? _inter;

    // The frame's quantizer with the inter residuals' rounding.
    private readonly Segment _interDqm = new();
    private Vp8ModeInfo? _info;
    private byte[] _partModes = [];
    private readonly ModeScore _rdInter = new();
    private readonly ModeScore _rdTrial = new();
    private byte[] _interOut = new byte[16 * S];
    private byte[] _interTrial = new byte[16 * S];
    private readonly byte[] _searchPred = new byte[16 * S];

    // The best prediction of the last 16 × 16 search, the candidates' luma predictions, and a split's.
    private readonly byte[] _searchBest = new byte[16 * S];
    private readonly byte[][] _candidatePred = BuildCandidateBuffers();
    private readonly byte[] _splitPred = new byte[16 * S];
    private readonly int[] _trialSub = new int[16];
    private readonly byte[] _trialPartModes = new byte[16];
    private readonly int[] _bestSub = new int[16];
    private readonly byte[] _bestPartModes = new byte[16];

    private readonly int[] _mvCost = new int[2 * MvCostSize];
    private readonly int[] _mvCounts = new int[2 * 2 * Vp8InterTables.MvProbCount];
    private readonly int[] _refCounts = new int[4];
    private readonly int[] _yModeCounts = new int[5];
    private readonly int[] _uvModeCounts = new int[4];
    private readonly byte[] _mvProbs = new byte[2 * Vp8InterTables.MvProbCount];
    private readonly bool[] _mvUpdate = new bool[2 * Vp8InterTables.MvProbCount];
    private readonly byte[] _yModeProbs = new byte[4];
    private readonly byte[] _uvModeProbs = new byte[3];
    private bool _updateYModes;
    private bool _updateUvModes;
    private int _lambda;
    private int _lambdaSad;

    // The intra, last and golden probabilities: estimates from the frame before while modes are chosen, then the frame's own.
    private int _probIntra = 63;
    private int _probLast = 128;
    private int _probGolden = 128;

    /// <summary>Gets which macroblocks of the frame encoded last have no coefficients, in raster order.</summary>
    internal bool[] ZeroFlags => _zero;

    /// <summary>Gets the intra macroblocks of the inter frame encoded last.</summary>
    internal int IntraMacroblocks { get; private set; }

    /// <summary>Gets the macroblocks without coefficients of the frame encoded last.</summary>
    internal int ZeroMacroblocks
    {
        get
        {
            int count = 0;
            foreach (bool z in _zero)
            {
                count += z ? 1 : 0;
            }

            return count;
        }
    }

    /// <summary>Encodes the planes as an inter frame of a video stream, against <paramref name="inter"/>'s references, and returns the frame.</summary>
    internal byte[] EncodeInter(Vp8EncoderOptions options, Vp8InterFrame inter)
    {
        _options = options;
        _inter = inter;
        inter.State.CoefficientProbabilities.CopyTo(_baseProbs);
        _maxI4HeaderBits = 256 * 16 * 16;
        foreach (Segment m in _dqm)
        {
            m.Alpha = 0;
            m.Beta = 0;
        }

        _segmentCount = 1;
        _uvAlpha = MidAlpha;
        Array.Clear(_segment);
        SetSegmentParams();
        _updateMap = false;
        Segment seg = _dqm[0];
        int qi = seg.Quant;
        _interDqm.Quant = qi;
        _interDqm.Y1.Set(Vp8Tables.DcTable[qi], Vp8Tables.AcTable[qi], 0, inter: true);
        _interDqm.Y2.Set(Vp8Tables.DcTable[qi] * 2, AcTable2[qi], 1, inter: true);
        _interDqm.Uv.Set(Vp8Tables.DcTable[Math.Clamp(qi + _dqUvDc, 0, 117)], Vp8Tables.AcTable[Math.Clamp(qi + _dqUvAc, 0, 127)], 2, inter: true);
        _interDqm.LambdaTrellisI4 = seg.LambdaTrellisI4;
        _interDqm.LambdaTrellisI16 = seg.LambdaTrellisI16;
        _interDqm.LambdaTrellisUv = seg.LambdaTrellisUv;
        _info ??= new Vp8ModeInfo(_mbw, _mbh);
        if (_partModes.Length == 0)
        {
            _partModes = new byte[_mbw * _mbh * 16];
        }

        int q = Vp8Tables.AcTable[_baseQuant];
        _lambda = Math.Max(1, (q * q * LambdaScale) >> 8);
        _lambdaSad = Math.Max(1, (int)Math.Sqrt(_lambda));
        BuildMvCosts(inter.State.MvProbabilities);
        try
        {
            return EncodeFrame();
        }
        finally
        {
            ReleaseTokens();
        }
    }

    // The row workers of inter frames (worker 0 is this encoder), how far each macroblock row has come, which worker coded
    // each row and where its tokens end in that worker's buffer.
    private Vp8Encoder[] _workers = [];
    private int[] _rowProgress = [];
    private int[] _rowWorker = [];
    private int[] _rowTokenEnd = [];
    private int _nextRow;

    /// <summary>
    /// A row worker for inter frames: it shares <paramref name="master"/>'s frame (the source, the per-macroblock choices, the
    /// rows above's context, the rate tables) and has work buffers, left contexts, counts and tokens of its own.
    /// </summary>
    private Vp8Encoder(Vp8Encoder master)
    {
        _src = master._src;
        _options = master._options;
        _mbw = master._mbw;
        _mbh = master._mbh;
        _dqm = master._dqm;
        _mbHeaderLimit = master._mbHeaderLimit;
        _segment = master._segment;
        _isI16 = master._isI16;
        _yMode = master._yMode;
        _uvMode = master._uvMode;
        _modes = master._modes;
        _zero = master._zero;
        _alpha = master._alpha;
        _topY = master._topY;
        _topU = master._topU;
        _topV = master._topV;
        _topNz = master._topNz;
        _modesAbove = master._modesAbove;
        _topDerr = master._topDerr;
        _tokenStart = master._tokenStart;
        _costs = master._costs;
        _interDqm = master._interDqm;
        _mvCost = master._mvCost;
    }

    /// <summary>Gets or sets the decoder whose threads share a video frame's macroblock rows; null for one thread.</summary>
    internal Vp8VideoDecoder? Pool { get; set; }

    private void InterLoop()
    {
        CodeRows();
        ChooseInterProbabilities();
    }

    /// <summary>
    /// Chooses and records every macroblock of a video frame. Rows go to the workers as they come free, a wavefront: a
    /// macroblock waits until the row above has finished the one above-right of it, the last neighbour it reads. A macroblock's
    /// choices depend only on its neighbours and the frame, so the frame is the same on any number of threads.
    /// </summary>
    private void CodeRows()
    {
        Array.Clear(_topNz);
        Array.Clear(_modesAbove);
        Array.Clear(_topDerr);
        Array.Clear(_zero);
        foreach (Segment m in _dqm)
        {
            m.MaxEdge = 0;
        }

        // The rates come from the probabilities the frame starts from throughout: estimates that followed the frame's own
        // statistics would make each macroblock depend on all before it.
        _costs.Update(_baseProbs);
        Vp8VideoDecoder? pool = Pool;
        int threads = pool is null ? 1 : Math.Min(pool.Threads, _mbh);
        if (_rowProgress.Length != _mbh)
        {
            _rowProgress = new int[_mbh];
            _rowWorker = new int[_mbh];
            _rowTokenEnd = new int[_mbh];
        }

        if (_workers.Length < threads)
        {
            Vp8Encoder[] workers = new Vp8Encoder[threads];
            workers[0] = this;
            for (int w = 1; w < threads; w++)
            {
                workers[w] = w < _workers.Length ? _workers[w] : new Vp8Encoder(this);
            }

            _workers = workers;
        }

        StartRows(this, threads);
        for (int w = 1; w < threads; w++)
        {
            _workers[w].StartRows(this, threads);
        }

        _nextRow = 0;
        if (threads == 1)
        {
            CodeRows(this, 0, null);
        }
        else
        {
            Array.Clear(_rowProgress);
            pool!.RunWorkers(threads, w => _workers[w].CodeRows(this, w, pool));
            MergeRows(threads);
        }

        _tokenStart[_mbw * _mbh] = _tokenCount;
    }

    /// <summary>Takes <paramref name="master"/>'s frame settings for a worker and clears its counts and tokens.</summary>
    private void StartRows(Vp8Encoder master, int threads)
    {
        Array.Clear(_mvCounts);
        Array.Clear(_refCounts);
        Array.Clear(_yModeCounts);
        Array.Clear(_uvModeCounts);
        IntraMacroblocks = 0;
        _useDerr = false;
        if (master == this)
        {
            ResetTokens();
            return;
        }

        _options = master._options;
        _inter = master._inter;
        _info = master._info;
        _partModes = master._partModes;
        _baseQuant = master._baseQuant;
        _dqUvAc = master._dqUvAc;
        _dqUvDc = master._dqUvDc;
        _uvAlpha = master._uvAlpha;
        _segmentCount = master._segmentCount;
        _maxI4HeaderBits = master._maxI4HeaderBits;
        _lambda = master._lambda;
        _lambdaSad = master._lambdaSad;
        _probIntra = master._probIntra;
        _probLast = master._probLast;
        _probGolden = master._probGolden;
        Reconstruction = master.Reconstruction;
        ResetTokens(Math.Max(4096, _mbw * _mbh * 64 / threads));
    }

    /// <summary>Codes macroblock rows as they come free until none is left; <paramref name="pool"/> is null on one thread.</summary>
    private void CodeRows(Vp8Encoder master, int worker, Vp8VideoDecoder? pool)
    {
        int[] progress = master._rowProgress;
        bool key = _inter is null;
        bool errorDiffusion = _options.Quality <= 98;
        while (true)
        {
            int mby = Interlocked.Increment(ref master._nextRow) - 1;
            if (mby >= _mbh)
            {
                return;
            }

            master._rowWorker[mby] = worker;
            Array.Clear(_leftNz);
            Array.Clear(_modesLeft);
            Array.Clear(_leftDerr);
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                if (pool is not null && mby > 0)
                {
                    pool.WaitFor(progress, mby - 1, Math.Min(mbx + 2, _mbw));
                }

                int mb = (mby * _mbw) + mbx;
                Import(mbx, mby);
                ReconstructedContext(mbx, mby);
                MakePredictions(mbx, mby);
                _topNz.AsSpan(mbx * 9, 9).CopyTo(_nzTop);
                _leftNz.CopyTo(_nzLeft);
                if (key)
                {
                    Decimate(mbx, mby, mb, errorDiffusion);
                }
                else
                {
                    DecideInter(mbx, mby, mb);
                }

                RecordMacroblock(mbx, mb);
                SaveBoundary(mbx, mby, mb);
                if (pool is not null)
                {
                    Volatile.Write(ref progress[mby], mbx + 1);
                }
            }

            master._rowTokenEnd[mby] = _tokenCount;
        }
    }

    /// <summary>Gathers the workers' tokens into this encoder's buffer in row order, and adds up their statistics and counts.</summary>
    private void MergeRows(int threads)
    {
        int total = 0;
        for (int w = 0; w < threads; w++)
        {
            total += _workers[w]._tokenCount;
        }

        ushort[] merged = ArrayPool<ushort>.Shared.Rent(Math.Max(total, 4096));
        int at = 0;
        for (int mby = 0; mby < _mbh; mby++)
        {
            Vp8Encoder owner = _workers[_rowWorker[mby]];
            int first = mby * _mbw;
            int start = _tokenStart[first], end = _rowTokenEnd[mby];
            owner._tokens.AsSpan(start, end - start).CopyTo(merged.AsSpan(at));
            int shift = at - start;
            for (int mb = first; mb < first + _mbw; mb++)
            {
                _tokenStart[mb] += shift;
            }

            at += end - start;
        }

        ArrayPool<ushort>.Shared.Return(_tokens);
        _tokens = merged;
        _tokenCount = total;
        for (int w = 1; w < threads; w++)
        {
            Vp8Encoder worker = _workers[w];
            Add(_totals, worker._totals);
            Add(_ones, worker._ones);
            Add(_zeroTotals, worker._zeroTotals);
            Add(_zeroOnes, worker._zeroOnes);
            Add(_mvCounts, worker._mvCounts);
            Add(_refCounts, worker._refCounts);
            Add(_yModeCounts, worker._yModeCounts);
            Add(_uvModeCounts, worker._uvModeCounts);
            IntraMacroblocks += worker.IntraMacroblocks;
        }

        static void Add(int[] to, int[] from)
        {
            for (int i = 0; i < to.Length; i++)
            {
                to[i] += from[i];
            }
        }
    }

    // --- Mode choice -------------------------------------------------------------------------------------------------------

    /// <summary>A candidate 16 × 16 vector mode and what it costs.</summary>
    private struct Candidate
    {
        internal int Reference;
        internal int Mode;
        internal int Mv;
        internal long Header;
        internal long Quick;
        internal int Sse;

        // Where its luma prediction is kept once measured (in _candidatePred).
        internal int Slot;
    }

    /// <summary>What the near vector search finds for a macroblock and reference, and the counts that pick the mode probabilities.</summary>
    private struct NearVectors
    {
        internal int Nearest;
        internal int Near;
        internal int Best;
        internal int Count0;
        internal int Count1;
        internal int Count2;
        internal int Splits;
    }

    private void DecideInter(int mbx, int mby, int mb)
    {
        Vp8InterFrame inter = _inter!;
        Vp8ModeInfo info = _info!;
        Segment dqm = _interDqm;
        int here = info.Index(mbx, mby);
        _doTrellis = inter.Trellis;
        _rdInterBest = long.MaxValue;

        Span<Candidate> candidates = stackalloc Candidate[12];
        Span<NearVectors> nears = stackalloc NearVectors[4];
        int n = 0;
        for (int r = 1; r <= 3; r++)
        {
            Vp8FrameBuffer? frame = inter.References[r];
            if (frame is null || (r > 1 && (frame == inter.References[1] || (r == 3 && frame == inter.References[2]))))
            {
                continue;
            }

            NearVectors near = FindNear(mbx, mby, r);
            nears[r] = near;
            long refCost = RefCost(r);
            int start = n;
            Add(candidates, ref n, start, r, Vp8ModeInfo.Zero, 0, refCost + VectorModeCost(Vp8ModeInfo.Zero, near));
            Add(candidates, ref n, start, r, Vp8ModeInfo.Nearest, near.Nearest, refCost + VectorModeCost(Vp8ModeInfo.Nearest, near));
            Add(candidates, ref n, start, r, Vp8ModeInfo.Near, near.Near, refCost + VectorModeCost(Vp8ModeInfo.Near, near));

            // A second reference is searched only where its near vectors come close to the best prediction of the ones before.
            bool search = start == 0 || inter.ReferenceGate == 0;
            if (!search)
            {
                long before = long.MaxValue, near3 = long.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    long sse = CandidateSse(ref candidates[i], mbx, mby);
                    if (i < start)
                    {
                        before = Math.Min(before, sse);
                    }
                    else
                    {
                        near3 = Math.Min(near3, sse);
                    }
                }

                search = 4 * near3 < inter.ReferenceGate * before;
            }

            if (search)
            {
                int previous = 0;
                if (inter.Previous is { } p && p.Reference[here] == r)
                {
                    previous = p.Mv[here];
                }

                int found = Search(frame, mbx, mby, 0, 0, 16, 16, near.Best, [near.Best, near.Nearest, near.Near, previous, 0], inter.SearchRange, out int foundSse);
                int at = Add(candidates, ref n, start, r, Vp8ModeInfo.New, found, refCost + VectorModeCost(Vp8ModeInfo.New, near) + MvCost(found, near.Best));
                if (candidates[at].Sse < 0 && foundSse >= 0)
                {
                    // The search's own prediction of its vector.
                    candidates[at].Sse = foundSse;
                    _searchBest.AsSpan().CopyTo(_candidatePred[candidates[at].Slot]);
                }
            }
        }

        // Rank by the luma prediction error, then code the best few in full.
        for (int i = 0; i < n; i++)
        {
            ref Candidate c = ref candidates[i];
            c.Quick = ((long)CandidateSse(ref c, mbx, mby) << 8) + (_lambda * c.Header);
        }

        for (int i = 1; i < n; i++)
        {
            Candidate c = candidates[i];
            int j = i - 1;
            while (j >= 0 && candidates[j].Quick > c.Quick)
            {
                candidates[j + 1] = candidates[j];
                j--;
            }

            candidates[j + 1] = c;
        }

        long bestScore = long.MaxValue;
        Candidate best = default;
        int bestLayout = -1;
        for (int i = 0; i < Math.Min(n, inter.Candidates); i++)
        {
            Candidate c = candidates[i];
            SetVector(here, c.Reference, c.Mode, c.Mv);
            long score = CodeInter(mbx, mby, c.Reference, dqm, c.Slot, c.Header);
            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        // Splits where the best 16 × 16 vector leaves much to code.
        int qs = Vp8Tables.AcTable[_baseQuant];
        if (inter.Split > 0 && n > 0 && _rdInter.Nz != 0 && candidates[0].Sse > SplitGate * qs * qs)
        {
            // Splits of the best reference, from its searched vector.
            int r = best.Reference;
            int seed = best.Mv;
            for (int i = 0; i < n; i++)
            {
                if (candidates[i].Reference == r && candidates[i].Mode == Vp8ModeInfo.New)
                {
                    seed = candidates[i].Mv;
                }
            }

            int layouts = inter.Split >= 2 ? 4 : 3;
            for (int layout = 0; layout < layouts; layout++)
            {
                long header = RefCost(r) + VectorModeCost(Vp8ModeInfo.Split, nears[r]) + LayoutCost(layout);
                header += PlanSplit(mbx, mby, r, nears[r], layout, seed);
                SetSplit(here, r, layout, _trialSub);
                long score = CodeInter(mbx, mby, r, dqm, -1, header);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = new Candidate { Reference = r, Mode = Vp8ModeInfo.Split };
                    bestLayout = layout;
                    _trialSub.CopyTo(_bestSub, 0);
                    _trialPartModes.CopyTo(_bestPartModes, 0);
                }
            }
        }

        // Intra only where a 16 × 16 intra prediction comes near the best vector's, and the vector leaves enough to code that it
        // might lose.
        bool tryIntra = n == 0 || inter.IntraGate == 0;
        if (!tryIntra && 4L * candidates[0].Sse >= (long)inter.IntraFloor * qs * qs)
        {
            int intra = int.MaxValue;
            for (int mode = 0; mode < 4; mode++)
            {
                intra = Math.Min(intra, Vp8EncDsp.Sse(_yuvIn, _predY16[mode], 0, 16, 16));
            }

            tryIntra = intra < inter.IntraGate * (long)candidates[0].Sse;
        }
        if (tryIntra)
        {
            Decimate(mbx, mby, mb, errorDiffusion: false);
            ModeScore rd = _rd;
            long d = Vp8EncDsp.Sse(_yuvIn, _yuvOut, 0, 16, 16) + Vp8EncDsp.Sse(_yuvIn, _yuvOut, UOff, 16, 8);
            long rate = rd.Nz == 0 ? 0 : TokenCost(rd, _isI16[mb]);
            long score = (256 * d) + (_lambda * (rate + IntraHeaderCost(mb)));
            if (score <= bestScore)
            {
                CommitIntra(here, mb);
                return;
            }
        }

        CommitInter(here, mb, best, bestLayout, nears[best.Reference]);
    }

    /// <summary>
    /// Adds a candidate unless one of the same reference already has its vector, which then takes the mode if its header costs
    /// less; returns where the candidate with the vector is.
    /// </summary>
    private static int Add(Span<Candidate> candidates, ref int n, int start, int reference, int mode, int mv, long header)
    {
        for (int i = start; i < n; i++)
        {
            if (candidates[i].Mv == mv)
            {
                if (header < candidates[i].Header)
                {
                    candidates[i].Mode = mode;
                    candidates[i].Header = header;
                }

                return i;
            }
        }

        candidates[n] = new Candidate { Reference = reference, Mode = mode, Mv = mv, Header = header, Sse = -1, Slot = n };
        return n++;
    }

    /// <summary>The luma squared error of a 16 × 16 candidate's prediction, measured once.</summary>
    private int CandidateSse(ref Candidate c, int mbx, int mby)
    {
        if (c.Sse < 0)
        {
            Vp8FrameBuffer frame = _inter!.References[c.Reference]!;
            byte[] pred = _candidatePred[c.Slot];
            Vp8InterPredict.Predict(frame.Y, frame.OriginY, frame.StrideY, frame.AlignedWidth, frame.AlignedHeight, Vp8FrameBuffer.Border, mbx * 16, mby * 16, 16, 16, Vp8ModeInfo.Row(c.Mv), Vp8ModeInfo.Col(c.Mv), true, pred, 0, S);
            c.Sse = Vp8EncDsp.Sse(_yuvIn, pred, 0, 16, 16);
        }

        return c.Sse;
    }

    /// <summary>
    /// Predicts the macroblock as <see cref="_info"/> now describes it, codes the residual into <see cref="_interTrial"/> and
    /// <see cref="_rdTrial"/>, and keeps it in <see cref="_interOut"/> and <see cref="_rdInter"/> when it is the best so far;
    /// returns the rate-distortion score. A 16 × 16 vector's luma prediction is the candidate's in <paramref name="slot"/>; −1 for
    /// a split.
    /// </summary>
    private long CodeInter(int mbx, int mby, int reference, Segment dqm, int slot, long header)
    {
        Vp8ModeInfo info = _info!;
        Vp8FrameBuffer frame = _inter!.References[reference]!;
        byte[] predUv = _predUv[InterSlot];
        bool split = slot < 0;
        byte[] predY = _predY16[InterSlot] = split ? _splitPred : _candidatePred[slot];
        Vp8Decoder.PredictMacroblock(info, info.Index(mbx, mby), frame, mbx, mby, _mbw, _mbh, 0, predY, 0, S, predUv.AsSpan(UOff), predUv.AsSpan(VOff), 0, S, luma: split);
        ModeScore cur = _rdTrial;
        cur.Init();
        long d;
        int nz;
        if (split)
        {
            nz = ReconstructSplit(dqm, cur, _interTrial);
            d = Vp8EncDsp.Sse(_yuvIn, _interTrial, 0, 16, 16);
        }
        else if (!_doTrellis && Vp8TransformSimd.IsSupported)
        {
            nz = ReconstructIntra16Measured(dqm, cur, _interTrial, InterSlot, NoSpectrum, out long sse, out _, out _);
            d = sse;
        }
        else
        {
            nz = ReconstructIntra16(dqm, cur, _interTrial, InterSlot);
            d = Vp8EncDsp.Sse(_yuvIn, _interTrial, 0, 16, 16);
        }

        nz |= ReconstructUv(mbx, dqm, cur, _interTrial, InterSlot, out long dUv, out _);
        if (dUv < 0)
        {
            dUv = Vp8EncDsp.Sse(_yuvIn, _interTrial, UOff, 16, 8);
        }

        cur.Nz = nz;
        cur.D = d + dUv;
        long rate = nz == 0 ? 0 : TokenCost(cur, !split);
        long score = (256 * cur.D) + (_lambda * (rate + header));
        cur.Score = score;
        if (score < _rdInterBest)
        {
            _rdInterBest = score;
            _rdInter.CopyAllFrom(cur);
            (_interOut, _interTrial) = (_interTrial, _interOut);
        }

        return score;
    }

    // The score of the macroblock's best inter coding so far, in _rdInter.
    private long _rdInterBest = long.MaxValue;

    /// <summary>Codes the luma of a split macroblock: sixteen 4 × 4 blocks against the prediction, without the Y2 block.</summary>
    private int ReconstructSplit(Segment dqm, ModeScore rd, byte[] output)
    {
        byte[] pred = _predY16[InterSlot];
        Span<short> tmp = stackalloc short[16];
        Span<byte> t = stackalloc byte[4];
        Span<byte> l = stackalloc byte[4];
        _nzTop.AsSpan(0, 4).CopyTo(t);
        _nzLeft.AsSpan(0, 4).CopyTo(l);
        int nz = 0;
        for (int n = 0; n < 16; n++)
        {
            int at = ScanY[n];
            int x = n & 3, y = n >> 2;
            Vp8EncDsp.ForwardTransform(_yuvIn, at, pred, at, tmp);
            Span<short> levels = rd.YAc.AsSpan(n * 16, 16);
            bool any = _doTrellis
                ? TrellisQuantize(tmp, levels, t[x] + l[y], 3, dqm.Y1, dqm.LambdaTrellisI4)
                : Vp8EncDsp.Quantize(tmp, levels, dqm.Y1);
            t[x] = l[y] = (byte)(any ? 1 : 0);
            if (any)
            {
                nz |= 1 << n;
            }

            Vp8EncDsp.Copy(pred, at, output, at, 4, 4);
            Vp8Dsp.InverseTransformAdd(tmp, output, at);
        }

        return nz;
    }

    /// <summary>The token cost of a macroblock's levels, luma with or without the Y2 block, with the working non-zero contexts.</summary>
    private long TokenCost(ModeScore rd, bool y2)
    {
        Span<byte> t = stackalloc byte[9];
        Span<byte> l = stackalloc byte[9];
        _nzTop.CopyTo(t);
        _nzLeft.CopyTo(l);
        long r = 0;
        int type = 3, first = 0;
        if (y2)
        {
            r += _costs.Residual(1, 0, t[8] + l[8], rd.YDc);
            type = 0;
            first = 1;
        }

        for (int n = 0; n < 16; n++)
        {
            int x = n & 3, y = n >> 2;
            ReadOnlySpan<short> levels = rd.YAc.AsSpan(n * 16, 16);
            r += _costs.Residual(type, first, t[x] + l[y], levels);
            t[x] = l[y] = (byte)(Vp8LevelCosts.LastNonZero(levels, first) >= 0 ? 1 : 0);
        }

        for (int ch = 0; ch <= 2; ch += 2)
        {
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 2; x++)
                {
                    ReadOnlySpan<short> levels = rd.UvLevels.AsSpan(((ch * 2) + (y * 2) + x) * 16, 16);
                    r += _costs.Residual(2, 0, t[4 + ch + x] + l[4 + ch + y], levels);
                    t[4 + ch + x] = l[4 + ch + y] = (byte)(Vp8LevelCosts.LastNonZero(levels, 0) >= 0 ? 1 : 0);
                }
            }
        }

        return r;
    }

    private void SetVector(int here, int reference, int mode, int mv)
    {
        Vp8ModeInfo info = _info!;
        info.Reference[here] = (byte)reference;
        info.Mode[here] = (byte)mode;
        info.UvMode[here] = 0;
        info.Mv[here] = mv;
        info.NeedsClamp[here] = 0;
    }

    private void SetSplit(int here, int reference, int layout, ReadOnlySpan<int> sub)
    {
        Vp8ModeInfo info = _info!;
        info.Reference[here] = (byte)reference;
        info.Mode[here] = Vp8ModeInfo.Split;
        info.UvMode[here] = 0;
        info.Partitioning[here] = (byte)layout;
        sub.CopyTo(info.SubMv.AsSpan(here * 16, 16));
        info.Mv[here] = sub[15];
        info.NeedsClamp[here] = 0;
    }

    private void CommitIntra(int here, int mb)
    {
        Vp8ModeInfo info = _info!;
        int mode = _isI16[mb] ? _yMode[mb] : Vp8ModeInfo.BPred;
        info.Reference[here] = 0;
        info.Mode[here] = (byte)mode;
        info.UvMode[here] = _uvMode[mb];
        info.Mv[here] = 0;
        info.NeedsClamp[here] = 0;
        _zero[mb] = _rd.Nz == 0;
        _refCounts[0]++;
        _yModeCounts[mode]++;
        _uvModeCounts[_uvMode[mb]]++;
        IntraMacroblocks++;
    }

    private void CommitInter(int here, int mb, Candidate best, int layout, NearVectors near)
    {
        bool split = best.Mode == Vp8ModeInfo.Split;
        if (split)
        {
            SetSplit(here, best.Reference, layout, _bestSub);
            _bestPartModes.CopyTo(_partModes, mb * 16);
            int parts = Vp8InterTables.SplitParts[layout];
            for (int j = 0; j < parts; j++)
            {
                if (_bestPartModes[j] == 3)
                {
                    CountMv(_bestSub[Vp8InterTables.SplitOffset[(layout * 16) + j]], near.Best);
                }
            }

            _modes.AsSpan(mb * 16, 16).Clear(); // B_DC_PRED, for the neighbours' 4 × 4 contexts
        }
        else
        {
            SetVector(here, best.Reference, best.Mode, best.Mv);
            if (best.Mode == Vp8ModeInfo.New)
            {
                CountMv(best.Mv, near.Best);
            }
        }

        _rd.CopyAllFrom(_rdInter);
        _interOut.AsSpan().CopyTo(_yuvOut);
        _isI16[mb] = !split;
        _yMode[mb] = 0;
        _uvMode[mb] = 0;
        _zero[mb] = _rd.Nz == 0;
        _refCounts[best.Reference]++;
        _rdInterBest = long.MaxValue;
    }

    // --- Near vectors and header costs -------------------------------------------------------------------------------------

    private bool SignBias(int reference) => reference == 2 ? _inter!.GoldenSignBias : reference == 3 && _inter!.AlternateSignBias;

    /// <summary>The near vector search of RFC 6386 section 16.3, as the decoder runs it on the modes chosen so far.</summary>
    private NearVectors FindNear(int mbx, int mby, int reference)
    {
        Vp8ModeInfo info = _info!;
        int here = info.Index(mbx, mby);
        int above = here - info.Stride;
        int left = here - 1;
        int aboveLeft = above - 1;
        bool bias = SignBias(reference);
        Span<int> near = [0, 0, 0, 0];
        Span<int> count = [0, 0, 0, 0];
        int n = 0;
        if (info.Reference[above] != 0)
        {
            int mv = info.Mv[above];
            if (mv != 0)
            {
                near[++n] = Vp8Decoder.Bias(mv, SignBias(info.Reference[above]) != bias);
            }

            count[n] += 2;
        }

        if (info.Reference[left] != 0)
        {
            int mv = info.Mv[left];
            if (mv != 0)
            {
                int biased = Vp8Decoder.Bias(mv, SignBias(info.Reference[left]) != bias);
                if (biased != near[n])
                {
                    near[++n] = biased;
                }

                count[n] += 2;
            }
            else
            {
                count[0] += 2;
            }
        }

        if (info.Reference[aboveLeft] != 0)
        {
            int mv = info.Mv[aboveLeft];
            if (mv != 0)
            {
                int biased = Vp8Decoder.Bias(mv, SignBias(info.Reference[aboveLeft]) != bias);
                if (biased != near[n])
                {
                    near[++n] = biased;
                }

                count[n] += 1;
            }
            else
            {
                count[0] += 1;
            }
        }

        NearVectors result = default;
        result.Count0 = count[0];
        if (count[3] > 0 && near[n] == near[1])
        {
            count[1]++;
        }

        if (count[2] > count[1])
        {
            (count[1], count[2]) = (count[2], count[1]);
            (near[1], near[2]) = (near[2], near[1]);
        }

        int toLeft = -(mbx * 16) << 3;
        int toRight = ((_mbw - 1 - mbx) * 16) << 3;
        int toTop = -(mby * 16) << 3;
        int toBottom = ((_mbh - 1 - mby) * 16) << 3;
        result.Nearest = Vp8Decoder.ClampNear(near[1], toLeft, toRight, toTop, toBottom);
        result.Near = Vp8Decoder.ClampNear(near[2], toLeft, toRight, toTop, toBottom);
        result.Best = Vp8Decoder.ClampNear(near[count[1] >= count[0] ? 1 : 0], toLeft, toRight, toTop, toBottom);
        result.Count1 = count[1];
        result.Count2 = count[2];
        result.Splits = (info.Mode[above] == Vp8ModeInfo.Split ? 2 : 0) + (info.Mode[left] == Vp8ModeInfo.Split ? 2 : 0) + (info.Mode[aboveLeft] == Vp8ModeInfo.Split ? 1 : 0);
        return result;
    }

    private long RefCost(int reference) =>
        Vp8Costs.Bit(1, _probIntra) + (reference == 1 ? Vp8Costs.Bit(0, _probLast) : Vp8Costs.Bit(1, _probLast) + Vp8Costs.Bit(reference == 3 ? 1 : 0, _probGolden));

    private static long VectorModeCost(int mode, in NearVectors near)
    {
        ReadOnlySpan<byte> ctx = Vp8InterTables.ModeContexts;
        if (mode == Vp8ModeInfo.Zero)
        {
            return Vp8Costs.Bit(0, ctx[near.Count0 * 4]);
        }

        long cost = Vp8Costs.Bit(1, ctx[near.Count0 * 4]);
        if (mode == Vp8ModeInfo.Nearest)
        {
            return cost + Vp8Costs.Bit(0, ctx[(near.Count1 * 4) + 1]);
        }

        cost += Vp8Costs.Bit(1, ctx[(near.Count1 * 4) + 1]);
        if (mode == Vp8ModeInfo.Near)
        {
            return cost + Vp8Costs.Bit(0, ctx[(near.Count2 * 4) + 2]);
        }

        cost += Vp8Costs.Bit(1, ctx[(near.Count2 * 4) + 2]);
        return cost + Vp8Costs.Bit(mode == Vp8ModeInfo.Split ? 1 : 0, ctx[(near.Splits * 4) + 3]);
    }

    private static long LayoutCost(int layout) => layout switch
    {
        3 => Vp8Costs.Bit(0, 110),
        2 => Vp8Costs.Bit(1, 110) + Vp8Costs.Bit(0, 111),
        _ => Vp8Costs.Bit(1, 110) + Vp8Costs.Bit(1, 111) + Vp8Costs.Bit(layout, 150),
    };

    /// <summary>The cost of an intra macroblock's modes in an inter frame: the intra flag, the mode trees with the stream's probabilities.</summary>
    private long IntraHeaderCost(int mb)
    {
        Vp8StreamState state = _inter!.State;
        long cost = Vp8Costs.Bit(0, _probIntra);
        if (_isI16[mb])
        {
            cost += YModeCost(_yMode[mb], state.YModeProbabilities);
        }
        else
        {
            cost += YModeCost(Vp8ModeInfo.BPred, state.YModeProbabilities);
            for (int n = 0; n < 16; n++)
            {
                cost += InterIntra4Costs[_modes[(mb * 16) + n]];
            }
        }

        return cost + UvModeCost(_uvMode[mb], state.UvModeProbabilities);
    }

    private static long YModeCost(int mode, ReadOnlySpan<byte> p) => mode switch
    {
        0 => Vp8Costs.Bit(0, p[0]),
        1 => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(0, p[1]) + Vp8Costs.Bit(0, p[2]),
        2 => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(0, p[1]) + Vp8Costs.Bit(1, p[2]),
        3 => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(1, p[1]) + Vp8Costs.Bit(0, p[3]),
        _ => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(1, p[1]) + Vp8Costs.Bit(1, p[3]),
    };

    private static long UvModeCost(int mode, ReadOnlySpan<byte> p) => mode switch
    {
        0 => Vp8Costs.Bit(0, p[0]),
        1 => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(0, p[1]),
        2 => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(1, p[1]) + Vp8Costs.Bit(0, p[2]),
        _ => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(1, p[1]) + Vp8Costs.Bit(1, p[2]),
    };

    private static ushort[] BuildInterIntra4Costs()
    {
        ushort[] costs = new ushort[10];
        ReadOnlySpan<byte> probs = Vp8InterTables.BModeProbs;
        for (int m = 0; m < 10; m++)
        {
            int cost = 0;
            foreach ((int node, int bit) in Vp8Costs.Intra4Paths[m])
            {
                cost += Vp8Costs.Bit(bit, probs[node]);
            }

            costs[m] = (ushort)cost;
        }

        return costs;
    }

    // --- Vectors -----------------------------------------------------------------------------------------------------------

    /// <summary>Fills the cost of every vector component difference, −1023 to 1023 quarter pixels, from the frame's probabilities.</summary>
    private void BuildMvCosts(ReadOnlySpan<byte> probabilities)
    {
        for (int c = 0; c < 2; c++)
        {
            ReadOnlySpan<byte> p = probabilities.Slice(c * Vp8InterTables.MvProbCount, Vp8InterTables.MvProbCount);
            for (int v = -MvLimit; v <= MvLimit; v++)
            {
                _mvCost[(c * MvCostSize) + v + MvLimit] = ComponentCost(v, p);
            }
        }
    }

    private static int ComponentCost(int v, ReadOnlySpan<byte> p)
    {
        int x = Math.Abs(v);
        int cost;
        if (x < 8)
        {
            int b1 = (x >> 1) & 1;
            cost = Vp8Costs.Bit(0, p[0]);
            cost += x >= 4
                ? Vp8Costs.Bit(1, p[2]) + Vp8Costs.Bit(b1, p[6]) + Vp8Costs.Bit(x & 1, b1 == 0 ? p[7] : p[8])
                : Vp8Costs.Bit(0, p[2]) + Vp8Costs.Bit(b1, p[3]) + Vp8Costs.Bit(x & 1, b1 == 0 ? p[4] : p[5]);
        }
        else
        {
            cost = Vp8Costs.Bit(1, p[0]);
            for (int i = 0; i < 10; i++)
            {
                if (i != 3 || (x & 0xFFF0) != 0)
                {
                    cost += Vp8Costs.Bit((x >> i) & 1, p[9 + i]);
                }
            }
        }

        return x != 0 ? cost + Vp8Costs.Bit(v < 0 ? 1 : 0, p[1]) : cost;
    }

    /// <summary>The cost of coding <paramref name="mv"/> against <paramref name="best"/>, or <see cref="Impossible"/> when it is too far.</summary>
    private int MvCost(int mv, int best)
    {
        int dr = (Vp8ModeInfo.Row(mv) - Vp8ModeInfo.Row(best)) >> 1;
        int dc = (Vp8ModeInfo.Col(mv) - Vp8ModeInfo.Col(best)) >> 1;
        if (Math.Abs(dr) > MvLimit || Math.Abs(dc) > MvLimit)
        {
            return Impossible;
        }

        return _mvCost[dr + MvLimit] + _mvCost[MvCostSize + dc + MvLimit];
    }

    /// <summary>Counts the branches a vector's components take, for the probability updates.</summary>
    private void CountMv(int mv, int best)
    {
        CountComponent((Vp8ModeInfo.Row(mv) - Vp8ModeInfo.Row(best)) >> 1, 0);
        CountComponent((Vp8ModeInfo.Col(mv) - Vp8ModeInfo.Col(best)) >> 1, Vp8InterTables.MvProbCount);
    }

    private void CountComponent(int v, int at)
    {
        int x = Math.Abs(v);
        if (x < 8)
        {
            int b1 = (x >> 1) & 1;
            Count(at, 0);
            if (x >= 4)
            {
                Count(at + 2, 1);
                Count(at + 6, b1);
                Count(at + (b1 == 0 ? 7 : 8), x & 1);
            }
            else
            {
                Count(at + 2, 0);
                Count(at + 3, b1);
                Count(at + (b1 == 0 ? 4 : 5), x & 1);
            }
        }
        else
        {
            Count(at, 1);
            for (int i = 0; i < 10; i++)
            {
                if (i != 3 || (x & 0xFFF0) != 0)
                {
                    Count(at + 9 + i, (x >> i) & 1);
                }
            }
        }

        if (x != 0)
        {
            Count(at + 1, v < 0 ? 1 : 0);
        }

        void Count(int i, int bit) => _mvCounts[(i * 2) + bit]++;
    }

    private void WriteMv(Vp8BoolEncoder bw, int mv, int best)
    {
        WriteComponent(bw, (Vp8ModeInfo.Row(mv) - Vp8ModeInfo.Row(best)) >> 1, _mvProbs.AsSpan(0, Vp8InterTables.MvProbCount));
        WriteComponent(bw, (Vp8ModeInfo.Col(mv) - Vp8ModeInfo.Col(best)) >> 1, _mvProbs.AsSpan(Vp8InterTables.MvProbCount, Vp8InterTables.MvProbCount));
    }

    private static void WriteComponent(Vp8BoolEncoder bw, int v, ReadOnlySpan<byte> p)
    {
        int x = Math.Abs(v);
        if (x < 8)
        {
            int b1 = (x >> 1) & 1;
            bw.PutBit(0, p[0]);
            if (bw.PutBit(x >= 4 ? 1 : 0, p[2]) != 0)
            {
                bw.PutBit(b1, p[6]);
                bw.PutBit(x & 1, b1 == 0 ? p[7] : p[8]);
            }
            else
            {
                bw.PutBit(b1, p[3]);
                bw.PutBit(x & 1, b1 == 0 ? p[4] : p[5]);
            }
        }
        else
        {
            // Bits 0 to 2, then 9 down to 4, then bit 3 unless it is implied (no higher bit set).
            bw.PutBit(1, p[0]);
            for (int i = 0; i < 3; i++)
            {
                bw.PutBit((x >> i) & 1, p[9 + i]);
            }

            for (int i = 9; i > 3; i--)
            {
                bw.PutBit((x >> i) & 1, p[9 + i]);
            }

            if ((x & 0xFFF0) != 0)
            {
                bw.PutBit((x >> 3) & 1, p[9 + 3]);
            }
        }

        if (x != 0)
        {
            bw.PutBit(v < 0 ? 1 : 0, p[1]);
        }
    }

    // --- Motion search -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds a vector for the <paramref name="w"/> × <paramref name="h"/> block at (<paramref name="bx"/>, <paramref name="by"/>)
    /// of the macroblock: whole pixels by the sum of absolute differences from the best of <paramref name="seeds"/>, in square
    /// steps halving from <paramref name="range"/>, then half and quarter pixels by the squared error of the six-tap prediction,
    /// each weighted with the vector's cost against <paramref name="best"/>. Vectors stay within 16 pixels of the frame, so
    /// they need no clamping. Returns the vector in eighths.
    /// </summary>
    private int Search(Vp8FrameBuffer frame, int mbx, int mby, int bx, int by, int w, int h, int best, ReadOnlySpan<int> seeds, int range, out int bestSse)
    {
        // A macroblock's search keeps its best prediction (in _searchBest) and that prediction's error.
        bool keep = w == 16 && h == 16;
        int sse = -1;
        int lo = -(mbx * 16) - 16, hi = ((_mbw - 1 - mbx) * 16) + 16;
        int top = -(mby * 16) - 16, bottom = ((_mbh - 1 - mby) * 16) + 16;
        int x0 = (mbx * 16) + bx, y0 = (mby * 16) + by;
        int src = (by * S) + bx;
        int bestX = 0, bestY = 0;
        long bestCost = long.MaxValue;
        foreach (int seed in seeds)
        {
            int sx = Math.Clamp((Vp8ModeInfo.Col(seed) + 4) >> 3, lo, hi);
            int sy = Math.Clamp((Vp8ModeInfo.Row(seed) + 4) >> 3, top, bottom);
            long cost = FullPelCost(sx, sy);
            if (cost < bestCost)
            {
                (bestX, bestY, bestCost) = (sx, sy, cost);
            }
        }

        ReadOnlySpan<int> dx = [-1, 0, 1, -1, 1, -1, 0, 1];
        ReadOnlySpan<int> dy = [-1, -1, -1, 0, 0, 1, 1, 1];
        // Large steps look once at the eight points around; from two pixels down the four nearest are followed while they improve,
        // then the eight at one pixel once more.
        for (int step = Math.Max(1, (int)BitOperationsFloor(range) / 2); step >= 1; step >>= 1)
        {
            bool fine = step <= 2;
            for (int pass = 0; pass < (fine ? range : 1); pass++)
            {
                int cx = bestX, cy = bestY;
                for (int d = fine ? 1 : 0; d < 8; d += fine ? 2 : 1)
                {
                    int x = cx + (dx[d] * step), y = cy + (dy[d] * step);
                    if (x < lo || x > hi || y < top || y > bottom)
                    {
                        continue;
                    }

                    long cost = FullPelCost(x, y);
                    if (cost < bestCost)
                    {
                        (bestX, bestY, bestCost) = (x, y, cost);
                    }
                }

                if (bestX == cx && bestY == cy)
                {
                    break;
                }
            }
        }

        for (int d = 0; d < 8; d += 2)
        {
            int x = bestX + dx[d], y = bestY + dy[d];
            if (x >= lo && x <= hi && y >= top && y <= bottom)
            {
                long cost = FullPelCost(x, y);
                if (cost < bestCost)
                {
                    (bestX, bestY, bestCost) = (x, y, cost);
                }
            }
        }

        // Fractions: halves, then quarters, around the best so far: the four nearest, then the corner between the better two.
        int mv = Vp8ModeInfo.Pack(bestY * 8, bestX * 8);
        long current = SubPelCost(mv);
        int keptSse = current == long.MaxValue ? -1 : sse;
        if (keep && keptSse >= 0)
        {
            _searchPred.AsSpan().CopyTo(_searchBest);
        }

        for (int step = 4; step >= (_inter!.QuarterPixel ? 2 : 4); step >>= 1)
        {
            int centre = mv;
            int r0 = Vp8ModeInfo.Row(centre), c0 = Vp8ModeInfo.Col(centre);
            long left = Try(r0, c0 - step), right = Try(r0, c0 + step), up = Try(r0 - step, c0), down = Try(r0 + step, c0);
            Try(r0 + (up < down ? -step : step), c0 + (left < right ? -step : step));
        }

        bestSse = keptSse;
        return mv;

        long Try(int row, int col)
        {
            if (col < lo * 8 || col > hi * 8 || row < top * 8 || row > bottom * 8)
            {
                return long.MaxValue;
            }

            int candidate = Vp8ModeInfo.Pack(row, col);
            long cost = SubPelCost(candidate);
            if (cost < current)
            {
                (mv, current, keptSse) = (candidate, cost, sse);
                if (keep)
                {
                    _searchPred.AsSpan().CopyTo(_searchBest);
                }
            }

            return cost;
        }

        long FullPelCost(int x, int y)
        {
            int rate = MvCost(Vp8ModeInfo.Pack(y * 8, x * 8), best);
            if (rate == Impossible)
            {
                return long.MaxValue;
            }

            int sad = Vp8EncDsp.Sad(_yuvIn, src, frame.Y, frame.OriginY + ((y0 + y) * frame.StrideY) + x0 + x, frame.StrideY, w, h);
            return ((long)sad << 8) + ((long)_lambdaSad * rate);
        }

        long SubPelCost(int vector)
        {
            int rate = MvCost(vector, best);
            if (rate == Impossible)
            {
                return long.MaxValue;
            }

            Vp8InterPredict.Predict(frame.Y, frame.OriginY, frame.StrideY, frame.AlignedWidth, frame.AlignedHeight, Vp8FrameBuffer.Border, x0, y0, w, h, Vp8ModeInfo.Row(vector), Vp8ModeInfo.Col(vector), true, _searchPred, src, S);
            sse = Vp8EncDsp.Sse(_yuvIn, _searchPred, src, w, h);
            return ((long)sse << 8) + ((long)_lambda * rate);
        }
    }

    private static uint BitOperationsFloor(int value) => System.Numerics.BitOperations.RoundUpToPowerOf2((uint)value + 1) >> 1;

    /// <summary>
    /// Chooses the vectors of a split layout part by part: a short search for each part, then whichever of the left vector,
    /// the above vector, zero and the searched one costs least. Leaves the vectors in <see cref="_trialSub"/>, the choices in
    /// <see cref="_trialPartModes"/>, and returns their header cost.
    /// </summary>
    private long PlanSplit(int mbx, int mby, int reference, in NearVectors near, int layout, int seed)
    {
        Vp8ModeInfo info = _info!;
        Vp8FrameBuffer frame = _inter!.References[reference]!;
        int here = info.Index(mbx, mby);
        int above = here - info.Stride, left = here - 1;
        int toLeft = -(mbx * 16) << 3, toRight = ((_mbw - 1 - mbx) * 16) << 3;
        int toTop = -(mby * 16) << 3, toBottom = ((_mbh - 1 - mby) * 16) << 3;
        int lo = toLeft - 128, hi = toRight + 128, top = toTop - 128, bottom = toBottom + 128;
        (int w, int h) = layout switch { 0 => (16, 8), 1 => (8, 16), 2 => (8, 8), _ => (4, 4) };
        int parts = Vp8InterTables.SplitParts[layout];
        int fill = Vp8InterTables.SplitFillCount[layout];
        int[] sub = _trialSub;
        ReadOnlySpan<byte> probs = Vp8InterTables.SubMvRefProbs;
        long header = 0;
        int previous = seed;
        for (int j = 0; j < parts; j++)
        {
            int k = Vp8InterTables.SplitOffset[(layout * 16) + j];
            int bx = (k & 3) * 4, by = (k >> 2) * 4;
            int leftMv = (k & 3) != 0 ? sub[k - 1] : info.Mode[left] != Vp8ModeInfo.Split ? info.Mv[left] : info.SubMv[(left * 16) + k + 3];
            int aboveMv = (k >> 2) != 0 ? sub[k - 4] : info.Mode[above] != Vp8ModeInfo.Split ? info.Mv[above] : info.SubMv[(above * 16) + k + 12];
            int context = ((aboveMv == 0 ? 1 : 0) << 2) | ((leftMv == 0 ? 1 : 0) << 1) | (leftMv == aboveMv ? 1 : 0);
            ReadOnlySpan<byte> p = probs.Slice(context * 3, 3);
            int found = Search(frame, mbx, mby, bx, by, w, h, near.Best, [previous, seed, 0, near.Best], layout == 3 ? 4 : 8, out _);
            Span<int> mvs = [leftMv, aboveMv, 0, found];
            long bestCost = long.MaxValue;
            int choice = 2;
            for (int m = 0; m < 4; m++)
            {
                int mv = mvs[m];
                if ((m == 1 && aboveMv == leftMv) || (m == 3 && (found == leftMv || found == aboveMv || found == 0)))
                {
                    continue;
                }

                if (Vp8Decoder.OutOfBounds(Vp8ModeInfo.Row(mv), Vp8ModeInfo.Col(mv), lo, hi, top, bottom) != 0)
                {
                    continue;
                }

                long bits = m switch
                {
                    0 => Vp8Costs.Bit(0, p[0]),
                    1 => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(0, p[1]),
                    2 => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(1, p[1]) + Vp8Costs.Bit(0, p[2]),
                    _ => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(1, p[1]) + Vp8Costs.Bit(1, p[2]) + MvCost(mv, near.Best),
                };
                int at = (by * S) + bx;
                Vp8InterPredict.Predict(frame.Y, frame.OriginY, frame.StrideY, frame.AlignedWidth, frame.AlignedHeight, Vp8FrameBuffer.Border, (mbx * 16) + bx, (mby * 16) + by, w, h, Vp8ModeInfo.Row(mv), Vp8ModeInfo.Col(mv), true, _searchPred, at, S);
                long cost = ((long)Vp8EncDsp.Sse(_yuvIn, _searchPred, at, w, h) << 8) + (_lambda * bits);
                if (cost < bestCost)
                {
                    (bestCost, choice) = (cost, m);
                }
            }

            int chosen = mvs[choice];
            header += choice switch
            {
                0 => Vp8Costs.Bit(0, p[0]),
                1 => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(0, p[1]),
                2 => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(1, p[1]) + Vp8Costs.Bit(0, p[2]),
                _ => Vp8Costs.Bit(1, p[0]) + Vp8Costs.Bit(1, p[1]) + Vp8Costs.Bit(1, p[2]) + MvCost(chosen, near.Best),
            };
            _trialPartModes[j] = (byte)choice;
            foreach (byte b in Vp8InterTables.SplitFill.Slice((layout * 16) + (j * fill), fill))
            {
                sub[b] = chosen;
            }

            previous = chosen;
        }

        return header;
    }

    // --- Loop filter search -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the luma squared error against the source of the inter frame encoded last, filtered at frame level
    /// <paramref name="level"/> as the decoder filters it (the normal filter, sharpness 0, the stream's reference and mode
    /// deltas): <see cref="Reconstruction"/>'s unfiltered luma is filtered in <paramref name="work"/>, a plane of its size.
    /// The same as decoding the frame at that level and measuring its luma, at a fraction of the cost.
    /// </summary>
    internal long FilteredLumaError(int level, byte[] work)
    {
        Vp8Planes recon = Reconstruction!;
        int stride = recon.Stride;
        recon.Y.AsSpan(0, stride * _mbh * 16).CopyTo(work);
        if (level > 0)
        {
            Span<Vp8Decoder.FilterInfo> strengths = stackalloc Vp8Decoder.FilterInfo[16];
            ReadOnlySpan<sbyte> refDeltas = ReferenceFilterDeltas, modeDeltas = ModeFilterDeltas;
            for (int reference = 0; reference < 4; reference++)
            {
                for (int mode = 0; mode < 4; mode++)
                {
                    int l = level + refDeltas[reference] + ((reference > 0 ? mode > 0 : mode == 0) ? modeDeltas[mode] : 0);
                    strengths[(reference * 4) + mode] = Vp8Decoder.Strength(Math.Clamp(l, 0, 63), 0, key: false);
                }
            }

            Vp8ModeInfo info = _info!;
            for (int mby = 0; mby < _mbh; mby++)
            {
                for (int mbx = 0; mbx < _mbw; mbx++)
                {
                    int here = info.Index(mbx, mby);
                    int reference = info.Reference[here], mode = info.Mode[here];
                    int index = reference == 0 ? (mode == Vp8ModeInfo.BPred ? 0 : 1) : mode == Vp8ModeInfo.Zero ? 1 : mode == Vp8ModeInfo.Split ? 3 : 2;
                    bool hasY2 = mode != Vp8ModeInfo.BPred && mode != Vp8ModeInfo.Split;
                    bool inner = !hasY2 || !_zero[(mby * _mbw) + mbx];
                    Vp8Decoder.FilterLuma(work, (mby * 16 * stride) + (mbx * 16), stride, strengths[(reference * 4) + index], mbx > 0, mby > 0, inner);
                }
            }
        }

        long sum = 0;
        for (int y = 0; y < _src.Height; y++)
        {
            sum += Vp8EncDsp.SquaredError(_src.Y.AsSpan(y * stride, _src.Width), work.AsSpan(y * stride, _src.Width));
        }

        return sum;
    }

    // --- Frame header ------------------------------------------------------------------------------------------------------

    /// <summary>Works out the frame's reference probabilities, and the mode and vector probability updates that save bits.</summary>
    private void ChooseInterProbabilities()
    {
        Vp8StreamState state = _inter!.State;
        int total = _mbw * _mbh;
        _probIntra = Probability(_refCounts[0], total - _refCounts[0]);
        _probLast = Probability(_refCounts[1], _refCounts[2] + _refCounts[3]);
        _probGolden = Probability(_refCounts[2], _refCounts[3]);

        Span<int> y = [_yModeCounts[0], _yModeCounts[1] + _yModeCounts[2] + _yModeCounts[3] + _yModeCounts[4], _yModeCounts[1] + _yModeCounts[2], _yModeCounts[3] + _yModeCounts[4], _yModeCounts[1], _yModeCounts[2], _yModeCounts[3], _yModeCounts[4]];
        _updateYModes = ChooseTree(y, state.YModeProbabilities, _yModeProbs);
        Span<int> uv = [_uvModeCounts[0], _uvModeCounts[1] + _uvModeCounts[2] + _uvModeCounts[3], _uvModeCounts[1], _uvModeCounts[2] + _uvModeCounts[3], _uvModeCounts[2], _uvModeCounts[3]];
        _updateUvModes = ChooseTree(uv, state.UvModeProbabilities, _uvModeProbs);

        ReadOnlySpan<byte> update = Vp8InterTables.MvUpdateProbs;
        for (int i = 0; i < _mvProbs.Length; i++)
        {
            int c0 = _mvCounts[i * 2], c1 = _mvCounts[(i * 2) + 1];
            int old = state.MvProbabilities[i];
            _mvProbs[i] = (byte)old;
            _mvUpdate[i] = false;
            if (c0 + c1 == 0)
            {
                continue;
            }

            int v = Probability(c0, c1) >> 1;
            int fresh = v != 0 ? v << 1 : 1;
            long oldCost = Branches(c0, c1, old) + Vp8Costs.Bit(0, update[i]);
            long newCost = Branches(c0, c1, fresh) + Vp8Costs.Bit(1, update[i]) + (7 * 256);
            if (newCost < oldCost)
            {
                _mvProbs[i] = (byte)fresh;
                _mvUpdate[i] = true;
            }
        }

        static int Probability(int zeros, int ones) => zeros + ones == 0 ? 128 : Math.Clamp(((zeros * 255) + ((zeros + ones) / 2)) / (zeros + ones), 1, 255);

        static long Branches(int zeros, int ones, int p) => ((long)zeros * Vp8Costs.Bit(0, p)) + ((long)ones * Vp8Costs.Bit(1, p));

        // A tree's probabilities, all updated (8 bits each) or none; counts are pairs of zeros and ones per node.
        static bool ChooseTree(ReadOnlySpan<int> counts, ReadOnlySpan<byte> current, Span<byte> chosen)
        {
            current.CopyTo(chosen);
            long oldCost = 0, newCost = 8L * 256 * current.Length;
            Span<byte> fresh = stackalloc byte[current.Length];
            for (int i = 0; i < current.Length; i++)
            {
                int c0 = counts[i * 2], c1 = counts[(i * 2) + 1];
                fresh[i] = c0 + c1 == 0 ? current[i] : (byte)Probability(c0, c1);
                oldCost += Branches(c0, c1, current[i]);
                newCost += Branches(c0, c1, fresh[i]);
            }

            if (newCost >= oldCost)
            {
                return false;
            }

            fresh.CopyTo(chosen);
            return true;
        }
    }

    private Vp8BoolEncoder WriteInterPartition0()
    {
        Vp8InterFrame inter = _inter!;
        Vp8BoolEncoder bw = new(Math.Max(1024, _segment.Length * 2));
        bw.PutBitUniform(0); // no segmentation
        WriteFilterHeader(bw, key: false);
        bw.PutValue(int.Log2(PartitionCount), 2);
        WriteQuantizers(bw);
        bw.PutBitUniform(inter.RefreshGolden ? 1 : 0);
        bw.PutBitUniform(inter.RefreshAlternate ? 1 : 0);
        if (!inter.RefreshGolden)
        {
            bw.PutValue(inter.CopyToGolden, 2);
        }

        if (!inter.RefreshAlternate)
        {
            bw.PutValue(inter.CopyToAlternate, 2);
        }

        bw.PutBitUniform(inter.GoldenSignBias ? 1 : 0);
        bw.PutBitUniform(inter.AlternateSignBias ? 1 : 0);
        bw.PutBitUniform(_options.RefreshEntropy ? 1 : 0);
        bw.PutBitUniform(inter.RefreshLast ? 1 : 0);
        WriteCoefficientProbabilities(bw);
        bw.PutValue(_probIntra, 8);
        bw.PutValue(_probLast, 8);
        bw.PutValue(_probGolden, 8);
        if (bw.PutBitUniform(_updateYModes ? 1 : 0) != 0)
        {
            foreach (byte p in _yModeProbs)
            {
                bw.PutValue(p, 8);
            }
        }

        if (bw.PutBitUniform(_updateUvModes ? 1 : 0) != 0)
        {
            foreach (byte p in _uvModeProbs)
            {
                bw.PutValue(p, 8);
            }
        }

        ReadOnlySpan<byte> update = Vp8InterTables.MvUpdateProbs;
        for (int i = 0; i < _mvProbs.Length; i++)
        {
            if (bw.PutBit(_mvUpdate[i] ? 1 : 0, update[i]) != 0)
            {
                bw.PutValue(_mvProbs[i] >> 1, 7);
            }
        }

        WriteInterModes(bw);
        return bw;
    }

    private void WriteInterModes(Vp8BoolEncoder bw)
    {
        Vp8ModeInfo info = _info!;
        ReadOnlySpan<byte> ctx = Vp8InterTables.ModeContexts;
        ReadOnlySpan<byte> subProbs = Vp8InterTables.SubMvRefProbs;
        ReadOnlySpan<byte> bProbs = Vp8InterTables.BModeProbs;
        for (int mby = 0; mby < _mbh; mby++)
        {
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                int mb = (mby * _mbw) + mbx;
                int here = info.Index(mbx, mby);
                if (_useSkip)
                {
                    bw.PutBit(_zero[mb] ? 1 : 0, _skipProb);
                }

                int reference = info.Reference[here];
                if (bw.PutBit(reference != 0 ? 1 : 0, _probIntra) == 0)
                {
                    int mode = info.Mode[here];
                    WriteYMode(bw, mode, _yModeProbs);
                    if (mode == Vp8ModeInfo.BPred)
                    {
                        for (int n = 0; n < 16; n++)
                        {
                            foreach ((int node, int bit) in Vp8Costs.Intra4Paths[_modes[(mb * 16) + n]])
                            {
                                bw.PutBit(bit, bProbs[node]);
                            }
                        }
                    }

                    int uv = info.UvMode[here];
                    if (bw.PutBit(uv != 0 ? 1 : 0, _uvModeProbs[0]) != 0 && bw.PutBit(uv != 1 ? 1 : 0, _uvModeProbs[1]) != 0)
                    {
                        bw.PutBit(uv == 3 ? 1 : 0, _uvModeProbs[2]);
                    }

                    continue;
                }

                if (bw.PutBit(reference != 1 ? 1 : 0, _probLast) != 0)
                {
                    bw.PutBit(reference == 3 ? 1 : 0, _probGolden);
                }

                NearVectors near = FindNear(mbx, mby, reference);
                int vectorMode = info.Mode[here];
                if (bw.PutBit(vectorMode != Vp8ModeInfo.Zero ? 1 : 0, ctx[near.Count0 * 4]) == 0
                    || bw.PutBit(vectorMode != Vp8ModeInfo.Nearest ? 1 : 0, ctx[(near.Count1 * 4) + 1]) == 0
                    || bw.PutBit(vectorMode != Vp8ModeInfo.Near ? 1 : 0, ctx[(near.Count2 * 4) + 2]) == 0)
                {
                    continue;
                }

                if (bw.PutBit(vectorMode == Vp8ModeInfo.Split ? 1 : 0, ctx[(near.Splits * 4) + 3]) == 0)
                {
                    WriteMv(bw, info.Mv[here], near.Best);
                    continue;
                }

                int layout = info.Partitioning[here];
                if (bw.PutBit(layout != 3 ? 1 : 0, 110) != 0 && bw.PutBit(layout != 2 ? 1 : 0, 111) != 0)
                {
                    bw.PutBit(layout, 150);
                }

                int parts = Vp8InterTables.SplitParts[layout];
                int at = here * 16;
                int above = here - info.Stride, left = here - 1;
                for (int j = 0; j < parts; j++)
                {
                    int k = Vp8InterTables.SplitOffset[(layout * 16) + j];
                    int leftMv = (k & 3) != 0 ? info.SubMv[at + k - 1] : info.Mode[left] != Vp8ModeInfo.Split ? info.Mv[left] : info.SubMv[(left * 16) + k + 3];
                    int aboveMv = (k >> 2) != 0 ? info.SubMv[at + k - 4] : info.Mode[above] != Vp8ModeInfo.Split ? info.Mv[above] : info.SubMv[(above * 16) + k + 12];
                    int context = ((aboveMv == 0 ? 1 : 0) << 2) | ((leftMv == 0 ? 1 : 0) << 1) | (leftMv == aboveMv ? 1 : 0);
                    ReadOnlySpan<byte> p = subProbs.Slice(context * 3, 3);
                    int choice = _partModes[(mb * 16) + j];
                    if (bw.PutBit(choice != 0 ? 1 : 0, p[0]) != 0 && bw.PutBit(choice != 1 ? 1 : 0, p[1]) != 0 && bw.PutBit(choice != 2 ? 1 : 0, p[2]) != 0)
                    {
                        WriteMv(bw, info.SubMv[at + k], near.Best);
                    }
                }
            }
        }
    }

    private static void WriteYMode(Vp8BoolEncoder bw, int mode, ReadOnlySpan<byte> p)
    {
        if (bw.PutBit(mode != 0 ? 1 : 0, p[0]) == 0)
        {
            return;
        }

        if (bw.PutBit(mode >= 3 ? 1 : 0, p[1]) == 0)
        {
            bw.PutBit(mode == 2 ? 1 : 0, p[2]);
        }
        else
        {
            bw.PutBit(mode == Vp8ModeInfo.BPred ? 1 : 0, p[3]);
        }
    }
}
