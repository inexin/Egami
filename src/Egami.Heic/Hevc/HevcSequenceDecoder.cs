namespace Egami.Heic.Hevc;

/// <summary>
/// Decodes a sequence of HEVC pictures from their NAL units, in decoding order, and gives them back in output order: picture
/// boundaries (7.4.2.4.4), the decoded picture buffer with its reference picture sets (<see cref="HevcDpb"/>), and each picture's
/// slice segments, then its in-loop filters.
/// </summary>
internal sealed class HevcSequenceDecoder(SafetyLimits limits, int parallelism = 1)
{
    private readonly HevcParameterSetStore _store = new();
    private readonly HevcDpb _dpb = new();
    private readonly List<HevcSegment> _segments = [];
    private HevcPicture? _picture;
    private HevcDpbPicture? _current;
    private HevcSliceHeader? _slice;
    private int _sliceAddrRs;
    private bool _picOutput;
    private bool _skipping;
    private HevcSliceRefs? _refs;

    // The pictures handed out that may come back once released and out of the buffer, and the pictures ready to be used again.
    private readonly List<HevcDpbPicture> _live = [];
    private readonly List<HevcPicture> _pool = [];

    /// <summary>
    /// Gets or sets whether samples are decoded. Without, only headers, order counts, reference marking and output order are
    /// worked out, as a test of those against a reference decoder's trace.
    /// </summary>
    internal bool DecodeSamples { get; set; } = true;

    /// <summary>Gets or sets what runs when a picture starts, after its reference picture set; for tests.</summary>
    internal Action<HevcDpbPicture>? PictureStarted { get; set; }

    /// <summary>Gets or sets whether damaged slice data leaves the picture as far as it decoded instead of throwing.</summary>
    internal bool ConcealErrors { get; set; }

    /// <summary>Gets the decoded picture buffer.</summary>
    internal HevcDpb Dpb => _dpb;

    /// <summary>Takes the next NAL unit; pictures that leave the decoded picture buffer go to <paramref name="output"/>.</summary>
    internal void Send(HevcNal nal, List<(HevcDpbPicture Picture, int Poc)> output, CancellationToken cancellationToken = default)
    {
        if (!nal.IsSlice)
        {
            if (nal.Type is HevcNal.Vps or HevcNal.Sps or HevcNal.Pps or HevcNal.Aud or HevcNal.PrefixSei or HevcNal.Eos or HevcNal.Eob)
            {
                // 7.4.2.4.4: these start a new access unit, so the current picture is complete.
                FinishPicture(output, cancellationToken);
            }

            if (nal.Type is HevcNal.Eos or HevcNal.Eob)
            {
                _dpb.EndOfSequence();
            }

            _store.Parse(nal, out HevcPictureHash? hash);
            if (hash is not null && _current is not null && nal.Type == HevcNal.SuffixSei)
            {
                // The decoded picture hash of the picture whose slices came before (D.3.19).
                _current.Hash = hash;
            }

            return;
        }

        if (nal.LayerId != 0)
        {
            // A layer above the base (Dolby Vision's enhancement layer, MV-HEVC): the base layer is decoded alone.
            return;
        }

        HevcSliceHeader? header = _store.Parse(nal, out _);
        if (header is null)
        {
            return;
        }

        if (header.FirstSliceSegmentInPic)
        {
            FinishPicture(output, cancellationToken);
            _skipping = _dpb.Skips(nal);
            if (_skipping)
            {
                return;
            }

            (HevcSps sps, HevcPps pps) = _store.Active(header.PpsId);
            sps.CheckSupported();
            limits.CheckFrameSize(sps.Width, sps.Height);
            _picture = NewPicture(sps, pps);
            _current = _dpb.Start(nal, header, _picture, output);
            _live.Add(_current);
            PictureStarted?.Invoke(_current);
            _picOutput = header.PicOutput;
        }
        else if (_skipping)
        {
            return;
        }
        else if (_picture is null)
        {
            throw HevcErrors.Corrupt("The first slice segment of a picture is missing.");
        }

        if (!header.Dependent)
        {
            _slice = header;
            _sliceAddrRs = header.SegmentAddress;
            _dpb.BuildLists(header);
            _refs = header.SliceType == HevcSliceHeader.I ? null
                : new HevcSliceRefs(_dpb.List0, _dpb.List1, _dpb.LongTerm0, _dpb.LongTerm1, _current!.Poc);
        }

        int firstTs = _picture.Layout.RsToTs[header.SegmentAddress];
        if (_segments.Count > 0)
        {
            if (firstTs <= _segments[^1].FirstTs)
            {
                throw HevcErrors.Corrupt("The slice segments of the picture are out of order.");
            }

            _segments[^1].EndTs = firstTs;
        }

        _segments.Add(new HevcSegment(nal, header, _slice ?? header, _sliceAddrRs, HevcSliceDecoder.SubstreamStarts(nal, header), firstTs) { Refs = _refs });
    }

