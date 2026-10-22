using Egami.Formats.Iso;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// Fragmented MP4 (ISO/IEC 14496-12 8.8): moof boxes with a traf a track, each a tfhd (defaults and base offset), a tfdt (decode
/// time) and truns of samples, read one fragment at a time as packets are asked for (V4.1 Design Q3).
/// </summary>
internal sealed partial class Mp4Demuxer
{
    private const uint BaseDataOffset = 0x1, DescriptionIndex = 0x2, DefaultDuration = 0x8, DefaultSize = 0x10, DefaultFlags = 0x20, DefaultBaseIsMoof = 0x20000;
    private const uint DataOffset = 0x1, FirstSampleFlags = 0x4, SampleDuration = 0x100, SampleSize = 0x200, SampleFlags = 0x400, SampleCompositionOffset = 0x800;
    private const uint NonSync = 0x10000;

    private (TrackState Track, long Pts)? _seekSync;

    /// <summary>Reads boxes up to the next moof with samples and queues its samples in file order; returns <see langword="false"/> at the end.</summary>
    private bool NextFragment()
    {
        _fragment.Clear();
        _fragmentAt = 0;
        while (true)
        {
            IsoBox box;
            if (_pendingBox is { } pending)
            {
                box = pending;
                _pendingBox = null;
            }
            else
            {
                if (_nextBox < 0 || _endOfBoxes)
                {
                    return false;
                }

                if (_input.CanSeek && _nextBox >= _input.Length)
                {
                    _endOfBoxes = true;
                    return false;
                }

                MoveTo(_nextBox);
                if (!NextBoxHeader(out box))
                {
                    _endOfBoxes = true;
                    return false;
                }
            }

            _nextBox = box.End;
            _endOfBoxes |= box.End == long.MaxValue;
            if (box.Type.ToString() != "moof")
            {
                continue;
            }

            byte[] content = ReadContent(box, _context.Limits.MaxHeaderBytes, "MaxHeaderBytes");
            ParseMoof(content, box, _fragment, decodeTimes: null);
            _fragment.Sort((a, b) => a.Sample.Offset.CompareTo(b.Sample.Offset));

            // Read the next box's header now: on a stream that cannot seek, the mdat after a moof is passed while reading samples.
            if (!_endOfBoxes && (!_input.CanSeek || box.End < _input.Length) && NextBoxHeader(out IsoBox after))
            {
                if (after.Type.ToString() == "mdat")
                {
                    _nextBox = after.End;
                    _endOfBoxes |= after.End == long.MaxValue;
                }
                else
                {
                    _pendingBox = after;
                }
            }

            if (_seekSync is { } sync)
            {
                _seekSync = null;
                (TrackState Track, Mp4Sample Sample)? key = _fragment.FirstOrDefault(s => s.Track == sync.Track && s.Sample.IsSync
                    && s.Sample.DecodeTime + s.Sample.CompositionOffset + s.Track.Shift == sync.Pts);
                long from = key?.Sample.Offset ?? long.MinValue;
                foreach (TrackState track in _tracks)
                {
                    track.SkipBefore = from;
                }
            }

            if (_fragment.Count > 0)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Parses a moof's trafs into samples. With <paramref name="decodeTimes"/> it only indexes (seek scans), keeping each track's
    /// running decode time there instead of in the track.
    /// </summary>
    private void ParseMoof(byte[] content, IsoBox moof, List<(TrackState Track, Mp4Sample Sample)> samples, Dictionary<TrackState, long>? decodeTimes)
    {
        IsoBoxReader reader = new(content, _input.FormatName, moof.ContentStart);
        long previousEnd = moof.Start;
        bool first = true;
        long total = 0;
        while (reader.Next(out IsoBox traf))
        {
            if (traf.Type.ToString() != "traf")
            {
                continue;
            }

            IsoBoxReader children = reader.Children(traf);
            TrackState? track = null;
            uint flags = 0, description = 1, duration = 0, size = 0, sampleFlags = 0;
            long baseOffset = first ? moof.Start : previousEnd;
            long? tfdt = null;
            List<IsoBox> truns = [];
            while (children.Next(out IsoBox box))
            {
                BoxFields f = new(children.Content(box), box);
                switch (box.Type.ToString())
                {
                    case "tfhd":
                        (_, flags) = f.FullBox();
                        uint id = f.U32();
                        track = _byId.GetValueOrDefault(id);
                        if (track is null)
                        {
                            Warn("mp4-fragment-track", $"A fragment holds samples of track {id}, which moov does not declare; they were skipped.");
                            break;
                        }

                        (description, duration, size, sampleFlags) = track.Track.FragmentDefaults;
                        if ((flags & BaseDataOffset) != 0)
                        {
                            baseOffset = (long)Math.Min(f.U64(), long.MaxValue);
                        }
                        else if ((flags & DefaultBaseIsMoof) != 0)
                        {
                            baseOffset = moof.Start;
                        }

                        description = (flags & DescriptionIndex) != 0 ? f.U32() : description;
                        duration = (flags & DefaultDuration) != 0 ? f.U32() : duration;
                        size = (flags & DefaultSize) != 0 ? f.U32() : size;
                        sampleFlags = (flags & DefaultFlags) != 0 ? f.U32() : sampleFlags;
                        break;
                    case "tfdt":
                        (int version, _) = f.FullBox();
                        tfdt = f.UVar(version == 1);
                        break;
                    case "trun":
                        truns.Add(box);
                        break;
                }
            }

            first = false;
            if (track is null)
            {
                continue;
            }

            long decode = tfdt ?? (decodeTimes is not null ? decodeTimes.GetValueOrDefault(track, track.NextDecodeTime) : track.NextDecodeTime);
            long dataAt = baseOffset;
            foreach (IsoBox trun in truns)
            {
                BoxFields f = new(children.Content(trun), trun);
                (_, uint runFlags) = f.FullBox();
                uint count = f.U32();
                int entrySize = 4 * (int)(((runFlags & SampleDuration) != 0 ? 1 : 0) + ((runFlags & SampleSize) != 0 ? 1 : 0) + ((runFlags & SampleFlags) != 0 ? 1 : 0) + ((runFlags & SampleCompositionOffset) != 0 ? 1 : 0));
                if ((runFlags & DataOffset) != 0)
                {
                    dataAt = baseOffset + f.I32();
                }

                uint firstFlags = (runFlags & FirstSampleFlags) != 0 ? f.U32() : sampleFlags;
                f.Need(count, Math.Max(entrySize, 0));
                total += count;
                if (total > _context.Limits.MaxIndexEntries || (entrySize == 0 && count > _context.Limits.MaxIndexEntries))
                {
                    throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, total);
                }

                for (uint i = 0; i < count; i++)
                {
                    long sampleDuration = (runFlags & SampleDuration) != 0 ? f.U32() : duration;
                    int sampleSize = (int)Math.Min((runFlags & SampleSize) != 0 ? f.U32() : size, int.MaxValue);
                    uint thisFlags = (runFlags & SampleFlags) != 0 ? f.U32() : i == 0 ? firstFlags : sampleFlags;
                    // Version 0 offsets are unsigned, but read as signed like ctts's, since writers store negative ones there too.
                    long composition = (runFlags & SampleCompositionOffset) != 0 ? f.I32() : 0;
                    samples.Add((track, new Mp4Sample(i, dataAt, sampleSize, decode, composition, sampleDuration, (thisFlags & NonSync) == 0, (int)Math.Min(description, int.MaxValue))));
                    dataAt += sampleSize;
                    decode += sampleDuration;
                }
            }

            previousEnd = dataAt;
            if (decodeTimes is not null)
            {
                decodeTimes[track] = decode;
            }
            else
            {
                track.NextDecodeTime = decode;
            }
        }
    }
}
