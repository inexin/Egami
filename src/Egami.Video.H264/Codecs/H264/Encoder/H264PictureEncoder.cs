using Egami.Nal;
using Egami.Pixels;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// Codes pictures into slices, macroblock by macroblock, and keeps the reconstructed, deblocked pictures a decoder makes of them:
/// the short-term references (most recent first, up to <see cref="H264StreamConfig.MaxRefFrames"/>) that P and B pictures
/// predict from. The oldest leaves by the sliding window; a referenced B-frame leaves when the next P or I picture marks it
/// unused (memory_management_control_operation 1).
/// </summary>
internal sealed class H264PictureEncoder
{
    // The adaptive quantizer's strength: QP steps per doubling of a macroblock's energy against the picture's mean.
    private const double AqStrength = 1.0;

    private readonly H264StreamConfig _config;
    private readonly int _effort;
    private readonly H264MbState _mb;
    private readonly H264MbSyntax _syntax;
    private readonly H264MbEncoder _encoder;
    private readonly H264Deblock _deblock = new();
    private readonly List<H264FilterParams> _filters = [new(0, 0, 0)];
    private readonly byte[] _srcY;
    private readonly byte[] _srcCb;
    private readonly byte[] _srcCr;
    private readonly CabacEncoder _cabac = new();
    private readonly List<H264RefPlanes> _free = [];
    private readonly List<H264RefPlanes> _refs = [];
    private readonly int[] _qpOffsets;
    private readonly double[] _logs;
    private readonly List<H264RefPlanes> _drop = [];
    private H264RefPlanes _current;
    private double _sliceCost;

    public H264PictureEncoder(H264StreamConfig config, int effort)
    {
        _config = config;
        _effort = effort;
        _current = new H264RefPlanes(config.WidthMbs, config.HeightMbs);
        H264Picture pic = _current.Picture;
        _mb = new H264MbState(config.WidthMbs, config.HeightMbs, 1);
        _syntax = new H264MbSyntax(_mb) { Cabac = config.Cabac, Transform8x8Mode = config.Transform8x8 };
        _srcY = new byte[pic.Y<byte>().Length];
        _srcCb = new byte[pic.Cb<byte>().Length];
        _srcCr = new byte[pic.Cr<byte>().Length];
        _encoder = new H264MbEncoder(pic, _mb, _syntax, _srcY, _srcCb, _srcCr, config.Transform8x8, effort);
        _qpOffsets = new int[config.WidthMbs * config.HeightMbs];
        _logs = new double[_qpOffsets.Length];
    }

    /// <summary>Gets the reconstructed picture of the last picture coded, deblocked.</summary>
    public H264Picture Recon => _current.Picture;

    /// <summary>Gets the number of reference pictures held, which P pictures may predict from.</summary>
    public int References => _refs.Count;

    /// <summary>Gets or sets whether macroblocks' QPs follow their energy (adaptive quantization).</summary>
    public bool AdaptiveQuantization { get; set; }

    /// <summary>Gets or sets the smallest QP a macroblock may take.</summary>
    public int MinQp { get; set; }

    /// <summary>Gets or sets the largest QP a macroblock may take.</summary>
    public int MaxQp { get; set; } = 51;

    /// <summary>Gets or sets how B slices predict direct blocks: null to choose (spatial, or the cheaper of both from effort 8), else temporal or not.</summary>
    internal bool? TemporalDirect { get; set; }

    /// <summary>Gets whether the last B picture predicted direct blocks temporally.</summary>
    internal bool LastTemporal { get; private set; }

    /// <summary>
    /// Copies a picture into the source planes, its last column and row repeated out to whole macroblocks; chroma is mid-grey
    /// when the picture has none.
    /// </summary>
    public void Load(YuvRaster<byte> picture)
    {
        H264Picture pic = _current.Picture;
        Copy(picture.Y, _srcY, pic.Origin, pic.Stride, _config.Width, _config.Height, _config.WidthMbs * 16, _config.HeightMbs * 16);
        int cw = (_config.Width + 1) >> 1, ch = (_config.Height + 1) >> 1;
        int pw = _config.WidthMbs * 8, ph = _config.HeightMbs * 8;
        if (picture.U is { } u && picture.V is { } v)
        {
            Copy(u, _srcCb, pic.ChromaOrigin, pic.ChromaStride, cw, ch, pw, ph);
            Copy(v, _srcCr, pic.ChromaOrigin, pic.ChromaStride, cw, ch, pw, ph);
        }
        else
        {
            Array.Fill(_srcCb, (byte)128);
            Array.Fill(_srcCr, (byte)128);
        }
    }

