using Egami.Video.Audio;

namespace Egami.Video.Analysis;

/// <summary>
/// Analyzes a file in one pass: decodes its video and audio once each and feeds every detector the options ask for, then returns
/// what they found. The video and audio are read side by side in time order, so progress moves evenly through the file.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-run"/>
/// </example>
public static class MediaAnalysis
{
    /// <summary>Analyzes a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">What to look for, or <see langword="null"/> for scenes only.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the analysis between frames.</param>
    /// <returns>What the detectors found.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches, or a track the options need is missing or cannot be decoded.</exception>
    /// <exception cref="OperationCanceledException">The analysis was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-scene-options"/>
    /// </example>
    public static MediaAnalysisResult Run(string path, MediaAnalysisOptions? options = null, MediaContext? context = null,
        IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= MediaAnalysisOptions.Default;
        context ??= MediaContext.Default;
        SceneDetector? scenes = options.Scenes is { } sceneOptions ? new SceneDetector(sceneOptions) : null;
        List<IVideoDetector> video = [.. options.VideoDetectors];
        if (scenes is not null)
        {
            video.Insert(0, scenes);
        }

        List<IAudioDetector> audio = [.. options.AudioDetectors];
        if (video.Count == 0 && audio.Count == 0)
        {
            throw new ArgumentException("The options ask for no detector.", nameof(options));
        }

        using VideoReader? videoReader = video.Count > 0
            ? VideoReader.Open(path, new VideoReaderOptions { TrackNumber = options.VideoTrack }, context)
            : null;
        using AudioReader? audioReader = audio.Count > 0
            ? AudioReader.Open(path, new AudioReaderOptions { TrackNumber = options.AudioTrack }, context)
            : null;
        MediaInfo info = videoReader?.Info ?? audioReader!.Info;
        MediaTime start = options.Start ?? MediaTime.Zero;
        MediaTime? stop = options.Duration is { } length ? start + length : null;
        if (start.Ticks > 0)
        {
            videoReader?.Seek(start);
            audioReader?.Seek(start);
        }

        MediaTime? duration = options.Duration ?? (info.Duration is { Ticks: > 0 } total ? total - start : null);
        Reporter? reporter = progress is null ? null : new Reporter(progress, duration, start);
        long frames = 0;
        MediaTime videoEnd = start, audioEnd = start;
        YuvVideoFrame? nextVideo = Next(videoReader, stop);
        AudioFrame? nextAudio = Next(audioReader, stop);
        try
        {
            while (nextVideo is not null || nextAudio is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (nextVideo is not null && (nextAudio is null || nextVideo.Timestamp <= nextAudio.Timestamp))
                {
                    using (YuvVideoFrame frame = nextVideo)
                    {
                        foreach (IVideoDetector detector in video)
                        {
                            detector.Add(frame);
                        }

                        frames++;
                        videoEnd = frame.Duration is { } shown ? frame.Timestamp + shown : frame.Timestamp;
                        reporter?.Advance(frame.Timestamp, frames);
                    }

                    nextVideo = Next(videoReader, stop);
                }
                else
                {
                    using (AudioFrame frame = nextAudio!)
                    {
                        foreach (IAudioDetector detector in audio)
                        {
                            detector.Add(frame);
                        }

                        audioEnd = frame.Timestamp + frame.Duration;
                        reporter?.Advance(frame.Timestamp, frames);
                    }

                    nextAudio = Next(audioReader, stop);
                }
            }
        }
        finally
        {
            nextVideo?.Dispose();
            nextAudio?.Dispose();
        }

        MediaTime end = Max(videoEnd, audioEnd);
        if (stop is null && info.Duration is { Ticks: > 0 } fileEnd && fileEnd > end)
        {
            end = fileEnd;
        }

        foreach (IVideoDetector detector in video)
        {
            detector.Complete(end);
        }

        foreach (IAudioDetector detector in audio)
        {
            detector.Complete(end);
        }

        reporter?.Finish(end, frames);
        return new MediaAnalysisResult(scenes, frames, start, end);
    }

    private static YuvVideoFrame? Next(VideoReader? reader, MediaTime? stop)
    {
        if (reader?.ReadFrame() is not { } frame)
        {
            return null;
        }

        if (stop is { } end && frame.Timestamp >= end)
        {
            frame.Dispose();
            return null;
        }

        return frame;
    }

    private static AudioFrame? Next(AudioReader? reader, MediaTime? stop)
    {
        if (reader?.ReadFrame() is not { } frame)
        {
            return null;
        }

        if (stop is { } end && frame.Timestamp >= end)
        {
            frame.Dispose();
            return null;
        }

        return frame;
    }

    private static MediaTime Max(MediaTime a, MediaTime b) => a >= b ? a : b;

