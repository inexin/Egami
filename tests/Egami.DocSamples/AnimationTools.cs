using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class AnimationTools
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ShrinkRecording()
    {
        using SampleFiles files = new();
        using Raster recording = Raster.Load("pan.gif");

        #region shrink
        // Shrink a screen recording: merge frames that barely change, drop frames no viewer shows, then resample to 25 fps.
        recording.Apply(ops => ops.MergeDuplicates(tolerance: 0.01f).DropZeroDelay().Retime(25), Ct);
        recording.Save("recording.gif");
        #endregion

        Assert.True(recording.FrameCount >= 1);
    }

    [Fact]
    public void Speed()
    {
        using SampleFiles files = new();
        using Raster clip = Raster.Load("pan.gif");

        #region speed
        // Twice as fast, then backwards: a boomerang clip is the clip followed by its reverse.
        using Raster fast = clip.Derive(ops => ops.ChangeSpeed(2), Ct);
        using Raster backwards = fast.Derive(ops => ops.Reverse(), Ct);
        #endregion

        Assert.Equal(fast.FrameCount, backwards.FrameCount);
    }

    [Fact]
    public void Sticker()
    {
        using SampleFiles files = new();
        using Raster background = Raster.Load("pan.gif");
        using Raster sticker = background.Derive(ops => ops.Resize(40, 0).ChangeSpeed(1.5), Ct);

        #region sticker
        // A looping sticker on an animated background: the sticker keeps its own speed and loops for the background's whole length.
        background.Apply(ops => ops.DrawAnimation(sticker, new Point(10, 10), new AnimationOverlayOptions { Repeat = 0, Opacity = 0.9f }), Ct);
        background.Save("with-sticker.gif");
        #endregion

        Assert.True(background.FrameCount >= 1);
    }
}
