using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Ffv1;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>FFV1 decoding on any number of threads, and what it allocates beyond the frame (V6 task 9).</summary>
public class Ffv1DecodeBudgetTests
{
    public static TheoryData<string, int> ClipsAndThreads
    {
        get
        {
            TheoryData<string, int> data = [];
            foreach (string clip in (string[])["v3-odd-97x61", "v3-slices-24", "v3-411", "v3-410", "v3-gop", "v3-gop-rice", "v3-bgr0", "v3-420p9", "v3-yuva420p"])
            {
                data.Add(clip, 1);
                data.Add(clip, 3);
                data.Add(clip, 16);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ClipsAndThreads))]
    public void FramesAreTheSameOnAnyNumberOfThreads(string clip, int threads)
    {
        (string file, int depth, List<string> expected) = Ffv1Tests.ReadReference(clip);
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        using VideoReader reader = VideoReader.Open(Ffv1Tests.PathOf(file), context: context);
        List<string> ours = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                ours.Add(Ffv1Tests.Md5(frame.Raster, depth));
            }
        }

        Assert.Equal(expected, ours);
    }

    [Theory]
    [InlineData("v3-slices-24", 1)]
    [InlineData("v3-slices-24", 4)]
    [InlineData("v3-gop-rice", 1)]
    [InlineData("v1-gop", 1)]
    public void SteadyStateDecodingAllocatesLittleBeyondTheFrame(string clip, int threads)
    {
        (string file, _, _) = Ffv1Tests.ReadReference(clip);
        VideoTrackInfo track;
        List<byte[]> packets = [];
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(Ffv1Tests.PathOf(file)))
        {
            track = demuxer.Info.VideoTrack!;
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    packets.Add(packet.Data.ToArray());
                }
            }
        }

        EgamiContext context = EgamiContext.Default with { MaxParallelism = threads };
        var color = new YuvColor(YuvMatrix.Bt601, YuvRange.Limited, ChromaSiting.Left);
        var decoder = new Ffv1Decoder(track.Width, track.Height, Ffv1Codec.ConfigurationRecord(track).Span, out _);
        List<Ffv1SliceError> errors = [];
        long frameOnly = long.MaxValue, decode = long.MaxValue;
        for (int run = 0; run < 4; run++)
        {
            foreach (byte[] packet in packets)
            {
                long start = GC.GetAllocatedBytesForCurrentThread();
                using YuvRaster frame = decoder.Decode(packet, packet.Length, color, context, errors, out _);
                long used = GC.GetAllocatedBytesForCurrentThread() - start;
                start = GC.GetAllocatedBytesForCurrentThread();
                using YuvRaster same = frame.BitDepth == 8
                    ? YuvRaster.Create(frame.Width, frame.Height, frame.ChromaFormat, frame.Color, frame.HasAlpha, context)
                    : YuvRaster.Create(frame.Width, frame.Height, frame.ChromaFormat, frame.BitDepth, frame.Color, frame.HasAlpha, context);
                long raster = GC.GetAllocatedBytesForCurrentThread() - start;
                if (run > 1)
                {
                    decode = Math.Min(decode, used);
                    frameOnly = Math.Min(frameOnly, raster);
                }
            }
        }

        Assert.Empty(errors);
        // One thread: a small object or two a frame. More threads: what Parallel.For allocates on the calling thread.
        long budget = threads == 1 ? 512 : 8 * 1024;
        Assert.True(decode - frameOnly < budget, $"{decode - frameOnly} bytes a frame beyond the frame's own {frameOnly}");
    }
}
