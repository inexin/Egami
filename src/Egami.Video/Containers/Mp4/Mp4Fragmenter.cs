using System.Text;

using Egami.Metadata;

namespace Egami.Video.Containers.Mp4;

/// <summary>A sample of the fragment being built. <see cref="Duration"/> is -1 until the next sample of its track comes.</summary>
internal struct FragmentSample
{
    public int Size;
    public long Dts;
    public int Composition;
    public bool Key;
    public bool Discard;
    public long Duration;
}

/// <summary>
/// One track of a fragmented MP4: the samples of the fragment being built and their bytes, and what moov, mfra and the playlists
/// need about it. Times are media ticks: decode times plus <see cref="Offset"/>, which keeps them from going negative.
/// </summary>
internal sealed class FragmentTrack
{
    private long? _lastDelta;
    private long? _lastDuration;
    private bool _any;
    private long _lastDts;

    public FragmentTrack(TrackInfo input, int id)
    {
        Input = input;
        Id = id;
        Output = input with { Number = id };
        Timescale = Mp4Muxer.TimescaleOf(input);
        TimeBase = new Rational(1, Timescale);
    }

    public TrackInfo Input { get; }

    public TrackInfo Output { get; }

    public int Id { get; }

    public uint Timescale { get; }

    public Rational TimeBase { get; }

    /// <summary>Gets the bytes of <see cref="Samples"/>, in order.</summary>
    public SampleBuffer Data { get; } = new();

    /// <summary>Gets the samples held: <see cref="Ready"/> of them with durations, and at most one more waiting for its next.</summary>
    public List<FragmentSample> Samples { get; } = [];

    public int Ready { get; private set; }

    public long ReadyBytes { get; private set; }

    /// <summary>Gets the ticks added to decode times, set from the first packet so that none is negative.</summary>
    public long Offset { get; private set; }

    /// <summary>Gets the earliest presentation time seen, in media ticks.</summary>
    public long FirstPts { get; private set; } = long.MaxValue;

    /// <summary>Gets the earliest presentation time of a sample that is not discardable, in media ticks.</summary>
    public long ShownStart { get; private set; } = long.MaxValue;

    /// <summary>Gets where the last sample that is not discardable ends, in media ticks.</summary>
    public long ShownEnd { get; private set; } = long.MinValue;

    /// <summary>Gets each fragment's first sync sample for mfra: its presentation time, its moof's offset, its traf and its number.</summary>
    public List<(long Time, long Moof, int Traf, int Sample)> Index { get; } = [];

    /// <summary>Gets each written fragment's decode time (its tfdt) in media ticks, its samples' total duration, and its moof and mdat's size.</summary>
    public List<(long Start, long Duration, long Bytes)> Fragments { get; } = [];

    /// <summary>Gets or sets the empty edit written in moov, in media ticks (0 for none).</summary>
    public long EditDelay { get; set; }

    /// <summary>Gets or sets whether moov has an edit list for the track.</summary>
    public bool HasEdit { get; set; }

    /// <summary>Gets or sets where the tkhd duration and the media edit's duration sit in moov, to write them back; -1 for none.</summary>
    public (long Tkhd, long Elst) Patches { get; set; } = (-1, -1);

    /// <summary>Gets or sets the number of the next moof written for the track, when each track has its own (segments).</summary>
    public uint Sequence { get; set; } = 1;

    /// <summary>Gets whether a packet was added.</summary>
    public bool Started => _any;

