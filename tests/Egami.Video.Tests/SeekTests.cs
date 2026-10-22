using Egami.Pixels;

namespace Egami.Video.Tests;

public class SeekTests
{
    [Theory]
    [InlineData("vp8/v0-keyframes.webm")]
    [InlineData("vp8/v0-altref.webm")]
    [InlineData("vp8/v0-altref.ivf")]
    [InlineData("vp8/v0-resilient.ivf")]
    [InlineData("vp8/alpha-odd-75x45.webm")]
    [InlineData("mkv-mjpeg-pcm.mkv")]
    [InlineData("vp9/p0-altref.webm")]
    [InlineData("vp9/p0-altref.ivf")]
    [InlineData("vp9/p0-keyframes.webm")]
    [InlineData("vp9/p0-resilient.ivf")]
    [InlineData("vp9/p2-10bit.mkv")]
    [InlineData("vp9/alpha-ramp.webm")]
    [InlineData("mp4/mp4-mjpeg.mp4")]
    [InlineData("mp4/mov-mjpeg-sowt.mov")]
    [InlineData("mp4/mp4box-vp8.mp4")]
    public void ExactSeeksGiveTheSameFramesAsDecodingFromTheStart(string clip)
    {
        AssertExactSeeks(Corpus.PathOf(clip), clip.Length);
    }

    [Fact]
    public void ExactSeeksOverShowExistingFramesAndSizeChanges()
    {
        string[] vectors = [.. Codecs.Vp9Corpus.Vectors.Where(v => Path.GetFileName(v) is "vp90-2-10-show-existing-frame.webm"
            or "vp90-2-10-show-existing-frame2.webm" or "vp90-2-21-resize_inter_320x180_5_1-2.webm")];
        Assert.SkipWhen(vectors.Length == 0, "The VP9 test vectors are not fetched (python tools/video/make-vp9-corpus.py --vectors).");
        foreach (string vector in vectors)
        {
            AssertExactSeeks(vector, vector.Length);
        }
    }

    [Theory]
    [InlineData("vp8/v0-keyframes.webm")]
    [InlineData("vp8/v0-altref.webm")]
    [InlineData("vp9/p0-keyframes.webm")]
    [InlineData("vp9/p0-altref.webm")]
    [InlineData("mkv-mjpeg-pcm.mkv")]
    public void ExactSeeksInMp4WrittenByUsGiveTheSameFrames(string clip)
    {
        string mp4 = Path.Combine(Path.GetTempPath(), $"egami-seek-{Guid.NewGuid():N}.mp4");
        try
        {
            MediaFile.Remux(Corpus.PathOf(clip), mp4, cancellationToken: TestContext.Current.CancellationToken);
            AssertExactSeeks(mp4, clip.Length);
        }
        finally
        {
            File.Delete(mp4);
        }
    }

    private static void AssertExactSeeks(string path, int seed)
    {
        List<(MediaTime Time, string Md5)> all = Decode(path);
        Random random = new(seed);
        MediaTime end = all[^1].Time + MediaTime.FromSeconds(0.2);
        using VideoReader reader = VideoReader.Open(path);
        for (int i = 0; i < 100; i++)
        {
            // Times from just before the start to just past the end, some exactly on a frame.
            MediaTime time = i % 4 == 0 ? all[random.Next(all.Count)].Time : MediaTime.FromSeconds((random.NextDouble() * (end.Seconds + 0.1)) - 0.05);
            int expected = Math.Max(0, all.FindLastIndex(f => f.Time <= time));
            reader.Seek(time);

            // The frame at the time, then the frames after it as from the start.
            for (int n = expected; n < Math.Min(all.Count, expected + 3); n++)
            {
                using YuvVideoFrame frame = reader.ReadFrame()!;
                Assert.True(all[n] == (frame.Timestamp, Md5(frame)), $"{Path.GetFileName(path)}: seek to {time}: frame {n} differs");
            }
        }
    }

    [Fact]
    public void KeyframeSeekLandsOnTheKeyframeBefore()
    {
        string path = Corpus.Vp8PathOf("v0-keyframes.webm");
        List<(MediaTime Time, string Md5)> all = Decode(path);
        using VideoReader reader = VideoReader.Open(path);

        // A keyframe every 7 frames at 30 frames a second: 0.5 s lands on frame 14.
        reader.Seek(MediaTime.FromSeconds(0.5), SeekMode.Keyframe);
        using (YuvVideoFrame frame = reader.ReadFrame()!)
        {
            Assert.Equal(all[14], (frame.Timestamp, Md5(frame)));
        }

        reader.Seek(MediaTime.FromSeconds(0.5));
        using (YuvVideoFrame frame = reader.ReadFrame()!)
        {
            Assert.Equal(all[all.FindLastIndex(f => f.Time <= MediaTime.FromSeconds(0.5))], (frame.Timestamp, Md5(frame)));
        }
    }

    [Fact]
    public void ReadFramesAfterAnExactSeekContinuesToTheEnd()
    {
        string path = Corpus.Vp8PathOf("v0-altref.webm");
        List<(MediaTime Time, string Md5)> all = Decode(path);
        using VideoReader reader = VideoReader.Open(path);
        reader.Seek(MediaTime.FromSeconds(0.8));
        int first = all.FindLastIndex(f => f.Time <= MediaTime.FromSeconds(0.8));
        Assert.Equal(all[first..], reader.ReadFrames().Select(f => { using (f) { return (f.Timestamp, Md5(f)); } }));
    }

    private static List<(MediaTime Time, string Md5)> Decode(string path)
    {
        using VideoReader reader = VideoReader.Open(path);
        return [.. reader.ReadFrames().Select(f => { using (f) { return (f.Timestamp, Md5(f)); } })];
    }

    private static string Md5(YuvVideoFrame frame) => frame.Raster is YuvRaster<byte> r
        ? Codecs.Vp8Tests.Md5(r) + (r.A is { } a ? Codecs.Vp8Tests.PlaneMd5(a) : "")
        : Codecs.Vp9Tests.Md5(frame.Raster);
}
