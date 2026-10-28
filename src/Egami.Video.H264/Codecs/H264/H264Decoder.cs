using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// The H.264 decoding process (clause 8): parameter sets, picture boundaries, picture order count, reference marking, reference
/// lists, the slices of each picture, deblocking and the decoded picture buffer with its output order. A picture is a frame or
/// a field; the two fields of a frame are decoded into the same frame buffer, woven. Access units go in; decoded frames come out
/// in <see cref="Output"/> in output order, to be copied before the next call.
/// </summary>
internal sealed class H264Decoder
{
    private readonly H264Sps?[] _sps = new H264Sps?[32];
    private readonly byte[]?[] _ppsRbsp = new byte[]?[256];
    private readonly H264Pps?[] _pps = new H264Pps?[256];
    private readonly NalBumpingBuffer<H264Picture> _dpb = new();
    private readonly List<H264Picture> _all = [];
    private readonly List<H264FilterParams> _sliceParams = [];
    private readonly H264SliceDecoder _slices = new();
    private readonly H264Deblock _deblock = new();
    private readonly List<int> _removed = [];
    private readonly List<byte[]> _padded = [];
    private readonly List<SliceJob> _jobs = [];
    private readonly List<H264SliceDecoder> _sliceDecoders = [];
    private readonly List<H264Deblock> _deblocks = [];
    private Action<int>? _deblockBehind;
    private int[] _rowProgress = [];

    // Macroblock rows of the current picture decoded from the top without a gap, and deblocked.
    private int _decodedRows;
    private int _deblockedRows;

    private H264Sps? _active;
    private H264Sps? _lastSps;
    private H264MbState? _mb;
    private H264MbState? _fieldMb;

    // The frame being decoded, and the picture its slices decode into: the frame, or a view of the field being decoded.
    private H264Picture? _cur;
    private H264Picture? _curPic;
    private H264MbState? _curMb;
    private bool _curSecondField;

    // A frame whose first field is decoded and whose second field may follow, and that field's slice header.
    private H264Picture? _pending;
    private H264SliceHeader? _pendingHeader;
    private H264SliceHeader? _first;
    private H264Sei _sei = new();
    private object? _tag;
    private bool _curDamaged;
    private bool _curMissingReference;
    private int _curPocMsb;
    private int _curFrameNumOffset;

    // Picture order count and frame_num state of the pictures before (8.2.1, 8.2.5.2).
    private int _prevPocMsb;
    private int _prevPocLsb;
    private bool _prevRefBottomField;
    private int _prevFrameNumOffset;
    private int _prevFrameNum;
    private int _prevRefFrameNum;
    private int _maxLongTermFrameIdx = -1;

    private bool _started;
    private int _recoveryFrameNum = -1;
    private H264Picture? _lastDecoded;

    internal H264Decoder(SafetyLimits limits, DecodeErrorMode errors)
    {
        Limits = limits;
        Errors = errors;
    }

    internal SafetyLimits Limits { get; }

    internal DecodeErrorMode Errors { get; }

    /// <summary>Gets or sets how many threads decode slices and deblock: 1 decodes on the calling thread only.</summary>
    internal int MaxParallelism { get; set; } = 1;

    /// <summary>Gets or sets the size of the NAL unit lengths of each packet, or 0 for Annex B packets.</summary>
    internal int LengthSize { get; set; }

    /// <summary>Gets or sets where problems are reported: a diagnostic id and a message.</summary>
    internal Action<string, string>? Report { get; set; }

    /// <summary>Gets the decoded frames ready for output, in output order. The caller copies them before the next call.</summary>
    internal List<H264Picture> Output { get; } = [];

    /// <summary>Gets the SPS of the frames being decoded, or null before the first.</summary>
    internal H264Sps? ActiveSps => _active;

    /// <summary>Gets how many pictures have started so far, so the adapter knows which packets gave a picture.</summary>
    internal int PicturesStarted { get; private set; }

    /// <summary>Gets the number of frame buffers allocated.</summary>
    internal int FrameBuffers => _all.Count;

    /// <summary>Reads the parameter sets of an avcC record.</summary>
    internal void Configure(H264Config config)
    {
        LengthSize = config.LengthSize;
        foreach (ReadOnlyMemory<byte> nal in config.ParameterSets)
        {
            DecodeNal(nal, null);
        }
    }

    /// <summary>Decodes one access unit: its parameter sets, SEI and the slices of its picture.</summary>
    /// <exception cref="CorruptDataException">The data is broken and the error mode is Throw.</exception>
    internal void Decode(ReadOnlyMemory<byte> packet, object? tag)
    {
        Output.Clear();
        List<ReadOnlyMemory<byte>> nals = Split(packet);
        _tag = tag;
        foreach (ReadOnlyMemory<byte> nal in nals)
        {
            DecodeNal(nal, tag);
        }

        FinishPicture();
    }

    /// <summary>
    /// Splits a packet into its NAL units: length-prefixed after an avcC record, else Annex B. A length-prefixed packet can start
    /// with bytes that look like a start code (a 4-byte size of 0x127 is 00 00 01 27), so the sizes are tried first, and a
    /// packet whose sizes do not add up but that starts with a start code is read as Annex B, as some muxers store it.
    /// </summary>
    private List<ReadOnlyMemory<byte>> Split(ReadOnlyMemory<byte> packet)
    {
        if (LengthSize == 0)
        {
            return NalFraming.SplitAnnexB(packet);
        }

        try
        {
            return NalFraming.SplitLengthPrefixed(packet, LengthSize);
        }
        catch (CorruptDataException) when (NalFraming.StartsWithStartCode(packet.Span))
        {
            return NalFraming.SplitAnnexB(packet);
        }
    }

    /// <summary>Ends the stream: outputs every waiting frame.</summary>
    internal void Flush()
    {
        Output.Clear();
        FinishPicture();
        FinishUnpairedField();
        while (_dpb.TryFlush(out H264Picture picture, out _))
        {
            Emit(picture);
        }
    }

    /// <summary>Drops every frame and the decoding state, for a seek: decoding restarts at the next IDR or recovery point.</summary>
    internal void Reset()
    {
        Output.Clear();
        _cur = null;
        _curPic = null;
        _first = null;
        _pending = null;
        _pendingHeader = null;
        _dpb.Clear();
        _started = false;
        _recoveryFrameNum = -1;
        _lastDecoded = null;
        _maxLongTermFrameIdx = -1;
        _sei = new H264Sei();
    }

    private void DecodeNal(ReadOnlyMemory<byte> nal, object? tag)
    {
        ReadOnlySpan<byte> span = nal.Span;
        if (span.Length == 0 || (span[0] & 0x80) != 0)
        {
            return;
        }

        int type = span[0] & 31;
        switch (type)
        {
            case 1 or 5:
                DecodeSlice(nal, tag);
                break;
            case 6:
                FinishPictureBeforeNonVcl();
                _sei.Read(NalFraming.Unescape(span), _lastSps ?? _active);
                break;
            case 7:
                {
                    FinishPictureBeforeNonVcl();
                    byte[] rbsp = NalFraming.Unescape(span);
                    H264Sps sps = H264Sps.Read(rbsp, Limits);
                    if (_sps[sps.Id] is { } old && old.Rbsp.AsSpan().SequenceEqual(rbsp))
                    {
                        _lastSps = old;
                        break;
                    }

                    _sps[sps.Id] = sps;
                    _lastSps = sps;
                    break;
                }

            case 8:
                {
                    FinishPictureBeforeNonVcl();
                    byte[] rbsp = NalFraming.Unescape(span);
                    (int ppsId, _) = H264Pps.ReadIds(rbsp);
                    if (_ppsRbsp[ppsId] is { } old && old.AsSpan().SequenceEqual(rbsp))
                    {
                        break;
                    }

                    _ppsRbsp[ppsId] = rbsp;
                    _pps[ppsId] = null;
                    break;
                }

            case 9 or 10 or 11:
                FinishPictureBeforeNonVcl();
                break;
            case 2 or 3 or 4:
                throw H264Errors.Unsupported("The stream uses data partitioning (Extended profile).");
        }
    }

