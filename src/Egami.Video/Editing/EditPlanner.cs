namespace Egami.Video.Editing;

/// <summary>Works out an <see cref="EditPlan"/>: probes the clips and lays the timeline out in whole frames.</summary>
internal static class EditPlanner
{
    public static EditPlan Plan(VideoEdit edit, MediaContext context)
    {
        List<string> errors = [], warnings = [];
        Dictionary<Clip, SourceInfo> sources = [];
        if (edit.Track.Count == 0)
        {
            throw new InvalidOperationException("The edit has no clips on its track.");
        }

        foreach (TrackItem item in edit.Track)
        {
            Probe(item.Clip, sources, context, errors);
        }

        foreach (Layer layer in edit.Layers)
        {
            Probe(layer.Clip, sources, context, errors);
        }

        Fail(errors);
        SourceInfo first = sources[edit.Track[0].Clip];
        EditCanvas canvas = edit.Canvas ?? new EditCanvas(Even(first.Size.Width), Even(first.Size.Height), first.FrameRate);
        canvas.Validate();

        List<PlannedClip> clips = [];
        long position = 0;
        for (int i = 0; i < edit.Track.Count; i++)
        {
            TrackItem item = edit.Track[i];
            SourceInfo info = sources[item.Clip];
            long frames = Frames(MediaTime.FromSeconds(new ClipTiming(item.Clip, info).Length), canvas);
            if (frames < 1)
            {
                errors.Add($"Clip {i + 1} is shorter than one frame.");
                continue;
            }

            long overlap = 0;
            if (item.Transition is Transition transition)
            {
                if (i == 0)
                {
                    warnings.Add("The first clip's transition has nothing to come from, so it is left out.");
                }
                else
                {
                    overlap = Frames(transition.Duration, canvas);
                    PlannedClip previous = clips.Count > 0 ? clips[^1] : null!;
                    if (previous is not null && (overlap > previous.FrameCount - previous.TransitionFrames || overlap > frames))
                    {
                        errors.Add($"The transition into clip {i + 1} ({transition.Duration.Seconds:0.###} s) is longer than a clip it joins.");
                        overlap = 0;
                    }
                }
            }

            long start = position - overlap;
            CheckFades(item.Clip, frames, canvas, $"Clip {i + 1}", warnings);
            clips.Add(new PlannedClip(i, item.Clip, start, frames, overlap, info.In, info.Size, info.HasAudio && !item.Clip.IsMuted));
            position = start + frames;
        }

        Fail(errors);
        long total = position;
        List<PlannedLayer> layers = [];
        for (int i = 0; i < edit.Layers.Count; i++)
        {
            Layer layer = edit.Layers[i];
            SourceInfo info = sources[layer.Clip];
            long start = Frames(layer.Start, canvas);
            long clipFrames = Frames(MediaTime.FromSeconds(new ClipTiming(layer.Clip, info).Length), canvas);
            long end = layer.End is MediaTime e ? Frames(e, canvas) : layer.HoldsLastFrame ? total : start + clipFrames;
            if (!layer.HoldsLastFrame && end > start + clipFrames)
            {
                if (layer.End is not null)
                {
                    warnings.Add($"Layer {i + 1}'s clip ends before the layer does; the layer ends with it. Use HoldLastFrame to keep showing it.");
                }

                end = start + clipFrames;
            }

            if (start >= total)
            {
                warnings.Add($"Layer {i + 1} starts after the edit ends, so it is left out.");
                continue;
            }

            if (end > total)
            {
                warnings.Add($"Layer {i + 1} runs past the edit's end and is cut there.");
                end = total;
            }

            if (end <= start)
            {
                warnings.Add($"Layer {i + 1} is shorter than one frame, so it is left out.");
                continue;
            }

            CheckFades(layer.Clip, end - start, canvas, $"Layer {i + 1}", warnings);
            layers.Add(new PlannedLayer(i, layer, start, end - start, Math.Min(clipFrames, end - start), info.Size, info.HasAudio && !layer.Clip.IsMuted));
        }

        return new EditPlan(edit, canvas, total, clips, layers, warnings, sources);
    }

