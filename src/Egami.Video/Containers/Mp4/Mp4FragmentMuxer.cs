using System.Buffers.Binary;

namespace Egami.Video.Containers.Mp4;

/// <summary>
/// Writes fragmented MP4 (V4.3 Design Q1 to Q5): ftyp and moov with mvex, then a moof and mdat pair a fragment as packets come,
/// and mfra at the end, so a stream that cannot seek gets the file as it is made. moov waits for the first fragment, whose
/// samples set the edit lists. On a seekable output the durations (mvhd, tkhd, mehd and the edits') are written back at the
/// end. Tags, cover art and Nero chapters go in moov; the QuickTime chapter track is left out, since it needs its samples in moov.
/// </summary>
internal sealed class Mp4FragmentMuxer : IMuxer
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly bool _seekable;
    private readonly long _origin;
    private readonly MediaMetadata _metadata;
    private readonly Mp4Fragmenter _fragmenter;
    private readonly BoxWriter _boxes = new();
    private readonly List<long> _offsets = [];
    private long _written;
    private long _moovAt = -1;
    private (long Mvhd, long Mehd) _patches;
    private uint _sequence = 1;
    private bool _finished;

    public Mp4FragmentMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen, Mp4FragmentOptions options)
    {
        if (!stream.CanWrite)
        {
            throw new NotSupportedException("The output is not writable.");
        }

        if (tracks.Count == 0 || tracks.Count > 1000)
        {
            throw new NotSupportedException("This writer holds 1 to 1000 tracks.");
        }

        if (options.Duration <= TimeSpan.Zero || options.MaxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A fragment's Duration and MaxBytes must be positive.");
        }

        foreach (TrackInfo track in tracks)
        {
            if (Mp4SampleEntries.Unsupported(track) is { } reason)
            {
                throw new NotSupportedException(reason + ".");
            }
        }

        _fragmenter = new Mp4Fragmenter(tracks, options.Duration, options.MaxBytes, context.Warnings, WriteFragment);
        Tracks = [.. _fragmenter.Tracks.Select(t => t.Output)];
        _stream = stream;
        _leaveOpen = leaveOpen;
        _seekable = stream.CanSeek;
        _origin = _seekable ? stream.Position : 0;
        _metadata = metadata;
        if (metadata.Attachments.Any(a => !Mp4Muxer.IsCover(a)) || metadata.Chapters.Any(c => c.Language is not null))
        {
            context.Warnings?.Invoke(new MediaWarning("metadata-dropped", "MP4 holds cover art but no other attachments, and no chapter languages; they are left out."));
        }

        if (metadata.Chapters.Count > 0)
        {
            context.Warnings?.Invoke(new MediaWarning("mp4-chapter-track", "Fragmented MP4 holds the chapters as Nero chpl only; the QuickTime chapter track is left out."));
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

        if (!_fragmenter.TryGet(packet.TrackNumber, out FragmentTrack? track))
        {
            throw new ArgumentException($"Track {packet.TrackNumber} was not given to the muxer.", nameof(packet));
        }

        _fragmenter.Add(track, packet);
    }

    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _fragmenter.Finish();
        if (_moovAt < 0)
        {
            WriteHeader();
        }

        _boxes.Clear();
        Mp4FragmentBoxes.Mfra(_boxes, _fragmenter.Tracks);
        Write(_boxes);
        if (_seekable)
        {
            WriteDurations();
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
            _fragmenter.Release();
            _boxes.Dispose();
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private void WriteHeader()
    {
        _boxes.Clear();
        Mp4FragmentBoxes.Ftyp(_boxes, _fragmenter.Tracks, cmaf: _fragmenter.Tracks.Count == 1);
        long ftyp = _boxes.Length;
        _patches = Mp4FragmentBoxes.Moov(_boxes, _fragmenter.Tracks, _metadata, mehd: _seekable);
        _moovAt = _written + ftyp;
        Write(_boxes);
    }

    private void WriteFragment()
    {
        if (_moovAt < 0)
        {
            WriteHeader();
        }

        _boxes.Clear();
        Mp4FragmentBoxes.Moof(_boxes, _sequence++, _fragmenter.Tracks, _written, _offsets);
        Write(_boxes);
        Mp4FragmentBoxes.Mdat(_stream, _fragmenter.Tracks);
        foreach (FragmentTrack track in _fragmenter.Tracks)
        {
            _written += track.ReadyBytes;
        }
    }

    private void Write(BoxWriter boxes)
    {
        boxes.WriteTo(_stream);
        _written += boxes.Length;
    }

    /// <summary>Writes the durations into moov, now that they are known.</summary>
    private void WriteDurations()
    {
        long end = _stream.Position;
        long movie = 0;
        foreach (FragmentTrack track in _fragmenter.Tracks)
        {
            long duration = track.MovieDuration(Mp4FragmentBoxes.MovieTimescale);
            movie = Math.Max(movie, duration);
            Patch(track.Patches.Tkhd, (uint)Math.Min(duration, uint.MaxValue));
            if (track.Patches.Elst >= 0)
            {
                long shown = track.ShownEnd == long.MinValue ? 0 : Math.Max(0, track.ShownEnd - track.Offset - track.EditDelay);
                Patch(track.Patches.Elst, (uint)Math.Min(track.Movie(shown, Mp4FragmentBoxes.MovieTimescale), uint.MaxValue));
            }
        }

        Patch(_patches.Mvhd, (uint)Math.Min(movie, uint.MaxValue));
        if (_patches.Mehd >= 0)
        {
            Patch(_patches.Mehd, (uint)(movie >> 32));
            Patch(_patches.Mehd + 4, (uint)movie);
        }

        _stream.Position = end;
    }

    private void Patch(long at, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _stream.Position = _origin + _moovAt + at;
        _stream.Write(b);
    }
}
