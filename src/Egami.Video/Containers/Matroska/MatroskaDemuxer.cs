using System.Buffers.Binary;
using System.IO.Compression;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Video.Containers.Matroska;

/// <summary>
/// Reads Matroska and WebM (RFC 9559 over the EBML of RFC 8794): the EBML header, the Segment's Info, Tracks, Tags and Cues,
/// and packets from Clusters, with SimpleBlocks, BlockGroups, all three lacing modes and unknown-size Segments and Clusters.
/// </summary>
/// <remarks>
/// Like the AVI reader, packets are read by walking elements without tracking nesting: Segments and Clusters are entered,
/// blocks are parsed and everything else is skipped. An unknown-size Cluster then needs no special end handling, and the
/// reader can resume at any Cluster, which is what seeking does.
/// </remarks>
internal sealed class MatroskaDemuxer : IDemuxer
{
    private const int ResyncLimit = 16 << 20;

    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly Dictionary<long, TrackState> _tracks = [];
    private readonly List<TrackState> _trackOrder = [];
    private readonly Dictionary<string, string> _tags = [];
    private readonly Dictionary<ulong, Dictionary<string, string>> _trackTags = [];
    private readonly List<ChapterInfo> _chapters = [];
    private readonly List<AttachmentInfo> _attachments = [];
    private readonly Queue<EncodedPacket> _pending = new();
    private readonly string _formatName;
    private long _segmentDataStart;
    private long _segmentEnd = long.MaxValue;
    private long _timestampScale = 1_000_000;
    private double? _duration;
    private long _cuesPosition = -1;
    private long _firstClusterPosition = -1;
    private long _clusterTimestamp;
    private bool _indexBuilt;