    private static void Copy(YuvPlane<byte> plane, byte[] dst, int origin, int stride, int width, int height, int paddedWidth, int paddedHeight)
    {
        for (int y = 0; y < paddedHeight; y++)
        {
            ReadOnlySpan<byte> row = plane.Row(Math.Min(y, height - 1))[..width];
            Span<byte> to = dst.AsSpan(origin + (y * stride), paddedWidth);
            row.CopyTo(to);
            to[width..].Fill(row[width - 1]);
        }
    }

    /// <summary>Codes the loaded picture as one I slice and returns its NAL unit; an IDR picture empties the references.</summary>
    public byte[] EncodeIntra(bool idr, int idrPicId, int frameNum, int pocLsb, int qp, bool referenced = true)
    {
        if (idr)
        {
            _free.AddRange(_refs);
            _refs.Clear();
            _encoder.ResetMotion();
        }

        int[] unmark = Marking(frameNum, referenced, disposable: false);
        StartPicture(qp);
        NalBitWriter w = new(_config.WidthMbs * _config.HeightMbs * 32);
        H264StreamWriter.SliceHeader(w, _config, 0, H264SliceType.I, idr, idrPicId, frameNum, pocLsb, qp, referenced: referenced, unmark: unmark);
        _syntax.StartSlice(0, H264SliceType.I, qp, 0);
        WriteSlice(w, qp, predicted: false);
        FinishPicture(referenced, false, frameNum, pocLsb);
        return H264StreamWriter.Nal(referenced ? 3 : 0, idr ? 5 : 1, w);
    }

    /// <summary>
    /// Codes the loaded picture as one P slice predicted from the references held (up to <paramref name="maxRefs"/>, most
    /// recent first) and returns its NAL unit.
    /// </summary>
    public byte[] EncodePredicted(int frameNum, int pocLsb, int qp, int maxRefs, bool referenced = true)
    {
        if (_refs.Count == 0)
        {
            throw new InvalidOperationException("A P picture needs a reference picture.");
        }

        int count = Math.Clamp(maxRefs, 1, _refs.Count);
        H264RefPlanes[] refs = [.. _refs.Take(count)];
        foreach (H264RefPlanes r in refs)
        {
            ChooseWeights(r);
        }

        int[] unmark = Marking(frameNum, referenced, disposable: false);
        StartPicture(qp);
        _syntax.RefIds = [.. refs.Select(r => r.Picture.Id)];
        _syntax.RefCount = count;
        int maxMvY = 4 * H264Levels.MaxVerticalMv(_config.LevelIdc);
        _encoder.SetReferences(refs, count, maxMvY, _effort >= 6 && _config.LevelIdc <= 30);
        NalBitWriter w = new(_config.WidthMbs * _config.HeightMbs * 32);
        H264StreamWriter.SliceHeader(w, _config, 0, H264SliceType.P, false, 0, frameNum, pocLsb, qp, 0, refs, referenced: referenced, unmark: unmark);
        _syntax.StartSlice(0, H264SliceType.P, qp, 0);
        WriteSlice(w, qp, predicted: true);
        FinishPicture(referenced, false, frameNum, pocLsb);
        return H264StreamWriter.Nal(referenced ? 2 : 0, 1, w);
    }