    /// <summary>Returns the number of whole output frames a time covers, rounded to the nearest.</summary>
    public static long Frames(MediaTime time, EditCanvas canvas) =>
        (long)Math.Round(time.Seconds * canvas.FrameRate.Numerator / canvas.FrameRate.Denominator, MidpointRounding.AwayFromZero);

    private static SourceInfo Probe(Clip clip, Dictionary<Clip, SourceInfo> sources, MediaContext context, List<string> errors)
    {
        if (sources.TryGetValue(clip, out SourceInfo? known))
        {
            return known;
        }

        SourceInfo info;
        try
        {
            info = clip.Source switch
            {
                FileClipSource file => FromMedia(MediaFile.Probe(file.Path, context), clip, () => MediaFile.OpenDemuxer(file.Path, context)),
                StreamClipSource stream => FromStream(stream.Stream, clip, context),
                StackClipSource stack => FromStack(stack, clip, sources, context, errors),
                StillClipSource still => Trimmed(clip, still.Image is { } image ? new Size(image.Width, image.Height) : ImageSize(still.Path!, context), new Rational(30, 1), still.Duration, true, false),
                AnimationClipSource animation => FromAnimation(animation.Path, clip, context),
                ColorClipSource color => Trimmed(clip, new Size(0, 0), new Rational(30, 1), color.Duration, true, false),
                AppClipSource app => Trimmed(clip, app.Source.Size, new Rational(30, 1), app.Source.Duration, true, false),
                _ => throw new InvalidOperationException("Unknown clip source."),
            };
        }
        catch (Exception e) when (e is IOException or EgamiException or UnauthorizedAccessException or InvalidDataException)
        {
            errors.Add($"{Name(clip)} cannot be read: {e.Message}");
            info = new SourceInfo(new Size(16, 16), new Rational(30, 1), MediaTime.FromSeconds(1), false, false, MediaTime.Zero, MediaTime.FromSeconds(1));
        }

        sources[clip] = info;
        return info;
    }

    private static SourceInfo FromStream(Stream stream, Clip clip, MediaContext context)
    {
        long position = stream.Position;
        try
        {
            MediaInfo media = MediaFile.Probe(stream, context);
            stream.Position = position;
            return FromMedia(media, clip, () => MediaFile.OpenDemuxer(stream, context, leaveOpen: true));
        }
        finally
        {
            stream.Position = position;
        }
    }

    private static SourceInfo FromMedia(MediaInfo media, Clip clip, Func<Containers.IDemuxer> open)
    {
        VideoTrackInfo? video = media.VideoTrack;
        bool audio = media.Tracks.Any(t => t is AudioTrackInfo);
        MediaTime duration = media.Duration ?? video?.Duration ?? media.Tracks.Select(t => t.Duration).FirstOrDefault(d => d is not null)
            ?? Scan(open) ?? throw new UnsupportedFormatException($"{Name(clip)} does not say how long it is, and has no packets to count.");
        Size size = video is null ? new Size(0, 0) : new Size(video.Width, video.Height);
        return Trimmed(clip, size, video?.FrameRate ?? new Rational(30, 1), duration, video is not null, audio);
    }