    private void FinishPictureBeforeNonVcl()
    {
        // A parameter set, SEI, delimiter or end of sequence after a picture's slices starts the next access unit.
        if (_cur is not null)
        {
            FinishPicture();
        }
    }

    private H264Pps? FindPps(int id)
    {
        if (id > 255 || _ppsRbsp[id] is not { } rbsp)
        {
            return null;
        }

        (_, int spsId) = H264Pps.ReadIds(rbsp);
        H264Sps? sps = _sps[spsId];
        if (sps is null)
        {
            return null;
        }

        if (_pps[id] is not { } pps || !ReferenceEquals(pps.Sps, sps))
        {
            pps = _pps[id] = H264Pps.Read(rbsp, sps);
        }

        return pps;
    }

    private void DecodeSlice(ReadOnlyMemory<byte> nal, object? tag)
    {
        byte[] rbsp = NalFraming.Unescape(nal.Span);
        H264SliceHeader h;
        try
        {
            h = H264SliceHeader.Read(rbsp, FindPps);
        }
        catch (CorruptDataException e)
        {
            // Before the first IDR picture or recovery point, slices whose parameter sets came earlier in the stream are
            // passed over like any other picture decoding cannot start at.
            if (_started)
            {
                Fail("H264.CorruptFrame", $"A slice header cannot be read ({e.Message}).", e);
            }

            return;
        }

        if (h.RedundantPicCnt > 0)
        {
            return;
        }

        if (_cur is not null && IsNewPicture(_first!, h))
        {
            FinishPicture();
        }

        if (_cur is null)
        {
            if (!StartPicture(h, tag))
            {
                return;
            }
        }

        DecodeSliceData(h, rbsp);
    }

    /// <summary>Whether a slice starts a new picture (7.4.1.2.4).</summary>
    internal static bool IsNewPicture(H264SliceHeader a, H264SliceHeader b) =>
        b.FirstMb == 0 || a.FrameNum != b.FrameNum || a.Pps.Id != b.Pps.Id || (a.NalRefIdc == 0) != (b.NalRefIdc == 0)
        || a.FieldPic != b.FieldPic || a.BottomField != b.BottomField
        || a.PocLsb != b.PocLsb || a.DeltaPocBottom != b.DeltaPocBottom || a.DeltaPoc0 != b.DeltaPoc0 || a.DeltaPoc1 != b.DeltaPoc1
        || a.Idr != b.Idr || (a.Idr && a.IdrPicId != b.IdrPicId);

    private bool StartPicture(H264SliceHeader h, object? tag)
    {
        H264Sps sps = h.Sps;
        bool second = _pending is not null && IsSecondField(_pendingHeader!, h);
        if (_pending is not null && !second)
        {
            FinishUnpairedField();
        }

        if (!_started)
        {
            if (!h.Idr && _sei.RecoveryFrameCount < 0)
            {
                // Decoding starts at an IDR picture or a recovery point.
                _sei = new H264Sei();
                return false;
            }

            _started = true;
            if (!h.Idr)
            {
                _recoveryFrameNum = (h.FrameNum + _sei.RecoveryFrameCount) % sps.MaxFrameNum;
                _prevRefFrameNum = h.FrameNum == 0 ? 0 : h.FrameNum - 1;
                _prevFrameNum = _prevRefFrameNum;
            }
        }

        if (_active is null || sps.Width != _active.Width || sps.Height != _active.Height || sps.ChromaFormatIdc != _active.ChromaFormatIdc
            || sps.BitDepthLuma != _active.BitDepthLuma || sps.BitDepthChroma != _active.BitDepthChroma)
        {
            // A new size or format: the frames before are output and the buffers start over.
            while (_dpb.TryFlush(out H264Picture picture, out _))
            {
                Emit(picture);
            }

            _dpb.Clear();
            _all.Clear();
            _lastDecoded = null;
            _pending = null;
            second = false;
            _mb = new H264MbState(sps.WidthInMbs, sps.HeightInMbs, sps.ChromaFormatIdc);
            _fieldMb = sps.FrameMbsOnly ? null : new H264MbState(sps.WidthInMbs, sps.HeightInMbs / 2, sps.ChromaFormatIdc, fieldPicture: true);
        }

        _active = sps;
        _dpb.Capacity = sps.DpbFrames;
        _dpb.MaxNumReorder = sps.MaxNumReorderFrames;
        H264Picture pic;
        if (second)
        {
            // The second field of the frame whose first field came last (7.4.1.2.4): it decodes into the same frame.
            pic = _pending!;
            _pending = null;
            _pendingHeader = null;
        }
        else
        {
            if (!h.Idr && h.FrameNum != _prevRefFrameNum && h.FrameNum != (_prevRefFrameNum + 1) % sps.MaxFrameNum)
            {
                FillFrameNumGap(h);
            }

            pic = Allocate(sps);
            pic.Sps = sps;
            pic.FrameNum = h.FrameNum;
            pic.DecodedFields = h.FieldPic ? 0 : 3;
            pic.CodedAs = h.FieldPic ? H264Coding.Fields : h.Mbaff ? H264Coding.Mbaff : H264Coding.Frame;
            pic.Tag = tag;
            _curDamaged = false;
            _curMissingReference = false;
        }

        ComputePoc(h, pic);
        _cur = pic;
        _curSecondField = second;
        _curPic = h.FieldPic ? pic.Field(h.BottomField ? 1 : 0) : pic;
        _curMb = h.FieldPic ? _fieldMb! : _mb!;
        _first = h;
        PicturesStarted++;
        _sliceParams.Clear();
        _curMb.Clear();
        _decodedRows = 0;
        _deblockedRows = 0;
        return true;
    }

    /// <summary>
    /// Whether a field picture is the second field of the frame whose first field is pending (7.4.1.2.4): the other parity, the
    /// same frame_num, not an IDR picture, and a reference field when the first is one.
    /// </summary>
    private static bool IsSecondField(H264SliceHeader first, H264SliceHeader h) =>
        h.FieldPic && first.BottomField != h.BottomField && first.FrameNum == h.FrameNum && !h.Idr
        && (first.NalRefIdc != 0) == (h.NalRefIdc != 0) && ReferenceEquals(first.Sps, h.Sps);

    /// <summary>
    /// Stores a frame of which only one field was decoded: the missing field's rows are copied from the decoded field's, as a
    /// frame needs both.
    /// </summary>
    private void FinishUnpairedField()
    {
        if (_pending is not { } pic)
        {
            return;
        }

        _pending = null;
        _pendingHeader = null;
        int decoded = pic.DecodedFields == 1 ? 0 : 1;
        Report?.Invoke("H264.UnpairedField", $"The {(decoded == 0 ? "top" : "bottom")} field of frame_num {pic.FrameNum} has no second field; its rows fill the frame.");
        CopyField(pic, decoded);
        pic.ExtendEdges();
        StoreFrame(pic);
    }

