using System.Buffers.Binary;

using Egami.Formats.Iso;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// Reads MP4 and QuickTime files (ISO/IEC 14496-12, 14496-14 and the QuickTime File Format) on Egami's public box walker. The
/// top-level boxes are walked from the stream, so media data is never read whole; moov is loaded and its sample tables kept as
/// runs (<see cref="Mp4SampleTable"/>). Packets come in file order across tracks. Fragmented files (moof boxes after an empty or
/// partial moov) are read one fragment at a time, so opening a long recording reads only its moov.
/// </summary>
internal sealed partial class Mp4Demuxer : IDemuxer
{
    /// <summary>The largest moov read into memory, whatever MaxHeaderBytes says: its sample tables grow with the file's length.</summary>
    private const long MaxMoovBytes = 1L << 30;

    private static readonly FourCC Chap = FourCC.Parse("chap");

    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly bool _ignoreEdits;
    private readonly List<TrackState> _tracks = [];
    private readonly Dictionary<uint, TrackState> _byId = [];
    private readonly Dictionary<string, string> _tags = [];
    private readonly List<ChapterInfo> _chapters = [];
    private readonly List<AttachmentInfo> _attachments = [];
    private readonly List<(TrackState Track, Mp4Sample Sample)> _fragment = [];
    private readonly HashSet<string> _warned = [];
    private int _fragmentAt;
    private bool _quickTime;
    private uint _movieTimescale = 1000;
    private long _movieDuration;
    private long _fragmentDuration;
    private bool _hasMvex;
    private long _nextBox = -1;
    private IsoBox? _pendingBox;
    private bool _endOfBoxes;
    private string? _timecode;