    /// <summary>The end of the last packet, for containers that do not record a duration (YUV4MPEG2, raw streams).</summary>
    private static MediaTime? Scan(Func<Containers.IDemuxer> open)
    {
        using Containers.IDemuxer demuxer = open();
        MediaTime? end = null;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                MediaTime last = packet.Duration is MediaTime d ? Times.Add(packet.Timestamp, d) : packet.Timestamp;
                end = end is MediaTime e && e >= last ? e : last;
            }
        }

        return end;
    }

    private static SourceInfo FromStack(StackClipSource stack, Clip clip, Dictionary<Clip, SourceInfo> sources, MediaContext context, List<string> errors)
    {
        SourceInfo[] children = [.. stack.Clips.Select(c => Probe(c, sources, context, errors))];
        Size firstSize = children[0].Size.Width > 0 ? children[0].Size : new Size(320, 240);
        int gap = stack.Options.Gap;
        Size size = stack.Layout switch
        {
            StackLayout.Horizontal => new Size(children.Sum(c => ScaledWidth(c.Size, firstSize.Height)) + (gap * (children.Length - 1)), firstSize.Height),
            StackLayout.Vertical => new Size(firstSize.Width, children.Sum(c => ScaledHeight(c.Size, firstSize.Width)) + (gap * (children.Length - 1))),
            _ => new Size((firstSize.Width * stack.Columns) + (gap * (stack.Columns - 1)), (firstSize.Height * stack.Rows) + (gap * (stack.Rows - 1))),
        };
        IEnumerable<MediaTime> lengths = stack.Clips.Select((c, i) => MediaTime.FromSeconds(new ClipTiming(c, children[i]).Length));
        MediaTime duration = stack.Options.Shortest ? lengths.Min() : lengths.Max();
        bool audio = stack.Options.MixAudio ? children.Any(c => c.HasAudio) : children[0].HasAudio;
        return Trimmed(clip, size, children[0].FrameRate, duration, true, audio);
    }

    private static Size ImageSize(string path, MediaContext context)
    {
        using Raster image = Raster.Load(path, context.Egami, new Egami.Formats.ReaderOptions { Frames = ..1 });
        return new Size(image.Width, image.Height);
    }

    private static SourceInfo FromAnimation(string path, Clip clip, MediaContext context)
    {
        using Raster<Egami.Pixels.Rgba8> animation = Raster.Load<Egami.Pixels.Rgba8>(path, context.Egami);
        double[] starts = AnimationFrames.Starts(animation);
        return Trimmed(clip, new Size(animation.Width, animation.Height), new Rational(30, 1), MediaTime.FromSeconds(starts[^1]), true, false);
    }

    private static SourceInfo Trimmed(Clip clip, Size size, Rational rate, MediaTime duration, bool video, bool audio)
    {
        MediaTime start = clip.In ?? MediaTime.Zero;
        MediaTime end = clip.Out ?? duration;
        if (start >= duration)
        {
            throw new InvalidDataException($"{Name(clip)} starts at {start.Seconds:0.###} s, after its source ends at {duration.Seconds:0.###} s.");
        }

        if (end > duration)
        {
            end = duration;
        }

        return new SourceInfo(size, rate.Numerator > 0 ? rate : new Rational(30, 1), duration, video, audio, start, end);
    }

    private static void CheckFades(Clip clip, long frames, EditCanvas canvas, string name, List<string> warnings)
    {
        if (Frames(clip.FadeInDuration, canvas) + Frames(clip.FadeOutDuration, canvas) > frames)
        {
            warnings.Add($"{name}'s fades are longer together than the clip; they overlap.");
        }
    }

    private static int ScaledWidth(Size size, int height) => size.Height <= 0 ? height * 4 / 3 : Math.Max(1, (int)Math.Round((double)size.Width * height / size.Height));

    private static int ScaledHeight(Size size, int width) => size.Width <= 0 ? width * 3 / 4 : Math.Max(1, (int)Math.Round((double)size.Height * width / size.Width));

    private static int Even(int value) => Math.Max(2, value + (value & 1));

    private static string Name(Clip clip) => clip.Source switch
    {
        FileClipSource file => $"\"{file.Path}\"",
        StillClipSource { Path: string still } => $"\"{still}\"",
        AnimationClipSource animation => $"\"{animation.Path}\"",
        StackClipSource => "A stack",
        _ => "A stream clip",
    };

    private static void Fail(List<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("The edit cannot render: " + string.Join(" ", errors));
        }
    }
}
