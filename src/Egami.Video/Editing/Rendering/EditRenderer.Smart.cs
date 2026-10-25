using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Trimming;

namespace Egami.Video.Editing;

/// <summary>
/// Smart render (V9 design, Build Plan task 23): the untouched parts of clips are copied packet for packet, from the first
/// keyframe on, when the source's stream matches what the writer's encoder makes; the encoder starts again with a keyframe after
/// each copied part. Built on V5.6's smart-cut stream checks (<see cref="TrimCodec"/>).
/// </summary>
internal sealed partial class EditRenderer
{
    // Sources checked against the encoder: the track to copy, or null when the stream does not match.
    private readonly Dictionary<string, VideoTrackInfo?> _matches = new(StringComparer.Ordinal);

    /// <summary>Gets or sets whether to copy untouched parts of clips (<see cref="EditRenderOptions.SmartRender"/>).</summary>
    public bool Smart { get; init; }

    /// <summary>Gets how many frames were copied rather than encoded.</summary>
    public long CopiedFrames { get; private set; }

    /// <summary>A part of the output copied from a source: output frames <see cref="Start"/> up to <see cref="End"/>.</summary>
    private sealed record CopyRun(long Start, long End, PlannedClip Clip, string Path, VideoTrackInfo Track);

    /// <summary>The parts of the output to copy, in order.</summary>
    private List<CopyRun> PlanCopies(VideoWriter writer)
    {
        List<CopyRun> runs = [];
        IReadOnlyList<PlannedClip> clips = _plan.Clips;
        for (int i = 0; i < clips.Count; i++)
        {
            PlannedClip planned = clips[i];
            Clip c = planned.Clip;
            if (c.Source is not FileClipSource file || c.SpeedFactor != 1 || c.IsReversed || c.LoopCount != 1 || c.HoldDuration != MediaTime.Zero
                || c.Freezes.Count > 0 || c.Motion is not null || c.Stabilization is not null || c.Effects.Count > 0
                || !_plan.Sources.TryGetValue(c, out SourceInfo? info) || info.Size.Width != _canvas.Width || info.Size.Height != _canvas.Height
                || info.FrameRate != _canvas.FrameRate)
            {
                continue;
            }

            if (Match(file.Path, writer) is not { } track)
            {
                continue;
            }

            // The clean part: after the transition in and the fade in, before the fade out and the next transition.
            long next = i + 1 < clips.Count ? clips[i + 1].TransitionFrames : 0;
            long start = planned.StartFrame + Math.Max(planned.TransitionFrames, EditPlanner.Frames(c.FadeInDuration, _canvas));
            long end = planned.StartFrame + planned.FrameCount - Math.Max(next, c.FadeOutDuration > MediaTime.Zero ? EditPlanner.Frames(c.FadeOutDuration, _canvas) : 0);
            List<(long Start, long End)> clean = [(start, end)];
            foreach (PlannedLayer layer in _plan.Layers)
            {
                clean = Without(clean, layer.StartFrame, layer.StartFrame + layer.FrameCount);
            }

            foreach (EditEffect effect in _plan.Edit.Effects)
            {
                long from = EditPlanner.Frames(effect.Start, _canvas);
                long to = effect.End is { } stop ? EditPlanner.Frames(stop, _canvas) : _plan.FrameCount;
                clean = Without(clean, from, to);
            }

            foreach ((long a, long b) in clean)
            {
                if (b > a && FirstKeyframe(planned, file.Path, track, a, b) is { } first && first < b)
                {
                    runs.Add(new CopyRun(first, b, planned, file.Path, track));
                }
            }
        }

        return runs;
    }

    private static List<(long Start, long End)> Without(List<(long Start, long End)> parts, long from, long to)
    {
        List<(long, long)> result = [];
        foreach ((long a, long b) in parts)
        {
            if (to <= a || from >= b)
            {
                result.Add((a, b));
                continue;
            }

            if (from > a)
            {
                result.Add((a, from));
            }

            if (to < b)
            {
                result.Add((to, b));
            }
        }

        return result;
    }

    /// <summary>
    /// Whether a source's video can be copied into this output: the same codec as the writer's, and the stream parameters of its
    /// first keyframe the same as those of a keyframe the writer's encoder makes at the canvas size.
    /// </summary>
    private VideoTrackInfo? Match(string path, VideoWriter writer)
    {
        if (_matches.TryGetValue(path, out VideoTrackInfo? known))
        {
            return known;
        }

        VideoTrackInfo? match = null;
        string? why = null;
        try
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(path, _context);
            VideoTrackInfo? track = demuxer.Tracks.OfType<VideoTrackInfo>().FirstOrDefault();
            using IVideoEncoder encoder = writer.CreateEncoder(_canvas.Width, _canvas.Height);
            if (track is null || track.CodecId != encoder.Track.CodecId || track.CodecId is not (CodecIds.Vp8 or CodecIds.Vp9 or CodecIds.Av1 or CodecIds.MotionJpeg))
            {
                why = $"its codec is not the one the edit encodes with ({writer.Codec.Name})";
            }
            else
            {
                TrimCodec codec = new(track);
                string? theirs = null;
                while (demuxer.ReadPacket() is { } packet)
                {
                    using (packet)
                    {
                        if (packet.TrackNumber == track.Number && codec.IsKeyframe(packet))
                        {
                            theirs = codec.StreamParameters(packet.Data);
                            break;
                        }
                    }
                }

                using VideoFrame gray = new BlankFrame(_canvas.Width, _canvas.Height);
                encoder.Send(gray);
                encoder.Flush();
                string? ours = null;
                while (encoder.TryReceive(out EncodedPacket? packet))
                {
                    using (packet)
                    {
                        ours ??= codec.StreamParameters(packet!.Data);
                    }
                }

                if (theirs == ours)
                {
                    match = track;
                }
                else
                {
                    why = "its stream parameters (profile, bit depth, chroma, color or sequence header) differ from what the encoder writes";
                }
            }
        }
        catch (Exception error) when (error is IOException or CorruptDataException or NotSupportedException or UnsupportedFormatException)
        {
            why = error.Message;
        }