    public Mp4Demuxer(Stream stream, MediaContext context, bool leaveOpen, bool ignoreEdits)
    {
        _input = new MediaInput(stream, leaveOpen, Mp4Format.Instance.Name);
        _context = context;
        _ignoreEdits = ignoreEdits;
        try
        {
            ReadHeaders();
            Tracks = [.. _tracks.Select(t => t.Info)];
            MediaTime? duration = _movieDuration > 0 && _movieTimescale > 0 ? new MediaTime(_movieDuration, new Rational(1, _movieTimescale))
                : _fragmentDuration > 0 && _movieTimescale > 0 ? new MediaTime(_fragmentDuration, new Rational(1, _movieTimescale))
                : null;
            Info = new MediaInfo
            {
                FormatName = _quickTime ? "QuickTime" : "MP4",
                Duration = duration,
                Tracks = Tracks,
                Tags = _tags,
                Chapters = _chapters,
                Attachments = _attachments,
                Timecode = _timecode,
            };
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

    public EncodedPacket? ReadPacket()
    {
        while (true)
        {
            if (_fragmentAt < _fragment.Count)
            {
                (TrackState fragmentTrack, Mp4Sample sample) = _fragment[_fragmentAt++];
                if (fragmentTrack.Ended || fragmentTrack.Skip(sample, fragment: true))
                {
                    continue;
                }

                if (Emit(fragmentTrack, sample, fragment: true) is { } fromFragment)
                {
                    return fromFragment;
                }

                continue;
            }

            // Flat samples: the track whose next sample comes first in the file.
            TrackState? next = null;
            long nextOffset = long.MaxValue;
            foreach (TrackState track in _tracks)
            {
                if (!track.Cursor.AtEnd)
                {
                    long offset = track.Cursor.Peek().Offset;
                    if (offset < nextOffset)
                    {
                        (next, nextOffset) = (track, offset);
                    }
                }
            }

            if (next is not null)
            {
                Mp4Sample sample = next.Cursor.Peek();
                next.Cursor.Advance();
                if (next.Skip(sample))
                {
                    continue;
                }

                if (Emit(next, sample) is { } packet)
                {
                    return packet;
                }

                continue;
            }

            if (!_hasMvex || !NextFragment())
            {
                return null;
            }
        }
    }

    public void Dispose() => _input.Dispose();

    /// <summary>Reads a sample's bytes into a packet; returns <see langword="null"/>, with an mp4-truncated warning, when the file ends first.</summary>
    private EncodedPacket? Emit(TrackState track, Mp4Sample sample, bool fragment = false)
    {
        if (sample.Size > _context.Limits.MaxPacketBytes)
        {
            throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, sample.Size);
        }

        if ((_input.CanSeek && sample.Offset + sample.Size > _input.Length) || (!_input.CanSeek && sample.Offset < _input.Position))
        {
            Truncated(track, sample);
            return null;
        }

        MoveTo(sample.Offset);
        EncodedPacket packet = EncodedPacket.Rent(track.Info.Number, sample.Size, default);
        try
        {
            if (_input.Read(packet.GetWritableData().Span) != sample.Size)
            {
                packet.Dispose();
                Truncated(track, sample);
                return null;
            }

            track.Describe(packet, sample, this, fragment);
            return packet;
        }
        catch
        {
            packet.Dispose();
            throw;
        }
    }

    /// <summary>Reports a sample past the end of the file and ends its track: the file was cut off while it was written or copied.</summary>
    private void Truncated(TrackState track, Mp4Sample sample)
    {
        Warn("mp4-truncated", $"Sample {sample.Index + 1} of track {track.Info.Number} lies past the end of the file, which was cut off; the track ends there.", track.Info.Number);
        track.Cursor = track.Track.Table.At(track.Track.Table.Count);
        track.Ended = true;
    }

    /// <summary>Moves the input to <paramref name="position"/>: by seeking, or on a stream that cannot seek by reading forward.</summary>
    private void MoveTo(long position)
    {
        if (_input.Position == position)
        {
            return;
        }

        if (_input.CanSeek)
        {
            _input.Seek(position);
            return;
        }

        if (position < _input.Position || !_input.Skip(position - _input.Position))
        {
            throw new UnsupportedFormatException($"The MP4 file's data at {position} lies before the stream's position ({_input.Position}); reading it needs a stream that can seek.");
        }
    }

    private void Warn(string code, string message, int? track = null)
    {
        if (_warned.Add(code + track))
        {
            _context.Warnings?.Invoke(new MediaWarning(code, message, track));
        }
    }

    /// <summary>Reads the header of the box at the input's position, or returns <see langword="false"/> at the end of the file.</summary>
    private bool NextBoxHeader(out IsoBox box)
    {
        box = default;
        long start = _input.Position;
        Span<byte> header = stackalloc byte[IsoBoxReader.MaxHeaderSize];
        int read = _input.Read(header[..8]);
        if (read == 0)
        {
            return false;
        }

        if (read < 8)
        {
            if (header[..read].IndexOfAnyExcept((byte)0) < 0)
            {
                // Zero padding at the end of the file.
                return false;
            }

            throw _input.Corrupt(start, "A box header is cut off at the end of the file.");
        }

        int length = 8;
        if (BinaryPrimitives.ReadUInt32BigEndian(header) == 1)
        {
            length += _input.Read(header.Slice(length, 8));
        }

        if (header[4..8].SequenceEqual("uuid"u8))
        {
            length += _input.Read(header.Slice(length, 16));
        }

        long end = _input.CanSeek ? _input.Length : long.MaxValue;
        box = IsoBoxReader.ReadHeader(header[..length], start, end, _input.FormatName);
        return true;
    }

    /// <summary>Reads a box's contents whole, within <paramref name="limit"/>.</summary>
    private byte[] ReadContent(IsoBox box, long limit, string limitName)
    {
        if (box.ContentLength > limit)
        {
            throw new LimitExceededException(limitName, limit, box.ContentLength);
        }

        if (_input.CanSeek && box.End > _input.Length)
        {
            throw _input.Corrupt(box.Start, $"The \"{box.Type}\" box runs past the end of the file.");
        }

        MoveTo(box.ContentStart);
        byte[] content = new byte[box.ContentLength];
        _input.ReadExactly(content);
        return content;
    }

    private void ReadHeaders()
    {
        bool moov = false, mediaFirst = false, ftyp = false;
        while (!moov || (_hasMvex && _nextBox < 0))
        {
            if (!NextBoxHeader(out IsoBox box))
            {
                break;
            }

            switch (box.Type.ToString())
            {
                case "ftyp" when !ftyp:
                    ftyp = true;
                    ReadFtyp(ReadContent(box, _context.Limits.MaxHeaderBytes, "MaxHeaderBytes"));
                    break;
                case "moov" when !moov:
                    moov = true;
                    _quickTime |= !ftyp;
                    ReadMoov(ReadContent(box, Math.Max(_context.Limits.MaxHeaderBytes, MaxMoovBytes), "MaxHeaderBytes"), box.ContentStart);
                    break;
                case "moof" when moov:
                    _nextBox = box.Start;
                    _pendingBox = box;
                    break;
                case "mdat" when !moov:
                    mediaFirst = true;
                    if (!_input.CanSeek)
                    {
                        throw new UnsupportedFormatException("The MP4 file's moov box comes after its media data; reading it needs a stream that can seek.");
                    }

                    break;
                case "mdat" when moov && _hasMvex && _nextBox < 0:
                    // Media data before the first fragment belongs to moov's own samples; fragments start after it.
                    break;
            }

            if (_nextBox >= 0)
            {
                break;
            }

            if (box.End == long.MaxValue || (_input.CanSeek && box.End >= _input.Length))
            {
                _endOfBoxes = true;
                break;
            }

            MoveTo(box.End);
        }

        if (!moov)
        {
            throw _input.Corrupt(0, mediaFirst ? "The file has media data but no moov box." : "The file has no moov box.");
        }

        if (_hasMvex && _nextBox < 0 && !_endOfBoxes && _input.CanSeek)
        {
            _nextBox = _input.Position;
        }

        _firstFragment = _nextBox;
        FindTimecode();
        ReadChapterTrack();
        FragmentFrameRates();
    }

    /// <summary>Gives video tracks with no samples in moov the frame rate of their first fragment, when its samples all last as long.</summary>
    private void FragmentFrameRates()
    {
        if (!_hasMvex || _nextBox < 0 || !_input.CanSeek || !_tracks.Any(t => t.Info is VideoTrackInfo { FrameRate: null } && t.Track.Table.Count == 0))
        {
            return;
        }

        long resume = _input.Position;
        IsoBox? pending = _pendingBox;
        long position = _nextBox;
        List<(TrackState Track, Mp4Sample Sample)> samples = [];
        for (int boxes = 0; boxes < 16 && position < _input.Length; boxes++)
        {
            _input.Seek(position);
            if (!NextBoxHeader(out IsoBox box))
            {
                break;
            }

            if (box.Type.ToString() == "moof")
            {
                ParseMoof(ReadContent(box, _context.Limits.MaxHeaderBytes, "MaxHeaderBytes"), box, samples, []);
                break;
            }

            position = box.End;
        }

        foreach (TrackState track in _tracks)
        {
            // The most common duration, when nearly every sample has it (a writer may give the first sample another).
            List<long> durations = [.. samples.Where(s => s.Track == track).Select(s => s.Sample.Duration)];
            if (durations.Count == 0)
            {
                continue;
            }

            (long Duration, int Count) common = durations.GroupBy(d => d).Select(g => (g.Key, g.Count())).MaxBy(g => g.Item2);
            if (track.Info is VideoTrackInfo { FrameRate: null } video && track.Track.Table.Count == 0 && common.Duration > 0 && common.Count * 4 >= durations.Count * 3)
            {
                track.Info = video with { FrameRate = new Rational(Math.Max(1, track.Track.Timescale), common.Duration) };
            }
        }

        _input.Seek(resume);
        _pendingBox = pending;
    }

    private void ReadFtyp(byte[] content)
    {
        if (content.Length >= 4)
        {
            FourCC major = new(BinaryPrimitives.ReadUInt32BigEndian(content));
            _quickTime = major.ToString() == "qt  ";
            _tags["MAJOR_BRAND"] = major.ToString();
        }
    }

    private void ReadMoov(byte[] content, long offset)
    {
        IsoBoxReader moov = new(content, _input.FormatName, offset);
        List<Mp4Track> tracks = [];
        while (NextInMoov(ref moov, tracks.Count, out IsoBox box))
        {
            switch (box.Type.ToString())
            {
                case "mvhd":
                    BoxFields f = new(moov.Content(box), box);
                    (int version, _) = f.FullBox();
                    f.UVar(version == 1);
                    f.UVar(version == 1);
                    _movieTimescale = f.U32();
                    _movieDuration = f.UVar(version == 1);
                    break;
                case "trak":
                    if (tracks.Count >= _context.Limits.MaxTracks)
                    {
                        throw new LimitExceededException("MaxTracks", _context.Limits.MaxTracks, tracks.Count + 1);
                    }

                    try
                    {
                        tracks.Add(Mp4Track.Read(moov, box, _quickTime, _context.Limits));
                    }
                    catch (CorruptDataException e)
                    {
                        Warn("mp4-bad-track", $"A track's boxes are broken, so it was skipped: {e.Message}");
                    }

                    break;
                case "mvex":
                    _hasMvex = true;
                    ReadMvex(moov, box, tracks);
                    break;
                case "udta":
                    Mp4Metadata.ReadMovie(moov, box, _tags, _chapters, _attachments);
                    break;
                case "meta":
                    Mp4Metadata.ReadMeta(moov, box, _tags, _attachments);
                    break;
            }
        }

        // trex boxes may come before or after the trak boxes they describe.
        if (_hasMvex)
        {
            moov = new IsoBoxReader(content, _input.FormatName, offset);
            while (moov.Next(out IsoBox box))
            {
                if (box.Type.ToString() == "mvex")
                {
                    ReadMvex(moov, box, tracks);
                }
            }
        }

        HashSet<uint> chapterTracks = [.. tracks.SelectMany(t => t.References.GetValueOrDefault(Chap) ?? [])];
        foreach (Mp4Track track in tracks)
        {
            if (track.Id == 0 || track.Id > int.MaxValue || _byId.ContainsKey(track.Id))
            {
                Warn("mp4-track-id", $"A track has the id {track.Id}, which is zero, too large or used twice; it was skipped.");
                continue;
            }

            TrackState state = new(track, this, chapterTracks.Contains(track.Id) && track.Handler != Mp4Track.Vide && track.Handler != Mp4Track.Soun);
            _byId[track.Id] = state;
            if (!state.IsChapters)
            {
                _tracks.Add(state);
            }
        }
    }

    /// <summary>Reads moov's next box; a broken size after some tracks were read ends moov with an mp4-bad-box warning instead.</summary>
    private bool NextInMoov(ref IsoBoxReader moov, int tracks, out IsoBox box)
    {
        try
        {
            return moov.Next(out box);
        }
        catch (CorruptDataException e) when (tracks > 0)
        {
            Warn("mp4-bad-box", $"moov's boxes are broken after {tracks} tracks; the rest of moov was skipped: {e.Message}");
            box = default;
            return false;
        }
    }

    private void ReadMvex(IsoBoxReader moov, IsoBox mvex, List<Mp4Track> tracks)
    {
        IsoBoxReader children = moov.Children(mvex);
        while (children.Next(out IsoBox box))
        {
            BoxFields f = new(children.Content(box), box);
            if (box.Type.ToString() == "trex")
            {
                f.FullBox();
                uint id = f.U32();
                (uint, uint, uint, uint) defaults = (f.U32(), f.U32(), f.U32(), f.U32());
                foreach (Mp4Track track in tracks.Where(t => t.Id == id))
                {
                    track.FragmentDefaults = defaults;
                }
            }
            else if (box.Type.ToString() == "mehd")
            {
                (int version, _) = f.FullBox();
                _fragmentDuration = f.UVar(version == 1);
            }
        }
    }

    /// <summary>Reads the start timecode from the first sample of a timecode (tmcd) track.</summary>
    private void FindTimecode()
    {
        if (!_input.CanSeek)
        {
            return;
        }

        foreach (TrackState state in _byId.Values)
        {
            Mp4Track track = state.Track;
            if (track.Handler != Mp4Track.Tmcd || track.Entries.Count == 0 || track.Entries[0].Type.ToString() != "tmcd" || track.Table.Count == 0)
            {
                continue;
            }

            byte[] entry = track.Entries[0].Box;
            Mp4Sample first = track.Table.Start().Peek();
            if (entry.Length < 8 + 8 + 4 + 4 + 4 + 1 || first.Size < 4 || first.Offset + 4 > _input.Length)
            {
                continue;
            }

            _input.Seek(first.Offset);
            Span<byte> frame = stackalloc byte[4];
            _input.ReadExactly(frame);
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(8 + 8 + 4));
            int fps = entry[8 + 8 + 4 + 4 + 4 + 4];
            _timecode = Timecode(BinaryPrimitives.ReadUInt32BigEndian(frame), fps, (flags & 1) != 0);
            return;
        }
    }