    public MatroskaDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, MatroskaFormat.Instance.Name);
        _context = context;
        try
        {
            _formatName = ReadEbmlHeader();
            ReadSegmentHeaders();
            Tracks = [.. _trackOrder.Select(t => t.Track)];
            MediaTime? duration = _duration is { } d && double.IsFinite(d) && d > 0
                ? new MediaTime((long)Math.Round(d), TimeBase)
                : null;
            Info = new MediaInfo { FormatName = _formatName, Tracks = Tracks, Duration = duration, Tags = _tags, Chapters = [.. _chapters.OrderBy(c => c.Start)], Attachments = _attachments };
        }
        catch
        {
            _input.Dispose();
            throw;
        }
    }

    public MediaInfo Info { get; }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    public bool CanSeek => _input.CanSeek;

    private Rational TimeBase => new(_timestampScale, 1_000_000_000);

    public EncodedPacket? ReadPacket()
    {
        while (true)
        {
            if (_pending.TryDequeue(out EncodedPacket? ready))
            {
                return ready;
            }

            if (!ReadNextBlock())
            {
                return null;
            }
        }
    }

    public void Seek(MediaTime time, int? trackNumber = null)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("The stream cannot seek.");
        }

        TrackState target = trackNumber is { } n
            ? _tracks.GetValueOrDefault(n) ?? throw new ArgumentException($"There is no track {n}.", nameof(trackNumber))
            : _trackOrder.FirstOrDefault(t => t.Track.Kind == TrackKind.Video) ?? _trackOrder.FirstOrDefault()
                ?? throw new InvalidOperationException("The file has no tracks.");

        ClearPending();
        foreach (TrackState t in _trackOrder)
        {
            t.Vorbis?.Reset();
        }

        EnsureIndex(target);

        // Index times are block times; packet times have the track's CodecDelay subtracted, so work in block times.
        MediaTime inBase = time.Rescale(TimeBase);
        long ticks = (new MediaTime(inBase.Ticks, TimeBase) > time ? inBase.Ticks - 1 : inBase.Ticks) + target.DelayTicks;

        // The cue at or before the time, else the earliest one.
        long cluster = -1, cueTime = long.MinValue;
        foreach ((long t, long position) in target.Keyframes)
        {
            if (t <= ticks && t >= cueTime)
            {
                (cueTime, cluster) = (t, position);
            }
        }

        if (cluster < 0 && target.Keyframes.Count > 0)
        {
            cluster = target.Keyframes.MinBy(k => k.Time).Cluster;
        }

        if (cluster < 0)
        {
            if (_firstClusterPosition >= 0)
            {
                _input.Seek(_firstClusterPosition);
            }

            return;
        }

        // Cues often point only at Cluster starts, so read on: keep everything from the target track's last keyframe at or
        // before the time, up to and including its first packet after the time.
        _input.Seek(cluster);
        List<EncodedPacket> kept = [];
        bool found = false;
        while (ReadPacket() is { } packet)
        {
            bool isTarget = packet.TrackNumber == target.Number;
            long blockTicks = packet.Timestamp.Ticks + target.DelayTicks;
            if (isTarget && blockTicks > ticks && (found || packet.IsKeyframe))
            {
                if (!found)
                {
                    // The time is before every keyframe: start at this one.
                    kept.ForEach(p => p.Dispose());
                    kept.Clear();
                }

                kept.Add(packet);
                found = true;
                break;
            }

            if (isTarget && packet.IsKeyframe)
            {
                kept.ForEach(p => p.Dispose());
                kept.Clear();
                found = true;
            }

            if (found)
            {
                kept.Add(packet);
            }
            else
            {
                packet.Dispose();
            }
        }

        List<EncodedPacket> rest = [.. _pending];
        _pending.Clear();
        foreach (EncodedPacket packet in kept.Concat(rest))
        {
            _pending.Enqueue(packet);
        }
    }

    public void Dispose()
    {
        ClearPending();
        _input.Dispose();
    }

    private void ClearPending()
    {
        while (_pending.TryDequeue(out EncodedPacket? packet))
        {
            packet.Dispose();
        }
    }

    private string ReadEbmlHeader()
    {
        if (!TryReadHeader(out uint id, out long size, out _) || id != MatroskaIds.Ebml || size == Ebml.UnknownSize)
        {
            throw _input.Corrupt(0, "The file does not start with an EBML header.");
        }

        string docType = "matroska";
        foreach ((uint child, ReadOnlyMemory<byte> body) in Ebml.Children(ReadBody(size)))
        {
            switch (child)
            {
                case MatroskaIds.DocType:
                    docType = Ebml.ReadString(body.Span);
                    break;
                case MatroskaIds.EbmlMaxIdLength when Ebml.ReadUnsigned(body.Span) > 4:
                case MatroskaIds.EbmlMaxSizeLength when Ebml.ReadUnsigned(body.Span) > 8:
                    throw _input.Corrupt("The EBML header allows ids or sizes longer than this reader supports.");
            }
        }

        return docType switch
        {
            "webm" => "WebM",
            "matroska" => "Matroska",
            _ => throw new UnsupportedFormatException($"EBML DocType '{docType}' is not Matroska or WebM."),
        };
    }

    private void ReadSegmentHeaders()
    {
        // Skip anything before the Segment (Void, a second EBML header).
        while (true)
        {
            if (!TryReadHeader(out uint id, out long size, out _))
            {
                throw _input.Corrupt("The file has no Segment.");
            }

            if (id == MatroskaIds.Segment)
            {
                _segmentDataStart = _input.Position;
                _segmentEnd = size == Ebml.UnknownSize ? long.MaxValue : _segmentDataStart + size;
                break;
            }

            if (size == Ebml.UnknownSize || !_input.Skip(size))
            {
                throw _input.Corrupt("The file has no Segment.");
            }
        }

        long tracksPosition = -1, infoPosition = -1, tagsPosition = -1, chaptersPosition = -1, attachmentsPosition = -1;
        bool sawTracks = false, sawInfo = false, sawTags = false, sawChapters = false, sawAttachments = false;
        while (_input.Position < _segmentEnd)
        {
            long elementStart = _input.Position;
            if (!TryReadHeader(out uint id, out long size, out _))
            {
                break;
            }

            if (id == MatroskaIds.Cluster)
            {
                _firstClusterPosition = elementStart;
                break;
            }

            if (size == Ebml.UnknownSize)
            {
                throw _input.Corrupt(elementStart, "Only Segments and Clusters may have an unknown size.");
            }

            switch (id)
            {
                case MatroskaIds.Info:
                    ReadInfo(ReadBody(size));
                    sawInfo = true;
                    break;
                case MatroskaIds.Tracks:
                    ReadTracks(ReadBody(size));
                    sawTracks = true;
                    break;
                case MatroskaIds.Tags:
                    ReadTags(ReadBody(size));
                    sawTags = true;
                    break;
                case MatroskaIds.Chapters:
                    ReadChapters(ReadBody(size));
                    sawChapters = true;
                    break;
                case MatroskaIds.Attachments:
                    if (size <= _context.Limits.MaxHeaderBytes)
                    {
                        ReadAttachments(ReadBody(size));
                    }
                    else if (!_input.Skip(size))
                    {
                        throw _input.Corrupt("The data ends inside the Attachments.");
                    }

                    sawAttachments = true;
                    break;
                case MatroskaIds.SeekHead:
                    foreach ((uint target, long position) in ReadSeekHead(ReadBody(size)))
                    {
                        long absolute = _segmentDataStart + position;
                        switch (target)
                        {
                            case MatroskaIds.Cues: _cuesPosition = absolute; break;
                            case MatroskaIds.Tracks: tracksPosition = absolute; break;
                            case MatroskaIds.Info: infoPosition = absolute; break;
                            case MatroskaIds.Tags: tagsPosition = absolute; break;
                            case MatroskaIds.Chapters: chaptersPosition = absolute; break;
                            case MatroskaIds.Attachments: attachmentsPosition = absolute; break;
                        }
                    }

                    break;
                case MatroskaIds.Cues:
                    _cuesPosition = elementStart;
                    if (!_input.Skip(size))
                    {
                        throw _input.Corrupt("The data ends inside the Cues.");
                    }

                    break;
                default:
                    if (!_input.Skip(size))
                    {
                        throw _input.Corrupt("The data ends before the first Cluster.");
                    }

                    break;
            }
        }

        if (_input.CanSeek)
        {
            long resume = _firstClusterPosition >= 0 ? _firstClusterPosition : _input.Position;
            if (!sawInfo && infoPosition >= 0)
            {
                ReadElementAt(infoPosition, MatroskaIds.Info, ReadInfo);
            }

            if (!sawTracks && tracksPosition >= 0)
            {
                ReadElementAt(tracksPosition, MatroskaIds.Tracks, ReadTracks);
                sawTracks = _trackOrder.Count > 0;
            }

            if (tagsPosition >= 0 && !sawTags)
            {
                ReadElementAt(tagsPosition, MatroskaIds.Tags, ReadTags);
            }

            if (chaptersPosition >= 0 && !sawChapters)
            {
                ReadElementAt(chaptersPosition, MatroskaIds.Chapters, ReadChapters);
            }

            if (attachmentsPosition >= 0 && !sawAttachments)
            {
                ReadElementAt(attachmentsPosition, MatroskaIds.Attachments, ReadAttachments);
            }

            _input.Seek(resume);
        }

        if (!sawTracks || _trackOrder.Count == 0)
        {
            throw _input.Corrupt("The Segment has no usable Tracks before its first Cluster.");
        }

        foreach (TrackState track in _trackOrder)
        {
            track.BuildTrack(TimeBase, _trackTags.TryGetValue(track.Uid, out Dictionary<string, string>? tags) ? tags : []);
        }
    }

    private void ReadElementAt(long position, uint expected, Action<ReadOnlyMemory<byte>> read)
    {
        if (position < 0 || position >= _input.Length)
        {
            return;
        }

        _input.Seek(position);
        if (TryReadHeader(out uint id, out long size, out _) && id == expected && size != Ebml.UnknownSize && size <= _context.Limits.MaxHeaderBytes)
        {
            read(ReadBody(size));
        }
    }

    private ReadOnlyMemory<byte> ReadBody(long size)
    {
        if (size < 0 || size > _context.Limits.MaxHeaderBytes)
        {
            throw new LimitExceededException("MaxHeaderBytes", _context.Limits.MaxHeaderBytes, size);
        }

        if (size > _input.Remaining)
        {
            throw _input.Corrupt("An element runs past the end of the file.");
        }

        byte[] body = new byte[size];
        _input.ReadExactly(body);
        return body;
    }

    private void ReadInfo(ReadOnlyMemory<byte> info)
    {
        foreach ((uint id, ReadOnlyMemory<byte> body) in Ebml.Children(info))
        {
            switch (id)
            {
                case MatroskaIds.TimestampScale:
                    ulong scale = Ebml.ReadUnsigned(body.Span);
                    _timestampScale = scale is 0 or > 1_000_000_000_000 ? 1_000_000 : (long)scale;
                    break;
                case MatroskaIds.Duration:
                    _duration = Ebml.ReadFloat(body.Span);
                    break;
                case MatroskaIds.Title:
                    _tags["TITLE"] = Ebml.ReadString(body.Span);
                    break;
                case MatroskaIds.MuxingApp:
                    _tags["MUXINGAPP"] = Ebml.ReadString(body.Span);
                    break;
                case MatroskaIds.WritingApp:
                    _tags["WRITINGAPP"] = Ebml.ReadString(body.Span);
                    break;
            }
        }
    }

    private void ReadTracks(ReadOnlyMemory<byte> tracks)
    {
        foreach ((uint id, ReadOnlyMemory<byte> body) in Ebml.Children(tracks))
        {
            if (id != MatroskaIds.TrackEntry)
            {
                continue;
            }

            if (_trackOrder.Count >= _context.Limits.MaxTracks)
            {
                throw new LimitExceededException("MaxTracks", _context.Limits.MaxTracks, _trackOrder.Count + 1);
            }

            TrackState track = TrackState.Parse(body);
            if (track.Number <= 0 || track.Number > int.MaxValue || _tracks.ContainsKey(track.Number))
            {
                // Tracks with a missing or repeated number cannot be told apart in blocks; leave them out.
                continue;
            }

            _tracks[track.Number] = track;
            _trackOrder.Add(track);
        }
    }

    private void ReadTags(ReadOnlyMemory<byte> tags)
    {
        foreach ((uint id, ReadOnlyMemory<byte> tag) in Ebml.Children(tags))
        {
            if (id != MatroskaIds.Tag)
            {
                continue;
            }

            List<ulong> trackUids = [];
            Dictionary<string, string> values = [];
            foreach ((uint child, ReadOnlyMemory<byte> body) in Ebml.Children(tag))
            {
                if (child == MatroskaIds.Targets)
                {
                    foreach ((uint target, ReadOnlyMemory<byte> uid) in Ebml.Children(body))
                    {
                        if (target == MatroskaIds.TagTrackUid && Ebml.ReadUnsigned(uid.Span) is > 0 and var value)
                        {
                            trackUids.Add(value);
                        }
                    }
                }
                else if (child == MatroskaIds.SimpleTag)
                {
                    string? name = null, value = null;
                    foreach ((uint field, ReadOnlyMemory<byte> text) in Ebml.Children(body))
                    {
                        if (field == MatroskaIds.TagName)
                        {
                            name = Ebml.ReadString(text.Span);
                        }
                        else if (field == MatroskaIds.TagString)
                        {
                            value = Ebml.ReadString(text.Span);
                        }
                    }

                    if (name is { Length: > 0 } && value is not null)
                    {
                        values[name] = value;
                    }
                }
            }

            // Tags without a track target belong to the whole file.
            if (trackUids.Count == 0)
            {
                foreach ((string name, string value) in values)
                {
                    _tags[name] = value;
                }

                continue;
            }

            foreach (ulong uid in trackUids)
            {
                Dictionary<string, string> trackTags = _trackTags.TryGetValue(uid, out Dictionary<string, string>? existing) ? existing : _trackTags[uid] = [];
                foreach ((string name, string value) in values)
                {
                    trackTags[name] = value;
                }
            }
        }
    }

    private void ReadChapters(ReadOnlyMemory<byte> chapters)
    {
        // The first edition is the default; later ones are alternatives.
        foreach ((uint id, ReadOnlyMemory<byte> edition) in Ebml.Children(chapters))
        {
            if (id == MatroskaIds.EditionEntry)
            {
                ReadChapterAtoms(edition, 0);
                return;
            }
        }
    }

    private void ReadChapterAtoms(ReadOnlyMemory<byte> parent, int depth)
    {
        if (depth > _context.Limits.MaxNesting)
        {
            return;
        }

        foreach ((uint id, ReadOnlyMemory<byte> atom) in Ebml.Children(parent))
        {
            if (id != MatroskaIds.ChapterAtom)
            {
                continue;
            }

            long start = -1;
            long? end = null;
            string? title = null, language = null;
            foreach ((uint child, ReadOnlyMemory<byte> body) in Ebml.Children(atom))
            {
                switch (child)
                {
                    case MatroskaIds.ChapterTimeStart:
                        start = (long)Math.Min(Ebml.ReadUnsigned(body.Span), long.MaxValue);
                        break;
                    case MatroskaIds.ChapterTimeEnd:
                        end = (long)Math.Min(Ebml.ReadUnsigned(body.Span), long.MaxValue);
                        break;
                    case MatroskaIds.ChapterDisplay when title is null:
                        foreach ((uint field, ReadOnlyMemory<byte> text) in Ebml.Children(body))
                        {
                            if (field == MatroskaIds.ChapString)
                            {
                                title = Ebml.ReadString(text.Span);
                            }
                            else if (field is MatroskaIds.ChapLanguage or MatroskaIds.ChapLanguageBcp47)
                            {
                                language = Ebml.ReadString(text.Span);
                            }
                        }

                        break;
                }
            }

            if (start >= 0)
            {
                _chapters.Add(new ChapterInfo(new MediaTime(start, Rational.Nanosecond), end is { } e ? new MediaTime(e, Rational.Nanosecond) : null, title, language));
            }

            ReadChapterAtoms(atom, depth + 1);
        }
    }

    private void ReadAttachments(ReadOnlyMemory<byte> attachments)
    {
        foreach ((uint id, ReadOnlyMemory<byte> file) in Ebml.Children(attachments))
        {
            if (id != MatroskaIds.AttachedFile)
            {
                continue;
            }

            string? name = null, mediaType = null, description = null;
            ReadOnlyMemory<byte> data = default;
            foreach ((uint child, ReadOnlyMemory<byte> body) in Ebml.Children(file))
            {
                switch (child)
                {
                    case MatroskaIds.FileName: name = Ebml.ReadString(body.Span); break;
                    case MatroskaIds.FileMediaType: mediaType = Ebml.ReadString(body.Span); break;
                    case MatroskaIds.FileDescription: description = Ebml.ReadString(body.Span); break;
                    case MatroskaIds.FileData: data = body; break;
                }
            }

            if (name is not null)
            {
                _attachments.Add(new AttachmentInfo(name, mediaType ?? "application/octet-stream", data, description));
            }
        }
    }

    private static List<(uint Id, long Position)> ReadSeekHead(ReadOnlyMemory<byte> seekHead)
    {
        List<(uint Id, long Position)> seeks = [];
        foreach ((uint id, ReadOnlyMemory<byte> seek) in Ebml.Children(seekHead))
        {
            if (id != MatroskaIds.Seek)
            {
                continue;
            }

            uint target = 0;
            long position = -1;
            foreach ((uint child, ReadOnlyMemory<byte> body) in Ebml.Children(seek))
            {
                if (child == MatroskaIds.SeekId)
                {
                    target = (uint)Ebml.ReadUnsigned(body.Span);
                }
                else if (child == MatroskaIds.SeekPosition)
                {
                    position = (long)Math.Min(Ebml.ReadUnsigned(body.Span), long.MaxValue / 2);
                }
            }

            if (target != 0 && position >= 0)
            {
                seeks.Add((target, position));
            }
        }

        return seeks;
    }

    /// <summary>Reads elements until a block yields packets into the queue; false at the end of the data.</summary>
    private bool ReadNextBlock()
    {
        Span<byte> ts = stackalloc byte[8];
        while (_input.Position < _segmentEnd)
        {
            long elementStart = _input.Position;
            if (!TryReadHeader(out uint id, out long size, out bool valid))
            {
                if (!valid && Resync())
                {
                    continue;
                }

                return false;
            }

            switch (id)
            {
                case MatroskaIds.Segment or MatroskaIds.Cluster:
                    // Enter: their children follow directly.
                    continue;
                case MatroskaIds.Timestamp when size is > 0 and <= 8:
                    if (!_input.TryReadExactly(ts[..(int)size]))
                    {
                        return false;
                    }

                    _clusterTimestamp = (long)Math.Min(Ebml.ReadUnsigned(ts[..(int)size]), long.MaxValue / 4);
                    continue;
                case MatroskaIds.SimpleBlock or MatroskaIds.BlockGroup:
                    if (size == Ebml.UnknownSize)
                    {
                        throw _input.Corrupt(elementStart, "A block has an unknown size.");
                    }

                    if (size > _context.Limits.MaxPacketBytes + 1024L)
                    {
                        throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, size);
                    }

                    if (size > _input.Remaining)
                    {
                        // A truncated last block: stop cleanly.
                        _input.Skip(size);
                        return false;
                    }

                    byte[] body = new byte[size];
                    _input.ReadExactly(body);
                    if (id == MatroskaIds.SimpleBlock)
                    {
                        ParseBlock(body, simple: true, blockDuration: null, hasReference: false, elementStart);
                    }
                    else
                    {
                        ParseBlockGroup(body, elementStart);
                    }

                    if (_pending.Count > 0)
                    {
                        return true;
                    }

                    continue;
                case MatroskaIds.Ebml:
                    // A chained second segment: read on.
                    if (size == Ebml.UnknownSize || !_input.Skip(size))
                    {
                        return false;
                    }

                    _segmentEnd = long.MaxValue;
                    continue;
                default:
                    if (size == Ebml.UnknownSize)
                    {
                        if (!Resync())
                        {
                            return false;
                        }

                        continue;
                    }

                    if (!_input.Skip(size))
                    {
                        return false;
                    }

                    continue;
            }
        }

        return false;
    }

    private void ParseBlockGroup(byte[] group, long elementStart)
    {
        ReadOnlyMemory<byte> block = default;
        long? duration = null;
        bool hasReference = false;
        long discardPadding = 0;
        List<PacketAddition>? additions = null;
        foreach ((uint id, ReadOnlyMemory<byte> body) in Ebml.Children(group))
        {
            switch (id)
            {
                case MatroskaIds.Block:
                    block = body;
                    break;
                case MatroskaIds.BlockDuration:
                    duration = (long)Math.Min(Ebml.ReadUnsigned(body.Span), long.MaxValue / 4);
                    break;
                case MatroskaIds.ReferenceBlock:
                    hasReference = true;
                    break;
                case MatroskaIds.DiscardPadding:
                    discardPadding = Math.Max(0, Ebml.ReadSigned(body.Span));
                    break;
                case MatroskaIds.BlockAdditions:
                    foreach ((uint moreId, ReadOnlyMemory<byte> more) in Ebml.Children(body))
                    {
                        if (moreId == MatroskaIds.BlockMore)
                        {
                            (additions ??= []).Add(ParseBlockMore(more));
                        }
                    }

                    break;
            }
        }

        if (!block.IsEmpty)
        {
            ParseBlock(block, simple: false, duration, hasReference, elementStart, additions, discardPadding);
        }
    }

    private static PacketAddition ParseBlockMore(ReadOnlyMemory<byte> more)
    {
        // BlockAddID defaults to 1.
        long addId = 1;
        ReadOnlyMemory<byte> data = default;
        foreach ((uint id, ReadOnlyMemory<byte> body) in Ebml.Children(more))
        {
            if (id == MatroskaIds.BlockAddId)
            {
                addId = (long)Math.Min(Ebml.ReadUnsigned(body.Span), long.MaxValue);
            }
            else if (id == MatroskaIds.BlockAdditional)
            {
                data = body;
            }
        }

        return new PacketAddition(addId, data);
    }

    private void ParseBlock(ReadOnlyMemory<byte> block, bool simple, long? blockDuration, bool hasReference, long elementStart, List<PacketAddition>? additions = null, long discardPadding = 0)
    {
        ReadOnlySpan<byte> span = block.Span;
        int trackLength = Ebml.ReadVint(span, out long trackNumber);
        if (trackLength == 0 || span.Length < trackLength + 3)
        {
            throw _input.Corrupt(elementStart, "A block header is malformed.");
        }

        if (!_tracks.TryGetValue(trackNumber, out TrackState? track))
        {
            return;
        }

        long timestamp = _clusterTimestamp + BinaryPrimitives.ReadInt16BigEndian(span[trackLength..]) - track.DelayTicks;
        byte flags = span[trackLength + 2];
        bool key = simple ? (flags & 0x80) != 0 : !hasReference;
        int lacing = (flags >> 1) & 3;
        int position = trackLength + 3;

        Span<int> sizes = stackalloc int[256];
        int frames;
        if (lacing == 0)
        {
            frames = 1;
            sizes[0] = span.Length - position;
        }
        else
        {
            if (position >= span.Length)
            {
                throw _input.Corrupt(elementStart, "A laced block has no frame count.");
            }

            frames = span[position++] + 1;
            position = ReadLaceSizes(span, position, lacing, frames, sizes, elementStart);
        }

        long durationTicks = blockDuration ?? track.DefaultDurationTicks(_timestampScale) ?? 0;
        long frameDuration = frames > 1 && blockDuration is { } d ? d / frames : track.DefaultDurationTicks(_timestampScale) ?? 0;
        for (int i = 0; i < frames; i++)
        {
            int size = sizes[i];
            ReadOnlySpan<byte> payload = span.Slice(position, size);
            position += size;
            EncodedPacket packet = track.MakePacket(payload, new MediaTime(timestamp + (i * frameDuration), TimeBase), _context.Limits.MaxPacketBytes);
            packet.IsKeyframe = key;
            if (frames == 1 && additions is not null)
            {
                packet.Additions = additions;
            }

            if (frames == 1 && durationTicks > 0)
            {
                packet.Duration = new MediaTime(durationTicks, TimeBase);
            }
            else if (frameDuration > 0)
            {
                packet.Duration = new MediaTime(frameDuration, TimeBase);
            }

            if (track.Track?.CodecId == CodecIds.Opus && frames == 1)
            {
                // Opus packets say how long they are; DiscardPadding (nanoseconds) cuts the end of the stream's last one.
                int samples = Audio.Codecs.Opus.OpusPacket.SampleCount(payload, 48000);
                if (samples > 0)
                {
                    MediaTime length = new(samples, new Rational(1, 48000));
                    packet.Duration = discardPadding > 0 ? length - new MediaTime(Math.Min(discardPadding, length.Rescale(Rational.Nanosecond).Ticks), Rational.Nanosecond) : length;
                }
            }

            else if (track.Track?.CodecId == CodecIds.Vorbis && frames == 1)
            {
                // Vorbis packets add half of two blocks; DiscardPadding cuts the end as Ogg's last granule position does.
                track.Vorbis ??= Audio.Codecs.Vorbis.VorbisPacketClock.Create(track.Track.CodecPrivate.Span);
                if (track.Vorbis?.Next(payload) is { } samples)
                {
                    Rational sample = new(1, ((AudioTrackInfo)track.Track).SampleRate);
                    long cut = discardPadding > 0 ? new MediaTime(discardPadding, Rational.Nanosecond).Rescale(sample).Ticks : 0;
                    packet.Duration = new MediaTime(Math.Max(0, samples - cut), sample);
                }
            }

            _pending.Enqueue(packet);
        }
    }

    private int ReadLaceSizes(ReadOnlySpan<byte> span, int position, int lacing, int frames, Span<int> sizes, long elementStart)
    {
        int total = span.Length - position;
        long known = 0;
        switch (lacing)
        {
            case 1:
                // Xiph: each size but the last as a run of 255s plus a final byte.
                for (int i = 0; i < frames - 1; i++)
                {
                    int size = 0;
                    byte b;
                    do
                    {
                        if (position >= span.Length)
                        {
                            throw _input.Corrupt(elementStart, "A Xiph lace size runs past the block.");
                        }

                        b = span[position++];
                        size += b;
                    }
                    while (b == 255 && size < int.MaxValue - 255);

                    sizes[i] = size;
                    known += size;
                }

                break;
            case 3:
                // EBML: the first size as a VINT, then signed differences.
                int length = Ebml.ReadVint(span[position..], out long first);
                if (length == 0 || first < 0)
                {
                    throw _input.Corrupt(elementStart, "An EBML lace size is malformed.");
                }

                position += length;
                long previous = first;
                sizes[0] = (int)Math.Min(first, int.MaxValue);
                known = first;
                for (int i = 1; i < frames - 1; i++)
                {
                    length = Ebml.ReadVint(span[position..], out long raw);
                    if (length == 0 || raw < 0)
                    {
                        throw _input.Corrupt(elementStart, "An EBML lace size is malformed.");
                    }

                    position += length;
                    long bias = (1L << ((7 * length) - 1)) - 1;
                    previous += raw - bias;
                    if (previous < 0 || previous > int.MaxValue)
                    {
                        throw _input.Corrupt(elementStart, "An EBML lace size is out of range.");
                    }

                    sizes[i] = (int)previous;
                    known += previous;
                }

                break;
            case 2:
                // Fixed: equal sizes.
                total = span.Length - position;
                if (total % frames != 0)
                {
                    throw _input.Corrupt(elementStart, "A fixed-size lace does not divide the block evenly.");
                }

                for (int i = 0; i < frames; i++)
                {
                    sizes[i] = total / frames;
                }

                return position;
        }

        long last = span.Length - position - known;
        if (last < 0)
        {
            throw _input.Corrupt(elementStart, "The lace sizes are larger than the block.");
        }

        sizes[frames - 1] = (int)last;
        return position;
    }

    private void EnsureIndex(TrackState target)
    {
        if (target.Keyframes.Count > 0 || _indexBuilt)
        {
            return;
        }

        long resume = _input.Position;
        if (_cuesPosition >= 0)
        {
            ReadElementAt(_cuesPosition, MatroskaIds.Cues, ReadCues);
        }

        if (target.Keyframes.Count == 0)
        {
            ScanClusters();
        }

        _indexBuilt = true;
        _input.Seek(resume);
    }

    private void ReadCues(ReadOnlyMemory<byte> cues)
    {
        long entries = 0;
        foreach ((uint id, ReadOnlyMemory<byte> point) in Ebml.Children(cues))
        {
            if (id != MatroskaIds.CuePoint)
            {
                continue;
            }

            long time = -1;
            List<(long Track, long Cluster)> positions = [];
            foreach ((uint child, ReadOnlyMemory<byte> body) in Ebml.Children(point))
            {
                if (child == MatroskaIds.CueTime)
                {
                    time = (long)Math.Min(Ebml.ReadUnsigned(body.Span), long.MaxValue / 4);
                }
                else if (child == MatroskaIds.CueTrackPositions)
                {
                    long track = 0, cluster = -1;
                    foreach ((uint field, ReadOnlyMemory<byte> value) in Ebml.Children(body))
                    {
                        if (field == MatroskaIds.CueTrack)
                        {
                            track = (long)Math.Min(Ebml.ReadUnsigned(value.Span), long.MaxValue);
                        }
                        else if (field == MatroskaIds.CueClusterPosition)
                        {
                            cluster = (long)Math.Min(Ebml.ReadUnsigned(value.Span), long.MaxValue / 2);
                        }
                    }

                    positions.Add((track, cluster));
                }
            }

            foreach ((long track, long cluster) in positions)
            {
                if (time >= 0 && cluster >= 0 && _tracks.TryGetValue(track, out TrackState? state))
                {
                    if (++entries > _context.Limits.MaxIndexEntries)
                    {
                        throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, entries);
                    }

                    state.Keyframes.Add((time, _segmentDataStart + cluster));
                }
            }
        }
    }

    /// <summary>Walks every Cluster once, reading block headers only, and records each track's keyframes.</summary>
    private void ScanClusters()
    {
        if (_firstClusterPosition < 0)
        {
            return;
        }

        foreach (TrackState track in _trackOrder)
        {
            track.Keyframes.Clear();
        }

        _input.Seek(_firstClusterPosition);
        long cluster = -1, clusterTime = 0, entries = 0;
        Span<byte> head = stackalloc byte[12];
        while (_input.Position < _segmentEnd)
        {
            long elementStart = _input.Position;
            if (!TryReadHeader(out uint id, out long size, out bool valid))
            {
                if (!valid && Resync())
                {
                    continue;
                }

                break;
            }

            if (id == MatroskaIds.Segment)
            {
                continue;
            }

            if (id == MatroskaIds.Cluster)
            {
                cluster = elementStart;
                continue;
            }

            if (size == Ebml.UnknownSize)
            {
                break;
            }

            if (id == MatroskaIds.Timestamp && size is > 0 and <= 8)
            {
                _input.ReadExactly(head[..(int)size]);
                clusterTime = (long)Math.Min(Ebml.ReadUnsigned(head[..(int)size]), long.MaxValue / 4);
                continue;
            }

            if (id is MatroskaIds.SimpleBlock or MatroskaIds.BlockGroup && cluster >= 0)
            {
                long trackNumber = 0, time = 0;
                bool key = false;
                if (id == MatroskaIds.SimpleBlock)
                {
                    int n = (int)Math.Min(size, head.Length);
                    _input.ReadExactly(head[..n]);
                    int length = Ebml.ReadVint(head[..n], out trackNumber);
                    if (length > 0 && n >= length + 3)
                    {
                        time = clusterTime + BinaryPrimitives.ReadInt16BigEndian(head[length..]);
                        key = (head[length + 2] & 0x80) != 0;
                    }

                    _input.Skip(size - n);
                }
                else if (size <= _context.Limits.MaxPacketBytes + 1024L && size <= _input.Remaining)
                {
                    byte[] group = new byte[size];
                    _input.ReadExactly(group);
                    bool reference = false;
                    foreach ((uint child, ReadOnlyMemory<byte> body) in Ebml.Children(group))
                    {
                        if (child == MatroskaIds.Block && Ebml.ReadVint(body.Span, out trackNumber) is var length and > 0 && body.Length >= length + 3)
                        {
                            time = clusterTime + BinaryPrimitives.ReadInt16BigEndian(body.Span[length..]);
                        }
                        else if (child == MatroskaIds.ReferenceBlock)
                        {
                            reference = true;
                        }
                    }

                    key = !reference;
                }
                else
                {
                    break;
                }

                if (key && _tracks.TryGetValue(trackNumber, out TrackState? track))
                {
                    if (++entries > _context.Limits.MaxIndexEntries)
                    {
                        throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, entries);
                    }

                    track.Keyframes.Add((time, cluster));
                }

                continue;
            }

            if (!_input.Skip(size))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Reads an element header. Returns false at the end of the data, with <paramref name="valid"/> false when the bytes there
    /// are not an element header (so the caller can resync).
    /// </summary>
    private bool TryReadHeader(out uint id, out long size, out bool valid)
    {
        id = 0;
        size = 0;
        valid = true;
        int first = _input.PeekByte();
        if (first < 0)
        {
            return false;
        }

        int idLength = Ebml.VintLength((byte)first);
        if (idLength is 0 or > 4)
        {
            valid = false;
            return false;
        }

        Span<byte> bytes = stackalloc byte[12];
        if (!_input.TryReadExactly(bytes[..idLength]))
        {
            return false;
        }

        int sizeFirst = _input.PeekByte();
        if (sizeFirst < 0)
        {
            return false;
        }

        int sizeLength = Ebml.VintLength((byte)sizeFirst);
        if (sizeLength == 0 || !_input.TryReadExactly(bytes.Slice(idLength, sizeLength)))
        {
            valid = sizeLength != 0;
            return false;
        }

        Ebml.ReadVint(bytes[..idLength], out long rawId, keepMarker: true);
        Ebml.ReadVint(bytes.Slice(idLength, sizeLength), out size);
        id = (uint)rawId;
        return true;
    }

    /// <summary>Scans forward for the next Cluster id after damaged data.</summary>
    private bool Resync()
    {
        uint window = 0;
        for (int scanned = 0; scanned < ResyncLimit; scanned++)
        {
            int b = _input.PeekByte();
            if (b < 0)
            {
                return false;
            }

            _input.ReadByte();
            window = (window << 8) | (uint)b;
            if (window == MatroskaIds.Cluster)
            {
                // Step back so the walker reads the Cluster header itself, or, when the stream cannot seek, enter the
                // Cluster here by reading past its size.
                if (_input.CanSeek)
                {
                    _input.Seek(_input.Position - 4);
                    return true;
                }

                int sizeFirst = _input.PeekByte();
                int sizeLength = sizeFirst < 0 ? 0 : Ebml.VintLength((byte)sizeFirst);
                return sizeLength > 0 && _input.Skip(sizeLength);
            }
        }

        return false;
    }

    /// <summary>One Matroska track: its parsed TrackEntry, content encoding and keyframe index.</summary>
    private sealed class TrackState
    {
        public long Number { get; private set; }

        public ulong Uid { get; private set; }

        public TrackInfo Track { get; private set; } = null!;

        public List<(long Time, long Cluster)> Keyframes { get; } = [];

        /// <summary>Gets or sets a Vorbis track's packet clock, made at its first packet.</summary>
        public Audio.Codecs.Vorbis.VorbisPacketClock? Vorbis { get; set; }

        private int _type;
        private string _codecId = "";
        private byte[] _codecPrivate = [];
        private string? _name;
        private string? _language;

        /// <summary>The track's language: English when the TrackEntry has none (Matroska's default), null for "und".</summary>
        private string? Language => _language is null ? "eng" : _language == "und" ? null : _language;
        private bool _default = true;
        private long _defaultDuration;
        private long _codecDelay;
        private long _seekPreRoll;
        private int _width, _height, _displayWidth, _displayHeight, _displayUnit;
        private int _fieldOrder, _interlaced;
        private ColorCodePoints? _color;
        private int _videoBitDepth;
        private ChromaFormat? _chromaFormat;
        private ChromaSiting? _chromaSiting;
        private HdrMetadata? _hdr;
        private bool _alpha;
        private string? _fourCC;
        private double _sampleRate = 8000;
        private int _channels = 1, _bitDepth;
        private byte[]? _strippedHeader;
        private bool _zlib;

        public static TrackState Parse(ReadOnlyMemory<byte> entry)
        {
            TrackState t = new();
            foreach ((uint id, ReadOnlyMemory<byte> body) in Ebml.Children(entry))
            {
                ReadOnlySpan<byte> b = body.Span;
                switch (id)
                {
                    case MatroskaIds.TrackNumber: t.Number = (long)Math.Min(Ebml.ReadUnsigned(b), long.MaxValue); break;
                    case MatroskaIds.TrackUid: t.Uid = Ebml.ReadUnsigned(b); break;
                    case MatroskaIds.TrackType: t._type = (int)Math.Min(Ebml.ReadUnsigned(b), 255); break;
                    case MatroskaIds.CodecId: t._codecId = Ebml.ReadString(b); break;
                    case MatroskaIds.CodecPrivate: t._codecPrivate = b.ToArray(); break;
                    case MatroskaIds.Name: t._name = Ebml.ReadString(b); break;
                    case MatroskaIds.Language: t._language ??= Ebml.ReadString(b); break;
                    case MatroskaIds.LanguageBcp47: t._language = Ebml.ReadString(b); break;
                    case MatroskaIds.FlagDefault: t._default = Ebml.ReadUnsigned(b) != 0; break;
                    case MatroskaIds.DefaultDuration: t._defaultDuration = (long)Math.Min(Ebml.ReadUnsigned(b), long.MaxValue / 4); break;
                    case MatroskaIds.CodecDelay: t._codecDelay = (long)Math.Min(Ebml.ReadUnsigned(b), long.MaxValue / 4); break;
                    case MatroskaIds.SeekPreRoll: t._seekPreRoll = (long)Math.Min(Ebml.ReadUnsigned(b), long.MaxValue / 4); break;
                    case MatroskaIds.Video: t.ParseVideo(body); break;
                    case MatroskaIds.Audio: t.ParseAudio(body); break;
                    case MatroskaIds.ContentEncodings: t.ParseEncodings(body); break;
                }
            }

            return t;
        }

        /// <summary>Gets the CodecDelay in Segment ticks, subtracted from every packet timestamp.</summary>
        public long DelayTicks { get; private set; }

        public long? DefaultDurationTicks(long timestampScale) =>
            _defaultDuration > 0 ? Math.Max(1, (long)Math.Round((double)_defaultDuration / timestampScale)) : null;

        public EncodedPacket MakePacket(ReadOnlySpan<byte> payload, MediaTime timestamp, int maxBytes)
        {
            if (_zlib)
            {
                using MemoryStream compressed = new(payload.ToArray());
                using ZLibStream inflater = new(compressed, CompressionMode.Decompress);
                using MemoryStream inflated = new();
                byte[] chunk = new byte[1 << 16];
                int n;
                while ((n = inflater.Read(chunk)) > 0)
                {
                    if (inflated.Length + n > maxBytes)
                    {
                        throw new LimitExceededException("MaxPacketBytes", maxBytes, inflated.Length + n);
                    }

                    inflated.Write(chunk, 0, n);
                }

                return Copy(inflated.GetBuffer().AsSpan(0, (int)inflated.Length), timestamp);
            }

            if (_strippedHeader is { Length: > 0 } header)
            {
                if ((long)header.Length + payload.Length > maxBytes)
                {
                    throw new LimitExceededException("MaxPacketBytes", maxBytes, (long)header.Length + payload.Length);
                }

                EncodedPacket packet = EncodedPacket.Rent((int)Number, header.Length + payload.Length, timestamp);
                Span<byte> target = packet.GetWritableData().Span;
                header.CopyTo(target);
                payload.CopyTo(target[header.Length..]);
                return packet;
            }

            return Copy(payload, timestamp);
        }

        private EncodedPacket Copy(ReadOnlySpan<byte> payload, MediaTime timestamp)
        {
            EncodedPacket packet = EncodedPacket.Rent((int)Number, payload.Length, timestamp);
            payload.CopyTo(packet.GetWritableData().Span);
            return packet;
        }

        public void BuildTrack(Rational timeBase, IReadOnlyDictionary<string, string> tags)
        {
            if (_codecId == CodecIds.VfwFourCC && _codecPrivate.Length >= 20
                && Avi.AviCodecs.UncompressedFourCC(BinaryPrimitives.ReadUInt32LittleEndian(_codecPrivate.AsSpan(16))) is { } layout)
            {
                // Uncompressed video in Matroska's Video for Windows mode: describe it as AVI does.
                _codecId = CodecIds.Uncompressed;
                _fourCC = layout;
                _codecPrivate = [];
            }

            int number = (int)Number;
            DelayTicks = _codecDelay > 0 ? new MediaTime(_codecDelay, Rational.Nanosecond).Rescale(timeBase).Ticks : 0;
            MediaTime? delay = _codecDelay > 0 ? new MediaTime(_codecDelay, Rational.Nanosecond) : null;
            Rational? frameRate = _defaultDuration > 0 ? FrameRateOf(_defaultDuration) : null;
            switch (_type)
            {
                case 1:
                    Rational aspect = new(1, 1);
                    if (_displayUnit == 0 && _displayWidth > 0 && _displayHeight > 0 && _width > 0 && _height > 0)
                    {
                        aspect = new Rational((long)_displayWidth * _height, (long)_displayHeight * _width);
                    }

                    int bitDepth = _videoBitDepth;
                    ChromaFormat? chromaFormat = _chromaFormat;
                    if (_codecId == CodecIds.Vp9 && Codecs.Vp9.Vp9CodecPrivate.Parse(_codecPrivate) is { } vp9)
                    {
                        // A VP9 track's CodecPrivate feature records give the bit depth and subsampling the Colour element may not.
                        if (bitDepth == 0 && vp9.BitDepth is 8 or 10 or 12)
                        {
                            bitDepth = vp9.BitDepth.Value;
                        }

                        chromaFormat ??= vp9.ChromaSubsampling switch
                        {
                            0 or 1 => ChromaFormat.Yuv420,
                            2 => ChromaFormat.Yuv422,
                            3 => ChromaFormat.Yuv444,
                            _ => null,
                        };
                    }

                    Track = new VideoTrackInfo
                    {
                        Number = number,
                        Kind = TrackKind.Video,
                        CodecId = _codecId,
                        CodecPrivate = _codecPrivate,
                        TimeBase = timeBase,
                        Width = _width,
                        Height = _height,
                        FrameRate = frameRate,
                        PixelAspectRatio = aspect,
                        Color = _color,
                        BitDepth = bitDepth,
                        ChromaFormat = chromaFormat,
                        ChromaSiting = _chromaSiting,
                        HasAlpha = _alpha,
                        FourCC = _fourCC,
                        Hdr = _hdr,
                        FieldOrder = _interlaced == 1 ? _fieldOrder is 6 or 9 ? FieldOrder.BottomFieldFirst : FieldOrder.TopFieldFirst : FieldOrder.Progressive,
                        Language = Language,
                        Name = _name,
                        IsDefault = _default,
                        Tags = tags,
                        CodecDelay = delay,
                    };
                    break;
                case 2:
                    Track = new AudioTrackInfo
                    {
                        Number = number,
                        Kind = TrackKind.Audio,
                        CodecId = _codecId,
                        CodecPrivate = _codecPrivate,
                        TimeBase = timeBase,
                        SampleRate = double.IsFinite(_sampleRate) ? (int)Math.Clamp(Math.Round(_sampleRate), 0, int.MaxValue) : 0,
                        Channels = _channels,
                        BitsPerSample = _bitDepth,
                        Language = Language,
                        Name = _name,
                        IsDefault = _default,
                        Tags = tags,
                        CodecDelay = delay,
                        SeekPreRoll = _seekPreRoll > 0 ? new MediaTime(_seekPreRoll, Rational.Nanosecond) : null,
                    };
                    break;
                default:
                    Track = new TrackInfo
                    {
                        Number = number,
                        Kind = _type == 17 ? TrackKind.Subtitle : TrackKind.Other,
                        CodecId = _codecId,
                        CodecPrivate = _codecPrivate,
                        TimeBase = timeBase,
                        Language = Language,
                        Name = _name,
                        IsDefault = _default,
                        Tags = tags,
                        CodecDelay = delay,
                    };
                    break;
            }
        }

        private void ParseVideo(ReadOnlyMemory<byte> video)
        {
            foreach ((uint id, ReadOnlyMemory<byte> body) in Ebml.Children(video))
            {
                int value = (int)Math.Min(Ebml.ReadUnsigned(body.Span), int.MaxValue);
                switch (id)
                {
                    case MatroskaIds.PixelWidth: _width = value; break;
                    case MatroskaIds.PixelHeight: _height = value; break;
                    case MatroskaIds.DisplayWidth: _displayWidth = value; break;
                    case MatroskaIds.DisplayHeight: _displayHeight = value; break;
                    case MatroskaIds.DisplayUnit: _displayUnit = value; break;
                    case MatroskaIds.FlagInterlaced: _interlaced = value; break;
                    case MatroskaIds.FieldOrder: _fieldOrder = value; break;
                    case MatroskaIds.Colour: ParseColour(body); break;
                    case MatroskaIds.ColourSpace when body.Length == 4:
                        _fourCC = string.Create(4, body, static (span, b) =>
                        {
                            for (int i = 0; i < 4; i++)
                            {
                                span[i] = (char)b.Span[i];
                            }
                        });
                        break;
                    case MatroskaIds.AlphaMode: _alpha = value != 0; break;
                }
            }
        }

        /// <summary>
        /// Turns a DefaultDuration in whole nanoseconds back into a frame rate, recognizing the NTSC rates (such as 30000/1001)
        /// whose frame duration was rounded to the nanosecond when written.
        /// </summary>
        private static Rational FrameRateOf(long defaultDuration)
        {
            foreach (long denominator in (ReadOnlySpan<long>)[1, 1001])
            {
                long numerator = (long)Math.Round(1e9 * denominator / defaultDuration);
                if (numerator > 0 && (denominator == 1 || numerator % 1000 == 0)
                    && Math.Abs((1e9 * denominator / numerator) - defaultDuration) < 1)
                {
                    return new Rational(numerator, denominator);
                }
            }

            return new Rational(1_000_000_000, defaultDuration);
        }

        private void ParseColour(ReadOnlyMemory<byte> colour)
        {
            int matrix = 2, transfer = 2, primaries = 2, range = 0;
            int subsamplingHorz = -1, subsamplingVert = -1, sitingHorz = 0, sitingVert = 0;
            double? maxCll = null, maxFall = null;
            foreach ((uint id, ReadOnlyMemory<byte> body) in Ebml.Children(colour))
            {
                int value = (int)Math.Min(Ebml.ReadUnsigned(body.Span), 65535);
                switch (id)
                {
                    case MatroskaIds.MatrixCoefficients: matrix = Math.Min(value, 255); break;
                    case MatroskaIds.TransferCharacteristics: transfer = Math.Min(value, 255); break;
                    case MatroskaIds.Primaries: primaries = Math.Min(value, 255); break;
                    case MatroskaIds.Range: range = value; break;
                    case MatroskaIds.BitsPerChannel: _videoBitDepth = Math.Min(value, 16); break;
                    case MatroskaIds.ChromaSubsamplingHorz: subsamplingHorz = value; break;
                    case MatroskaIds.ChromaSubsamplingVert: subsamplingVert = value; break;
                    case MatroskaIds.ChromaSitingHorz: sitingHorz = value; break;
                    case MatroskaIds.ChromaSitingVert: sitingVert = value; break;
                    case MatroskaIds.MaxCll: maxCll = value; break;
                    case MatroskaIds.MaxFall: maxFall = value; break;
                    case MatroskaIds.MasteringMetadata: _hdr = ParseMastering(body); break;
                }
            }

            _color = matrix == 2 && transfer == 2 && primaries == 2 && range is 0 or 3
                ? null
                : new ColorCodePoints((byte)primaries, (byte)transfer, (byte)matrix, range == 2);
            _chromaFormat = (subsamplingHorz, subsamplingVert) switch
            {
                (1, 1) => Pixels.ChromaFormat.Yuv420,
                (1, 0) => Pixels.ChromaFormat.Yuv422,
                (0, 0) => Pixels.ChromaFormat.Yuv444,
                _ => null,
            };
            _chromaSiting = (sitingHorz, sitingVert) switch
            {
                (2, 2) => Pixels.ChromaSiting.Center,
                (1, 2) => Pixels.ChromaSiting.Left,
                (1, 1) => Pixels.ChromaSiting.TopLeft,
                _ => null,
            };
            if (maxCll is not null || maxFall is not null)
            {
                _hdr = (_hdr ?? new HdrMetadata()) with { MaxContentLightLevel = maxCll, MaxFrameAverageLightLevel = maxFall };
            }
        }

        private static HdrMetadata ParseMastering(ReadOnlyMemory<byte> mastering)
        {
            double[] v = new double[10];
            bool[] seen = new bool[10];
            foreach ((uint id, ReadOnlyMemory<byte> body) in Ebml.Children(mastering))
            {
                int index = (int)id - (int)MatroskaIds.PrimaryRChromaticityX;
                if (index is >= 0 and < 10)
                {
                    double value = Ebml.ReadFloat(body.Span);
                    v[index] = double.IsFinite(value) ? value : 0;
                    seen[index] = true;
                }
            }

            bool primaries = seen.AsSpan(0, 8).IndexOf(false) < 0;
            return new HdrMetadata
            {
                MasteringPrimaries = primaries
                    ? new ColorPrimaries(new Chromaticity(v[0], v[1]), new Chromaticity(v[2], v[3]), new Chromaticity(v[4], v[5]), new Chromaticity(v[6], v[7]))
                    : null,
                MasteringMaxLuminance = seen[8] ? v[8] : null,
                MasteringMinLuminance = seen[9] ? v[9] : null,
            };
        }

        private void ParseAudio(ReadOnlyMemory<byte> audio)
        {
            foreach ((uint id, ReadOnlyMemory<byte> body) in Ebml.Children(audio))
            {
                switch (id)
                {
                    case MatroskaIds.SamplingFrequency: _sampleRate = Ebml.ReadFloat(body.Span); break;
                    case MatroskaIds.Channels: _channels = (int)Math.Min(Ebml.ReadUnsigned(body.Span), 255); break;
                    case MatroskaIds.BitDepth: _bitDepth = (int)Math.Min(Ebml.ReadUnsigned(body.Span), 255); break;
                }
            }
        }

        private void ParseEncodings(ReadOnlyMemory<byte> encodings)
        {
            foreach ((uint id, ReadOnlyMemory<byte> encoding) in Ebml.Children(encodings))
            {
                if (id != MatroskaIds.ContentEncoding)
                {
                    continue;
                }

                int type = 0;
                int scope = 1;
                ReadOnlyMemory<byte> compression = default;
                foreach ((uint child, ReadOnlyMemory<byte> body) in Ebml.Children(encoding))
                {
                    switch (child)
                    {
                        case MatroskaIds.ContentEncodingType: type = (int)Ebml.ReadUnsigned(body.Span); break;
                        case MatroskaIds.ContentEncodingScope: scope = (int)Ebml.ReadUnsigned(body.Span); break;
                        case MatroskaIds.ContentCompression: compression = body; break;
                    }
                }

                if ((scope & 1) == 0)
                {
                    continue;
                }

                if (type != 0)
                {
                    throw new UnsupportedFormatException("Encrypted Matroska tracks are not supported.");
                }

                int algorithm = 0;
                byte[] settings = [];
                foreach ((uint child, ReadOnlyMemory<byte> body) in Ebml.Children(compression))
                {
                    if (child == MatroskaIds.ContentCompAlgo)
                    {
                        algorithm = (int)Ebml.ReadUnsigned(body.Span);
                    }
                    else if (child == MatroskaIds.ContentCompSettings)
                    {
                        settings = body.ToArray();
                    }
                }

                switch (algorithm)
                {
                    case 0: _zlib = true; break;
                    case 3: _strippedHeader = settings; break;
                    default: throw new UnsupportedFormatException($"Matroska compression algorithm {algorithm} is not supported.");
                }
            }
        }
    }
}
