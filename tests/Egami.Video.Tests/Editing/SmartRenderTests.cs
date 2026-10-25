using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Editing;

namespace Egami.Video.Tests.Editing;

/// <summary>
/// Smart render (Build Plan task 23): untouched parts of clips are copied byte for byte, and the result decodes cleanly, with
/// the frame count and pictures of a full render.
/// </summary>
public class SmartRenderTests
{
    private const int Width = 160, Height = 96, Frames = 50;

    /// <summary>Writes a moving pattern with keyframes every 10 frames; returns its path.</summary>
    private static string Source(string name, VideoCodec codec, int shift)
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-smart-{name}-{Environment.ProcessId}.webm");
        using VideoWriter writer = VideoWriter.Create(path, new VideoWriterOptions { Codec = codec, FrameRate = new Rational(25, 1), KeyframeInterval = 10, SceneCutKeyframes = false, Quality = 80 });
        for (int i = 0; i < Frames; i++)
        {
            using Raster<Rgba8> frame = Raster.Create(Width, Height, new Rgba8(0, 0, 0, 255));
            for (int y = 0; y < Height; y++)
            {
                Span<Rgba8> row = frame.Row(y);
                for (int x = 0; x < Width; x++)
                {
                    row[x] = new Rgba8((byte)((x * 3) + (i * shift)), (byte)((y * 2) + i), (byte)(((x + y) / 2) ^ (i * 4)), 255);
                }
            }

            writer.WriteFrame(frame, new MediaTime(i, new Rational(1, 25)));
        }

        writer.Finish();
        return path;
    }

    public static TheoryData<string> Codecs() => ["vp8", "vp9", "av1"];

    [Theory]
    [MemberData(nameof(Codecs))]
    public void CopiesUntouchedPartsAndDecodesCleanly(string name)
    {
        VideoCodec codec = name switch { "vp8" => Vp8Codec.Instance, "vp9" => Vp9Codec.Instance, _ => Av1Codec.Instance };
        string a = Source(name + "-a", codec, 2), b = Source(name + "-b", codec, 5);
        string smart = Path.ChangeExtension(a, ".smart.webm"), full = Path.ChangeExtension(a, ".full.webm");
        List<MediaWarning> warnings = [];
        try
        {
            // A fade in, a dissolve between the clips and a layer near the end: the rest is untouched.
            VideoEdit edit = new();
            edit.Track.Add(Clip.Open(a).FadeIn(MediaTime.FromSeconds(0.2))).Add(Clip.Open(b), Transition.Dissolve(MediaTime.FromSeconds(0.4)));
            edit.Layers.Add(Layer.Of(Clip.Open(a).Muted()).Place(new Rect(8, 8, 40, 24)).Between(MediaTime.FromSeconds(3.2), MediaTime.FromSeconds(3.6)));
            VideoWriterOptions writer = new() { Codec = codec, KeyframeInterval = 10, SceneCutKeyframes = false, Quality = 80 };
            MediaContext context = MediaContext.Default with { Warnings = warnings.Add };
            edit.Render(smart, new EditRenderOptions { Writer = writer, SmartRender = true, Context = context }, null, TestContext.Current.CancellationToken);
            edit.Render(full, new EditRenderOptions { Writer = writer }, null, TestContext.Current.CancellationToken);

            // Decodes cleanly to the same frame count, close to the full render.
            List<Raster<Rgba8>> smartFrames = Decode(smart), fullFrames = Decode(full);
            Assert.Equal(edit.Plan().FrameCount, smartFrames.Count);
            Assert.Equal(fullFrames.Count, smartFrames.Count);
            double worst = double.MaxValue;
            for (int i = 0; i < smartFrames.Count; i++)
            {
                worst = Math.Min(worst, Psnr(smartFrames[i], fullFrames[i]));
            }

            Assert.True(worst > 28, $"worst {worst:F1} dB against the full render");

            // Packets copied byte for byte from the sources, unless the encoder's stream does not match theirs (said in a warning).
            HashSet<string> sources = [.. Packets(a).Concat(Packets(b))];
            int copied = Packets(smart).Count(sources.Contains);
            TestContext.Current.TestOutputHelper?.WriteLine($"{name}: {copied} of {smartFrames.Count} packets copied; {string.Join("; ", warnings.Select(w => w.Message))}");
            if (warnings.Any(w => w.Code == "smart-render"))
            {
                Assert.Equal(0, copied);
            }
            else
            {
                Assert.True(copied >= 30, $"{copied} packets copied");
            }

            foreach (Raster<Rgba8> frame in smartFrames.Concat(fullFrames))
            {
                frame.Dispose();
            }
        }
        finally
        {
            foreach (string file in new[] { a, b, smart, full })
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void ASourceOfAnotherCodecIsEncodedWithAWarning()
    {
        string a = Source("mismatch", Vp9Codec.Instance, 3), output = Path.ChangeExtension(a, ".out.webm");
        List<MediaWarning> warnings = [];
        try
        {
            VideoEdit edit = new();
            edit.Track.Add(Clip.Open(a));
            MediaContext context = MediaContext.Default with { Warnings = warnings.Add };
            edit.Render(output, new EditRenderOptions { Writer = new VideoWriterOptions { Codec = Vp8Codec.Instance }, SmartRender = true, Context = context }, null, TestContext.Current.CancellationToken);
            Assert.Contains(warnings, w => w.Code == "smart-render");
            Assert.Equal(Frames, Decode(output).Count);
        }
        finally
        {
            File.Delete(a);
            File.Delete(output);
        }
    }

    private static List<Raster<Rgba8>> Decode(string path)
    {
        using VideoReader reader = VideoReader.Open(path);
        List<Raster<Rgba8>> frames = [];
        foreach (VideoFrame<Rgba8> frame in reader.ReadFrames<Rgba8>())
        {
            using (frame)
            {
                frames.Add(frame.Raster.Copy());
            }
        }

        return frames;
    }

    private static List<string> Packets(string path)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        int video = demuxer.Tracks.OfType<VideoTrackInfo>().First().Number;
        List<string> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == video)
                {
                    packets.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(packet.Data.Span)));
                }
            }
        }

        return packets;
    }

    private static double Psnr(Raster<Rgba8> x, Raster<Rgba8> y)
    {
        double sum = 0;
        for (int row = 0; row < x.Height; row++)
        {
            ReadOnlySpan<Rgba8> a = x.Row(row), b = y.Row(row);
            for (int i = 0; i < a.Length; i++)
            {
                sum += ((a[i].R - b[i].R) * (a[i].R - b[i].R)) + ((a[i].G - b[i].G) * (a[i].G - b[i].G)) + ((a[i].B - b[i].B) * (a[i].B - b[i].B));
            }
        }

        double mse = sum / (x.Width * x.Height * 3.0);
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
