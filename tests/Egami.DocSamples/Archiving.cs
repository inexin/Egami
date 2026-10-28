using Egami.Pixels;
using Egami.Video;
using Egami.Video.Codecs;

namespace Egami.DocSamples;

/// <summary>The lossless archiving guide (docs/articles/guides/lossless-archiving.md): FFV1 masters, fixity checks and restores.</summary>
public class Archiving
{
    [Fact]
    public void ArchiveACapture()
    {
        using SampleFiles files = new();

        #region archive-write
        // An uncompressed 10-bit 4:4:4 capture into an FFV1 master: the planes keep their layout and depth, and the defaults are
        // the archival profile (version 3, every frame a keyframe, a CRC on every slice).
        using (VideoReader capture = VideoReader.Open("capture.y4m"))
        using (VideoWriter master = VideoWriter.Create("master.mkv", new VideoWriterOptions
        {
            Codec = VideoCodecs.Ffv1,
            FrameRate = capture.Track.FrameRate ?? new Rational(25, 1),
        }))
        {
            foreach (YuvVideoFrame frame in capture.ReadFrames())
            {
                using (frame)
                {
                    master.WriteFrame(frame);
                }
            }
        }
        #endregion

        Assert.Equal(CodecIds.Ffv1, MediaFile.Probe("master.mkv").VideoTrack!.CodecId);
        Assert.Equal(10, MediaFile.Probe("master.mkv").VideoTrack!.BitDepth);
    }

    [Fact]
    public void CheckFixityAndLosslessness()
    {
        using SampleFiles files = new();
        using (VideoReader capture = VideoReader.Open("capture.y4m"))
        using (VideoWriter writer = VideoWriter.Create("master.mkv", new VideoWriterOptions { Codec = VideoCodecs.Ffv1 }))
        {
            foreach (YuvVideoFrame frame in capture.ReadFrames())
            {
                using (frame)
                {
                    writer.WriteFrame(frame);
                }
            }
        }

        #region archive-verify
        // A routine fixity check: every slice's CRC, without decoding a picture.
        Ffv1VerifyResult check = Ffv1Codec.Verify("master.mkv");
        Console.WriteLine($"{check.Frames} frames, {check.Slices} slices: {(check.IsIntact ? "intact" : $"{check.Errors.Count} damaged")}");

        // A full check after migration: decode the master and compare every sample with the source.
        using VideoReader source = VideoReader.Open("capture.y4m");
        using VideoReader master = VideoReader.Open("master.mkv");
        int frames = 0, different = 0;
        foreach (YuvVideoFrame original in source.ReadFrames())
        {
            using (original)
            using (YuvVideoFrame copy = master.ReadFrame()!)
            {
                frames++;
                different += SamePlanes(original.Raster, copy.Raster) ? 0 : 1;
            }
        }

        Console.WriteLine($"{frames} frames compared, {different} differ");
        #endregion

        Assert.True(check.IsIntact);
        Assert.True(check.HasSliceCrcs);
        Assert.Equal(0, different);
        Assert.True(frames > 0);
    }

    [Fact]
    public void RestoreAndTune()
    {
        using SampleFiles files = new();
        using Raster<Rgb8> duck = Raster.Load<Rgb8>("duck.png");

        #region archive-rgb
        // Scanned film or graphics in RGB stays RGB: frames given as RGB rasters are coded through FFV1's reversible color
        // transform and come back exactly, here as 16-bit RGB for a restoration tool.
        using (VideoWriter writer = VideoWriter.Create("scan.mkv", new VideoWriterOptions { Codec = VideoCodecs.Ffv1, FrameRate = new Rational(24, 1) }))
        {
            writer.WriteFrame(duck);
        }

        using VideoReader reader = VideoReader.Open("scan.mkv");
        using VideoFrame<Rgb16> restored = reader.ReadFrames<Rgb16>().First();
        #endregion

        #region archive-options
        // Smaller and faster masters for a working copy: Golomb-Rice coding, a keyframe every second, more slices for more cores.
        // Slice CRCs stay on, so Ffv1Codec.Verify still checks every frame.
        VideoWriterOptions mezzanine = new()
        {
            Codec = VideoCodecs.Ffv1,
            FrameRate = new Rational(25, 1),
            KeyframeInterval = 25,
            Ffv1 = new Ffv1EncoderOptions { Coder = Ffv1Coder.GolombRice, Slices = 24 },
        };
        #endregion

        Assert.Equal(duck.Width, restored.Width);
        using VideoReader again = VideoReader.Open("scan.mkv");
        using VideoFrame<Rgb8> eight = again.ReadFrames<Rgb8>().First();
        Raster<Rgb8> back = eight.Raster;
        for (int y = 0; y < duck.Height; y++)
        {
            Assert.True(duck.Row(y).SequenceEqual(back.Row(y)));
        }

        Assert.Equal(24, mezzanine.Ffv1!.Slices);
    }

    private static bool SamePlanes(YuvRaster a, YuvRaster b)
    {
        if (a.ChromaFormat != b.ChromaFormat || a.BitDepth != b.BitDepth || a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        if (a is YuvRaster<ushort> aw && b is YuvRaster<ushort> bw)
        {
            return Same(aw.Y, bw.Y) && Same(aw.U, bw.U) && Same(aw.V, bw.V);
        }

        var ab = (YuvRaster<byte>)a;
        var bb = (YuvRaster<byte>)b;
        return Same(ab.Y, bb.Y) && Same(ab.U, bb.U) && Same(ab.V, bb.V);
    }

    private static bool Same<T>(YuvPlane<T>? a, YuvPlane<T>? b)
        where T : unmanaged, IEquatable<T>
    {
        for (int y = 0; a is not null && b is not null && y < a.Height; y++)
        {
            if (!a.Row(y).SequenceEqual(b.Row(y)))
            {
                return false;
            }
        }

        return (a is null) == (b is null);
    }
}
