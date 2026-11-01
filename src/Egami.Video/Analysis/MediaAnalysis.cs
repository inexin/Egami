using Egami.Video.Audio;
using Egami.Video.Containers;

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
        MediaContext opened = context;
        MediaAnalysisOptions asked = options;
        return Run(options, (video, audio) => Both(
            () => video ? VideoReader.Open(path, new VideoReaderOptions { TrackNumber = asked.VideoTrack }, opened) : null,
            () => audio ? AudioReader.Open(path, new AudioReaderOptions { TrackNumber = asked.AudioTrack }, opened) : null), progress, cancellationToken);
    }

    /// <summary>
    /// Analyzes a stream, as <see cref="Run(string, MediaAnalysisOptions?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>
    /// does a file. A stream that cannot seek is read once, its video and audio packets shared out as they come; frames before
    /// <see cref="MediaAnalysisOptions.Start"/> are then decoded and skipped. The caller owns the stream: it is read from its
    /// current position and not disposed.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="options">What to look for, or <see langword="null"/> for scenes only.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the analysis between frames.</param>
    /// <returns>What the detectors found.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches, or a track the options need is missing or cannot be decoded.</exception>
    /// <exception cref="OperationCanceledException">The analysis was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-stream"/>
    /// </example>
    public static MediaAnalysisResult Run(Stream stream, MediaAnalysisOptions? options = null, MediaContext? context = null,
        IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= MediaAnalysisOptions.Default;
        context ??= MediaContext.Default;
        MediaContext opened = context;
        VideoReaderOptions videoOptions = new() { TrackNumber = options.VideoTrack };
        AudioReaderOptions audioOptions = new() { TrackNumber = options.AudioTrack };
        return Run(options, (video, audio) =>
        {
            if (stream.CanSeek)
            {
                // Two views with positions of their own, as two handles on a file.
                long start = stream.Position;
                return Both(
                    () => video ? VideoReader.Open(new StreamSources.View(stream, start), videoOptions, opened) : null,
                    () => audio ? AudioReader.Open(new StreamSources.View(stream, start), audioOptions, opened) : null);
            }

            if (!(video && audio))
            {
                return (video ? VideoReader.Open(stream, videoOptions, opened, leaveOpen: true) : null,
                    audio ? AudioReader.Open(stream, audioOptions, opened, leaveOpen: true) : null);
            }

            StreamSources.Shared shared = new(MediaFile.OpenDemuxer(stream, opened, leaveOpen: true));
            StreamSources.Shared.Part videoPart = (StreamSources.Shared.Part)shared.Open(0);
            StreamSources.Shared.Part audioPart = (StreamSources.Shared.Part)shared.Open(1);
            VideoReader? videoReader = null;
            try
            {
                videoReader = VideoReader.Create(videoPart, videoOptions, opened);
                videoPart.Track = videoReader.Track.Number;
                AudioReader audioReader = AudioReader.Create(audioPart, audioOptions, opened);
                audioPart.Track = audioReader.Track.Number;
                return (videoReader, audioReader);
            }
            catch
            {
                if (videoReader is null)
                {
                    audioPart.Dispose();
                }

                videoReader?.Dispose();
                throw;
            }
        }, progress, cancellationToken);
    }

    /// <summary>Opens the video reader, then the audio reader, disposing the first when the second fails.</summary>
    private static (VideoReader? Video, AudioReader? Audio) Both(Func<VideoReader?> video, Func<AudioReader?> audio)
    {
        VideoReader? videoReader = video();
        try
        {
            return (videoReader, audio());
        }
        catch
        {
            videoReader?.Dispose();
            throw;
        }
    }

    private static MediaAnalysisResult Run(MediaAnalysisOptions options, Func<bool, bool, (VideoReader? Video, AudioReader? Audio)> open,
        IProgress<MediaProgress>? progress, CancellationToken cancellationToken)
    {
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

        (VideoReader? openedVideo, AudioReader? openedAudio) = open(video.Count > 0, audio.Count > 0);
        using VideoReader? videoReader = openedVideo;
        using AudioReader? audioReader = openedAudio;
        MediaInfo info = videoReader?.Info ?? audioReader!.Info;
        MediaTime start = options.Start ?? MediaTime.Zero;
        MediaTime? stop = options.Duration is { } length ? start + length : null;
        MediaTime? skip = null;
        if (start.Ticks > 0)
        {
            if (videoReader?.CanSeek == false || audioReader?.CanSeek == false)
            {
                // A stream read once: decode up to the start and skip what comes before it.
                skip = start;
            }
            else
            {
                videoReader?.Seek(start);
                audioReader?.Seek(start);
            }
        }

        MediaTime? duration = options.Duration ?? (info.Duration is { Ticks: > 0 } total ? total - start : null);
        AnalysisReporter? reporter = progress is null ? null : new(progress, duration, start);
        long frames = 0;
        MediaTime videoEnd = start, audioEnd = start;
        YuvVideoFrame? nextVideo = Next(videoReader, skip, stop);
        AudioFrame? nextAudio = Next(audioReader, skip, stop);
        try
        {
            while (nextVideo is not null || nextAudio is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (nextVideo is not null && (nextAudio is null || nextVideo.Timestamp <= nextAudio.Timestamp))
                {
                    YuvVideoFrame frame = nextVideo;
                    nextVideo = null;
                    bool kept = false;
                    try
                    {
                        foreach (IVideoDetector detector in video)
                        {
                            // A freeze detector may keep the frame itself as its reference rather than copy it; it then owns it.
                            if (!kept && detector is FreezeDetector freeze)
                            {
                                kept = freeze.AddAndKeep(frame);
                            }
                            else
                            {
                                detector.Add(frame);
                            }
                        }

                        frames++;
                        videoEnd = frame.Duration is { } shown ? frame.Timestamp + shown : frame.Timestamp;
                        reporter?.Advance(frame.Timestamp, frames);
                    }
                    finally
                    {
                        if (!kept)
                        {
                            frame.Dispose();
                        }
                    }

                    nextVideo = Next(videoReader, skip, stop);
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

                    nextAudio = Next(audioReader, skip, stop);
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

    private static YuvVideoFrame? Next(VideoReader? reader, MediaTime? skip, MediaTime? stop)
    {
        YuvVideoFrame? frame;
        while ((frame = reader?.ReadFrame()) is not null && skip is { } from && frame.Timestamp < from)
        {
            frame.Dispose();
        }

        if (frame is null)
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

    private static AudioFrame? Next(AudioReader? reader, MediaTime? skip, MediaTime? stop)
    {
        AudioFrame? frame;
        while ((frame = reader?.ReadFrame()) is not null && skip is { } from && frame.Timestamp + frame.Duration <= from)
        {
            frame.Dispose();
        }

        if (frame is null)
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
}

/// <summary>What <see cref="MediaAnalysis.Run(string, MediaAnalysisOptions?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/> looks for and where.</summary>
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

/// <summary>What <see cref="MediaAnalysis.Run(string, MediaAnalysisOptions?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/> found.</summary>
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
    public IReadOnlyList<SceneScore> SceneScores => _scenes?.Series ?? [];

    /// <summary>Gets how many video frames were analyzed.</summary>
    public long VideoFrames { get; }

    /// <summary>Gets where the analysis started.</summary>
    public MediaTime Start { get; }

    /// <summary>Gets where it ended: the end of the last frame or sample read, or the file's duration when that is later.</summary>
    public MediaTime End { get; }
}
