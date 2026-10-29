using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>A decoded picture in the decoded picture buffer, with its picture order count and reference marking.</summary>
internal sealed class HevcDpbPicture(HevcPicture picture, int poc, int id) : INalDpbPicture
{
    internal const int Unused = 0;
    internal const int ShortTerm = 1;
    internal const int LongTerm = 2;

    /// <summary>Gets the decoded picture.</summary>
    internal HevcPicture Picture { get; } = picture;

    /// <summary>Gets PicOrderCntVal.</summary>
    internal int Poc { get; } = poc;

    /// <summary>Gets a number unique to the picture in the stream, for telling references apart.</summary>
    internal int Id { get; } = id;

    /// <summary>Gets or sets the marking: <see cref="Unused"/>, <see cref="ShortTerm"/> or <see cref="LongTerm"/>.</summary>
    internal int Marking { get; set; } = ShortTerm;

    /// <summary>Gets or sets whether the picture waits for output (pic_output_flag), set when it is stored.</summary>
    internal bool PicOutput { get; set; }

    /// <summary>Gets or sets whether whoever took the picture from the output is done with its samples.</summary>
    internal bool Released { get; set; }

    /// <summary>Gets or sets whether the picture was in the buffer at the last look; for recycling.</summary>
    internal bool InBuffer { get; set; }

    /// <summary>Gets or sets the type of the picture's NAL units.</summary>
    internal HevcPictureKind Kind { get; set; }

    /// <summary>Gets or sets the picture's TemporalId.</summary>
    internal int TemporalId { get; set; }

    /// <summary>Gets or sets the picture's decoded picture hash SEI message, or null.</summary>
    internal HevcPictureHash? Hash { get; set; }

    /// <summary>Gets or sets the number of the coded video sequence the picture is in, from 0.</summary>
    internal int Sequence { get; set; }

    /// <summary>Gets or sets whether the picture was generated for a missing reference (8.3.3) rather than decoded.</summary>
    internal bool Generated { get; set; }

    /// <inheritdoc/>
    public bool IsReference => Marking != Unused;
}

/// <summary>
/// The decoded picture buffer of an HEVC stream: picture order counts (H.265 8.3.1), reference picture sets and marking (8.3.2),
/// pictures generated for missing references (8.3.3), reference picture lists (8.3.4) and output in order through the shared
/// bumping buffer (C.5.2).
/// </summary>
internal sealed class HevcDpb
{
    private readonly NalBumpingBuffer<HevcDpbPicture> _buffer = new();
    private readonly List<HevcDpbPicture> _stCurrBefore = [];
    private readonly List<HevcDpbPicture> _stCurrAfter = [];
    private readonly List<HevcDpbPicture> _ltCurr = [];
    private readonly HashSet<HevcDpbPicture> _keep = [];
    private bool _first = true;
    private bool _afterEndOfSequence;
    private int _prevTid0Poc;
    private int _nextId;
    private bool _skipRasl;
    private int _sequence = -1;

    /// <summary>Gets the reference picture list 0 of the last slice <see cref="BuildLists"/> worked out.</summary>
    internal HevcDpbPicture[] List0 { get; private set; } = [];

    /// <summary>Gets the reference picture list 1 of the last slice <see cref="BuildLists"/> worked out.</summary>
    internal HevcDpbPicture[] List1 { get; private set; } = [];

    /// <summary>Gets, per entry of <see cref="List0"/>, whether it is a long-term reference for the current picture.</summary>
    internal bool[] LongTerm0 { get; private set; } = [];

    /// <summary>Gets, per entry of <see cref="List1"/>, whether it is a long-term reference for the current picture.</summary>
    internal bool[] LongTerm1 { get; private set; } = [];

    /// <summary>Gets the pictures of the buffer.</summary>
    internal IEnumerable<HevcDpbPicture> Pictures => _buffer.Pictures();

    /// <summary>Notes an end of sequence NAL unit: the next picture starts a new coded video sequence.</summary>
    internal void EndOfSequence() => _afterEndOfSequence = true;