    /// <summary>
    /// Codes the loaded picture, whose order count is <paramref name="poc"/>, as one B slice predicted from the references
    /// before and after it (8.2.4.2.3: list 0 the earlier ones nearest first, then the later ones; list 1 the other way round),
    /// and returns its NAL unit. A referenced B picture is <see cref="H264RefPlanes.Disposable"/>.
    /// </summary>
    public byte[] EncodeBidirectional(int frameNum, int poc, int qp, bool referenced)
    {
        List<H264RefPlanes> list0 = [.. _refs.Where(r => r.Poc < poc).OrderByDescending(r => r.Poc), .. _refs.Where(r => r.Poc > poc).OrderBy(r => r.Poc)];
        List<H264RefPlanes> list1 = [.. _refs.Where(r => r.Poc > poc).OrderBy(r => r.Poc), .. _refs.Where(r => r.Poc < poc).OrderByDescending(r => r.Poc)];
        if (list0.Count == 0)
        {
            throw new InvalidOperationException("A B picture needs a reference picture.");
        }

        if (list1.Count > 1 && list1.SequenceEqual(list0))
        {
            (list1[0], list1[1]) = (list1[1], list1[0]);
        }

        int count0 = Math.Min(list0.Count, _effort >= 4 ? 2 : 1);
        H264RefPlanes[] refs0 = [.. list0.Take(count0)], refs1 = [list1[0]];
        foreach (H264RefPlanes r in _refs)
        {
            r.Weighted = false;
        }

        int[] unmark = Marking(frameNum, referenced, disposable: true);
        StartPicture(qp);
        _syntax.RefIds = [.. refs0.Select(r => r.Picture.Id)];
        _syntax.RefIds1 = [refs1[0].Picture.Id];
        _syntax.RefCount = count0;
        _syntax.RefCount1 = 1;
        _syntax.Col = refs1[0].Picture;
        _syntax.ColPoc = refs1[0].Poc;
        _syntax.RefPocs = [.. refs0.Select(r => r.Poc)];
        _syntax.CurrentPoc = poc;
        _encoder.SetBReferences(refs0, count0, refs1, 1, 4 * H264Levels.MaxVerticalMv(_config.LevelIdc));
        bool temporalAllowed = _syntax.TemporalAllowed();
        bool temporal = TemporalDirect is bool forced ? forced && temporalAllowed : false;
        NalBitWriter w = CodeB(frameNum, poc, qp, referenced, unmark, temporal);
        if (TemporalDirect is null && _effort >= 8 && temporalAllowed)
        {
            // Both ways, keeping the cheaper: the second pass is coded again when the first was.
            double first = _sliceCost;
            ResetPicture(qp);
            NalBitWriter other = CodeB(frameNum, poc, qp, referenced, unmark, !temporal);
            if (_sliceCost < first)
            {
                (w, temporal) = (other, !temporal);
            }
            else
            {
                ResetPicture(qp);
                w = CodeB(frameNum, poc, qp, referenced, unmark, temporal);
            }
        }

        LastTemporal = temporal;
        FinishPicture(referenced, true, frameNum, poc);
        return H264StreamWriter.Nal(referenced ? 2 : 0, 1, w);
    }

    private NalBitWriter CodeB(int frameNum, int poc, int qp, bool referenced, int[] unmark, bool temporal)
    {
        _syntax.DirectSpatial = !temporal;
        NalBitWriter w = new(_config.WidthMbs * _config.HeightMbs * 32);
        H264RefPlanes[] refs0 = new H264RefPlanes[_syntax.RefCount];
        H264StreamWriter.SliceHeader(w, _config, 0, H264SliceType.B, false, 0, frameNum, poc, qp, 0, refs0, 1, !temporal, referenced, unmark);
        _syntax.StartSlice(0, H264SliceType.B, qp, 0);
        WriteSlice(w, qp, predicted: true);
        return w;
    }

    /// <summary>
    /// Works out how a picture marks the references (8.2.5.3, 8.2.5.4): a P or I picture marks the referenced B-frames unused,
    /// and with them as many of the oldest as the new picture needs room for; returns difference_of_pic_nums_minus1 of each, or
    /// nothing for the sliding window. The pictures go when the picture is finished.
    /// </summary>
    private int[] Marking(int frameNum, bool referenced, bool disposable)
    {
        _drop.Clear();
        if (!referenced || disposable)
        {
            return [];
        }

        _drop.AddRange(_refs.Where(r => r.Disposable));
        if (_drop.Count == 0)
        {
            return [];
        }

        for (int i = _refs.Count - 1; i >= 0 && _refs.Count - _drop.Count + 1 > _config.MaxRefFrames; i--)
        {
            if (!_drop.Contains(_refs[i]))
            {
                _drop.Add(_refs[i]);
            }
        }

        int max = 1 << H264StreamWriter.FrameNumBits;
        return [.. _drop.Select(r => frameNum - (r.FrameNum > frameNum ? r.FrameNum - max : r.FrameNum) - 1)];
    }

    /// <summary>Takes a free picture to reconstruct into, and works out the macroblocks' QP offsets.</summary>
    private void StartPicture(int qp)
    {
        if (_free.Count > 0)
        {
            _current = _free[^1];
            _free.RemoveAt(_free.Count - 1);
        }
        else if (_refs.Contains(_current))
        {
            _current = new H264RefPlanes(_config.WidthMbs, _config.HeightMbs);
        }

        _syntax.RefIds = [];
        _syntax.RefCount = 1;
        ResetPicture(qp);
    }

    /// <summary>Clears the picture being coded for a pass over it.</summary>
    private void ResetPicture(int qp)
    {
        H264Picture pic = _current.Picture;
        pic.Reset();
        _encoder.Target(pic);
        _syntax.Motion = pic;
        _mb.Clear();
        Array.Clear(pic.MbIntra);
        if (AdaptiveQuantization)
        {
            AdaptQuantizers();
        }
        else
        {
            Array.Clear(_qpOffsets);
        }
    }

