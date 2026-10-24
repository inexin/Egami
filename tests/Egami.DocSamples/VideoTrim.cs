using Egami.Video;

namespace Egami.DocSamples;

[Collection("Samples")]
public class VideoTrim
{
    [Fact]
    public void Smart()
    {
        using SampleFiles files = new();

        #region video-trim
        // Frame-exact: only the frames from 0.3 s up to the next keyframe are encoded again; every other packet is copied.
        TrimResult result = MediaFile.Trim("show.webm", "cut.webm", MediaTime.FromSeconds(0.3), MediaTime.FromSeconds(0.8));
        Console.WriteLine($"{result.Frames} frames from {result.Start.Seconds:0.000} s, {result.ReencodedFrames} encoded again");
        foreach (MediaWarning warning in result.Warnings)
        {
            Console.WriteLine(warning.Message);
        }

        // No decoding at all: the cut starts at the keyframe at or before 0.3 s.
        MediaFile.Trim("show.webm", "copy.webm", MediaTime.FromSeconds(0.3), MediaTime.FromSeconds(0.8), TrimMode.Keyframe);
        #endregion

        Assert.Equal(TrimMode.Smart, result.Mode);
        Assert.True(result.ReencodedFrames > 0 && result.ReencodedFrames < result.Frames);
        Assert.True(File.Exists("copy.webm"));
    }

    [Fact]
    public void Streams()
    {
        using SampleFiles files = new();
        using CancellationTokenSource cts = new();

        #region video-trim-streams
        // Streams in and out, such as cloud blobs: a non-seekable input is read from its start.
        using Stream source = File.OpenRead("movie.webm");
        using Stream upload = File.Create("cut.mkv");
        IProgress<MediaProgress> progress = new Progress<MediaProgress>(p => Console.Write($"\r{p.Fraction ?? 0:P0}"));
        TrimResult result = MediaFile.Trim(source, upload, "mkv", MediaTime.FromSeconds(1), null, TrimMode.Smart,
            tracks: t => t.Kind == TrackKind.Video, leaveOpen: true, progress: progress, cancellationToken: cts.Token);
        #endregion

        Assert.True(result.Frames > 0);
    }
}