    /// <summary>
    /// Whether a picture is to be left out: a RASL picture of an IRAP picture that starts decoding (8.1.3), whose references were
    /// never decoded.
    /// </summary>
    internal bool Skips(HevcNal nal)
    {
        if (nal.IsIrap)
        {
            _skipRasl = _first || _afterEndOfSequence || nal.Type is >= HevcNal.BlaWLp and <= HevcNal.IdrNLp;
            return false;
        }

        return _skipRasl && nal.Type is HevcNal.RaslN or HevcNal.RaslR;
    }

    /// <summary>
    /// Starts a picture from its first slice segment header: its order count, the marking by its reference picture set, the
    /// pictures generated for missing references, and the pictures that leave the buffer for output before it is decoded.
    /// </summary>
    /// <param name="nal">The picture's first slice segment NAL unit.</param>
    /// <param name="h">Its header.</param>
    /// <param name="picture">The picture to decode into.</param>
    /// <param name="output">Receives the pictures output, with their order counts, in output order.</param>
    /// <returns>The picture's buffer entry, to pass to <see cref="Finish"/>.</returns>
    internal HevcDpbPicture Start(HevcNal nal, HevcSliceHeader h, HevcPicture picture, List<(HevcDpbPicture Picture, int Poc)> output)
    {
        HevcSps sps = picture.Sps;
        bool noRaslOutput = nal.IsIrap && (_first || _afterEndOfSequence || nal.Type is >= HevcNal.BlaWLp and <= HevcNal.IdrNLp);
        int poc = PictureOrderCount(nal, h, sps, noRaslOutput);
        if (nal.TemporalIdPlus1 == 1 && !IsRaslRadlOrSubLayerNonReference(nal.Type))
        {
            _prevTid0Poc = poc;
        }

        ApplyReferencePictureSet(nal, h, picture, poc, noRaslOutput);
        _buffer.Capacity = sps.MaxDecPicBuffering;
        _buffer.MaxNumReorder = sps.MaxNumReorder;
        _buffer.MaxLatencyPictures = sps.MaxLatencyPictures;
        if (noRaslOutput && !_first)
        {
            // C.5.2.2: an IRAP picture that starts a sequence outputs or drops what waits (a CRA infers NoOutputOfPriorPicsFlag 1).
            if (nal.Type == HevcNal.CraNut || h.NoOutputOfPriorPics)
            {
                _buffer.Clear();
            }
            else
            {
                while (_buffer.TryFlush(out HevcDpbPicture leaving, out int leavingPoc))
                {
                    output.Add((leaving, leavingPoc));
                }

                _buffer.Clear();
            }
        }
        else
        {
            while (_buffer.TryBumpBeforeInsert(out HevcDpbPicture leaving, out int leavingPoc))
            {
                output.Add((leaving, leavingPoc));
            }
        }

        _first = false;
        _afterEndOfSequence = false;
        _sequence += noRaslOutput ? 1 : 0;
        HevcPictureKind kind = nal.Type switch
        {
            HevcNal.IdrWRadl or HevcNal.IdrNLp => HevcPictureKind.Idr,
            HevcNal.CraNut => HevcPictureKind.Cra,
            >= HevcNal.BlaWLp and <= HevcNal.BlaNLp => HevcPictureKind.Bla,
            6 or 7 => HevcPictureKind.DecodableLeading,
            HevcNal.RaslN or HevcNal.RaslR => HevcPictureKind.SkippedLeading,
            _ => HevcPictureKind.Trailing,
        };
        return new HevcDpbPicture(picture, poc, _nextId++) { Sequence = _sequence, Kind = kind, TemporalId = nal.TemporalIdPlus1 - 1 };
    }

    /// <summary>Stores a decoded picture (C.5.2.3), marked short-term, and outputs what must leave after it.</summary>
    internal void Finish(HevcDpbPicture current, bool picOutput, List<(HevcDpbPicture Picture, int Poc)> output)
    {
        current.Marking = HevcDpbPicture.ShortTerm;
        current.PicOutput = picOutput;
        _buffer.Insert(current, current.Poc, picOutput);
        while (_buffer.TryBumpAfterInsert(out HevcDpbPicture leaving, out int poc))
        {
            output.Add((leaving, poc));
        }
    }