    /// <summary>
    /// Deblocks the picture and, when it is a reference, marks the references as its slice header says and puts it first among
    /// them.
    /// </summary>
    private void FinishPicture(bool referenced, bool disposable, int frameNum, int poc)
    {
        _deblock.Filter(_current.Picture, _mb, _filters, 8, 8, 0, _config.HeightMbs);
        if (!referenced)
        {
            return;
        }

        _current.Build();
        _current.FrameNum = frameNum;
        _current.Poc = poc;
        _current.Disposable = disposable;
        if (_drop.Count > 0)
        {
            foreach (H264RefPlanes r in _drop)
            {
                _refs.Remove(r);
                _free.Add(r);
            }

            _drop.Clear();
        }
        else
        {
            while (_refs.Count >= _config.MaxRefFrames)
            {
                _free.Add(_refs[^1]);
                _refs.RemoveAt(_refs.Count - 1);
            }
        }

        _refs.Insert(0, _current);
    }

    private void WriteSlice(NalBitWriter w, int qp, bool predicted)
    {
        int total = _config.WidthMbs * _config.HeightMbs;
        _sliceCost = 0;
        if (_config.Cabac)
        {
            while (!w.ByteAligned)
            {
                w.U(1, 1); // cabac_alignment_one_bit
            }

            _cabac.Start();
            CabacWriteSink sink = new(_cabac);
            for (int addr = 0; addr < total; addr++)
            {
                H264MbCode code = Code(addr, qp, predicted);
                if (predicted)
                {
                    _syntax.SkipFlagCabac(ref sink, H264MbState.IsSkip(code.Kind));
                }

                _syntax.Begin(addr % _config.WidthMbs, addr / _config.WidthMbs, code);
                if (!H264MbState.IsSkip(code.Kind))
                {
                    _syntax.WriteCabac(code, ref sink);
                }

                _syntax.Commit(code);
                _cabac.Terminate(addr == total - 1 ? 1 : 0); // end_of_slice_flag
            }

            w.Bytes(_cabac.Finish());
        }
        else
        {
            BitWriteSink sink = new(w);
            int run = 0;
            for (int addr = 0; addr < total; addr++)
            {
                H264MbCode code = Code(addr, qp, predicted);
                _syntax.Begin(addr % _config.WidthMbs, addr / _config.WidthMbs, code);
                if (H264MbState.IsSkip(code.Kind))
                {
                    run++;
                    _syntax.Commit(code);
                    continue;
                }

                if (predicted)
                {
                    w.Ue((uint)run); // mb_skip_run
                    run = 0;
                }

                _syntax.WriteCavlc(code, ref sink);
                _syntax.Commit(code);
            }

            if (run > 0)
            {
                w.Ue((uint)run);
            }

            w.TrailingBits();
        }
    }

    private H264MbCode Code(int addr, int qp, bool predicted)
    {
        int mbX = addr % _config.WidthMbs, mbY = addr / _config.WidthMbs;
        int mbQp = Math.Clamp(qp + _qpOffsets[addr], MinQp, MaxQp);
        _sliceCost += !predicted ? _encoder.Encode(mbX, mbY, mbQp)
            : _syntax.SliceType == H264SliceType.B ? _encoder.EncodeB(mbX, mbY, mbQp) : _encoder.EncodeP(mbX, mbY, mbQp);

        // mb_skip_flag's contexts come from this macroblock's neighbours.
        _syntax.Locate(mbX, mbY);
        return _encoder.Best;
    }

    /// <summary>
    /// Sets each macroblock's QP offset from its energy, the squared deviation of its 8x8 luma blocks and its chroma from their
    /// means, against the picture's: busy macroblocks, where errors show less, get coarser steps, flat ones finer; the mean
    /// offset is about zero.
    /// </summary>
    private void AdaptQuantizers()
    {
        H264Picture pic = _current.Picture;
        int w = _config.WidthMbs, h = _config.HeightMbs;
        double[] logs = _logs;
        double sum = 0;
        for (int mbY = 0; mbY < h; mbY++)
        {
            for (int mbX = 0; mbX < w; mbX++)
            {
                long energy = 0;
                int luma = pic.Origin + (mbY * 16 * pic.Stride) + (mbX * 16);
                for (int i8 = 0; i8 < 4; i8++)
                {
                    energy += Energy(_srcY, luma + ((i8 >> 1) * 8 * pic.Stride) + ((i8 & 1) * 8), pic.Stride);
                }

                int chroma = pic.ChromaOrigin + (mbY * 8 * pic.ChromaStride) + (mbX * 8);
                energy += Energy(_srcCb, chroma, pic.ChromaStride) + Energy(_srcCr, chroma, pic.ChromaStride);
                double log = Math.Log2(Math.Max(energy, 1));
                logs[(mbY * w) + mbX] = log;
                sum += log;
            }
        }

        double mean = sum / logs.Length;
        for (int i = 0; i < logs.Length; i++)
        {
            _qpOffsets[i] = Math.Clamp((int)Math.Round(AqStrength * (logs[i] - mean)), -12, 12);
        }
    }