    /// <summary>Writes a frame count as SMPTE timecode; drop-frame counts skip frame numbers 0 and 1 (or 0 to 3 at 60) each minute but every tenth.</summary>
    internal static string? Timecode(uint frame, int fps, bool dropFrame)
    {
        if (fps <= 0)
        {
            return null;
        }

        long f = frame;
        if (dropFrame)
        {
            int drop = (int)Math.Round(fps * 0.066666);
            long perTenMinutes = (fps * 600L) - (9L * drop);
            long perMinute = (fps * 60L) - drop;
            long tens = f / perTenMinutes, rest = f % perTenMinutes;
            f += (9L * drop * tens) + (rest > drop ? drop * ((rest - drop) / perMinute) : 0);
        }

        long frames = f % fps, seconds = f / fps;
        return $"{seconds / 3600 % 24:00}:{seconds / 60 % 60:00}:{seconds % 60:00}{(dropFrame ? ';' : ':')}{frames:00}";
    }

    /// <summary>Reads chapters from a QuickTime chapter track (tref chap), which take the place of Nero chpl ones.</summary>
    private void ReadChapterTrack()
    {
        TrackState? chapters = _byId.Values.FirstOrDefault(t => t.IsChapters);
        if (chapters is null || !_input.CanSeek)
        {
            return;
        }

        Mp4Track track = chapters.Track;
        Rational timeBase = new(1, Math.Max(1, track.Timescale));
        List<ChapterInfo> read = [];
        Mp4SampleTable.Cursor cursor = track.Table.Start();
        for (int i = 0; !cursor.AtEnd && i < 10_000; i++, cursor.Advance())
        {
            Mp4Sample sample = cursor.Peek();
            if (sample.Size > _context.Limits.MaxHeaderBytes || sample.Offset + sample.Size > _input.Length)
            {
                break;
            }

            byte[] bytes = new byte[sample.Size];
            _input.Seek(sample.Offset);
            _input.ReadExactly(bytes);
            long start = sample.DecodeTime + chapters.Shift;
            read.Add(new ChapterInfo(new MediaTime(start, timeBase), new MediaTime(start + sample.Duration, timeBase), Mp4Metadata.ChapterText(bytes) is { Length: > 0 } t ? t : null));
        }

        if (read.Count > 0)
        {
            _chapters.Clear();
            _chapters.AddRange(read);
        }
    }