    /// <summary>Outputs every waiting picture, in output order, at the end of the stream.</summary>
    internal void Flush(List<(HevcDpbPicture Picture, int Poc)> output)
    {
        while (_buffer.TryFlush(out HevcDpbPicture leaving, out int poc))
        {
            output.Add((leaving, poc));
        }
    }

    /// <summary>Empties the buffer without output, for a seek; the next picture must be an IRAP picture.</summary>
    internal void Clear()
    {
        _buffer.Clear();
        _first = true;
        _afterEndOfSequence = false;
    }

    /// <summary>Builds the reference picture lists of a P or B slice (8.3.4) from the current picture's reference picture set.</summary>
    internal void BuildLists(HevcSliceHeader h)
    {
        if (h.SliceType == HevcSliceHeader.I)
        {
            List0 = List1 = [];
            LongTerm0 = LongTerm1 = [];
            return;
        }

        int total = _stCurrBefore.Count + _stCurrAfter.Count + _ltCurr.Count;
        if (total == 0)
        {
            throw HevcErrors.Corrupt("A P or B slice has no reference pictures.");
        }

        (List0, LongTerm0) = BuildList(h.NumRefIdxL0, h.ListEntryL0, _stCurrBefore, _stCurrAfter, total);
        if (h.SliceType == HevcSliceHeader.B)
        {
            (List1, LongTerm1) = BuildList(h.NumRefIdxL1, h.ListEntryL1, _stCurrAfter, _stCurrBefore, total);
        }
        else
        {
            List1 = [];
            LongTerm1 = [];
        }
    }

    private (HevcDpbPicture[] List, bool[] LongTerm) BuildList(int count, int[]? entries, List<HevcDpbPicture> first, List<HevcDpbPicture> second, int total)
    {
        // RefPicListTemp (8-8, 8-10): the first set, the second, then the long-term ones, repeated to NumRpsCurrTempList.
        int tempCount = Math.Max(count, total);
        HevcDpbPicture[] temp = new HevcDpbPicture[tempCount];
        bool[] tempLong = new bool[tempCount];
        int r = 0;
        while (r < tempCount)
        {
            for (int i = 0; i < first.Count && r < tempCount; i++, r++)
            {
                temp[r] = first[i];
            }

            for (int i = 0; i < second.Count && r < tempCount; i++, r++)
            {
                temp[r] = second[i];
            }

            for (int i = 0; i < _ltCurr.Count && r < tempCount; i++, r++)
            {
                temp[r] = _ltCurr[i];
                tempLong[r] = true;
            }
        }

        HevcDpbPicture[] list = new HevcDpbPicture[count];
        bool[] longTerm = new bool[count];
        for (int i = 0; i < count; i++)
        {
            int index = entries is null ? i : entries[i];
            if (index >= tempCount)
            {
                throw HevcErrors.Corrupt($"list_entry is {index}, past the {tempCount} pictures of the list.");
            }

            list[i] = temp[index];
            longTerm[i] = tempLong[index];
        }

        return (list, longTerm);
    }

    /// <summary>PicOrderCntVal (8.3.1).</summary>
    private int PictureOrderCount(HevcNal nal, HevcSliceHeader h, HevcSps sps, bool noRaslOutput)
    {
        int maxLsb = 1 << sps.Log2MaxPocLsb;
        int lsb = h.PocLsb, msb;
        if (nal.IsIrap && noRaslOutput)
        {
            msb = 0;
        }
        else
        {
            int prevLsb = _prevTid0Poc & (maxLsb - 1), prevMsb = _prevTid0Poc - prevLsb;
            msb = lsb < prevLsb && prevLsb - lsb >= maxLsb / 2 ? prevMsb + maxLsb
                : lsb > prevLsb && lsb - prevLsb > maxLsb / 2 ? prevMsb - maxLsb
                : prevMsb;
        }

        return msb + lsb;
    }

    // RASL_N, RASL_R, RADL_N, RADL_R and the sub-layer non-reference types TRAIL_N to RSV_VCL_N14 (even types up to 14).
    private static bool IsRaslRadlOrSubLayerNonReference(int type) => type is >= 6 and <= 9 || (type <= 14 && (type & 1) == 0);

