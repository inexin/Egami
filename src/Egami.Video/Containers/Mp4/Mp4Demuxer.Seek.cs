using Egami.Formats.Iso;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// Seeking by sync samples (V4.1 task 12): stss (or sdtp) in a flat file, the sample flags of truns in a fragmented one, whose
/// fragments are indexed once, the first time a seek needs them, by walking the moof boxes and skipping their media data.
/// </summary>
internal sealed partial class Mp4Demuxer
{
    private List<(uint Track, long Pts, long Moof, Dictionary<TrackState, long> DecodeTimes)>? _fragmentIndex;
    private long _firstFragment = -1;

    public void Seek(MediaTime time, int? trackNumber = null)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("The stream cannot seek.");
        }

        if (_tracks.Count == 0)
        {
            // Nothing to read, so nothing to move.
            return;
        }

        foreach (TrackState track in _tracks)
        {
            track.Vorbis?.Reset();
        }

        TrackState target = trackNumber is { } n
            ? _tracks.FirstOrDefault(t => t.Info.Number == n) ?? throw new ArgumentException($"There is no track {n}.", nameof(trackNumber))
            : _tracks.FirstOrDefault(t => t.Info.Kind == TrackKind.Video) ?? _tracks[0];

        _fragment.Clear();
        _fragmentAt = 0;
        _seekSync = null;
        foreach (TrackState track in _tracks)
        {
            track.SkipBefore = long.MinValue;
        }

        MediaTime inBase = time.Rescale(target.Info.TimeBase);
        long ticks = new MediaTime(inBase.Ticks, target.Info.TimeBase) > time ? inBase.Ticks - 1 : inBase.Ticks;
        Mp4SampleTable table = target.Track.Table;
        bool inFlat = table.Count > 0 && (!_hasMvex || _firstFragment < 0 || ticks < FlatEnd(target));
        if (inFlat)
        {
            if (target.IsVp8 && !target.SyncChecked)
            {
                VerifyVp8Sync(target);
            }

            long index = table.IndexAtDecodeTime(ticks - target.Shift);
            long sync = table.SyncAtOrBefore(index);
            while (sync > 0 && Pts(target, sync) > ticks)
            {
                long earlier = table.SyncAtOrBefore(sync - 1);
                if (earlier >= sync)
                {
                    break;
                }

                sync = earlier;
            }

            target.Cursor = table.At(sync);
            long offset = target.Cursor.AtEnd ? long.MaxValue : target.Cursor.Peek().Offset;
            foreach (TrackState track in _tracks.Where(t => t != target))
            {
                track.Cursor = track.Track.Table.At(track.Track.Table.IndexAtOffset(offset));
            }

            // Fragments follow the flat samples; start them again from the first.
            _nextBox = _firstFragment;
            _pendingBox = null;
            _endOfBoxes = false;
            foreach (TrackState track in _tracks)
            {
                track.NextDecodeTime = FlatEnd(track) - track.Shift;
            }

            return;
        }

        foreach (TrackState track in _tracks)
        {
            track.Cursor = track.Track.Table.At(track.Track.Table.Count);
        }

        IndexFragments();
        (uint Track, long Pts, long Moof, Dictionary<TrackState, long> DecodeTimes)? best = null, earliest = null;
        foreach (var entry in _fragmentIndex!)
        {
            if (entry.Track != target.Track.Id)
            {
                continue;
            }

            if (entry.Pts <= ticks && (best is null || entry.Pts >= best.Value.Pts))
            {
                best = entry;
            }

            if (earliest is null || entry.Pts < earliest.Value.Pts)
            {
                earliest = entry;
            }
        }

        best ??= earliest;
        _pendingBox = null;
        _endOfBoxes = false;
        if (best is not { } chosen)
        {
            _nextBox = _firstFragment;
            return;
        }

        _nextBox = chosen.Moof;
        foreach ((TrackState track, long decode) in chosen.DecodeTimes)
        {
            track.NextDecodeTime = decode;
        }

        _seekSync = (target, chosen.Pts);
    }

    /// <summary>
    /// Builds a VP8 track's sync samples from each frame's tag (bit 0 clear for a keyframe), the first time it is sought: some
    /// writers' stss lists other frames.
    /// </summary>
    private void VerifyVp8Sync(TrackState track)
    {
        track.SyncChecked = true;
        Mp4SampleTable table = track.Track.Table;
        long resume = _input.Position;
        List<uint> sync = [];
        Span<byte> tag = stackalloc byte[1];
        for (Mp4SampleTable.Cursor cursor = table.Start(); !cursor.AtEnd; cursor.Advance())
        {
            Mp4Sample sample = cursor.Peek();
            if (sample.Size > 0 && sample.Offset < _input.Length)
            {
                _input.Seek(sample.Offset);
                if (_input.Read(tag) == 1 && (tag[0] & 1) == 0)
                {
                    sync.Add((uint)(sample.Index + 1));
                }
            }
        }

        table.SyncSamples = [.. sync];
        _input.Seek(Math.Min(resume, _input.Length));
    }

    private static long Pts(TrackState track, long index)
    {
        Mp4SampleTable.Cursor cursor = track.Track.Table.At(index);
        if (cursor.AtEnd)
        {
            return long.MaxValue;
        }

        Mp4Sample sample = cursor.Peek();
        return sample.DecodeTime + sample.CompositionOffset + track.Shift;
    }

    /// <summary>Gets the presentation time just after a track's flat samples.</summary>
    private static long FlatEnd(TrackState track)
    {
        Mp4SampleTable table = track.Track.Table;
        return table.Count == 0 ? track.Shift : table.TotalDuration() + track.Shift;
    }

    /// <summary>Indexes the sync samples of every fragment once, by walking the moof boxes.</summary>
    private void IndexFragments()
    {
        if (_fragmentIndex is not null)
        {
            return;
        }

        _fragmentIndex = [];
        if (_firstFragment < 0)
        {
            return;
        }

        long resume = _input.Position;
        Dictionary<TrackState, long> decodeTimes = [];
        foreach (TrackState track in _tracks)
        {
            decodeTimes[track] = FlatEnd(track) - track.Shift;
        }

        long position = _firstFragment;
        List<(TrackState Track, Mp4Sample Sample)> samples = [];
        while (position >= 0 && position < _input.Length)
        {
            _input.Seek(position);
            if (!NextBoxHeader(out IsoBox box))
            {
                break;
            }

            if (box.Type.ToString() == "moof")
            {
                Dictionary<TrackState, long> start = new(decodeTimes);
                samples.Clear();
                ParseMoof(ReadContent(box, _context.Limits.MaxHeaderBytes, "MaxHeaderBytes"), box, samples, decodeTimes);
                foreach ((TrackState track, Mp4Sample sample) in samples)
                {
                    if (sample.IsSync)
                    {
                        _fragmentIndex.Add((track.Track.Id, sample.DecodeTime + sample.CompositionOffset + track.Shift, box.Start, start));
                    }
                }

                if (_fragmentIndex.Count > _context.Limits.MaxIndexEntries)
                {
                    throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, _fragmentIndex.Count);
                }
            }

            if (box.End == long.MaxValue)
            {
                break;
            }

            position = box.End;
        }

        _input.Seek(Math.Min(resume, _input.Length));
    }
}