    /// <summary>
    /// Notes that whoever took a picture from the output is done with its samples, so the picture can be used again for a later
    /// one once it has left the buffer. Pictures never released are never used again.
    /// </summary>
    internal static void Release(HevcDpbPicture picture) => picture.Released = true;

    /// <summary>A picture to decode into: a recycled one of the same parameter sets, else a new one.</summary>
    private HevcPicture NewPicture(HevcSps sps, HevcPps pps)
    {
        foreach (HevcDpbPicture p in _live)
        {
            p.InBuffer = false;
        }

        foreach (HevcDpbPicture p in _dpb.Pictures)
        {
            p.InBuffer = true;
        }

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            HevcDpbPicture p = _live[i];
            if (!p.InBuffer && (p.Released || !p.PicOutput))
            {
                _pool.Add(p.Picture);
                _live.RemoveAt(i);
            }
        }

        for (int i = 0; i < _pool.Count; i++)
        {
            HevcPicture recycled = _pool[i];
            if (recycled.Fits(sps, pps))
            {
                _pool.RemoveAt(i);
                recycled.Clear(sps, pps);
                return recycled;
            }
        }

        if (_pool.Count > 16)
        {
            // Pictures of parameter sets no longer used.
            _pool.Clear();
        }

        return new HevcPicture(sps, pps);
    }

    /// <summary>Completes the last picture and outputs every picture still waiting, at the end of the stream.</summary>
    internal void Flush(List<(HevcDpbPicture Picture, int Poc)> output, CancellationToken cancellationToken = default)
    {
        FinishPicture(output, cancellationToken);
        _dpb.Flush(output);
    }

    /// <summary>Drops the picture being decoded and empties the buffer, for a seek; decoding starts again at an IRAP picture.</summary>
    internal void Reset()
    {
        _segments.Clear();
        _picture = null;
        _current = null;
        _dpb.Clear();
        _live.Clear();
    }

    private void FinishPicture(List<(HevcDpbPicture Picture, int Poc)> output, CancellationToken cancellationToken)
    {
        if (_picture is null || _current is null)
        {
            return;
        }

        HevcPicture picture = _picture;
        HevcDpbPicture current = _current;
        _picture = null;
        _current = null;
        try
        {
            if (DecodeSamples)
            {
                _segments[^1].EndTs = picture.Layout.TsToRs.Length;
                try
                {
                    new HevcChainSet(HevcDecoder.Chains(picture, _segments), parallelism, cancellationToken).Decode(picture);
                }
                catch (CorruptDataException) when (ConcealErrors)
                {
                    // The coding tree blocks before the damage stay; the rest keep the samples the picture was made with.
                }

                HevcLoopFilter.Apply(picture, parallelism, cancellationToken);
                if (picture.ColMotion is null)
                {
                    picture.ColMotion = new HevcColMotion(picture);
                }
                else
                {
                    picture.ColMotion.Update(picture);
                }
            }
        }
        finally
        {
            _segments.Clear();
        }

        _dpb.Finish(current, _picOutput, output);
    }
}