    /// <summary>
    /// The reference picture set (8.3.2): finds each picture the current one lists, generates missing ones (8.3.3), marks the
    /// long-term ones and marks every other picture unused for reference.
    /// </summary>
    private void ApplyReferencePictureSet(HevcNal nal, HevcSliceHeader h, HevcPicture current, int poc, bool noRaslOutput)
    {
        HevcSps sps = current.Sps;
        _stCurrBefore.Clear();
        _stCurrAfter.Clear();
        _ltCurr.Clear();
        _keep.Clear();
        if (nal.IsIrap && noRaslOutput)
        {
            foreach (HevcDpbPicture p in _buffer.Pictures())
            {
                p.Marking = HevcDpbPicture.Unused;
            }
        }

        if (nal.IsIdr)
        {
            _buffer.RemoveUnused();
            return;
        }

        int maxLsb = 1 << sps.Log2MaxPocLsb;
        List<HevcDpbPicture> ltFoll = [];

        // Long-term pictures first: any reference picture may become one (8-5).
        foreach ((int pocLsb, bool used, int msbCycle) in h.LongTerm)
        {
            int ltPoc = msbCycle < 0 ? pocLsb : pocLsb + poc - (msbCycle * maxLsb) - (poc & (maxLsb - 1));
            HevcDpbPicture? found = null;
            foreach (HevcDpbPicture p in _buffer.Pictures())
            {
                if (p.IsReference && (msbCycle < 0 ? (p.Poc & (maxLsb - 1)) == ltPoc : p.Poc == ltPoc))
                {
                    found = p;
                    break;
                }
            }

            if (found is null && used)
            {
                found = Generate(current, ltPoc, HevcDpbPicture.LongTerm);
            }

            if (found is not null)
            {
                (used ? _ltCurr : ltFoll).Add(found);
                _keep.Add(found);
            }
        }

        // Short-term pictures (8-6): only short-term reference pictures that are not now long-term.
        HevcShortTermRps rps = h.ShortTermRps;
        for (int i = 0; i < rps.Deltas.Length; i++)
        {
            int stPoc = poc + rps.Deltas[i];
            HevcDpbPicture? found = null;
            foreach (HevcDpbPicture p in _buffer.Pictures())
            {
                if (p.Marking == HevcDpbPicture.ShortTerm && p.Poc == stPoc && !_keep.Contains(p))
                {
                    found = p;
                    break;
                }
            }

            if (found is null && rps.Used[i])
            {
                found = Generate(current, stPoc, HevcDpbPicture.ShortTerm);
            }

            if (found is null)
            {
                continue;
            }

            _keep.Add(found);
            if (rps.Used[i])
            {
                (i < rps.NumNegative ? _stCurrBefore : _stCurrAfter).Add(found);
            }
        }

        foreach (HevcDpbPicture p in _ltCurr)
        {
            p.Marking = HevcDpbPicture.LongTerm;
        }

        foreach (HevcDpbPicture p in ltFoll)
        {
            p.Marking = HevcDpbPicture.LongTerm;
        }

        foreach (HevcDpbPicture p in _buffer.Pictures())
        {
            if (!_keep.Contains(p))
            {
                p.Marking = HevcDpbPicture.Unused;
            }
        }

        _buffer.RemoveUnused();
    }

    /// <summary>
    /// A picture for a missing reference (8.3.3.2): every sample the middle value, intra, not output. A stream that starts at a
    /// CRA or BLA picture has these; elsewhere they stand in for lost pictures.
    /// </summary>
    private HevcDpbPicture Generate(HevcPicture current, int poc, int marking)
    {
        HevcSps sps = current.Sps;
        HevcPicture picture = new(sps, current.Pps);
        for (int c = 0; c < picture.Planes.Length; c++)
        {
            Array.Fill(picture.Planes[c], (ushort)(1 << ((c == 0 ? sps.BitDepthLuma : sps.BitDepthChroma) - 1)));
        }

        HevcDpbPicture generated = new(picture, poc, _nextId++) { Marking = marking, Generated = true };
        _buffer.Insert(generated, poc, needsOutput: false);
        return generated;
    }
}