    /// <summary>Copies each row of one field of a frame into the row of the other field below or above it.</summary>
    private static void CopyField(H264Picture pic, int from)
    {
        CopyRows(pic.YPlane, pic.Origin, pic.Stride, pic.Height, from);
        if (pic.ChromaFormatIdc != 0)
        {
            CopyRows(pic.CbPlane, pic.ChromaOrigin, pic.ChromaStride, pic.ChromaHeight, from);
            CopyRows(pic.CrPlane, pic.ChromaOrigin, pic.ChromaStride, pic.ChromaHeight, from);
        }

        static void CopyRows(Array plane, int origin, int stride, int height, int from)
        {
            for (int y = 0; y < height; y += 2)
            {
                Array.Copy(plane, origin + ((y + from) * stride), plane, origin + ((y + 1 - from) * stride), stride - (2 * H264Picture.Pad));
            }
        }
    }

    private H264Picture Allocate(H264Sps sps)
    {
        foreach (H264Picture candidate in _all)
        {
            if (candidate == _cur || candidate == _pending || Output.Contains(candidate) || InDpb(candidate))
            {
                continue;
            }

            candidate.Reset();
            return candidate;
        }

        H264Picture pic = new(sps.WidthInMbs, sps.HeightInMbs, sps.ChromaFormatIdc, sps.BitDepthLuma == 8 && sps.BitDepthChroma == 8);
        pic.Reset();
        _all.Add(pic);
        return pic;
    }