    /// <summary>A track's packets: its sample cursor, the edit list as a shift and a window, and its place in fragments.</summary>
    private sealed class TrackState
    {
        private readonly bool _quickTime;

        public TrackState(Mp4Track track, Mp4Demuxer demuxer, bool isChapters)
        {
            Track = track;
            IsChapters = isChapters;
            _quickTime = demuxer._quickTime;
            Cursor = track.Table.Start();
            int number = (int)track.Id;
            long held = track.Table.ChunkSampleCount();
            if (track.Table.Count > held)
            {
                demuxer.Warn("mp4-sample-table", $"Track {number}'s tables describe {track.Table.Count} samples, but its chunks hold {held}; the rest were left out.", number);
                track.Table.Count = held;
            }

            ApplyEdits(demuxer);
            long durationTicks = End is { } end ? end - Start : track.MediaDuration;
            MediaTime? duration = durationTicks > 0 && track.Timescale > 0 ? new MediaTime(durationTicks, new Rational(1, track.Timescale)) : null;
            Info = Mp4Codecs.Build(track, number, _quickTime, duration);
            if (Info is AudioTrackInfo && Info.CodecId is CodecIds.PcmLittleEndian or CodecIds.PcmBigEndian or CodecIds.PcmFloat
                && track.Table.ConstantSize > 0 && track.Table.Times.All(t => t.Delta == 1))
            {
                track.Table.GroupChunks = true;
                Cursor = track.Table.Start();
            }

            IsVp8 = Info.CodecId == CodecIds.Vp8;
            if (track.Entries.Count > 1 && track.Entries.Skip(1).Any(e => !e.Box.AsSpan().SequenceEqual(track.Entries[0].Box)))
            {
                demuxer.Warn("mp4-sample-descriptions", $"Track {number} has {track.Entries.Count} different sample descriptions; its track info describes the first.", number);
            }
        }