    /// <summary>Adds a packet; returns its decode time in media ticks.</summary>
    public long Add(EncodedPacket packet, Action<MediaWarning>? warnings)
    {
        long pts = packet.Timestamp.Rescale(TimeBase).Ticks;
        long dts = (packet.DecodeTimestamp ?? packet.Timestamp).Rescale(TimeBase).Ticks;
        if (!_any)
        {
            Offset = Math.Max(0, -dts);
        }

        long media = dts + Offset;
        if (_any && media < _lastDts)
        {
            warnings?.Invoke(new MediaWarning("mp4-timestamps", $"Track {Id} has a packet that decodes before the one before it; it was moved after it.", Id));
            media = _lastDts;
        }
        else if (media < 0)
        {
            warnings?.Invoke(new MediaWarning("mp4-timestamps", $"Track {Id} has a packet that decodes before 0 after the first; it was moved to 0.", Id));
            media = 0;
        }

        if (Samples.Count > Ready)
        {
            Complete(media - _lastDts);
        }

        int composition = (int)Math.Clamp(pts - dts, int.MinValue, int.MaxValue);
        Samples.Add(new FragmentSample { Size = packet.Data.Length, Dts = media, Composition = composition, Key = packet.IsKeyframe, Discard = packet.IsDiscardable, Duration = -1 });
        Data.Write(packet.Data.Span);
        long shown = media + composition;
        FirstPts = Math.Min(FirstPts, shown);
        if (!packet.IsDiscardable)
        {
            ShownStart = Math.Min(ShownStart, shown);
        }

        long duration = packet.Duration?.Rescale(TimeBase).Ticks ?? 0;
        _lastDuration = duration > 0 ? duration : null;
        _lastDts = media;
        _any = true;
        return media;
    }

    /// <summary>Gives the last sample its duration from the packet's own, or the one before's, as flat MP4 does.</summary>
    public void End()
    {
        if (Samples.Count > Ready)
        {
            Complete(_lastDuration ?? _lastDelta ?? 1);
        }
    }

    /// <summary>Drops the ready samples, which were written, and keeps the one still waiting.</summary>
    public void Consume()
    {
        Samples.RemoveRange(0, Ready);
        Data.Consume(ReadyBytes);
        Ready = 0;
        ReadyBytes = 0;
    }

    /// <summary>Gets the duration of what the track shows, in the movie's timescale: up to the end of its last shown sample.</summary>
    public long MovieDuration(uint movieTimescale) => ShownEnd == long.MinValue ? 0 : Movie(Math.Max(0, ShownEnd - Offset), movieTimescale);

    /// <summary>Converts ticks to the movie timescale, rounding up so an edit never ends before the last sample it shows.</summary>
    public long Movie(long ticks, uint movieTimescale) => (long)(((Int128)ticks * movieTimescale + Timescale - 1) / Timescale);

    private void Complete(long duration)
    {
        FragmentSample sample = Samples[Ready];
        sample.Duration = Math.Max(0, duration);
        Samples[Ready] = sample;
        Ready++;
        ReadyBytes += sample.Size;
        _lastDelta = sample.Duration;
        if (!sample.Discard)
        {
            ShownEnd = Math.Max(ShownEnd, sample.Dts + sample.Composition + Math.Max(sample.Duration, 1));
        }
    }
}

/// <summary>
/// Gathers packets into fragments (V4.3 Design Q3): a fragment closes before the first keyframe of the reference track (the
/// first video track, else the first track) that comes <c>duration</c> after the fragment's start, or once it holds
/// <c>maxBytes</c>. A sample is written once its track's next sample gives its duration, so a fragment may end a sample short
/// on the other tracks; that sample starts the next one. The first fragment also waits for a packet of every track, up to 32 MB,
/// since moov goes before it and each track's first decode time sets its edit list.
/// </summary>
internal sealed class Mp4Fragmenter
{
    private const long FirstFragmentBytes = 32 << 20;

    private readonly Dictionary<int, FragmentTrack> _byInput = [];
    private readonly long _duration;
    private readonly long _maxBytes;
    private readonly bool _video;
    private readonly Action<MediaWarning>? _warnings;
    private readonly Action _flush;
    private long _pending;
    private long _start;
    private bool _hasStart;
    private bool _allStarted;

