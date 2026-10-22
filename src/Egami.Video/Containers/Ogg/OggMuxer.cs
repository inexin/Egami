using System.Buffers;

namespace Egami.Video.Containers.Ogg;

/// <summary>
/// Writes Ogg: each track's identification header alone on its beginning-of-stream page, the other headers on the pages after,
/// then the packets in pages of about 4 KB, each page's granule position the end of the last packet it completes (with the
/// pre-skip added for Opus), and the last page of each stream marked end-of-stream.
/// </summary>
internal sealed class OggMuxer : IMuxer
{
    private const int PageBytes = 4096;

    private readonly OutputStream _out;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly Dictionary<int, Writer> _writers = [];
    private bool _finished;

    public OggMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        if (tracks.Count == 0 || tracks.Any(t => t.Kind != TrackKind.Audio))
        {
            throw new NotSupportedException("Ogg holds audio tracks: Opus, Vorbis and FLAC.");
        }

        _stream = stream;
        _out = new OutputStream(stream);
        _leaveOpen = leaveOpen;
        if (metadata.Chapters.Count > 0 || metadata.Attachments.Count > 0)
        {
            context.Warnings?.Invoke(new MediaWarning("metadata-dropped", "Ogg holds tags but no chapters or attachments; they are left out."));
        }

        List<TrackInfo> numbered = [];
        List<(Writer Writer, List<byte[]> Headers)> starts = [];
        for (int i = 0; i < tracks.Count; i++)
        {
            TrackInfo track = tracks[i];
            Dictionary<string, string> tags = new(metadata.Tags);
            foreach ((string key, string value) in track.Tags)
            {
                tags[key] = value;
            }

            (OggCodec codec, List<byte[]> headers) = OggCodecs.Headers(track, tags);
            long preSkip = codec == OggCodec.Opus && Audio.Codecs.Opus.OpusHead.Read(headers[0]) is { } head ? head.PreSkip : 0;
            // Audio granule positions count samples (Opus's at 48 kHz), whatever the packets' time base.
            Rational timeBase = codec == OggCodec.Opus ? new Rational(1, 48000) : track is AudioTrackInfo { SampleRate: > 0 } audio ? new Rational(1, audio.SampleRate) : track.TimeBase;
            Writer writer = new(0x45474D00u + (uint)i, codec, timeBase, preSkip);
            _writers[track.Number] = writer;
            numbered.Add(track with { Number = i + 1 });
            starts.Add((writer, headers));
        }

        Tracks = numbered;

        // Every stream's first page comes first, then the remaining headers, each stream's on pages of its own.
        foreach ((Writer writer, List<byte[]> headers) in starts)
        {
            writer.Add(headers[0]);
            writer.Flush(_out, granule: 0, OggPage.BeginningOfStream);
        }

        foreach ((Writer writer, List<byte[]> headers) in starts)
        {
            foreach (byte[] header in headers.Skip(1))
            {
                writer.Add(header);
            }

            if (headers.Count > 1)
            {
                writer.Flush(_out, granule: 0, 0);
            }
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

        if (!_writers.TryGetValue(packet.TrackNumber, out Writer? writer))
        {
            throw new ArgumentException($"Track {packet.TrackNumber} was not given to the muxer.", nameof(packet));
        }

        long start = packet.Timestamp.Rescale(writer.TimeBase).Ticks;
        if (writer.Next is { } next && writer.TimeBase.Denominator >= 8000 && Math.Abs(start - next) <= writer.TimeBase.Denominator / (1000 * writer.TimeBase.Numerator))
        {
            // Millisecond timestamps (Matroska's) are rounded: a packet within a millisecond of the last one's end follows it.
            start = next;
        }

        long end = start + (packet.Duration is { } d ? d.Rescale(writer.TimeBase).Ticks : 0);
        writer.Next = end;
        long granule = end + writer.PreSkip;
        if (writer.Pending is { } held)
        {
            // Each packet waits for the next, so the last one can go on the end-of-stream page.
            writer.Add(held.Data);
            writer.Granule = held.Granule;
            if (writer.BodyLength >= PageBytes || writer.SegmentCount > 200)
            {
                writer.Flush(_out, writer.Granule, 0);
            }
        }

        writer.Pending = (packet.Data.ToArray(), Math.Max(granule, writer.Granule));
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        foreach (Writer writer in _writers.Values)
        {
            if (writer.Pending is { } held)
            {
                writer.Add(held.Data);
                writer.Granule = held.Granule;
            }

            writer.Flush(_out, writer.Granule, OggPage.EndOfStream, force: true);
        }

        _out.Flush();
    }

    public void Dispose()
    {
        try
        {
            Finish();
        }
        finally
        {
            _out.Dispose();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    /// <summary>One logical stream's page under construction.</summary>
    private sealed class Writer(uint serial, OggCodec codec, Rational timeBase, long preSkip)
    {
        private readonly OggPage _page = new() { Serial = serial };
        private readonly ArrayBufferWriter<byte> _body = new();
        private readonly List<byte> _lacing = [];
        private uint _sequence;
        private bool _continued;

        public OggCodec Codec { get; } = codec;

        public Rational TimeBase { get; } = timeBase;

        public long PreSkip { get; } = preSkip;

        public long Granule;

        /// <summary>The end of the last packet in granule ticks, without pre-skip.</summary>
        public long? Next;

        public (byte[] Data, long Granule)? Pending;

        public int BodyLength => _body.WrittenCount;

        public int SegmentCount => _lacing.Count;

        public void Add(byte[] packet)
        {
            int length = packet.Length;
            _body.Write(packet);
            while (length >= 255)
            {
                _lacing.Add(255);
                length -= 255;
            }

            _lacing.Add((byte)length);
        }

        /// <summary>Writes the packets added so far on pages of at most 255 segments; the last page gets the flags and granule.</summary>
        public void Flush(Stream output, long granule, byte flags, bool force = false)
        {
            if (_lacing.Count == 0 && !force)
            {
                return;
            }

            byte[] body = _body.WrittenSpan.ToArray();
            int segment = 0;
            int offset = 0;
            do
            {
                int count = Math.Min(255, _lacing.Count - segment);
                int length = 0;
                for (int i = 0; i < count; i++)
                {
                    _page.Lacing[i] = _lacing[segment + i];
                    length += _lacing[segment + i];
                }

                bool lastPage = segment + count == _lacing.Count;

                // A page that ends inside a packet (its last segment 255) completes no packet: granule -1.
                bool endsPacket = count > 0 && _page.Lacing[count - 1] < 255;
                _page.Flags = (byte)((_continued ? OggPage.Continued : 0) | (lastPage ? flags : flags & OggPage.BeginningOfStream));
                _page.Granule = lastPage ? granule : endsPacket ? granule : -1;
                _page.Sequence = _sequence++;
                _page.SegmentCount = count;
                if (_page.Body.Length < length)
                {
                    _page.Body = new byte[length];
                }

                body.AsSpan(offset, length).CopyTo(_page.Body);
                _page.BodyLength = length;
                _page.Write(output);
                _continued = count > 0 && _page.Lacing[count - 1] == 255;
                segment += count;
                offset += length;
                flags = (byte)(flags & ~OggPage.BeginningOfStream);
            }
            while (segment < _lacing.Count);

            _body.ResetWrittenCount();
            _lacing.Clear();
        }
    }
}