        public Mp4Track Track { get; }

        public TrackInfo Info { get; set; }

        public bool IsChapters { get; }

        /// <summary>Gets whether keyframes come from the VP8 frame tag, which some writers' sync sample tables get wrong.</summary>
        public bool IsVp8 { get; private set; }

        /// <summary>Gets or sets a Vorbis track's packet clock, made at its first packet.</summary>
        public Audio.Codecs.Vorbis.VorbisPacketClock? Vorbis { get; set; }

        /// <summary>Gets or sets whether the file was cut off inside the track's data, which ends it.</summary>
        public bool Ended { get; set; }

        /// <summary>Gets or sets whether a VP8 track's sync samples were read from its frames.</summary>
        public bool SyncChecked { get; set; }

        public Mp4SampleTable.Cursor Cursor;

        /// <summary>Gets whether an edit list sets <see cref="Shift"/>, <see cref="Start"/> and <see cref="End"/>.</summary>
        public bool EditsApplied { get; private set; }

        /// <summary>Gets the ticks added to media times to give presentation times.</summary>
        public long Shift { get; private set; }

        /// <summary>Gets where the edit's window starts in presentation ticks.</summary>
        public long Start { get; private set; }

        /// <summary>Gets where the edit's window ends in presentation ticks, or <see langword="null"/> when it runs to the end.</summary>
        public long? End { get; private set; }

