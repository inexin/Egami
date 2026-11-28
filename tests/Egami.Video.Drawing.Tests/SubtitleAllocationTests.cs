using Egami.Pixels;
using Egami.Video.Subtitles;
using Egami.Video.Subtitles.Rendering;

namespace Egami.Video.Drawing.Tests;

/// <summary>
/// Burn-in allocates nothing per frame while the same cues stay on screen (gate B4 of the subtitle burn-in plan). Run alone, in
/// Release, with EGAMI_ALLOCATION_TESTS=1.
/// </summary>
public class SubtitleAllocationTests
{
    [Theory]
    [Trait("Category", "Allocation")]
    [InlineData(ChromaFormat.Yuv420, 8)]
    [InlineData(ChromaFormat.Yuv422, 10)]
    [InlineData(ChromaFormat.Yuv444, 12)]
    public void FramesWithUnchangedCuesAllocateNothing(ChromaFormat chroma, int bitDepth)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        SubtitleDocument document = SubtitleRendererTests.Ass("""
            Dialogue: 0,0:00:01.00,0:00:09.00,Default,,0,0,0,,The first cue, {\i1}with a span{\i0}\Nand two lines
            Dialogue: 1,0:00:02.00,0:00:08.00,Default,,0,0,0,,{\an8}A second cue at the top
            """);
        using var renderer = new SubtitleRenderer(document, SubtitleRendererTests.Options);
        using YuvRaster raster = bitDepth == 8
            ? YuvRaster.Create(1920, 1080, chroma, YuvColor.Bt709Limited)
            : YuvRaster.Create(1920, 1080, chroma, bitDepth, YuvColor.Bt709Limited);
        var frames = new YuvVideoFrame[100];
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = new YuvVideoFrame(raster, MediaTime.FromSeconds(3 + (i * 0.04)));
        }

        renderer.Draw(frames[0]);
        renderer.Draw(frames[1]);
        Assert.Equal(0, Fewest(() =>
        {
            for (int i = 2; i < frames.Length; i++)
            {
                renderer.Draw(frames[i]);
            }
        }));

        using Raster<Rgba8> rgba = Raster.Create(1920, 1080, new Rgba8(0, 0, 0, 255));
        renderer.Draw(rgba, MediaTime.FromSeconds(3));
        renderer.Draw(rgba, MediaTime.FromSeconds(3.04));
        Assert.Equal(0, Fewest(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                renderer.Draw(rgba, MediaTime.FromSeconds(3.08 + (i * 0.04)));
            }
        }));
    }

    /// <summary>
    /// The fewest bytes the test thread allocates in three runs of <paramref name="action"/>: the runtime itself can allocate
    /// on the thread now and then (tiering up a method the runs call), which the renderer does not.
    /// </summary>
    private static long Fewest(Action action)
    {
        long fewest = long.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return fewest;
    }
}