    private bool InDpb(H264Picture picture)
    {
        foreach (H264Picture p in _dpb.Pictures())
        {
            if (p == picture)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The picture order count (8.2.1) of a frame, or of the field being decoded.</summary>
    private void ComputePoc(H264SliceHeader h, H264Picture pic)
    {
        H264Sps sps = h.Sps;
        bool top = !h.FieldPic || !h.BottomField, bottom = !h.FieldPic || h.BottomField;
        if (h.Idr)
        {
            _prevPocMsb = _prevPocLsb = 0;
            _prevFrameNumOffset = 0;
        }

        int frameNumOffset = h.Idr ? 0 : _prevFrameNum > h.FrameNum ? _prevFrameNumOffset + sps.MaxFrameNum : _prevFrameNumOffset;
        _curFrameNumOffset = frameNumOffset;
        switch (sps.PocType)
        {
            case 0:
                {
                    int maxLsb = 1 << sps.Log2MaxPocLsb;
                    int lsb = h.PocLsb;
                    int msb = lsb < _prevPocLsb && _prevPocLsb - lsb >= maxLsb / 2 ? _prevPocMsb + maxLsb
                        : lsb > _prevPocLsb && lsb - _prevPocLsb > maxLsb / 2 ? _prevPocMsb - maxLsb
                        : _prevPocMsb;
                    _curPocMsb = msb;
                    if (!h.FieldPic)
                    {
                        pic.TopPoc = msb + lsb;
                        pic.BottomPoc = pic.TopPoc + h.DeltaPocBottom;
                    }
                    else if (h.BottomField)
                    {
                        pic.BottomPoc = msb + lsb;
                    }
                    else
                    {
                        pic.TopPoc = msb + lsb;
                    }

                    break;
                }

            case 1:
                {
                    int cycle = sps.OffsetForRefFrame.Length;
                    int absFrameNum = cycle != 0 ? frameNumOffset + h.FrameNum : 0;
                    if (h.NalRefIdc == 0 && absFrameNum > 0)
                    {
                        absFrameNum--;
                    }

                    long expected = 0;
                    if (absFrameNum > 0)
                    {
                        long deltaPerCycle = 0;
                        foreach (int offset in sps.OffsetForRefFrame)
                        {
                            deltaPerCycle += offset;
                        }

                        int cycleCount = (absFrameNum - 1) / cycle, inCycle = (absFrameNum - 1) % cycle;
                        expected = cycleCount * deltaPerCycle;
                        for (int i = 0; i <= inCycle; i++)
                        {
                            expected += sps.OffsetForRefFrame[i];
                        }
                    }

                    if (h.NalRefIdc == 0)
                    {
                        expected += sps.OffsetForNonRefPic;
                    }

                    if (!h.FieldPic)
                    {
                        pic.TopPoc = (int)(expected + h.DeltaPoc0);
                        pic.BottomPoc = pic.TopPoc + sps.OffsetForTopToBottomField + h.DeltaPoc1;
                    }
                    else if (h.BottomField)
                    {
                        pic.BottomPoc = (int)(expected + sps.OffsetForTopToBottomField + h.DeltaPoc0);
                    }
                    else
                    {
                        pic.TopPoc = (int)(expected + h.DeltaPoc0);
                    }

                    break;
                }

            default:
                {
                    int temp = h.Idr ? 0 : h.NalRefIdc == 0 ? (2 * (frameNumOffset + h.FrameNum)) - 1 : 2 * (frameNumOffset + h.FrameNum);
                    if (top)
                    {
                        pic.TopPoc = temp;
                    }

                    if (bottom)
                    {
                        pic.BottomPoc = temp;
                    }

                    break;
                }
        }
    }

    /// <summary>The decoding process for gaps in frame_num (8.2.5.2): "non-existing" short-term frames fill the gap.</summary>
    private void FillFrameNumGap(H264SliceHeader h)
    {
        H264Sps sps = h.Sps;
        if (!sps.GapsInFrameNumAllowed)
        {
            Report?.Invoke("H264.MissingReference", $"frame_num jumps from {_prevRefFrameNum} to {h.FrameNum}; the frames between are missing.");
        }

        int frameNum = (_prevRefFrameNum + 1) % sps.MaxFrameNum;
        int guard = 0;
        while (frameNum != h.FrameNum && guard++ < sps.MaxFrameNum)
        {
            H264Picture gap = Allocate(sps);
            gap.NonExisting = true;
            gap.CodedAs = H264Coding.Frame;
            gap.Sps = sps;
            gap.FrameNum = frameNum;
            Array.Fill(gap.MbIntra, true);
            if (_lastDecoded is not null && _lastDecoded.Narrow == gap.Narrow && _lastDecoded.YPlane.Length == gap.YPlane.Length
                && _lastDecoded.CbPlane.Length == gap.CbPlane.Length)
            {
                Array.Copy(_lastDecoded.YPlane, gap.YPlane, gap.YPlane.Length);
                Array.Copy(_lastDecoded.CbPlane, gap.CbPlane, gap.CbPlane.Length);
                Array.Copy(_lastDecoded.CrPlane, gap.CrPlane, gap.CrPlane.Length);
            }
            else
            {
                FillGray(gap, sps);
            }

            int offset = _prevFrameNum > frameNum ? _prevFrameNumOffset + sps.MaxFrameNum : _prevFrameNumOffset;
            int poc = 2 * (offset + frameNum);
            gap.TopPoc = gap.BottomPoc = poc;
            SlidingWindow(sps, frameNum);
            gap.IsShortTermReference = true;
            _dpb.RemoveUnused();
            _dpb.Insert(gap, poc, false);
            _prevFrameNumOffset = offset;
            _prevFrameNum = frameNum;
            _prevRefFrameNum = frameNum;
            frameNum = (frameNum + 1) % sps.MaxFrameNum;
        }
    }

    private static void FillGray(H264Picture pic, H264Sps sps)
    {
        if (pic.Narrow)
        {
            Array.Fill(pic.Y<byte>(), (byte)128);
            Array.Fill(pic.Cb<byte>(), (byte)128);
            Array.Fill(pic.Cr<byte>(), (byte)128);
            return;
        }

        Array.Fill(pic.Y<ushort>(), (ushort)(1 << (sps.BitDepthLuma - 1)));
        Array.Fill(pic.Cb<ushort>(), (ushort)(1 << (sps.BitDepthChroma - 1)));
        Array.Fill(pic.Cr<ushort>(), (ushort)(1 << (sps.BitDepthChroma - 1)));
    }

    private void DecodeSliceData(H264SliceHeader h, byte[] rbsp)
    {
        H264Picture pic = _curPic!;
        int sliceNum = _sliceParams.Count;
        _sliceParams.Add(new H264FilterParams(h.DisableDeblockingFilterIdc, h.FilterOffsetA, h.FilterOffsetB));
        H264Picture[] list0 = [], list1 = [];
        int[] implicitWeights = [];
        try
        {
            if (!h.IsI)
            {
                list0 = h.FieldPic ? BuildFieldList(h, 0) : BuildList(h, 0);
                if (h.IsB)
                {
                    list1 = h.FieldPic ? BuildFieldList(h, 1) : BuildList(h, 1);
                    if (h.Pps.WeightedBipredIdc == 2)
                    {
                        implicitWeights = ImplicitWeights(pic, list0, list1);
                    }
                }
            }

            if (MaxParallelism > 1)
            {
                // Kept until the picture ends, when its slices decode side by side.
                _jobs.Add(new SliceJob(h, Pad(_jobs.Count, rbsp), rbsp.Length, sliceNum, list0, list1, implicitWeights));
                return;
            }

            byte[] padded = Pad(0, rbsp);
            // An MBAFF frame is deblocked once it is decoded, pair by pair.
            _slices.RowFinished = h.Mbaff ? null : _deblockBehind ??= DeblockBehind;
            try
            {
                _slices.Decode(h, padded, rbsp.Length, pic, _curMb!, sliceNum, list0, list1, implicitWeights);
            }
            finally
            {
                _slices.RowFinished = null;
            }
        }
        catch (CorruptDataException e)
        {
            _curDamaged = true;
            Fail("H264.CorruptFrame", $"A slice of frame_num {h.FrameNum} cannot be decoded ({e.Message}).", e);
        }
    }

    /// <summary>Copies a slice's RBSP into reused buffer <paramref name="index"/> with 16 zero bytes after it for the bit reader.</summary>
    private byte[] Pad(int index, byte[] rbsp)
    {
        while (_padded.Count <= index)
        {
            _padded.Add([]);
        }

        byte[] padded = _padded[index];
        if (padded.Length < rbsp.Length + 16)
        {
            padded = _padded[index] = new byte[rbsp.Length + 1024];
        }

        rbsp.CopyTo(padded, 0);
        padded.AsSpan(rbsp.Length, 16).Clear();
        return padded;
    }

    /// <summary>Decodes the slices kept for the picture, side by side: slices predict only from their own macroblocks.</summary>
    /// <returns>The first slice's error, which Throw mode raises once the picture is finished.</returns>
    private CorruptDataException? RunJobs()
    {
        if (_jobs.Count == 0)
        {
            return null;
        }

        while (_sliceDecoders.Count < Math.Min(_jobs.Count, MaxParallelism))
        {
            _sliceDecoders.Add(new H264SliceDecoder());
        }

        Exception?[] errors = new Exception?[_jobs.Count];
        H264Picture pic = _curPic!;
        H264MbState mb = _curMb!;
        if (_jobs.Count == 1)
        {
            errors[0] = Run(_slices, _jobs[0], pic, mb);
        }
        else
        {
            int next = -1;
            Parallel.For(0, _sliceDecoders.Count, new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism }, worker =>
            {
                H264SliceDecoder decoder = _sliceDecoders[worker];
                int job;
                while ((job = Interlocked.Increment(ref next)) < _jobs.Count)
                {
                    errors[job] = Run(decoder, _jobs[job], pic, mb);
                }
            });
        }

        CorruptDataException? first = null;
        for (int i = 0; i < _jobs.Count; i++)
        {
            if (errors[i] is CorruptDataException e)
            {
                _curDamaged = true;
                Report?.Invoke("H264.CorruptFrame", $"A slice of frame_num {_jobs[i].Header.FrameNum} cannot be decoded ({e.Message}).");
                first ??= e;
            }
        }

        _jobs.Clear();
        return first;

        static Exception? Run(H264SliceDecoder decoder, SliceJob job, H264Picture pic, H264MbState mb)
        {
            try
            {
                decoder.Decode(job.Header, job.Data, job.Length, pic, mb, job.SliceNum, job.List0, job.List1, job.ImplicitWeights);
                return null;
            }
            catch (CorruptDataException e)
            {
                return e;
            }
        }
    }

    /// <summary>
    /// Deblocks the picture on <see cref="MaxParallelism"/> threads in a wavefront: each takes the next macroblock row and filters
    /// a macroblock once the row above has filtered the one up and to its right, whose left edge filter reaches the samples this
    /// macroblock's top edge filter reads. The result equals filtering in address order.
    /// </summary>
    /// <summary>
    /// Deblocks the rows of a picture decoded on one thread as decoding goes, while their samples are in the cache: a row once
    /// the row below it is decoded, since that row's intra prediction reads the samples before the filter. A row filters its
    /// own samples and the three rows above it, so the row below stays as decoded.
    /// </summary>
    private void DeblockBehind(int row)
    {
        H264MbState mb = _curMb!;
        int cols = mb.WidthMbs;
        int[] slice = mb.Slice;
        while (_decodedRows <= row && slice.AsSpan(_decodedRows * cols, cols).IndexOf(-1) < 0)
        {
            _decodedRows++;
        }

        if (_deblockedRows < _decodedRows - 1)
        {
            H264Sps sps = _first!.Sps;
            _deblock.Filter(_curPic!, mb, _sliceParams, sps.BitDepthLuma, sps.BitDepthChroma, _deblockedRows, _decodedRows - 1);
            _deblockedRows = _decodedRows - 1;
        }
    }

    private void Deblock(H264Picture pic, H264MbState mb, H264Sps sps)
    {
        if (_first!.Mbaff)
        {
            _deblock.FilterMbaff(pic, mb, _sliceParams, sps.BitDepthLuma, sps.BitDepthChroma);
            return;
        }

        int rows = mb.HeightMbs, cols = sps.WidthInMbs;
        int workers = Math.Min(MaxParallelism, rows - _deblockedRows);
        if (workers <= 1)
        {
            _deblock.Filter(pic, mb, _sliceParams, sps.BitDepthLuma, sps.BitDepthChroma, _deblockedRows, rows);
            return;
        }

        while (_deblocks.Count < workers)
        {
            _deblocks.Add(new H264Deblock());
        }

        if (_rowProgress.Length < rows)
        {
            _rowProgress = new int[rows];
        }

        int[] progress = _rowProgress;
        Array.Clear(progress, 0, rows);
        int first = _deblockedRows, next = first - 1;
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
        {
            H264Deblock deblock = _deblocks[worker];
            int row;
            while ((row = Interlocked.Increment(ref next)) < rows)
            {
                // The row above is read only when the last value seen is not enough, and this row's progress is published
                // every few macroblocks, so the threads touch the shared counters rarely.
                int above = row > first ? 0 : cols;
                for (int x = 0; x < cols; x++)
                {
                    int need = Math.Min(x + 2, cols);
                    if (above < need)
                    {
                        SpinWait spin = default;
                        while ((above = Volatile.Read(ref progress[row - 1])) < need)
                        {
                            spin.SpinOnce(sleep1Threshold: -1);
                        }
                    }

                    deblock.FilterOne(pic, mb, _sliceParams, sps.BitDepthLuma, sps.BitDepthChroma, x, row);
                    if ((x & 7) == 7 || x == cols - 1)
                    {
                        Volatile.Write(ref progress[row], x + 1);
                    }
                }
            }
        });
    }

