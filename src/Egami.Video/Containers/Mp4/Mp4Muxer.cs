using System.Text;

using Egami.Metadata;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// Writes ISO MP4 (V4.1 Design Q4, Q8, Q11): ftyp, moov and one mdat, each track's samples in chunks of about half a second,
/// interleaved by time. By default moov goes before mdat: on a stream that can seek and read, the media is written in place and
/// moved forward at the end; otherwise it waits in a temporary file. With moov at the end, the media goes straight to the
/// output. Edit lists keep start offsets and codec delays; tags, cover art and chapters (Nero chpl and a QuickTime chapter
/// track) are written.
/// </summary>
internal sealed class Mp4Muxer : IMuxer
{
    private const int HeaderReserve = 16;
    private const int ChunkBytes = 1 << 20;
    private const uint MovieTimescale = 1000;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly bool _moovAtEnd;
    private readonly bool _inPlace;
    private readonly MediaMetadata _metadata;
    private readonly Action<MediaWarning>? _warnings;
    private readonly List<TrackWriter> _tracks = [];
    private readonly Dictionary<int, TrackWriter> _byInput = [];
    private readonly Stream _media;
    private readonly byte[] _ftyp;
    private readonly long _mediaStart;
    private long _mediaLength;
    private bool _finished;

    public Mp4Muxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen, bool moovAtEnd)
    {
        if (!stream.CanWrite)
        {
            throw new NotSupportedException("The output is not writable.");
        }

        if (tracks.Count == 0 || tracks.Count > 1000)
        {
            throw new NotSupportedException("This writer holds 1 to 1000 tracks.");
        }

        if (moovAtEnd && !stream.CanSeek)
        {
            throw new NotSupportedException("Writing moov after the media needs a stream that can seek, to set mdat's size; write moov first instead.");
        }

        foreach (TrackInfo track in tracks)
        {
            if (Mp4SampleEntries.Unsupported(track) is { } reason)
            {
                throw new NotSupportedException(reason + ".");
            }

            if (_byInput.ContainsKey(track.Number))
            {
                throw new ArgumentException($"Track number {track.Number} appears twice.", nameof(tracks));
            }

            TrackWriter writer = new(track, _tracks.Count + 1);
            _tracks.Add(writer);
            _byInput[track.Number] = writer;
        }

        Tracks = [.. _tracks.Select(t => t.Output)];
        _stream = stream;
        _leaveOpen = leaveOpen;
        _moovAtEnd = moovAtEnd;
        _metadata = metadata;
        _warnings = context.Warnings;
        _ftyp = Ftyp(tracks);
        _inPlace = moovAtEnd || (stream.CanSeek && stream.CanRead);
        if (_inPlace)
        {
            _mediaStart = stream.Position + _ftyp.Length + HeaderReserve;
            stream.Write(_ftyp);
            stream.Write(new byte[HeaderReserve]);
            _media = new BufferedStream(stream, 1 << 16);
        }
        else
        {
            _media = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        }

        if (metadata.Attachments.Any(a => !IsCover(a)) || metadata.Chapters.Any(c => c.Language is not null))
        {
            _warnings?.Invoke(new MediaWarning("metadata-dropped", "MP4 holds cover art but no other attachments, and no chapter languages; they are left out."));
        }
    }

    public IReadOnlyList<TrackInfo> Tracks { get; }

    public void WritePacket(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_finished)
        {
            throw new InvalidOperationException("The muxer was finished.");
        }

        if (!_byInput.TryGetValue(packet.TrackNumber, out TrackWriter? track))
        {
            throw new ArgumentException($"Track {packet.TrackNumber} was not given to the muxer.", nameof(packet));
        }

        if (track.Add(packet, _warnings))
        {
            Flush(track);
        }
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        foreach (TrackWriter track in _tracks.Where(t => t.PendingCount > 0).OrderBy(t => t.PendingStart))
        {
            Flush(track);
        }

        ChapterTrack? chapters = _metadata.Chapters.Count > 0 ? WriteChapterSamples() : null;
        _media.Flush();
        long mdatSize = HeaderReserve + _mediaLength;
        long headerAt = _mediaStart - HeaderReserve;
        if (_moovAtEnd)
        {
            byte[] moov = Moov(_mediaStart, chapters);
            _stream.Position = headerAt;
            _stream.Write(MdatHeader(mdatSize));
            _stream.Position = _mediaStart + _mediaLength;
            _stream.Write(moov);
        }
        else
        {
            // moov first: its size moves the media, and the move may need 64-bit chunk offsets, which changes its size again.
            // From a temporary file the output is written from its start.
            long ftypEnd = _inPlace ? headerAt : _ftyp.Length;
            byte[] moov = Moov(ftypEnd + HeaderReserve, chapters);
            for (int i = 0; i < 3; i++)
            {
                byte[] next = Moov(ftypEnd + moov.Length + HeaderReserve, chapters);
                if (next.Length == moov.Length)
                {
                    moov = next;
                    break;
                }

                moov = next;
            }

            if (_inPlace)
            {
                ShiftForward(headerAt, mdatSize, moov.Length);
                _stream.Position = headerAt;
                _stream.Write(moov);
                _stream.Write(MdatHeader(mdatSize));
            }
            else
            {
                _stream.Write(_ftyp);
                _stream.Write(moov);
                _stream.Write(MdatHeader(mdatSize));
                _media.Position = 0;
                _media.CopyTo(_stream);
            }
        }

        _stream.Flush();
    }

    public void Dispose()
    {
        try
        {
            Finish();
        }
        finally
        {
            if (!_inPlace)
            {
                _media.Dispose();
            }

            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private static bool IsCover(AttachmentInfo a) => a.IsCoverArt && a.MediaType is "image/jpeg" or "image/png";

    private void Flush(TrackWriter track)
    {
        track.Chunks.Add((_mediaLength, track.PendingCount));
        _media.Write(track.Pending.GetBuffer().AsSpan(0, (int)track.Pending.Length));
        _mediaLength += track.Pending.Length;
        track.ClearPending();
    }

    /// <summary>Moves <paramref name="length"/> bytes at <paramref name="start"/> forward by <paramref name="by"/>, last block first.</summary>
    private void ShiftForward(long start, long length, long by)
    {
        byte[] block = new byte[1 << 20];
        for (long end = start + length; end > start;)
        {
            int n = (int)Math.Min(block.Length, end - start);
            _stream.Position = end - n;
            _stream.ReadExactly(block, 0, n);
            _stream.Position = end - n + by;
            _stream.Write(block, 0, n);
            end -= n;
        }
    }

    private static byte[] MdatHeader(long size)
    {
        BoxWriter w = new();
        if (size - 8 <= uint.MaxValue)
        {
            // An 8-byte free box fills the reserve, then a 32-bit mdat header.
            w.U32(8);
            w.Type("free");
            w.U32((uint)(size - 8));
            w.Type("mdat");
        }
        else
        {
            w.U32(1);
            w.Type("mdat");
            w.U64((ulong)size);
        }

        return w.ToArray();
    }

    private static byte[] Ftyp(IReadOnlyList<TrackInfo> tracks)
    {
        BoxWriter w = new();
        w.Start("ftyp");
        w.Type("isom");
        w.U32(0x200);
        w.Type("isom");
        w.Type("iso2");
        w.Type("iso6");
        if (tracks.Any(t => t.CodecId == CodecIds.Av1))
        {
            w.Type("av01");
        }

        w.Type("mp41");
        w.End();
        return w.ToArray();
    }

    /// <summary>Writes the chapter titles as samples of a text track, after the media.</summary>
    private ChapterTrack WriteChapterSamples()
    {
        List<ChapterInfo> chapters = [.. _metadata.Chapters.OrderBy(c => c.Start)];
        Rational ms = new(1, 1000);
        ChapterTrack track = new(_tracks.Count + 1);
        long offset = _mediaLength;
        for (int i = 0; i < chapters.Count; i++)
        {
            long start = Math.Max(0, chapters[i].Start.Rescale(ms).Ticks);
            long end = i + 1 < chapters.Count ? chapters[i + 1].Start.Rescale(ms).Ticks : chapters[i].End?.Rescale(ms).Ticks ?? start + 1;
            byte[] title = Encoding.UTF8.GetBytes(chapters[i].Title ?? "");
            int length = Math.Min(title.Length, ushort.MaxValue);
            BoxWriter sample = new();
            sample.U16(length);
            sample.Bytes(title.AsSpan(0, length));
            sample.U32(12); // encd: the text is UTF-8
            sample.Type("encd");
            sample.U32(0x100);
            byte[] bytes = sample.ToArray();
            _media.Write(bytes);
            track.Samples.Add((bytes.Length, Math.Max(1, end - start), start));
            _mediaLength += bytes.Length;
        }

        track.Offset = offset;
        return track;
    }

    private byte[] Moov(long mediaData, ChapterTrack? chapters)
    {
        BoxWriter w = new();
        long movieDuration = _tracks.Count == 0 ? 0 : _tracks.Max(t => t.MovieDuration(MovieTimescale));
        w.Start("moov");
        w.StartFull("mvhd", 0, 0);
        w.U32(0);
        w.U32(0);
        w.U32(MovieTimescale);
        w.U32((uint)Math.Min(movieDuration, uint.MaxValue));
        w.U32(0x00010000); // rate
        w.U16(0x0100); // volume
        w.Zeros(10);
        Matrix(w, ImageOrientation.Normal, 0, 0);
        w.Zeros(24);
        w.U32((uint)(_tracks.Count + (chapters is null ? 1 : 2)));
        w.End();
        foreach (TrackWriter track in _tracks)
        {
            track.WriteTrak(w, mediaData, MovieTimescale, chapters is not null && track == _tracks[0] ? chapters.Id : null);
        }

        chapters?.WriteTrak(w, mediaData, MovieTimescale, movieDuration);
        Udta(w);
        w.End();
        return w.ToArray();
    }

    private void Udta(BoxWriter w)
    {
        const uint Freeform = 0x2D2D2D2D; // "----"
        List<(uint Type, string? Name, string Value)> items = [];
        Dictionary<string, uint> reverse = Mp4Metadata.ItemTags.GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
        foreach ((string name, string value) in _metadata.Tags)
        {
            if (name == "MAJOR_BRAND")
            {
                continue;
            }

            items.Add(reverse.TryGetValue(name, out uint code) ? (code, null, value) : (Freeform, name, value));
        }

        List<AttachmentInfo> covers = [.. _metadata.Attachments.Where(IsCover)];
        if (items.Count == 0 && covers.Count == 0 && _metadata.Chapters.Count == 0)
        {
            return;
        }

        w.Start("udta");
        if (_metadata.Chapters.Count > 0)
        {
            // Nero chapters: start times in 100 ns units and Pascal titles.
            List<ChapterInfo> chapters = [.. _metadata.Chapters.OrderBy(c => c.Start).Take(255)];
            w.StartFull("chpl", 1, 0);
            w.U32(0);
            w.U8(chapters.Count);
            foreach (ChapterInfo chapter in chapters)
            {
                w.U64((ulong)Math.Max(0, chapter.Start.Rescale(new Rational(1, 10_000_000)).Ticks));
                byte[] title = Encoding.UTF8.GetBytes(chapter.Title ?? "");
                int length = Utf8Prefix(title, 255);
                w.U8(length);
                w.Bytes(title.AsSpan(0, length));
            }

            w.End();
        }

        if (items.Count > 0 || covers.Count > 0)
        {
            w.StartFull("meta", 0, 0);
            w.StartFull("hdlr", 0, 0);
            w.U32(0);
            w.Type("mdir");
            w.Type("appl");
            w.Zeros(9);
            w.End();
            w.Start("ilst");
            foreach ((uint type, string? name, string value) in items)
            {
                w.Start(type);
                if (name is not null)
                {
                    w.StartFull("mean", 0, 0);
                    w.Bytes("com.apple.iTunes"u8);
                    w.End();
                    w.StartFull("name", 0, 0);
                    w.Bytes(Encoding.UTF8.GetBytes(name));
                    w.End();
                }

                w.Start("data");
                w.U32(1); // UTF-8
                w.U32(0);
                w.Bytes(Encoding.UTF8.GetBytes(value));
                w.End();
                w.End();
            }

            if (covers.Count > 0)
            {
                w.Start("covr");
                foreach (AttachmentInfo cover in covers)
                {
                    w.Start("data");
                    w.U32(cover.MediaType == "image/png" ? 14u : 13u);
                    w.U32(0);
                    w.Bytes(cover.Data.Span);
                    w.End();
                }

                w.End();
            }

            w.End();
            w.End();
        }

        w.End();
    }

    /// <summary>Gets how many leading bytes of a UTF-8 string fit in <paramref name="max"/> without cutting a character.</summary>
    private static int Utf8Prefix(byte[] text, int max)
    {
        int length = Math.Min(text.Length, max);
        while (length > 0 && length < text.Length && (text[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return length;
    }

    /// <summary>Writes a tkhd matrix that turns the coded picture upright: the reverse of <see cref="Mp4Codecs.OrientationOf"/>.</summary>
    internal static void Matrix(BoxWriter w, ImageOrientation orientation, int width, int height)
    {
        const int One = 0x10000;
        (int a, int b, int c, int d, int x, int y) = orientation switch
        {
            ImageOrientation.FlipHorizontal => (-One, 0, 0, One, width, 0),
            ImageOrientation.Rotate180 => (-One, 0, 0, -One, width, height),
            ImageOrientation.FlipVertical => (One, 0, 0, -One, 0, height),
            ImageOrientation.Transpose => (0, One, One, 0, 0, 0),
            ImageOrientation.Rotate90 => (0, One, -One, 0, height, 0),
            ImageOrientation.Transverse => (0, -One, -One, 0, height, width),
            ImageOrientation.Rotate270 => (0, -One, One, 0, 0, width),
            _ => (One, 0, 0, One, 0, 0),
        };
        w.I32(a);
        w.I32(b);
        w.U32(0);
        w.I32(c);
        w.I32(d);
        w.U32(0);
        w.I32(x << 16);
        w.I32(y << 16);
        w.U32(0x40000000);
    }

    /// <summary>The ISO 639-2/T language packed five bits a letter, as mdhd stores it; "und" when unknown.</summary>
    internal static int PackLanguage(string? language)
    {
        string code = language is { Length: 3 } l && l.All(char.IsAsciiLetterLower) ? l : "und";
        return ((code[0] - 0x60) << 10) | ((code[1] - 0x60) << 5) | (code[2] - 0x60);
    }

    /// <summary>The samples of one track, gathered as packets come, with its chunk being filled.</summary>
    private sealed class TrackWriter
    {
        private readonly List<int> _sizes = [];
        private readonly List<long> _decodeTimes = [];
        private readonly List<int> _compositions = [];
        private readonly List<uint> _sync = [];
        private readonly List<bool> _discard = [];
        private long? _lastDuration;
        private bool _hasCompositions;

        public TrackWriter(TrackInfo input, int id)
        {
            Input = input;
            Id = id;
            Output = input with { Number = id };
            Timescale = input is AudioTrackInfo audio && (audio.SampleRate > 0 || input.CodecId == CodecIds.Opus)
                ? (uint)(input.CodecId == CodecIds.Opus ? 48000 : audio.SampleRate)
                : input.TimeBase.Numerator == 1 && input.TimeBase.Denominator <= uint.MaxValue ? (uint)input.TimeBase.Denominator : 90000;
            TimeBase = new Rational(1, Timescale);
        }

        public TrackInfo Input { get; }

        public TrackInfo Output { get; }

        public int Id { get; }

        public uint Timescale { get; }

        public Rational TimeBase { get; }

        public MemoryStream Pending { get; } = new();

        public int PendingCount { get; private set; }

        public long PendingStart { get; private set; }

        /// <summary>Gets the chunks: where each starts in the media data and how many samples it holds.</summary>
        public List<(long Offset, int Samples)> Chunks { get; } = [];

        /// <summary>Adds a packet to the chunk being filled; returns whether the chunk is full (half a second, or 1 MB).</summary>
        public bool Add(EncodedPacket packet, Action<MediaWarning>? warnings)
        {
            long pts = packet.Timestamp.Rescale(TimeBase).Ticks;
            long dts = (packet.DecodeTimestamp ?? packet.Timestamp).Rescale(TimeBase).Ticks;
            if (_decodeTimes.Count > 0 && dts < _decodeTimes[^1])
            {
                warnings?.Invoke(new MediaWarning("mp4-timestamps", $"Track {Output.Number} has a packet that decodes before the one before it; it was moved after it.", Output.Number));
                dts = _decodeTimes[^1];
            }

            if (_decodeTimes.Count >= int.MaxValue - 1)
            {
                throw new NotSupportedException("A track holds at most 2^31 samples.");
            }

            long composition = Math.Clamp(pts - dts, int.MinValue, int.MaxValue);
            _hasCompositions |= composition != 0;
            _sizes.Add(packet.Data.Length);
            _decodeTimes.Add(dts);
            _compositions.Add((int)composition);
            if (packet.IsKeyframe)
            {
                _sync.Add((uint)_sizes.Count);
            }

            long duration = packet.Duration?.Rescale(TimeBase).Ticks ?? 0;
            _lastDuration = duration > 0 ? duration : null;
            _discard.Add(packet.IsDiscardable);

            if (PendingCount == 0)
            {
                PendingStart = dts;
            }

            Pending.Write(packet.Data.Span);
            PendingCount++;
            return dts - PendingStart >= Timescale / 2 || Pending.Length >= ChunkBytes;
        }

        public void ClearPending()
        {
            Pending.SetLength(0);
            PendingCount = 0;
        }

        private long Duration(int i) => i + 1 < _decodeTimes.Count ? _decodeTimes[i + 1] - _decodeTimes[i]
            : _lastDuration ?? (_decodeTimes.Count > 1 ? _decodeTimes[^1] - _decodeTimes[^2] : 1);

        private long MediaDuration => _decodeTimes.Count == 0 ? 0 : _decodeTimes[^1] - _decodeTimes[0] + Duration(_decodeTimes.Count - 1);

        /// <summary>Gets the presented span: from the first packet shown to the end of the last, in the track's timescale.</summary>
        private (long Start, long End) Shown()
        {
            long start = long.MaxValue, end = long.MinValue;
            for (int i = 0; i < _decodeTimes.Count; i++)
            {
                if (!_discard[i])
                {
                    long pts = _decodeTimes[i] + _compositions[i];
                    start = Math.Min(start, pts);
                    // A sample without a duration still shows: an edit that ended at its start would cut it.
                    end = Math.Max(end, pts + Math.Max(Duration(i), 1));
                }
            }

            return start == long.MaxValue ? (0, 0) : (start, end);
        }

        public long MovieDuration(uint movieTimescale)
        {
            (long start, long end) = Shown();
            long delay = Math.Max(0, start);
            return (long)((Int128)(delay + Math.Max(0, end - Math.Max(start, 0))) * movieTimescale / Timescale);
        }

        public void WriteTrak(BoxWriter w, long mediaData, uint movieTimescale, int? chapterTrack)
        {
            (long shownStart, long shownEnd) = Shown();
            long delay = Math.Max(0, shownStart);
            long movieDuration = (long)((Int128)Math.Max(0, shownEnd - Math.Max(shownStart, 0) + delay) * movieTimescale / Timescale);
            VideoTrackInfo? video = Input as VideoTrackInfo;
            w.Start("trak");
            w.StartFull("tkhd", 0, Input.IsDefault ? 3u : 2u);
            w.U32(0);
            w.U32(0);
            w.U32((uint)Id);
            w.U32(0);
            w.U32((uint)Math.Min(movieDuration, uint.MaxValue));
            w.Zeros(8);
            w.U16(0); // layer
            w.U16(0); // alternate group
            w.U16(Input.Kind == TrackKind.Audio ? 0x0100 : 0);
            w.U16(0);
            Matrix(w, video?.Orientation ?? ImageOrientation.Normal, video?.Width ?? 0, video?.Height ?? 0);
            double aspect = video is null ? 1 : (double)video.PixelAspectRatio.Numerator / Math.Max(1, video.PixelAspectRatio.Denominator);
            w.U32(video is null ? 0 : (uint)Math.Clamp(Math.Round(video.Width * aspect * 65536), 0, uint.MaxValue));
            w.U32(video is null ? 0 : (uint)video.Height << 16);
            w.End();
            if (chapterTrack is { } chap)
            {
                w.Start("tref");
                w.Start("chap");
                w.U32((uint)chap);
                w.End();
                w.End();
            }

            // An edit list when the track starts later than 0 (an empty edit) or its media starts before what it shows.
            long mediaTime = _decodeTimes.Count == 0 ? 0 : (shownStart > 0 ? shownStart : 0) - _decodeTimes[0];
            if (delay > 0 || mediaTime != 0)
            {
                w.Start("edts");
                w.StartFull("elst", 0, 0);
                w.U32(delay > 0 ? 2u : 1u);
                if (delay > 0)
                {
                    w.U32((uint)Math.Min((long)Math.Round((double)delay * movieTimescale / Timescale), uint.MaxValue));
                    w.I32(-1);
                    w.U32(0x10000);
                }

                w.U32(Movie(Math.Max(0, shownEnd - Math.Max(shownStart, 0)), movieTimescale));
                w.I32((int)Math.Clamp(mediaTime, 0, int.MaxValue));
                w.U32(0x10000);
                w.End();
                w.End();
            }

            w.Start("mdia");
            w.StartFull("mdhd", 0, 0);
            w.U32(0);
            w.U32(0);
            w.U32(Timescale);
            w.U32((uint)Math.Min(MediaDuration, uint.MaxValue));
            w.U16(PackLanguage(Input.Language));
            w.U16(0);
            w.End();
            string handler = Mp4SampleEntries.HandlerOf(Input);
            Hdlr(w, handler, Input.Tags.GetValueOrDefault("HANDLER_NAME") ?? handler switch
            {
                "vide" => "VideoHandler",
                "soun" => "SoundHandler",
                "sbtl" => "SubtitleHandler",
                "tmcd" => "TimeCodeHandler",
                _ => "DataHandler",
            });
            w.Start("minf");
            MediaHeader(w, handler);
            Dinf(w);
            w.Start("stbl");
            w.StartFull("stsd", 0, 0);
            w.U32(1);
            Mp4SampleEntries.Write(w, Input);
            w.End();
            WriteTables(w, mediaData);
            w.End();
            w.End();
            w.End();
            if (Input.Name is { } name)
            {
                w.Start("udta");
                w.Start("name");
                w.Bytes(Encoding.UTF8.GetBytes(name));
                w.End();
                w.End();
            }

            w.End();
        }

        /// <summary>Converts ticks to the movie timescale, rounding up so an edit never ends before the last sample it shows.</summary>
        private uint Movie(long ticks, uint movieTimescale) => (uint)Math.Min((long)(((Int128)ticks * movieTimescale + Timescale - 1) / Timescale), uint.MaxValue);

        private void WriteTables(BoxWriter w, long mediaData)
        {
            // stts: runs of equal durations.
            List<(long Count, long Delta)> times = [];
            for (int i = 0; i < _decodeTimes.Count; i++)
            {
                long delta = Math.Clamp(Duration(i), 0, uint.MaxValue);
                if (times.Count > 0 && times[^1].Delta == delta)
                {
                    times[^1] = (times[^1].Count + 1, delta);
                }
                else
                {
                    times.Add((1, delta));
                }
            }

            w.StartFull("stts", 0, 0);
            w.U32((uint)times.Count);
            foreach ((long count, long delta) in times)
            {
                w.U32((uint)count);
                w.U32((uint)delta);
            }

            w.End();
            if (_hasCompositions)
            {
                List<(long Count, int Offset)> runs = [];
                foreach (int c in _compositions)
                {
                    if (runs.Count > 0 && runs[^1].Offset == c)
                    {
                        runs[^1] = (runs[^1].Count + 1, c);
                    }
                    else
                    {
                        runs.Add((1, c));
                    }
                }

                w.StartFull("ctts", _compositions.Any(c => c < 0) ? 1 : 0, 0);
                w.U32((uint)runs.Count);
                foreach ((long count, int offset) in runs)
                {
                    w.U32((uint)count);
                    w.I32(offset);
                }

                w.End();
            }

            if (_sync.Count < _sizes.Count)
            {
                w.StartFull("stss", 0, 0);
                w.U32((uint)_sync.Count);
                foreach (uint s in _sync)
                {
                    w.U32(s);
                }

                w.End();
            }

            // stsc: runs of chunks with the same number of samples.
            List<(int FirstChunk, int Samples)> chunkRuns = [];
            for (int i = 0; i < Chunks.Count; i++)
            {
                if (chunkRuns.Count == 0 || chunkRuns[^1].Samples != Chunks[i].Samples)
                {
                    chunkRuns.Add((i + 1, Chunks[i].Samples));
                }
            }

            w.StartFull("stsc", 0, 0);
            w.U32((uint)chunkRuns.Count);
            foreach ((int first, int samples) in chunkRuns)
            {
                w.U32((uint)first);
                w.U32((uint)samples);
                w.U32(1);
            }

            w.End();
            bool constant = _sizes.Count > 0 && _sizes.All(s => s == _sizes[0]);
            w.StartFull("stsz", 0, 0);
            w.U32(constant ? (uint)_sizes[0] : 0);
            w.U32((uint)_sizes.Count);
            if (!constant)
            {
                foreach (int size in _sizes)
                {
                    w.U32((uint)size);
                }
            }

            w.End();
            bool wide = Chunks.Count > 0 && mediaData + Chunks[^1].Offset > uint.MaxValue;
            w.StartFull(wide ? "co64" : "stco", 0, 0);
            w.U32((uint)Chunks.Count);
            foreach ((long offset, _) in Chunks)
            {
                if (wide)
                {
                    w.U64((ulong)(mediaData + offset));
                }
                else
                {
                    w.U32((uint)(mediaData + offset));
                }
            }

            w.End();
        }
    }

    /// <summary>The QuickTime chapter text track (V4.1 Design Q8): a disabled text track whose samples are the chapter titles.</summary>
    private sealed class ChapterTrack(int id)
    {
        public int Id { get; } = id;

        public long Offset { get; set; }

        public List<(int Size, long Duration, long Start)> Samples { get; } = [];

        public void WriteTrak(BoxWriter w, long mediaData, uint movieTimescale, long movieDuration)
        {
            long first = Samples.Count > 0 ? Samples[0].Start : 0;
            long duration = Samples.Sum(s => s.Duration);
            w.Start("trak");
            w.StartFull("tkhd", 0, 0); // disabled: players list it as chapters, not as a track
            w.U32(0);
            w.U32(0);
            w.U32((uint)Id);
            w.U32(0);
            w.U32((uint)Math.Min(Math.Max(movieDuration, first + duration), uint.MaxValue));
            w.Zeros(8);
            w.Zeros(8);
            Matrix(w, ImageOrientation.Normal, 0, 0);
            w.U32(0);
            w.U32(0);
            w.End();
            if (first > 0)
            {
                w.Start("edts");
                w.StartFull("elst", 0, 0);
                w.U32(2);
                w.U32((uint)first);
                w.I32(-1);
                w.U32(0x10000);
                w.U32((uint)duration);
                w.I32(0);
                w.U32(0x10000);
                w.End();
                w.End();
            }

            w.Start("mdia");
            w.StartFull("mdhd", 0, 0);
            w.U32(0);
            w.U32(0);
            w.U32(movieTimescale);
            w.U32((uint)Math.Min(duration, uint.MaxValue));
            w.U16(PackLanguage(null));
            w.U16(0);
            w.End();
            Hdlr(w, "text", "ChapterHandler");
            w.Start("minf");
            w.StartFull("nmhd", 0, 0);
            w.End();
            Dinf(w);
            w.Start("stbl");
            w.StartFull("stsd", 0, 0);
            w.U32(1);
            w.Start("text");
            w.Zeros(6);
            w.U16(1);
            w.Zeros(51); // display flags, justification, colors, text box, font and face: all defaults
            w.U8(0); // empty font name
            w.End();
            w.End();
            w.StartFull("stts", 0, 0);
            w.U32((uint)Samples.Count);
            foreach ((_, long d, _) in Samples)
            {
                w.U32(1);
                w.U32((uint)Math.Min(d, uint.MaxValue));
            }

            w.End();
            w.StartFull("stsc", 0, 0);
            w.U32(1);
            w.U32(1);
            w.U32((uint)Samples.Count);
            w.U32(1);
            w.End();
            w.StartFull("stsz", 0, 0);
            w.U32(0);
            w.U32((uint)Samples.Count);
            foreach ((int size, _, _) in Samples)
            {
                w.U32((uint)size);
            }

            w.End();
            bool wide = mediaData + Offset > uint.MaxValue;
            w.StartFull(wide ? "co64" : "stco", 0, 0);
            w.U32(1);
            if (wide)
            {
                w.U64((ulong)(mediaData + Offset));
            }
            else
            {
                w.U32((uint)(mediaData + Offset));
            }

            w.End();
            w.End();
            w.End();
            w.End();
            w.End();
        }
    }

    private static void Hdlr(BoxWriter w, string handler, string name)
    {
        w.StartFull("hdlr", 0, 0);
        w.U32(0);
        w.Type(handler);
        w.Zeros(12);
        w.Bytes(Encoding.UTF8.GetBytes(name));
        w.U8(0);
        w.End();
    }

    private static void MediaHeader(BoxWriter w, string handler)
    {
        switch (handler)
        {
            case "vide":
                w.StartFull("vmhd", 0, 1);
                w.Zeros(8);
                break;
            case "soun":
                w.StartFull("smhd", 0, 0);
                w.Zeros(4);
                break;
            case "sbtl":
                w.StartFull("sthd", 0, 0);
                break;
            default:
                w.StartFull("nmhd", 0, 0);
                break;
        }

        w.End();
    }

    private static void Dinf(BoxWriter w)
    {
        w.Start("dinf");
        w.StartFull("dref", 0, 0);
        w.U32(1);
        w.StartFull("url ", 0, 1); // the media is in this file
        w.End();
        w.End();
        w.End();
    }
}