    public Mp4Fragmenter(IReadOnlyList<TrackInfo> tracks, TimeSpan duration, long maxBytes, Action<MediaWarning>? warnings, Action flush)
    {
        foreach (TrackInfo track in tracks)
        {
            if (_byInput.ContainsKey(track.Number))
            {
                throw new ArgumentException($"Track number {track.Number} appears twice.", nameof(tracks));
            }

            FragmentTrack writer = new(track, Tracks.Count + 1);
            Tracks.Add(writer);
            _byInput[track.Number] = writer;
        }

        Reference = Tracks.FirstOrDefault(t => t.Input.Kind == TrackKind.Video) ?? Tracks[0];
        _video = Reference.Input.Kind == TrackKind.Video;
        _duration = Math.Max(1, MediaTime.FromTimeSpan(duration).Rescale(Reference.TimeBase).Ticks);
        _maxBytes = maxBytes;
        _warnings = warnings;
        _flush = flush;
    }

    public List<FragmentTrack> Tracks { get; } = [];

    public FragmentTrack Reference { get; }

    public bool TryGet(int number, out FragmentTrack track) => _byInput.TryGetValue(number, out track!);

    public void Add(FragmentTrack track, EncodedPacket packet)
    {
        if (_hasStart && track == Reference && (packet.IsKeyframe || !_video) && AllStarted(track))
        {
            long dts = (packet.DecodeTimestamp ?? packet.Timestamp).Rescale(track.TimeBase).Ticks + track.Offset;
            if (dts - _start >= _duration)
            {
                // The keyframe waits for its duration, so the cut leaves it to start the next fragment.
                track.Add(packet, _warnings);
                Cut();
                return;
            }
        }

        long media = track.Add(packet, _warnings);
        _pending += packet.Data.Length;
        if (!_hasStart && track == Reference)
        {
            _start = media;
            _hasStart = true;
        }

        if (_pending >= _maxBytes || (!_allStarted && _pending >= FirstFragmentBytes))
        {
            Cut();
        }
    }

    /// <summary>Gets whether every track has had a packet, counting the one <paramref name="adding"/> is getting.</summary>
    private bool AllStarted(FragmentTrack adding)
    {
        if (!_allStarted)
        {
            _allStarted = Tracks.All(t => t.Started || t == adding);
        }

        return _allStarted;
    }

    /// <summary>Gives the tracks' sample buffers back to the pool.</summary>
    public void Release()
    {
        foreach (FragmentTrack track in Tracks)
        {
            track.Data.Dispose();
        }
    }

    /// <summary>Ends every track and writes what is left.</summary>
    public void Finish()
    {
        foreach (FragmentTrack track in Tracks)
        {
            track.End();
        }

        Cut();
    }

    /// <summary>Writes the ready samples as a fragment; the samples still waiting for their durations start the next.</summary>
    private void Cut()
    {
        if (Tracks.Any(t => t.Ready > 0))
        {
            _flush();
            foreach (FragmentTrack t in Tracks)
            {
                t.Consume();
            }
        }

        _pending = 0;
        foreach (FragmentTrack t in Tracks)
        {
            _pending += t.Data.Length;
        }

        _hasStart = Reference.Samples.Count > 0;
        _start = _hasStart ? Reference.Samples[0].Dts : 0;
    }
}

/// <summary>The boxes of fragmented MP4 (ISO/IEC 14496-12 8.8): moov with mvex, moof with a traf a track, mdat and mfra.</summary>
internal static class Mp4FragmentBoxes
{
    public const uint MovieTimescale = 1000;

    private const uint SyncFlags = 0x02000000; // sample_depends_on 2: depends on no other sample
    private const uint NonSyncFlags = 0x01010000; // sample_depends_on 1 and sample_is_non_sync_sample

    /// <summary>Writes ftyp, or styp for a media segment: iso6 and mp41, av01 for AV1, cmfc (or cmfs in styp) for CMAF.</summary>
    public static void Ftyp(BoxWriter w, IEnumerable<FragmentTrack> tracks, bool cmaf, bool segment = false)
    {
        w.Start(segment ? "styp" : "ftyp");
        w.Type(segment ? "msdh" : "iso6");
        w.U32(0);
        if (segment)
        {
            w.Type("msdh");
            w.Type("msix");
        }

        w.Type("iso6");
        if (tracks.Any(t => t.Input.CodecId == CodecIds.Av1))
        {
            w.Type("av01");
        }

        if (cmaf)
        {
            w.Type(segment ? "cmfs" : "cmfc");
        }

        w.Type("mp41");
        w.End();
    }