        if (why is not null)
        {
            _context.Warnings?.Invoke(new MediaWarning("smart-render", $"Smart render encodes {System.IO.Path.GetFileName(path)} again: {why}."));
        }

        _matches[path] = match;
        return match;
    }

    /// <summary>The output frame of the first source keyframe at or after output frame <paramref name="from"/> and before <paramref name="to"/>.</summary>
    private long? FirstKeyframe(PlannedClip planned, string path, VideoTrackInfo track, long from, long to)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path, _context);
        TrimCodec codec = new(track);
        demuxer.Seek(SourceTime(planned, from), track.Number);
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber != track.Number || !codec.ShowsFrame(packet))
                {
                    continue;
                }

                long n = OutputFrame(planned, packet.Timestamp);
                if (n >= to)
                {
                    return null;
                }

                if (n >= from && codec.IsKeyframe(packet))
                {
                    return n;
                }
            }
        }

        return null;
    }

    private MediaTime SourceTime(PlannedClip planned, long frame) =>
        Times.Add(planned.SourceIn, _plan.TimeOf(frame - planned.StartFrame));

    /// <summary>The output frame a source packet's frame lands on: the slot its time falls in, as the nearest-frame rule picks.</summary>
    private long OutputFrame(PlannedClip planned, MediaTime time)
    {
        double local = Times.Sub(time, planned.SourceIn).Seconds * _canvas.FrameRate.ToDouble();
        return planned.StartFrame + (long)Math.Round(local, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Copies a run's packets, retimed onto the output's frames; stops early (returning the frame it reached) when the source
    /// skips or repeats a frame, so every output slot gets exactly one frame.
    /// </summary>
    private long Copy(CopyRun run, VideoWriter writer, CancellationToken cancellationToken)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(run.Path, _context);
        TrimCodec codec = new(run.Track);
        demuxer.Seek(SourceTime(run.Clip, run.Start), run.Track.Number);
        long expected = run.Start;
        bool started = false;
        List<EncodedPacket> hidden = [];
        try
        {
            while (expected < run.End && demuxer.ReadPacket() is { } packet)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (packet.TrackNumber != run.Track.Number)
                {
                    packet.Dispose();
                    continue;
                }

                if (!codec.ShowsFrame(packet))
                {
                    // A hidden frame goes out with the frame that shows it.
                    if (started || codec.IsKeyframe(packet))
                    {
                        hidden.Add(packet);
                    }
                    else
                    {
                        packet.Dispose();
                    }

                    continue;
                }

                using (packet)
                {
                    long n = OutputFrame(run.Clip, packet.Timestamp);
                    if (!started)
                    {
                        if (n < run.Start)
                        {
                            Dispose(hidden);
                            continue;
                        }

                        if (n != run.Start || !codec.IsKeyframe(hidden.Count > 0 ? hidden[0] : packet))
                        {
                            return run.Start;
                        }

                        started = true;
                    }
                    else if (n != expected)
                    {
                        Dispose(hidden);
                        return expected;
                    }

                    MediaTime at = _plan.TimeOf(n);
                    foreach (EncodedPacket before in hidden)
                    {
                        before.Timestamp = at;
                        before.DecodeTimestamp = null;
                        before.Duration = MediaTime.Zero;
                        writer.WriteCopiedPacket(before, _canvas.Width, _canvas.Height);
                    }

                    Dispose(hidden);
                    packet.Timestamp = at;
                    packet.DecodeTimestamp = null;
                    packet.Duration = _plan.TimeOf(1);
                    writer.WriteCopiedPacket(packet, _canvas.Width, _canvas.Height);
                    CopiedFrames++;
                    expected++;
                }
            }
        }
        finally
        {
            Dispose(hidden);
        }

        return expected;
    }

    private static void Dispose(List<EncodedPacket> packets)
    {
        foreach (EncodedPacket packet in packets)
        {
            packet.Dispose();
        }

        packets.Clear();
    }

    /// <summary>A mid-gray frame, only for asking the encoder what its stream looks like.</summary>
    private sealed class BlankFrame(int width, int height) : VideoFrame(MediaTime.Zero, null, null)
    {
        private readonly Raster<Rgba8> _pixels = Raster.Create(width, height, new Rgba8(128, 128, 128, 255));

        public override int Width => _pixels.Width;

        public override int Height => _pixels.Height;

        public override Raster? Pixels => _pixels;

        public override YuvRaster? Planes => null;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pixels.Dispose();
            }
        }
    }
}