        /// <summary>Gets or sets the decode time the next fragment's samples start at when it has no tfdt.</summary>
        public long NextDecodeTime { get; set; }

        /// <summary>Gets or sets the number of samples to drop after a fragment seek, before the target's keyframe.</summary>
        public long SkipBefore { get; set; } = long.MinValue;

        /// <summary>
        /// Gets whether a sample is left out: a chapter sample, one before the keyframe a fragment seek starts at, or one the edit
        /// list ends before, as FFmpeg leaves those out of its index.
        /// </summary>
        public bool Skip(Mp4Sample sample, bool fragment = false) => IsChapters || sample.Offset < SkipBefore
            || (!fragment && EditsApplied && End is { } end && sample.DecodeTime + sample.CompositionOffset + Shift >= end);

        public void Describe(EncodedPacket packet, Mp4Sample sample, Mp4Demuxer demuxer, bool fragment)
        {
            Rational timeBase = Info.TimeBase;
            long dts = sample.DecodeTime + Shift;
            long pts = dts + sample.CompositionOffset;
            packet.Timestamp = new MediaTime(pts, timeBase);
            packet.DecodeTimestamp = pts != dts ? new MediaTime(dts, timeBase) : null;
            // In fragments an edit list only shifts the times, as FFmpeg reads them: fragments come after moov's edit was written.
            bool trims = EditsApplied && !fragment;
            long duration = sample.Duration;
            if (Info is AudioTrackInfo { CodecId: CodecIds.Vorbis } vorbis)
            {
                // Writers disagree on Vorbis sample durations (FFmpeg's are one packet late), so count them from the packets. A
                // shorter last one ends the stream early, as Ogg's last granule position does.
                Vorbis ??= Audio.Codecs.Vorbis.VorbisPacketClock.Create(vorbis.CodecPrivate.Span);
                if (Vorbis is not null)
                {
                    long counted = new MediaTime(Vorbis.Next(packet.Data.Span), new Rational(1, vorbis.SampleRate)).Rescale(timeBase).Ticks;
                    bool last = !fragment && sample.Index == Track.Table.Count - 1;
                    duration = last && duration > 0 ? Math.Min(duration, counted) : counted;
                }
            }

            if (trims && End is { } stop && pts < stop && pts + duration > stop)
            {
                // The last packet shows only up to the edit's end.
                duration = stop - pts;
            }

            packet.Duration = duration > 0 ? new MediaTime(duration, timeBase) : null;
            packet.IsKeyframe = IsVp8 && packet.Data.Length > 0 ? (packet.Data.Span[0] & 1) == 0 : sample.IsSync;
            packet.IsDiscardable = trims && sample.Duration > 0 && pts + sample.Duration <= Start;
            if (sample.Description > 1 && sample.Description <= Track.Entries.Count && !Track.Entries[sample.Description - 1].Box.AsSpan().SequenceEqual(Track.Entries[0].Box))
            {
                demuxer.Warn("mp4-sample-descriptions", $"Track {Info.Number} switches to sample description {sample.Description}; packets keep the first one's track info.", Info.Number);
            }
        }

