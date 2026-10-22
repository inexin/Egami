using System.Buffers;

using Egami.Video.Audio.Codecs.Flac;

namespace Egami.Video.Containers.Ogg;

/// <summary>
/// Reads Ogg (RFC 3533): pages with their CRC checked, packets rebuilt across pages, and timestamps from granule positions
/// (counted back from the first page that ends a packet, then forward packet by packet; Opus timestamps have the pre-skip
/// taken off). A seekable file is scanned once on open for its links, duration and seek index: links in a chain with the
/// same codec settings join into one track, with each link's TITLE as a chapter; a link that changes them ends the track.
/// </summary>
internal sealed class OggDemuxer : IDemuxer
{
    private readonly MediaInput _input;
    private readonly MediaContext _context;
    private readonly OggPage _page = new();
    private readonly Dictionary<uint, LogicalStream> _streams = [];
    private readonly Queue<EncodedPacket> _ready = new();
    private readonly List<Link> _links = [];
    private readonly List<IndexEntry> _index = [];
    private readonly long _dataStart;
    private int _badPages;
    private bool _ended;
    private bool _warnedChain;

    public OggDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        _input = new MediaInput(stream, leaveOpen, OggFormat.Instance.Name);
        _context = context;
        try
        {
            List<TrackInfo> tracks = [];

            // The first link's beginning-of-stream pages come first, then each stream's other header packets.
            while (true)
            {
                if (!_page.Read(_input, ref _badPages))
                {
                    break;
                }

                if (_page.IsBeginning && !_streams.ContainsKey(_page.Serial) && !_streams.Values.Any(s => s.SawData))
                {
                    LogicalStream s = new() { Serial = _page.Serial, Number = _streams.Count + 1 };
                    _streams[_page.Serial] = s;
                }

                Process(_page, opening: true);
                if (_streams.Count > 0 && _streams.Values.All(s => s.HeadersLeft == 0))
                {
                    break;
                }

                if (_streams.Count > context.Limits.MaxTracks)
                {
                    throw new LimitExceededException("MaxTracks", context.Limits.MaxTracks, _streams.Count);
                }
            }

            if (_streams.Count == 0)
            {
                throw _input.Corrupt(0, "No Ogg stream starts in the file.");
            }

            foreach (LogicalStream s in _streams.Values.OrderBy(s => s.Number))
            {
                s.Track ??= new TrackInfo { Number = s.Number, Kind = TrackKind.Other, CodecId = "D_OGG", TimeBase = new Rational(1, 1000) };
                tracks.Add(s.Track);
            }

            Tracks = tracks;
            _dataStart = _page.Position + _page.Length;
            MediaTime? duration = null;
            List<ChapterInfo> chapters = [];
            Dictionary<string, string> tags = new(tracks.OfType<AudioTrackInfo>().FirstOrDefault()?.Tags ?? new Dictionary<string, string>());
            if (_input.CanSeek)
            {
                long resume = _input.Position;
                Scan();
                _input.Seek(resume);
                (duration, chapters) = Summarize();
            }

            Tracks = [.. tracks.Select(t => t is AudioTrackInfo a && duration is { } d ? a with { Duration = d } : t)];
            Info = new MediaInfo { FormatName = OggFormat.Instance.Name, Duration = duration, Tracks = Tracks, Tags = tags, Chapters = chapters };
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
            if (_ready.TryDequeue(out EncodedPacket? packet))
            {
                return packet;
            }

            if (_ended || !_page.Read(_input, ref _badPages))
            {
                Warn();
                return null;
            }

            Process(_page, opening: false);
        }
    }

    public void Seek(MediaTime time, int? trackNumber = null)
    {
        if (!CanSeek)
        {
            throw new NotSupportedException("The stream cannot seek.");
        }

        TrackInfo track = trackNumber is { } n
            ? Tracks.FirstOrDefault(t => t.Number == n) ?? throw new ArgumentException($"Track {n} is not in the file.", nameof(trackNumber))
            : Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio) ?? Tracks[0];
        long target = time.Rescale(track.TimeBase).Ticks;
        long position = _dataStart;
        foreach (IndexEntry entry in _index)
        {
            if (entry.Number == track.Number && entry.End <= target)
            {
                position = entry.NextPage;
            }
            else if (entry.Number == track.Number && entry.End > target)
            {
                break;
            }
        }

        // The link the position falls in: its streams take over, out of step until a page with a granule position.
        Link? link = _links.LastOrDefault(l => l.Start <= position);
        foreach (EncodedPacket p in _ready)
        {
            p.Dispose();
        }

        _ready.Clear();
        _streams.Clear();
        if (link is not null)
        {
            foreach (LogicalStream s in link.Streams.Where(s => s.Usable && s.Track is not null))
            {
                s.Partial.ResetWrittenCount();
                s.Position = null;
                s.SkipContinued = true;
                s.PreviousBlock = 0;
                s.Pending.Clear();
                _streams[s.Serial] = s;
            }
        }

        _ended = false;
        _input.Seek(position);
    }

    public void Dispose()
    {
        foreach (EncodedPacket p in _ready)
        {
            p.Dispose();
        }

        _input.Dispose();
    }

    private void Warn()
    {
        if (_badPages > 0)
        {
            _context.Warnings?.Invoke(new MediaWarning("ogg-crc", $"{_badPages} Ogg pages failed their CRC check and were skipped."));
            _badPages = 0;
        }
    }

    /// <summary>Adds a page's segments to its stream's packets, and assigns times when it ends packets.</summary>
    private void Process(OggPage page, bool opening)
    {
        if (!_streams.TryGetValue(page.Serial, out LogicalStream? s))
        {
            if (!page.IsBeginning || opening)
            {
                return;
            }

            // A new link of a chain: the streams of the last link have ended.
            if (!StartLink(page))
            {
                return;
            }

            s = _streams[page.Serial];
        }

        int offset = 0;
        bool first = true;
        for (int i = 0; i < page.SegmentCount; i++)
        {
            int length = page.Lacing[i];
            if (first && s.SkipContinued)
            {
                // After a seek, the rest of a packet begun on an earlier page is useless.
                if (page.IsContinued)
                {
                    offset += length;
                    if (length < 255)
                    {
                        first = false;
                        s.SkipContinued = false;
                    }

                    continue;
                }

                s.SkipContinued = false;
            }

            first = false;
            if (s.Partial.WrittenCount + length > _context.Limits.MaxPacketBytes)
            {
                throw new LimitExceededException("MaxPacketBytes", _context.Limits.MaxPacketBytes, s.Partial.WrittenCount + length);
            }

            s.Partial.Write(page.Body.AsSpan(offset, length));
            offset += length;
            if (length < 255)
            {
                Complete(s);
            }
        }

        if (page.Granule != -1 && s.HeadersLeft == 0)
        {
            Assign(s, page.Granule, page.IsEnd);
        }

        if (page.IsEnd)
        {
            s.Ended = true;
        }
    }

    private static void Complete(LogicalStream s)
    {
        byte[] packet = s.Partial.WrittenSpan.ToArray();
        s.Partial.ResetWrittenCount();
        if (s.HeadersLeft > 0)
        {
            if (s.Headers.Count == 0)
            {
                s.Codec = OggCodecs.Identify(packet);
                s.HeadersLeft = s.Codec == OggCodec.Unknown || packet.Length < 13 && s.Codec == OggCodec.Flac ? 1 : OggCodecs.HeaderCount(s.Codec, packet);
            }

            s.Headers.Add(packet);
            s.HeadersLeft--;
            if (s.HeadersLeft == 0)
            {
                Dictionary<string, string> tags = [];
                s.Track ??= OggCodecs.Track(s.Codec, s.Number, s.Headers, tags);
                s.Tags = tags;
                if (s.Codec == OggCodec.Flac && s.Track is { } t)
                {
                    s.Flac = FlacMetadata.ReadBlocks(t.CodecPrivate.Span).FirstOrDefault(b => b.Type == FlacBlock.StreamInfo).Body is { } info
                        ? FlacStreamInfo.Read(info)
                        : null;
                }

                if (s.Codec == OggCodec.Opus && Audio.Codecs.Opus.OpusHead.Read(s.Headers[0]) is { } head)
                {
                    s.PreSkip = head.PreSkip;
                }
            }

            return;
        }

        s.SawData = true;
        s.Pending.Add(packet);
    }

    /// <summary>Times the packets a page ended: counted back from its granule position when the stream is not yet in step.</summary>
    private void Assign(LogicalStream s, long granule, bool last)
    {
        if (s.Pending.Count == 0)
        {
            s.Position ??= granule;
            return;
        }

        int count = s.Pending.Count;
        Span<long> durations = count <= 256 ? stackalloc long[count] : new long[count];
        if (s.Codec == OggCodec.Vorbis && s.Vorbis is null && !s.VorbisFailed)
        {
            // Vorbis packet lengths depend on the modes in the setup header and on the block before.
            try
            {
                s.Vorbis = new Audio.Codecs.Vorbis.VorbisDecoder(s.Headers);
            }
            catch (EgamiException)
            {
                s.VorbisFailed = true;
            }
        }

        for (int i = 0; i < count; i++)
        {
            durations[i] = s.Vorbis is { } vorbis
                ? vorbis.PacketSamples(s.Pending[i], ref s.PreviousBlock)
                : OggCodecs.Duration(s.Codec, s.Pending[i], s.Flac);
        }

        long start;
        if (s.Position is { } position)
        {
            start = position;
        }
        else
        {
            start = granule;
            for (int i = 0; i < count; i++)
            {
                start -= durations[i];
            }

            // A stream whose first audio page is also its last was end-trimmed: it starts at 0 (RFC 7845 section 4.4).
            if (last && start < 0)
            {
                start = 0;
            }
        }

        long t = start;
        for (int i = 0; i < count; i++)
        {
            long duration = durations[i];
            if (last && i == count - 1 && t + duration > granule && granule >= t)
            {
                // The last page's granule position ends the stream before the end of its last packet: that much is padding.
                duration = granule - t;
            }

            Emit(s, s.Pending[i], t, duration);
            t += durations[i];
        }

        s.Pending.Clear();
        s.Position = granule;
    }

    private void Emit(LogicalStream s, byte[] data, long granuleStart, long duration)
    {
        TrackInfo track = s.Track!;
        long ticks = granuleStart - s.PreSkip + s.Offset;
        EncodedPacket packet = new(track.Number, data, new MediaTime(ticks, track.TimeBase))
        {
            IsKeyframe = true,
            Duration = duration > 0 || s.Codec is OggCodec.Opus or OggCodec.Flac || s.Vorbis is not null ? new MediaTime(Math.Max(0, duration), track.TimeBase) : null,
        };
        _ready.Enqueue(packet);
        s.End = Math.Max(s.End, ticks + Math.Max(0, duration));
    }

    /// <summary>Starts the next link of a chain; returns false (and stops the track) when its settings differ.</summary>
    private bool StartLink(OggPage page)
    {
        // A beginning-of-stream page after the data started is the next link, even when the last link's end page was lost.

        LogicalStream? previous = _streams.Values.Where(s => s.Track is AudioTrackInfo).MaxBy(s => s.Number);
        LogicalStream next = new() { Serial = page.Serial, Number = previous?.Number ?? 1 };
        int at = 0;
        int length = 0;
        while (at < page.SegmentCount && page.Lacing[at] == 255)
        {
            length += 255;
            at++;
        }

        length += at < page.SegmentCount ? page.Lacing[at] : 0;
        ReadOnlySpan<byte> first = page.Body.AsSpan(0, Math.Min(length, page.BodyLength));
        OggCodec codec = OggCodecs.Identify(first);
        if (previous?.Track is not AudioTrackInfo old || codec != previous.Codec || !SameSettings(codec, previous.Headers[0], first))
        {
            if (!_warnedChain)
            {
                _warnedChain = true;
                _context.Warnings?.Invoke(new MediaWarning("ogg-chained-stream", "A chained Ogg link changes the codec or its settings; the track ends there.", previous?.Number));
            }

            _ended = true;
            return false;
        }

        next.Track = old;
        next.Offset = previous.End;
        _streams.Clear();
        _streams[page.Serial] = next;
        return true;
    }

    private static bool SameSettings(OggCodec codec, ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => codec switch
    {
        // Channel count, input rate aside, and the mapping for Opus; channels and rate for Vorbis; STREAMINFO's format for FLAC.
        OggCodec.Opus => a.Length >= 19 && b.Length >= 19 && a[9] == b[9] && a[18] == b[18],
        OggCodec.Vorbis => a.Length >= 16 && b.Length >= 16 && a[11..16].SequenceEqual(b[11..16]),
        OggCodec.Flac => a.Length >= 31 && b.Length >= 31 && a.Slice(27, 4).SequenceEqual(b.Slice(27, 4)),
        _ => false,
    };

    /// <summary>One pass over the page headers of a seekable file: links, their header packets and an index of granule positions.</summary>
    private void Scan()
    {
        _input.Seek(0);
        OggPage page = new();
        int bad = 0;
        Dictionary<uint, LogicalStream> current = [];
        Link? link = null;
        int number = 0;
        long offset = 0;
        while (page.Read(_input, ref bad))
        {
            if (page.IsBeginning && !current.ContainsKey(page.Serial))
            {
                if (link is null || current.Values.All(s => s.Ended))
                {
                    if (link is not null)
                    {
                        offset = link.Streams.Where(s => s.Track is AudioTrackInfo).Select(s => s.End).DefaultIfEmpty(offset).Max();
                    }

                    link = new Link { Start = page.Position, Offset = offset };
                    _links.Add(link);
                    current.Clear();
                    number = 0;
                }

                LogicalStream s = new() { Serial = page.Serial, Number = ++number, Offset = link.Offset };
                current[page.Serial] = s;
                link.Streams.Add(s);
            }

            if (!current.TryGetValue(page.Serial, out LogicalStream? st))
            {
                continue;
            }

            // Header packets are rebuilt; audio packets only counted.
            if (st.HeadersLeft > 0)
            {
                int o = 0;
                for (int i = 0; i < page.SegmentCount && st.HeadersLeft > 0; i++)
                {
                    st.Partial.Write(page.Body.AsSpan(o, page.Lacing[i]));
                    o += page.Lacing[i];
                    if (page.Lacing[i] < 255)
                    {
                        Complete(st);
                    }
                }

                st.Pending.Clear();
                st.Partial.ResetWrittenCount();
            }

            if (page.Granule != -1 && st.Track is { } track && st.HeadersLeft == 0)
            {
                long end = page.Granule - st.PreSkip + st.Offset;
                st.End = Math.Max(st.End, end);
                _index.Add(new IndexEntry(st.Number, end, page.Position + page.Length));
            }

            if (page.IsEnd)
            {
                st.Ended = true;
            }

            if (_index.Count > _context.Limits.MaxIndexEntries)
            {
                throw new LimitExceededException("MaxIndexEntries", _context.Limits.MaxIndexEntries, _index.Count);
            }
        }

        // The first link's streams, as read on open, keep their state; the scan's copies of them are swapped in for seeking.
        if (_links.Count > 0)
        {
            foreach (LogicalStream s in _links[0].Streams)
            {
                if (_streams.TryGetValue(s.Serial, out LogicalStream? open))
                {
                    s.Track = open.Track;
                    s.Flac = open.Flac;
                    s.PreSkip = open.PreSkip;
                    s.Codec = open.Codec;
                }
            }
        }

        // Links after the first join the first link's track when their settings match.
        for (int i = 1; i < _links.Count; i++)
        {
            foreach (LogicalStream s in _links[i].Streams)
            {
                LogicalStream? match = _links[0].Streams.FirstOrDefault(f => f.Codec == s.Codec && f.Headers.Count > 0 && s.Headers.Count > 0 && SameSettings(s.Codec, f.Headers[0], s.Headers[0]));
                s.Track = match?.Track;
                s.Number = match?.Number ?? 0;
                s.Usable = match is not null;
            }
        }

        _badPages += bad;
    }

    private (MediaTime? Duration, List<ChapterInfo> Chapters) Summarize()
    {
        TrackInfo? audio = Tracks.FirstOrDefault(t => t.Kind == TrackKind.Audio);
        if (audio is null || _links.Count == 0)
        {
            return (null, []);
        }

        List<ChapterInfo> chapters = [];
        long end = 0;
        for (int i = 0; i < _links.Count; i++)
        {
            LogicalStream? s = _links[i].Streams.FirstOrDefault(x => x.Number == audio.Number && (i == 0 || x.Usable));
            if (s is null)
            {
                break;
            }

            end = Math.Max(end, s.End);
            if (_links.Count > 1)
            {
                chapters.Add(new ChapterInfo(new MediaTime(s.Offset, audio.TimeBase), new MediaTime(s.End, audio.TimeBase), OggCodecs.Title(s.Tags)));
            }
        }

        return (end > 0 ? new MediaTime(end, audio.TimeBase) : null, chapters);
    }

    private sealed record IndexEntry(int Number, long End, long NextPage);

    private sealed class Link
    {
        public long Start;
        public long Offset;
        public readonly List<LogicalStream> Streams = [];
    }

    private sealed class LogicalStream
    {
        public uint Serial;
        public int Number;
        public OggCodec Codec;
        public int HeadersLeft = 1;
        public readonly List<byte[]> Headers = [];
        public Dictionary<string, string> Tags = [];
        public TrackInfo? Track;
        public FlacStreamInfo? Flac;
        public Audio.Codecs.Vorbis.VorbisDecoder? Vorbis;
        public bool VorbisFailed;
        public int PreviousBlock;
        public long PreSkip;
        public readonly ArrayBufferWriter<byte> Partial = new();
        public readonly List<byte[]> Pending = [];
        public long? Position;
        public long Offset;
        public long End;
        public bool Ended;
        public bool SkipContinued;
        public bool SawData;
        public bool Usable = true;
    }
}