    /// <summary>
    /// Writes moov for fragments: the tracks' setup with empty sample tables, edit lists from the samples held so far, mvex with a
    /// trex a track, and mehd when <paramref name="mehd"/> (its value is written back at the end). Returns where mvhd's duration
    /// and mehd's sit in the box, to write them back; -1 for no mehd.
    /// </summary>
    public static (long Mvhd, long Mehd) Moov(BoxWriter w, IReadOnlyList<FragmentTrack> tracks, MediaMetadata? metadata, bool mehd)
    {
        long start = w.Length;
        w.Start("moov");
        w.StartFull("mvhd", 0, 0);
        w.U32(0);
        w.U32(0);
        w.U32(MovieTimescale);
        long mvhd = w.Length - start;
        w.U32(0);
        w.U32(0x00010000); // rate
        w.U16(0x0100); // volume
        w.Zeros(10);
        Mp4Muxer.Matrix(w, ImageOrientation.Normal, 0, 0);
        w.Zeros(24);
        w.U32((uint)(tracks.Max(t => t.Id) + 1));
        w.End();
        foreach (FragmentTrack track in tracks)
        {
            Trak(w, track, start);
        }

        w.Start("mvex");
        long mehdAt = -1;
        if (mehd)
        {
            w.StartFull("mehd", 1, 0);
            mehdAt = w.Length - start;
            w.U64(0);
            w.End();
        }

        foreach (FragmentTrack track in tracks)
        {
            w.StartFull("trex", 0, 0);
            w.U32((uint)track.Id);
            w.U32(1); // sample description
            w.U32(0); // duration, size and flags: every traf gives its own
            w.U32(0);
            w.U32(0);
            w.End();
        }

        w.End();
        if (metadata is not null)
        {
            Mp4Muxer.Udta(w, metadata);
        }

        w.End();
        return (mvhd, mehdAt);
    }

    private static void Trak(BoxWriter w, FragmentTrack track, long moovStart)
    {
        TrackInfo input = track.Input;
        VideoTrackInfo? video = input as VideoTrackInfo;

        // An edit list only when the times need one: the media starts after an offset that kept decode times from going
        // negative, or leading samples are discardable, which a media edit that starts later cuts (as flat MP4 does).
        bool leading = track.ShownStart != long.MaxValue && track.ShownStart > track.FirstPts;
        long mediaTime = track.Offset;
        if (leading)
        {
            long shown = track.ShownStart - track.Offset;
            track.EditDelay = Math.Max(0, shown);
            mediaTime = Math.Max(0, shown) + track.Offset;
        }

        track.HasEdit = track.Offset > 0 || leading;
        w.Start("trak");
        w.StartFull("tkhd", 0, input.IsDefault ? 3u : 2u);
        w.U32(0);
        w.U32(0);
        w.U32((uint)track.Id);
        w.U32(0);
        long tkhd = w.Length - moovStart;
        w.U32(0);
        w.Zeros(8);
        w.U16(0); // layer
        w.U16(0); // alternate group
        w.U16(input.Kind == TrackKind.Audio ? 0x0100 : 0);
        w.U16(0);
        Mp4Muxer.Matrix(w, video?.Orientation ?? ImageOrientation.Normal, video?.Width ?? 0, video?.Height ?? 0);
        double aspect = video is null ? 1 : (double)video.PixelAspectRatio.Numerator / Math.Max(1, video.PixelAspectRatio.Denominator);
        w.U32(video is null ? 0 : (uint)Math.Clamp(Math.Round(video.Width * aspect * 65536), 0, uint.MaxValue));
        w.U32(video is null ? 0 : (uint)video.Height << 16);
        w.End();
        long elst = -1;
        if (track.HasEdit)
        {
            w.Start("edts");
            w.StartFull("elst", 0, 0);
            w.U32(track.EditDelay > 0 ? 2u : 1u);
            if (track.EditDelay > 0)
            {
                w.U32((uint)Math.Min((long)Math.Round((double)track.EditDelay * MovieTimescale / track.Timescale), uint.MaxValue));
                w.I32(-1);
                w.U32(0x10000);
            }

            // A zero duration runs the edit to the end of the fragments; on a seekable output the real one is written back.
            elst = w.Length - moovStart;
            w.U32(0);
            w.I32((int)Math.Clamp(mediaTime, 0, int.MaxValue));
            w.U32(0x10000);
            w.End();
            w.End();
        }

        track.Patches = (tkhd, elst);
        w.Start("mdia");
        w.StartFull("mdhd", 0, 0);
        w.U32(0);
        w.U32(0);
        w.U32(track.Timescale);
        w.U32(0);
        w.U16(Mp4Muxer.PackLanguage(input.Language));
        w.U16(0);
        w.End();
        string handler = Mp4Muxer.TrackHandler(w, input);
        w.Start("minf");
        Mp4Muxer.MediaHeader(w, handler);
        Mp4Muxer.Dinf(w);
        w.Start("stbl");
        w.StartFull("stsd", 0, 0);
        w.U32(1);
        Mp4SampleEntries.Write(w, input);
        w.End();
        foreach (string empty in new[] { "stts", "stsc", "stco" })
        {
            w.StartFull(empty, 0, 0);
            w.U32(0);
            w.End();
        }

        w.StartFull("stsz", 0, 0);
        w.U32(0);
        w.U32(0);
        w.End();
        w.End();
        w.End();
        w.End();
        if (input.Name is { } name)
        {
            w.Start("udta");
            w.Start("name");
            w.Bytes(Encoding.UTF8.GetBytes(name));
            w.End();
            w.End();
        }

        w.End();
    }