    /// <summary>The squared deviation of an 8x8 block from its mean.</summary>
    private static long Energy(byte[] plane, int at, int stride)
    {
        long sum = 0, squares = 0;
        for (int y = 0; y < 8; y++)
        {
            int row = at + (y * stride);
            for (int x = 0; x < 8; x++)
            {
                int v = plane[row + x];
                sum += v;
                squares += v * v;
            }
        }

        return squares - ((sum * sum) >> 6);
    }

    /// <summary>
    /// Weights a reference explicitly when the picture is a fade of it: each plane's weight from the ratio of their deviations
    /// and offset from their means, kept when it predicts the picture's luma at least a tenth better than the reference as it is.
    /// </summary>
    private void ChooseWeights(H264RefPlanes r)
    {
        int unit = 1 << H264RefPlanes.Denominator;
        r.Weighted = false;
        r.LumaWeight = r.CbWeight = r.CrWeight = (unit, 0);
        if (!_config.WeightedPred)
        {
            return;
        }

        H264Picture pic = r.Picture;
        int w = _config.WidthMbs * 16, h = _config.HeightMbs * 16;
        (int Weight, int Offset) luma = Fit(_srcY, pic.Y<byte>(), pic.Origin, pic.Stride, w, h, out double meanSrc, out double meanRef);
        if (Math.Abs(meanSrc - meanRef) < 2 || luma == (unit, 0))
        {
            return;
        }

        long plain = 0, weighted = 0;
        byte[] refY = pic.Y<byte>();
        for (int y = 0; y < h; y += 2)
        {
            int row = pic.Origin + (y * pic.Stride);
            for (int x = 0; x < w; x += 2)
            {
                int s = _srcY[row + x], p = refY[row + x];
                int q = Math.Clamp((((p * luma.Weight) + (unit >> 1)) >> H264RefPlanes.Denominator) + luma.Offset, 0, 255);
                plain += Math.Abs(s - p);
                weighted += Math.Abs(s - q);
            }
        }

        if (weighted * 10 > plain * 9)
        {
            return;
        }

        r.Weighted = true;
        r.LumaWeight = luma;
        r.CbWeight = Fit(_srcCb, pic.Cb<byte>(), pic.ChromaOrigin, pic.ChromaStride, w / 2, h / 2, out _, out _);
        r.CrWeight = Fit(_srcCr, pic.Cr<byte>(), pic.ChromaOrigin, pic.ChromaStride, w / 2, h / 2, out _, out _);
    }

    /// <summary>
    /// The weight (over 2^<see cref="H264RefPlanes.Denominator"/>) and offset that map a reference plane's mean and deviation
    /// onto the source's.
    /// </summary>
    private static (int Weight, int Offset) Fit(byte[] src, byte[] reference, int origin, int stride, int w, int h, out double meanSrc, out double meanRef)
    {
        double s1 = 0, s2 = 0, r1 = 0, r2 = 0;
        int n = 0;
        for (int y = 0; y < h; y += 2)
        {
            int row = origin + (y * stride);
            for (int x = 0; x < w; x += 2)
            {
                double s = src[row + x], p = reference[row + x];
                s1 += s;
                s2 += s * s;
                r1 += p;
                r2 += p * p;
                n++;
            }
        }

        meanSrc = s1 / n;
        meanRef = r1 / n;
        double devSrc = Math.Sqrt(Math.Max((s2 / n) - (meanSrc * meanSrc), 0)), devRef = Math.Sqrt(Math.Max((r2 / n) - (meanRef * meanRef), 0));
        int unit = 1 << H264RefPlanes.Denominator;
        int weight = devRef < 1 ? unit : Math.Clamp((int)Math.Round(unit * devSrc / devRef), -128, 127);
        int offset = Math.Clamp((int)Math.Round(meanSrc - (weight * meanRef / unit)), -128, 127);
        return (weight, offset);
    }
}