    /// <summary>Reports progress at most every 250 ms, as the other jobs do, plus a final report.</summary>
    private sealed class Reporter(IProgress<MediaProgress> progress, MediaTime? duration, MediaTime start)
    {
        private readonly long _interval = TimeProvider.System.TimestampFrequency / 4;
        private readonly long _started = TimeProvider.System.GetTimestamp();
        private long _next;
        private readonly MediaTime _start = start;
        private MediaTime _position = start;
        private double _fraction;

        public void Advance(MediaTime position, long frames)
        {
            if (position > _position)
            {
                _position = position;
            }

            long now = TimeProvider.System.GetTimestamp();
            if (now >= _next)
            {
                _next = now + _interval;
                Report(now, frames, final: false);
            }
        }

        public void Finish(MediaTime end, long frames)
        {
            _position = end;
            Report(TimeProvider.System.GetTimestamp(), frames, final: true);
        }

        private void Report(long now, long frames, bool final)
        {
            double done = (_position - _start).Seconds;
            double? fraction = final ? 1 : duration is { Ticks: > 0 } d ? Math.Clamp(done / d.Seconds, 0, 1) : null;
            if (fraction is { } f)
            {
                _fraction = Math.Max(_fraction, f);
                fraction = _fraction;
            }

            TimeSpan elapsed = TimeProvider.System.GetElapsedTime(_started, now);
            progress.Report(new MediaProgress
            {
                Position = _position,
                Duration = duration,
                Fraction = fraction,
                Frames = frames,
                Elapsed = elapsed,
                Speed = elapsed > TimeSpan.Zero ? done / elapsed.TotalSeconds : null,
                IsFinal = final,
            });
        }
    }
}

/// <summary>What <see cref="MediaAnalysis.Run"/> looks for and where.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-run"/>
/// </example>
public sealed record MediaAnalysisOptions
{
    /// <summary>Gets the default options: scenes with their default settings.</summary>
    public static MediaAnalysisOptions Default { get; } = new();

    /// <summary>Gets how to find scenes, or <see langword="null"/> to skip them. Default <see cref="SceneOptions.Default"/>.</summary>
    public SceneOptions? Scenes { get; init; } = SceneOptions.Default;

    /// <summary>Gets more video detectors to feed from the same decode, such as an app's own.</summary>
    public IReadOnlyList<IVideoDetector> VideoDetectors { get; init; } = [];

    /// <summary>Gets audio detectors to feed from the same pass. The audio track is decoded only when there is one.</summary>
    public IReadOnlyList<IAudioDetector> AudioDetectors { get; init; } = [];

    /// <summary>Gets the video track to read, or <see langword="null"/> for the first.</summary>
    public int? VideoTrack { get; init; }

    /// <summary>Gets the audio track to read, or <see langword="null"/> for the first.</summary>
    public int? AudioTrack { get; init; }

    /// <summary>Gets where to start, or <see langword="null"/> for the beginning. Frame numbers then count from there.</summary>
    public MediaTime? Start { get; init; }

    /// <summary>Gets how much to analyze from <see cref="Start"/>, or <see langword="null"/> for the rest of the file.</summary>
    public MediaTime? Duration { get; init; }
}

/// <summary>What <see cref="MediaAnalysis.Run"/> found.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-run"/>
/// </example>
public sealed class MediaAnalysisResult
{
    private readonly SceneDetector? _scenes;

    internal MediaAnalysisResult(SceneDetector? scenes, long frames, MediaTime start, MediaTime end)
    {
        _scenes = scenes;
        VideoFrames = frames;
        Start = start;
        End = end;
    }

    /// <summary>Gets the scenes, or an empty list when scenes were not asked for.</summary>
    public IReadOnlyList<Scene> Scenes => _scenes?.Scenes ?? [];

    /// <summary>Gets the cuts that start the scenes, the first at the first frame.</summary>
    public IReadOnlyList<SceneCut> Cuts => _scenes?.Cuts ?? [];

    /// <summary>Gets the fades to and from black found while looking for scenes.</summary>
    public IReadOnlyList<Fade> Fades => _scenes?.Fades ?? [];

    /// <summary>Gets every frame's scene score and luma, when <see cref="SceneOptions.KeepSeries"/> was set.</summary>
    public IReadOnlyList<SceneScore> SceneScores => _scenes?.Scores ?? [];

    /// <summary>Gets how many video frames were analyzed.</summary>
    public long VideoFrames { get; }

    /// <summary>Gets where the analysis started.</summary>
    public MediaTime Start { get; }

    /// <summary>Gets where it ended: the end of the last frame or sample read, or the file's duration when that is later.</summary>
    public MediaTime End { get; }
}