    /// <summary>
    /// Writes moof and the mdat header for the ready samples of <paramref name="tracks"/>, a traf each in that order; the samples'
    /// bytes follow it, a track after the other. Notes each track's first sync sample for mfra when <paramref name="moofAt"/>
    /// is not negative, and the fragment's start, duration and size. Returns how many bytes the fragment takes.
    /// </summary>
    public static long Moof(BoxWriter w, uint sequence, IReadOnlyList<FragmentTrack> tracks, long moofAt, List<long> offsets)
    {
        long media = 0;
        foreach (FragmentTrack t in tracks)
        {
            media += t.ReadyBytes;
        }

        int header = media + 8 > uint.MaxValue ? 16 : 8;
        long start = w.Length;
        offsets.Clear();
        w.Start("moof");
        w.StartFull("mfhd", 0, 0);
        w.U32(sequence);
        w.End();
        int traf = 0;
        foreach (FragmentTrack track in tracks)
        {
            if (track.Ready > 0)
            {
                traf++;
                Traf(w, track, offsets, moofAt, traf);
            }
        }

        w.End();
        long moofSize = w.Length - start;
        long offset = moofSize + header;
        int at = 0;
        foreach (FragmentTrack track in tracks)
        {
            if (track.Ready > 0)
            {
                if (offset > int.MaxValue)
                {
                    throw new NotSupportedException("A fragment holds more than 2 GB of media; set a smaller Mp4FragmentOptions.MaxBytes.");
                }

                w.Patch(offsets[at++], (uint)offset);
                offset += track.ReadyBytes;
            }
        }

        if (header == 8)
        {
            w.U32((uint)(media + 8));
            w.Type("mdat");
        }
        else
        {
            w.U32(1);
            w.Type("mdat");
            w.U64((ulong)(media + 16));
        }

        long total = moofSize + header + media;
        foreach (FragmentTrack track in tracks)
        {
            if (track.Ready > 0)
            {
                long duration = 0;
                for (int i = 0; i < track.Ready; i++)
                {
                    duration += track.Samples[i].Duration;
                }

                track.Fragments.Add((track.Samples[0].Dts, duration, total));
            }
        }

        return total;
    }

    /// <summary>Writes the ready samples' bytes, a track after the other, after <see cref="Moof"/>.</summary>
    public static void Mdat(Stream stream, IReadOnlyList<FragmentTrack> tracks)
    {
        foreach (FragmentTrack track in tracks)
        {
            track.Data.WriteTo(stream, track.ReadyBytes);
        }
    }

