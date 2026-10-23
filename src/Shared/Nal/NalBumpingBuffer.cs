namespace Egami.Nal;

/// <summary>A decoded picture the bumping buffer holds: the codec keeps its reference marking.</summary>
internal interface INalDpbPicture
{
    /// <summary>Gets whether the picture is still used for reference (short- or long-term), which keeps it in the buffer.</summary>
    bool IsReference { get; }
}

/// <summary>
/// The output side of the decoded picture buffer, which H.264 (C.4.5.3) and HEVC (H.265 C.5.2) share: decoded pictures wait for
/// output and leave in picture order count order through the "bumping" process. A picture leaves the buffer once it is neither
/// waiting for output nor used for reference. The codec sets the limits from its sequence parameter set, marks references, and
/// calls <see cref="TryBumpBeforeInsert"/>, <see cref="Insert"/> and <see cref="TryBumpAfterInsert"/> for each picture, and
/// <see cref="TryFlush"/> or <see cref="Clear"/> at a new coded video sequence and at the end of the stream.
/// </summary>
/// <typeparam name="T">The codec's picture.</typeparam>
internal sealed class NalBumpingBuffer<T>
    where T : class, INalDpbPicture
{
    private readonly List<Entry> _entries = [];

    /// <summary>Gets or sets the buffer's size in pictures (H.264 max_dec_frame_buffering, HEVC sps_max_dec_pic_buffering_minus1 + 1).</summary>
    internal int Capacity { get; set; } = 16;

    /// <summary>
    /// Gets or sets how many pictures may wait for output before the oldest leaves (H.264 max_num_reorder_frames, HEVC
    /// sps_max_num_reorder_pics), or -1 for no limit but the capacity.
    /// </summary>
    internal int MaxNumReorder { get; set; } = -1;

    /// <summary>Gets or sets HEVC's SpsMaxLatencyPictures, or 0 for no latency limit (H.264 has none).</summary>
    internal int MaxLatencyPictures { get; set; }

    /// <summary>Gets the number of pictures in the buffer.</summary>
    internal int Count => _entries.Count;

    /// <summary>Gets the number of pictures waiting for output.</summary>
    internal int Waiting
    {
        get
        {
            int waiting = 0;
            foreach (Entry entry in _entries)
            {
                waiting += entry.NeedsOutput ? 1 : 0;
            }

            return waiting;
        }
    }

    /// <summary>Removes the pictures that neither wait for output nor are used for reference.</summary>
    internal void RemoveUnused() => _entries.RemoveAll(static e => !e.NeedsOutput && !e.Picture.IsReference);

    /// <summary>
    /// Before the current picture is stored (H.265 C.5.2.2, H.264 C.4.5.3): first removes unused pictures, then bumps one picture
    /// while too many wait for output, one has waited too long, or the buffer is full. Call until it returns false.
    /// </summary>
    /// <param name="picture">The picture that leaves for output.</param>
    /// <param name="poc">Its picture order count.</param>
    /// <returns>Whether a picture was bumped. A full buffer of reference pictures with none waiting returns false; the codec
    /// decides what a stream that overfills its buffer means.</returns>
    internal bool TryBumpBeforeInsert(out T picture, out int poc)
    {
        RemoveUnused();
        return (TooManyWaiting() || _entries.Count >= Capacity) ? Bump(out picture, out poc) : None(out picture, out poc);
    }

    /// <summary>
    /// Stores the current picture (H.265 C.5.2.3): when it waits for output, every picture already waiting has waited one more
    /// picture.
    /// </summary>
    internal void Insert(T picture, int poc, bool needsOutput)
    {
        if (needsOutput)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].NeedsOutput)
                {
                    _entries[i] = _entries[i] with { Latency = _entries[i].Latency + 1 };
                }
            }
        }

        _entries.Add(new Entry(picture, poc, needsOutput, 0));
    }

    /// <summary>
    /// After the current picture is stored (H.265 C.5.2.3, "additional bumping"): bumps one picture while too many wait for output
    /// or one has waited too long. Call until it returns false.
    /// </summary>
    internal bool TryBumpAfterInsert(out T picture, out int poc) =>
        TooManyWaiting() ? Bump(out picture, out poc) : None(out picture, out poc);

    /// <summary>Outputs the waiting pictures one at a time in output order, for the end of a sequence or of the stream.</summary>
    internal bool TryFlush(out T picture, out int poc)
    {
        if (Bump(out picture, out poc))
        {
            return true;
        }

        RemoveUnused();
        return false;
    }

    /// <summary>Empties the buffer without output (HEVC NoOutputOfPriorPicsFlag, H.264 no_output_of_prior_pics_flag, a seek).</summary>
    internal void Clear() => _entries.Clear();

    /// <summary>Gets the pictures in the buffer, in the order they were stored.</summary>
    internal IEnumerable<T> Pictures()
    {
        foreach (Entry entry in _entries)
        {
            yield return entry.Picture;
        }
    }

    private static bool None(out T picture, out int poc)
    {
        picture = null!;
        poc = 0;
        return false;
    }

    private bool TooManyWaiting()
    {
        int waiting = 0;
        bool tooLate = false;
        foreach (Entry entry in _entries)
        {
            if (entry.NeedsOutput)
            {
                waiting++;
                tooLate |= MaxLatencyPictures > 0 && entry.Latency >= MaxLatencyPictures;
            }
        }

        return (MaxNumReorder >= 0 && waiting > MaxNumReorder) || tooLate;
    }

    /// <summary>The bumping process (H.265 C.5.2.4, H.264 C.4.5.3): the waiting picture first in output order leaves.</summary>
    private bool Bump(out T picture, out int poc)
    {
        int best = -1;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].NeedsOutput && (best < 0 || _entries[i].Poc < _entries[best].Poc))
            {
                best = i;
            }
        }

        if (best < 0)
        {
            return None(out picture, out poc);
        }

        Entry entry = _entries[best];
        picture = entry.Picture;
        poc = entry.Poc;
        if (entry.Picture.IsReference)
        {
            _entries[best] = entry with { NeedsOutput = false };
        }
        else
        {
            _entries.RemoveAt(best);
        }

        return true;
    }

    private readonly record struct Entry(T Picture, int Poc, bool NeedsOutput, int Latency);
}