    private void Fail(string id, string message, Exception e)
    {
        Report?.Invoke(id, message);
        if (Errors == DecodeErrorMode.Throw)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw();
        }
    }

    /// <summary>Builds RefPicList0 or 1 (8.2.4): the initial order, then the modifications, padded when pictures are missing.</summary>
    private H264Picture[] BuildList(H264SliceHeader h, int list)
    {
        H264Sps sps = h.Sps;
        int count = list == 0 ? h.NumRefIdxL0 : h.NumRefIdxL1;
        List<H264Picture> shortTerm = [], longTerm = [];
        foreach (H264Picture p in _dpb.Pictures())
        {
            if (p.IsShortTermReference)
            {
                p.FrameNumWrap = p.FrameNum > h.FrameNum ? p.FrameNum - sps.MaxFrameNum : p.FrameNum;
                shortTerm.Add(p);
            }
            else if (p.IsLongTermReference)
            {
                longTerm.Add(p);
            }
        }

        longTerm.Sort(static (a, b) => a.LongTermFrameIdx.CompareTo(b.LongTermFrameIdx));
        List<H264Picture> initial = [];
        if (!h.IsB)
        {
            shortTerm.Sort(static (a, b) => b.FrameNumWrap.CompareTo(a.FrameNumWrap));
            initial.AddRange(shortTerm);
        }
        else
        {
            int poc = _cur!.Poc;
            List<H264Picture> before = [.. shortTerm.Where(p => p.Poc < poc).OrderByDescending(p => p.Poc)];
            List<H264Picture> after = [.. shortTerm.Where(p => p.Poc > poc).OrderBy(p => p.Poc)];
            initial.AddRange(list == 0 ? before : after);
            initial.AddRange(list == 0 ? after : before);
        }

        initial.AddRange(longTerm);
        if (list == 1 && initial.Count > 1)
        {
            List<H264Picture> l0 = [.. shortTerm.Where(p => p.Poc < _cur!.Poc).OrderByDescending(p => p.Poc)];
            l0.AddRange(shortTerm.Where(p => p.Poc > _cur!.Poc).OrderBy(p => p.Poc));
            l0.AddRange(longTerm);
            if (l0.SequenceEqual(initial))
            {
                (initial[0], initial[1]) = (initial[1], initial[0]);
            }
        }

        if (initial.Count > count)
        {
            initial.RemoveRange(count, initial.Count - count);
        }

        H264ListModification[] mods = list == 0 ? h.ModificationsL0 : h.ModificationsL1;
        if (mods.Length > 0)
        {
            Modify(h, initial, mods, count);
        }

        if (initial.Count < count || initial.Contains(null!))
        {
            _curMissingReference = true;
            H264Picture? stand = initial.FirstOrDefault(p => p is not null) ?? _lastDecoded ?? shortTerm.FirstOrDefault() ?? longTerm.FirstOrDefault();
            if (stand is null)
            {
                stand = Allocate(sps);
                FillGray(stand, sps);
                Array.Fill(stand.MbIntra, true);
                stand.ExtendEdges();
            }

            for (int i = 0; i < initial.Count; i++)
            {
                initial[i] ??= stand;
            }

            while (initial.Count < count)
            {
                initial.Add(stand);
            }
        }

        return [.. initial];
    }

    /// <summary>
    /// Builds RefPicList0 or 1 of a field (8.2.4.2.2, 8.2.4.2.4, 8.2.4.2.5): the reference frames in frame order, by FrameNumWrap
    /// for P and by order count for B, then long-term by LongTermFrameIdx, each split into fields alternating between the
    /// current parity and the other; then the modifications, padded when pictures are missing.
    /// </summary>
    private H264Picture[] BuildFieldList(H264SliceHeader h, int list)
    {
        H264Sps sps = h.Sps;
        int count = list == 0 ? h.NumRefIdxL0 : h.NumRefIdxL1;
        int parity = h.BottomField ? 1 : 0;
        List<H264Picture> shortTerm = [], longTerm = [];
        foreach (H264Picture p in ReferenceFrames())
        {
            if (p.HasShortTermField)
            {
                p.FrameNumWrap = p.FrameNum > h.FrameNum ? p.FrameNum - sps.MaxFrameNum : p.FrameNum;
                shortTerm.Add(p);
            }

            if (p.HasLongTermField)
            {
                longTerm.Add(p);
            }
        }

        longTerm.Sort(static (a, b) => a.LongTermFrameIdx.CompareTo(b.LongTermFrameIdx));
        if (!h.IsB)
        {
            shortTerm.Sort(static (a, b) => b.FrameNumWrap.CompareTo(a.FrameNumWrap));
        }
        else
        {
            // Entries at or before the current field's order count, nearest first, then those after, nearest first; list 1 the
            // other way round. An entry's order count is that of its fields marked for short-term reference.
            int poc = _curPic!.Poc;
            List<H264Picture> before = [.. shortTerm.Where(p => ShortTermPoc(p) <= poc).OrderByDescending(ShortTermPoc)];
            List<H264Picture> after = [.. shortTerm.Where(p => ShortTermPoc(p) > poc).OrderBy(ShortTermPoc)];
            shortTerm = list == 0 ? [.. before, .. after] : [.. after, .. before];
        }

        List<H264Picture> initial = [];
        SplitFields(shortTerm, parity, false, initial);
        SplitFields(longTerm, parity, true, initial);
        if (list == 1 && initial.Count > 1)
        {
            // List 1 is list 0 with its first two entries swapped when the two would be the same (8.2.4.2.4).
            int poc = _curPic!.Poc;
            List<H264Picture> frames0 = [.. shortTerm.Where(p => ShortTermPoc(p) <= poc).OrderByDescending(ShortTermPoc)];
            frames0.AddRange(shortTerm.Where(p => ShortTermPoc(p) > poc).OrderBy(ShortTermPoc));
            List<H264Picture> l0 = [];
            SplitFields(frames0, parity, false, l0);
            SplitFields(longTerm, parity, true, l0);
            if (l0.SequenceEqual(initial))
            {
                (initial[0], initial[1]) = (initial[1], initial[0]);
            }
        }

        if (initial.Count > count)
        {
            initial.RemoveRange(count, initial.Count - count);
        }

        H264ListModification[] mods = list == 0 ? h.ModificationsL0 : h.ModificationsL1;
        if (mods.Length > 0)
        {
            Modify(h, initial, mods, count);
        }

        if (initial.Count < count || initial.Contains(null!))
        {
            _curMissingReference = true;
            H264Picture? stand = initial.FirstOrDefault(p => p is not null) ?? _lastDecoded?.Field(parity) ?? shortTerm.FirstOrDefault()?.Field(parity) ?? longTerm.FirstOrDefault()?.Field(parity);
            if (stand is null)
            {
                H264Picture gray = Allocate(sps);
                FillGray(gray, sps);
                Array.Fill(gray.MbIntra, true);
                gray.ExtendEdges();
                stand = gray.Field(parity);
                Array.Fill(stand.MbIntra, true);
            }

            for (int i = 0; i < initial.Count; i++)
            {
                initial[i] ??= stand;
            }

            while (initial.Count < count)
            {
                initial.Add(stand);
            }
        }

        return [.. initial];
    }

    /// <summary>The order count of a frame's fields marked for short-term reference (8.2.4.2.4): the smaller when both are.</summary>
    private static int ShortTermPoc(H264Picture frame)
    {
        bool top = frame.Field(0).IsShortTermReference && (frame.DecodedFields & 1) != 0;
        bool bottom = frame.Field(1).IsShortTermReference && (frame.DecodedFields & 2) != 0;
        return top && bottom ? Math.Min(frame.TopPoc, frame.BottomPoc) : top ? frame.TopPoc : frame.BottomPoc;
    }

    /// <summary>
    /// Appends the fields of the frames (8.2.4.2.5), alternating between the parity given and the other, starting with it, each
    /// taken in frame order from the frames that have that field marked; when one parity runs out the rest of the other follow.
    /// </summary>
    private static void SplitFields(List<H264Picture> frames, int parity, bool longTerm, List<H264Picture> into)
    {
        List<H264Picture> same = [], other = [];
        foreach (H264Picture frame in frames)
        {
            for (int f = 0; f < 2; f++)
            {
                H264Picture field = frame.Field(f);
                if ((frame.DecodedFields & (1 << f)) != 0 && (longTerm ? field.IsLongTermReference : field.IsShortTermReference))
                {
                    (f == parity ? same : other).Add(field);
                }
            }
        }

        int a = 0, b = 0;
        while (a < same.Count || b < other.Count)
        {
            if (a < same.Count)
            {
                into.Add(same[a++]);
            }

            if (b < other.Count)
            {
                into.Add(other[b++]);
            }
        }
    }

    /// <summary>The modification process for reference picture lists (8.2.4.3), for frames and fields.</summary>
    private void Modify(H264SliceHeader h, List<H264Picture> refs, H264ListModification[] mods, int count)
    {
        int maxPicNum = h.MaxPicNum;
        int currPicNum = h.CurrPicNum;
        int picNumPred = currPicNum;
        while (refs.Count < count + 1)
        {
            refs.Add(null!);
        }

        int refIdx = 0;
        foreach (H264ListModification mod in mods)
        {
            if (refIdx >= count)
            {
                throw H264Errors.Corrupt("A reference list has more modifications than entries.");
            }

            H264Picture? pic;
            bool isLong = mod.Idc == 2;
            int num;
            if (!isLong)
            {
                int abs = mod.Value + 1;
                int noWrap = mod.Idc == 0 ? picNumPred - abs : picNumPred + abs;
                if (noWrap < 0)
                {
                    noWrap += maxPicNum;
                }
                else if (noWrap >= maxPicNum)
                {
                    noWrap -= maxPicNum;
                }

                picNumPred = noWrap;
                num = noWrap > currPicNum ? noWrap - maxPicNum : noWrap;
                pic = FindShortTerm(h, num);
            }
            else
            {
                num = mod.Value;
                pic = FindLongTerm(h, num);
            }

            if (pic is null)
            {
                _curMissingReference = true;
                Report?.Invoke("H264.MissingReference", $"A reference list names {(isLong ? "long-term" : "short-term")} picture {num}, which is not in the buffer.");
            }

            for (int c = count; c > refIdx; c--)
            {
                refs[c] = refs[c - 1];
            }

            refs[refIdx++] = pic!;
            int n = refIdx;
            for (int c = refIdx; c <= count; c++)
            {
                H264Picture? p = refs[c];
                bool same = p is not null && p == pic;
                if (!same)
                {
                    refs[n++] = p!;
                }
            }
        }

        refs.RemoveRange(count, refs.Count - count);
    }

    /// <summary>The implicit bi-prediction weights w0 per (refIdxL0, refIdxL1) pair (8.4.2.3.1), w1 being 64 - w0.</summary>
    internal static int[] ImplicitWeights(H264Picture cur, H264Picture[] list0, H264Picture[] list1)
    {
        int[] w = new int[32 * 32];
        Array.Fill(w, 32);
        for (int i = 0; i < list0.Length; i++)
        {
            for (int j = 0; j < list1.Length; j++)
            {
                H264Picture p0 = list0[i], p1 = list1[j];
                int td = Math.Clamp(p1.Poc - p0.Poc, -128, 127);
                if (td == 0 || p0.IsLongTermReference || p1.IsLongTermReference)
                {
                    continue;
                }

                int tb = Math.Clamp(cur.Poc - p0.Poc, -128, 127);
                int tx = (16384 + Math.Abs(td / 2)) / td;
                int scale = Math.Clamp(((tb * tx) + 32) >> 6, -1024, 1023);
                if ((scale >> 2) < -64 || (scale >> 2) > 128)
                {
                    continue;
                }

                w[(i * 32) + j] = 64 - (scale >> 2);
            }
        }

        return w;
    }

    /// <summary>
    /// Ends the current picture: conceals what is missing, deblocks, marks references and stores the frame for output. A first
    /// field waits for its second field before the frame is stored.
    /// </summary>
    private void FinishPicture()
    {
        if (_cur is not { } frame || _first is not { } h)
        {
            _sei = new H264Sei();
            return;
        }

        H264Picture pic = _curPic!;
        CorruptDataException? pending = RunJobs();
        pending = Errors == DecodeErrorMode.Throw ? pending : null;
        H264Sps sps = h.Sps;
        H264MbState mb = _curMb!;
        int missing = 0;
        for (int i = 0; i < mb.Slice.Length; i++)
        {
            missing += mb.Slice[i] < 0 ? 1 : 0;
        }

        if (missing > 0)
        {
            _curDamaged = true;
            string problem = $"{missing} of {mb.Slice.Length} macroblocks of frame_num {h.FrameNum} are missing";
            Report?.Invoke("H264.CorruptFrame", problem + "; they are filled from the frame before.");
            Conceal(pic, mb, sps);

            // In Throw mode the picture is still finished, so the decoder stays whole, and the error comes after.
            pending ??= Errors == DecodeErrorMode.Throw ? H264Errors.Corrupt(problem + ".") : null;
        }

        if (_curMissingReference)
        {
            _curDamaged = true;
        }

        if (h.Mbaff)
        {
            // Direct prediction of later pictures reads which pairs are fields.
            bool[] pairs = frame.FieldPairs is { } kept && kept.Length == mb.FieldMb.Length ? kept : new bool[mb.FieldMb.Length];
            mb.FieldMb.CopyTo(pairs, 0);
            frame.FieldPairs = pairs;
        }

        Deblock(pic, mb, sps);
        frame.ExtendEdges();
        frame.DecodedFields |= h.FieldPic ? 1 << pic.Parity : 3;
        frame.Damaged = _curDamaged;
        if (!_curSecondField)
        {
            frame.Sei = _sei;
        }

        _sei = new H264Sei();

        bool mmco5 = false;
        if (h.NalRefIdc != 0)
        {
            mmco5 = MarkReferences(h, pic);
        }

        if (h.Idr || mmco5)
        {
            if (h.NoOutputOfPriorPics)
            {
                _dpb.Clear();
            }
            else
            {
                while (_dpb.TryFlush(out H264Picture prior, out _))
                {
                    Emit(prior);
                }
            }

            _dpb.Clear();
        }

        if (mmco5)
        {
            // tempPicOrderCnt (8.2.1): the picture's order count becomes 0.
            if (!h.FieldPic)
            {
                int temp = Math.Min(frame.TopPoc, frame.BottomPoc);
                frame.TopPoc -= temp;
                frame.BottomPoc -= temp;
            }
            else if (h.BottomField)
            {
                frame.BottomPoc = 0;
            }
            else
            {
                frame.TopPoc = 0;
            }
        }

        if (!_curSecondField)
        {
            bool output = _recoveryFrameNum < 0 || h.Idr || frame.FrameNum == _recoveryFrameNum;
            if (output)
            {
                _recoveryFrameNum = -1;
            }

            frame.Output = output;
        }

        _prevFrameNum = mmco5 ? 0 : h.FrameNum;
        _prevFrameNumOffset = mmco5 ? 0 : _curFrameNumOffset;
        if (h.NalRefIdc != 0)
        {
            _prevRefFrameNum = mmco5 ? 0 : h.FrameNum;
            _prevPocMsb = mmco5 ? 0 : _curPocMsb;
            _prevPocLsb = mmco5 ? (h.FieldPic && h.BottomField ? 0 : frame.TopPoc) : h.PocLsb;
            _prevRefBottomField = h.FieldPic && h.BottomField;
        }

        _cur = null;
        _curPic = null;
        _first = null;
        if (h.FieldPic && !_curSecondField)
        {
            // The first field: the frame waits for its second field.
            _pending = frame;
            _pendingHeader = h;
        }
        else
        {
            StoreFrame(frame);
        }

        if (pending is not null)
        {
            throw pending;
        }
    }

    /// <summary>Stores a finished frame for output, unless it is damaged in Skip mode or before a recovery point.</summary>
    private void StoreFrame(H264Picture frame)
    {
        Store(frame, frame.Output && !(frame.Damaged && Errors == DecodeErrorMode.Skip));
        _lastDecoded = frame;
    }

    /// <summary>Stores a decoded frame in the buffer and outputs what the bumping process lets go (C.4.4, C.4.5).</summary>
    private void Store(H264Picture pic, bool output)
    {
        _dpb.RemoveUnused();
        if (!pic.IsReference && !output)
        {
            return;
        }

        _dpb.Insert(pic, pic.Poc, output);

        // Storing first and bumping after keeps the output order right when the buffer is full and the new frame comes first.
        while (_dpb.Count > _dpb.Capacity && _dpb.Waiting > 0 && _dpb.TryFlush(out H264Picture bumped, out _))
        {
            Emit(bumped);
            _dpb.RemoveUnused();
        }

        while (_dpb.TryBumpAfterInsert(out H264Picture bumped, out _))
        {
            Emit(bumped);
        }

        _dpb.RemoveUnused();
    }

    private void Emit(H264Picture picture)
    {
        if (!picture.NonExisting)
        {
            Output.Add(picture);
        }
    }

    /// <summary>
    /// The frames that hold reference pictures for the current picture: those in the buffer, and the current frame while its
    /// second field is decoded, whose first field it may refer to.
    /// </summary>
    private IEnumerable<H264Picture> ReferenceFrames()
    {
        foreach (H264Picture p in _dpb.Pictures())
        {
            yield return p;
        }

        if (_curSecondField && _cur is { } frame)
        {
            yield return frame;
        }
    }

    /// <summary>
    /// The decoded reference picture marking process (8.2.5) for the current frame or field. Returns whether a
    /// memory_management_control_operation 5 came.
    /// </summary>
    private bool MarkReferences(H264SliceHeader h, H264Picture pic)
    {
        H264Sps sps = h.Sps;
        if (h.Idr)
        {
            foreach (H264Picture p in _dpb.Pictures())
            {
                p.IsShortTermReference = p.IsLongTermReference = false;
            }

            if (h.LongTermReference)
            {
                pic.IsLongTermReference = true;
                pic.LongTermFrameIdx = 0;
                _maxLongTermFrameIdx = 0;
            }
            else
            {
                pic.IsShortTermReference = true;
                _maxLongTermFrameIdx = -1;
            }

            return false;
        }

        bool mmco5 = false;
        bool currentLong = false;
        H264Picture frame = pic.Frame;
        if (h.AdaptiveRefPicMarking)
        {
            foreach (H264Mmco op in h.Mmcos)
            {
                switch (op.Op)
                {
                    case 1:
                        if (FindShortTerm(h, h.CurrPicNum - op.DifferenceOfPicNums) is { } unmarked)
                        {
                            unmarked.IsShortTermReference = false;
                        }

                        break;
                    case 2:
                        if (FindLongTerm(h, op.LongTermPicNum) is { } lt)
                        {
                            lt.IsLongTermReference = false;
                        }

                        break;
                    case 3:
                        if (FindShortTerm(h, h.CurrPicNum - op.DifferenceOfPicNums) is { } made)
                        {
                            UnmarkLongTermIdx(op.LongTermFrameIdx, made.Frame);
                            made.IsShortTermReference = false;
                            made.IsLongTermReference = true;
                            made.LongTermFrameIdx = op.LongTermFrameIdx;
                        }

                        break;
                    case 4:
                        _maxLongTermFrameIdx = op.MaxLongTermFrameIdxPlus1 - 1;
                        foreach (H264Picture p in ReferenceFrames())
                        {
                            if (p.HasLongTermField && p.LongTermFrameIdx > _maxLongTermFrameIdx)
                            {
                                p.IsLongTermReference = false;
                            }
                        }

                        break;
                    case 5:
                        foreach (H264Picture p in ReferenceFrames())
                        {
                            if (p != frame)
                            {
                                p.IsShortTermReference = p.IsLongTermReference = false;
                            }
                        }

                        _maxLongTermFrameIdx = -1;
                        mmco5 = true;
                        break;
                    case 6:
                        UnmarkLongTermIdx(op.LongTermFrameIdx, frame);
                        pic.IsShortTermReference = false;
                        pic.IsLongTermReference = true;
                        pic.LongTermFrameIdx = op.LongTermFrameIdx;
                        currentLong = true;
                        break;
                }
            }
        }
        else if (!_curSecondField || !frame.HasShortTermField)
        {
            // The sliding window, except for the second field of a pair whose first field is a short-term reference (8.2.5.3).
            SlidingWindow(sps, h.FrameNum);
        }

        if (!currentLong)
        {
            if (_curSecondField && frame.HasLongTermField)
            {
                // The second field joins its first field as a long-term reference (8.2.5.1).
                pic.IsLongTermReference = true;
            }
            else
            {
                pic.IsShortTermReference = true;
            }
        }

        // A stream that keeps more references than it may (8.2.5.4 overflow) loses its oldest short-term frames.
        while (CountReferences() > Math.Max(sps.MaxNumRefFrames, 1) && DropOldestShortTerm(h.FrameNum, sps, frame))
        {
        }

        frame.HadMmco5 |= mmco5;
        return mmco5;
    }

    /// <summary>
    /// Frees a LongTermFrameIdx for a field or frame of <paramref name="keep"/> (operations 3 and 6): a long-term frame or field
    /// pair holding it is unmarked, and so is a long-term field holding it unless it is <paramref name="keep"/>'s other field.
    /// </summary>
    private void UnmarkLongTermIdx(int idx, H264Picture keep)
    {
        foreach (H264Picture p in ReferenceFrames())
        {
            if (p.HasLongTermField && p.LongTermFrameIdx == idx && p != keep)
            {
                p.IsLongTermReference = false;
            }
        }
    }

    /// <summary>Counts the reference frames, the current one included: a frame counts once whichever of its fields are marked.</summary>
    private int CountReferences()
    {
        int n = 1;
        foreach (H264Picture p in _dpb.Pictures())
        {
            n += p.IsReference ? 1 : 0;
        }

        return n;
    }

    /// <summary>The sliding window (8.2.5.3): when the references fill max_num_ref_frames, the oldest short-term frame goes.</summary>
    private void SlidingWindow(H264Sps sps, int frameNum)
    {
        int shortCount = 0, longCount = 0;
        foreach (H264Picture p in _dpb.Pictures())
        {
            shortCount += p.HasShortTermField ? 1 : 0;
            longCount += p.HasLongTermField && !p.HasShortTermField ? 1 : 0;
        }

        if (shortCount > 0 && shortCount + longCount >= Math.Max(sps.MaxNumRefFrames, 1))
        {
            DropOldestShortTerm(frameNum, sps, null);
        }
    }

    private bool DropOldestShortTerm(int frameNum, H264Sps sps, H264Picture? except)
    {
        H264Picture? oldest = null;
        int oldestWrap = int.MaxValue;
        foreach (H264Picture p in _dpb.Pictures())
        {
            if (p.HasShortTermField && p != except)
            {
                int wrap = p.FrameNum > frameNum ? p.FrameNum - sps.MaxFrameNum : p.FrameNum;
                if (wrap < oldestWrap)
                {
                    oldestWrap = wrap;
                    oldest = p;
                }
            }
        }

        if (oldest is null)
        {
            return false;
        }

        // Both fields of the frame or field pair go.
        oldest.IsShortTermReference = false;
        return true;
    }

    /// <summary>
    /// The short-term frame (for a frame picture) or field (for a field picture) with PicNum <paramref name="picNum"/> (8.2.4.1):
    /// a field of the current parity has PicNum 2 FrameNumWrap + 1, one of the other parity 2 FrameNumWrap.
    /// </summary>
    private H264Picture? FindShortTerm(H264SliceHeader h, int picNum)
    {
        int maxFrameNum = h.Sps.MaxFrameNum;
        foreach (H264Picture p in ReferenceFrames())
        {
            int wrap = p.FrameNum > h.FrameNum ? p.FrameNum - maxFrameNum : p.FrameNum;
            if (!h.FieldPic)
            {
                if (p.IsShortTermReference && wrap == picNum)
                {
                    return p;
                }

                continue;
            }

            int parity = h.BottomField ? 1 : 0;
            for (int f = 0; f < 2; f++)
            {
                H264Picture field = p.Field(f);
                if (field.IsShortTermReference && (2 * wrap) + (f == parity ? 1 : 0) == picNum)
                {
                    return field;
                }
            }
        }

        return null;
    }

    /// <summary>The long-term frame or field with LongTermPicNum <paramref name="longTermPicNum"/>, as <see cref="FindShortTerm"/>.</summary>
    private H264Picture? FindLongTerm(H264SliceHeader h, int longTermPicNum)
    {
        foreach (H264Picture p in ReferenceFrames())
        {
            if (!h.FieldPic)
            {
                if (p.IsLongTermReference && p.LongTermFrameIdx == longTermPicNum)
                {
                    return p;
                }

                continue;
            }

            int parity = h.BottomField ? 1 : 0;
            for (int f = 0; f < 2; f++)
            {
                H264Picture field = p.Field(f);
                if (field.IsLongTermReference && (2 * p.LongTermFrameIdx) + (f == parity ? 1 : 0) == longTermPicNum)
                {
                    return field;
                }
            }
        }

        return null;
    }

    /// <summary>Fills the macroblocks no slice decoded from the last decoded frame, or gray before any.</summary>
    private void Conceal(H264Picture pic, H264MbState mb, H264Sps sps)
    {
        H264Picture? source = pic.Parity >= 0 ? _lastDecoded?.Field(pic.Parity) : _lastDecoded;
        int w4 = mb.W4;
        for (int addr = 0; addr < mb.Slice.Length; addr++)
        {
            if (mb.Slice[addr] >= 0)
            {
                continue;
            }

            int mbX = addr % sps.WidthInMbs, mbY = addr / sps.WidthInMbs;
            H264Picture? from = source is not null && source.Narrow == pic.Narrow && source.YPlane.Length == pic.YPlane.Length
                && source.CbPlane.Length == pic.CbPlane.Length ? source : null;
            if (pic.Narrow)
            {
                ConcealBlock<byte>(from, pic, sps, mbX, mbY);
            }
            else
            {
                ConcealBlock<ushort>(from, pic, sps, mbX, mbY);
            }

            pic.MbIntra[addr] = true;
            for (int y = 0; y < 4; y++)
            {
                int b = (((mbY * 4) + y) * w4) + (mbX * 4);
                for (int list = 0; list < 2; list++)
                {
                    pic.RefIdx[list].AsSpan(b, 4).Fill(-1);
                    pic.RefPicId[list].AsSpan(b, 4).Fill(-1);
                    pic.Mv[list].AsSpan(b, 4).Clear();
                }
            }
        }
    }

    private static void ConcealBlock<T>(H264Picture? source, H264Picture pic, H264Sps sps, int mbX, int mbY)
        where T : unmanaged
    {
        CopyBlock(source?.Y<T>(), pic.Y<T>(), pic.Origin + (mbY * 16 * pic.Stride) + (mbX * 16), pic.Stride, 16, 16, 1 << (sps.BitDepthLuma - 1));
        if (sps.ChromaFormatIdc != 0)
        {
            int cw = sps.MbWidthC, ch = sps.MbHeightC;
            int at = pic.ChromaOrigin + (mbY * ch * pic.ChromaStride) + (mbX * cw);
            int gray = 1 << (sps.BitDepthChroma - 1);
            CopyBlock(source?.Cb<T>(), pic.Cb<T>(), at, pic.ChromaStride, cw, ch, gray);
            CopyBlock(source?.Cr<T>(), pic.Cr<T>(), at, pic.ChromaStride, cw, ch, gray);
        }
    }

    private static void CopyBlock<T>(T[]? source, T[] target, int at, int stride, int w, int h, int gray)
        where T : unmanaged
    {
        for (int y = 0; y < h; y++)
        {
            Span<T> row = target.AsSpan(at + (y * stride), w);
            if (source is null)
            {
                for (int x = 0; x < w; x++)
                {
                    H264Samples.Set(target, at + (y * stride) + x, gray);
                }
            }
            else
            {
                source.AsSpan(at + (y * stride), w).CopyTo(row);
            }
        }
    }

    /// <summary>A slice kept for decoding at the end of its picture.</summary>
    private readonly record struct SliceJob(H264SliceHeader Header, byte[] Data, int Length, int SliceNum, H264Picture[] List0, H264Picture[] List1, int[] ImplicitWeights);
}