    private static void Traf(BoxWriter w, FragmentTrack track, List<long> offsets, long moofAt, int traf)
    {
        List<FragmentSample> samples = track.Samples;
        int count = track.Ready;
        FragmentSample first = samples[0];
        bool sameDuration = true, sameSize = true, compositions = false, negative = false, restKey = true, restNonKey = true;
        int firstKey = -1;
        for (int i = 0; i < count; i++)
        {
            FragmentSample s = samples[i];
            sameDuration &= s.Duration == first.Duration;
            sameSize &= s.Size == first.Size;
            compositions |= s.Composition != 0;
            negative |= s.Composition < 0;
            if (i > 0)
            {
                restKey &= s.Key;
                restNonKey &= !s.Key;
            }

            if (s.Key && firstKey < 0)
            {
                firstKey = i;
            }
        }

        sameDuration &= first.Duration <= uint.MaxValue;

        // Sample flags: all sync, or one sync sample first and the rest not (a group of pictures), go in the defaults.
        uint? defaults = restKey && first.Key ? SyncFlags : restNonKey ? NonSyncFlags : null;
        bool firstFlags = defaults == NonSyncFlags && first.Key;
        uint tfhd = 0x20000 | (sameDuration ? 0x8u : 0) | (sameSize ? 0x10u : 0) | (defaults is not null ? 0x20u : 0);
        w.Start("traf");
        w.StartFull("tfhd", 0, tfhd);
        w.U32((uint)track.Id);
        if (sameDuration)
        {
            w.U32((uint)first.Duration);
        }

        if (sameSize)
        {
            w.U32((uint)first.Size);
        }

        if (defaults is { } d)
        {
            w.U32(d);
        }

        w.End();
        w.StartFull("tfdt", 1, 0);
        w.U64((ulong)first.Dts);
        w.End();
        uint trun = 0x1 | (firstFlags ? 0x4u : 0) | (sameDuration ? 0 : 0x100u) | (sameSize ? 0 : 0x200u) | (defaults is null ? 0x400u : 0) | (compositions ? 0x800u : 0);
        w.StartFull("trun", negative ? 1 : 0, trun);
        w.U32((uint)count);
        offsets.Add(w.Length);
        w.I32(0);
        if (firstFlags)
        {
            w.U32(SyncFlags);
        }

        for (int i = 0; i < count; i++)
        {
            FragmentSample s = samples[i];
            if (!sameDuration)
            {
                w.U32((uint)Math.Min(s.Duration, uint.MaxValue));
            }

            if (!sameSize)
            {
                w.U32((uint)s.Size);
            }

            if (defaults is null)
            {
                w.U32(s.Key ? SyncFlags : NonSyncFlags);
            }

            if (compositions)
            {
                w.I32(s.Composition);
            }
        }

        w.End();
        w.End();
        if (moofAt >= 0 && firstKey >= 0)
        {
            FragmentSample key = samples[firstKey];
            track.Index.Add((key.Dts + key.Composition, moofAt, traf, firstKey + 1));
        }
    }

    /// <summary>Writes mfra: a tfra a track listing each fragment's first sync sample, then mfro with mfra's size.</summary>
    public static void Mfra(BoxWriter w, IReadOnlyList<FragmentTrack> tracks)
    {
        long start = w.Length;
        w.Start("mfra");
        foreach (FragmentTrack track in tracks)
        {
            if (track.Index.Count == 0)
            {
                continue;
            }

            w.StartFull("tfra", 1, 0);
            w.U32((uint)track.Id);
            w.U32(0x3F); // traf, trun and sample numbers in 4 bytes each
            w.U32((uint)track.Index.Count);
            foreach ((long time, long moof, int traf, int sample) in track.Index)
            {
                w.U64((ulong)Math.Max(0, time));
                w.U64((ulong)moof);
                w.U32((uint)traf);
                w.U32(1);
                w.U32((uint)sample);
            }

            w.End();
        }

        w.StartFull("mfro", 0, 0);
        w.U32((uint)(w.Length - start + 4));
        w.End();
        w.End();
    }
}