        /// <summary>
        /// Turns the edit list into a shift and a window (V4.1 Design Q5): leading empty edits delay the track, and one edit into the
        /// media sets where it starts and how long it shows. Several media edits, or a rate other than 1, are reported and not applied.
        /// </summary>
        private void ApplyEdits(Mp4Demuxer demuxer)
        {
            Mp4Track track = Track;
            if (demuxer._ignoreEdits || track.Edits.Count == 0 || track.Timescale == 0 || demuxer._movieTimescale == 0)
            {
                return;
            }

            long empty = 0;
            int at = 0;
            while (at < track.Edits.Count && track.Edits[at].MediaTime == -1)
            {
                empty += track.Edits[at].Duration;
                at++;
            }

            List<Mp4Edit> media = [.. track.Edits.Skip(at)];
            long delay = Rescale(empty, demuxer._movieTimescale, track.Timescale);
            if (media.Count == 0)
            {
                Shift = Start = delay;
                EditsApplied = true;
                return;
            }

            Mp4Edit edit = media[0];
            if (media.Count > 1 || edit.Rate != 0x10000)
            {
                demuxer.Warn("mp4-edit-list", $"Track {track.Id} has an edit list of {track.Edits.Count} edits, which is not applied: only leading empty edits and one media edit at rate 1 are.", (int)track.Id);
                return;
            }

            // Video starts at its first frame at or after the edit's media time, as FFmpeg shows it: a frame that starts before the
            // edit is not shown, and the next one starts the track. Audio keeps the exact sample offset.
            long first = edit.MediaTime;
            if (track.Handler == Mp4Track.Vide && track.Table.Count > 0)
            {
                first = FirstPresentationAtOrAfter(track.Table, edit.MediaTime) ?? first;
            }

            Shift = delay - first;
            Start = delay;
            EditsApplied = true;

            // A zero duration means the edit runs to the end of the media (fragmented files write it so).
            End = edit.Duration > 0 ? delay + Rescale(edit.Duration, demuxer._movieTimescale, track.Timescale) : null;
        }

        /// <summary>Finds the earliest presentation time at or after <paramref name="time"/> among the first samples, in media ticks.</summary>
        private static long? FirstPresentationAtOrAfter(Mp4SampleTable table, long time)
        {
            long? best = null;
            Mp4SampleTable.Cursor cursor = table.Start();
            for (int i = 0; !cursor.AtEnd && i < 100_000; i++, cursor.Advance())
            {
                Mp4Sample sample = cursor.Peek();
                if (best is { } b && sample.DecodeTime > b)
                {
                    break;
                }

                long pts = sample.DecodeTime + sample.CompositionOffset;
                if (pts >= time && (best is null || pts < best))
                {
                    best = pts;
                }
            }

            return best;
        }

        private static long Rescale(long value, long from, long to) => (long)((Int128)value * to / from);
    }
}
