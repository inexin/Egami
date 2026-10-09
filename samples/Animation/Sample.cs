using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Samples.Animation;

/// <summary>Edits a GIF's frames, saves it as GIF and APNG, and builds a new animation from one photo.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        RasterInfo info = Raster.Probe(SampleRunner.Image("pan.gif"));
        Console.WriteLine($"pan.gif: {info.FrameCount} frames, plays {info.LoopCount} times (0 = forever)");

        using Raster<Rgba8> clip = Raster.Load<Rgba8>(SampleRunner.Image("pan.gif"));
        clip.Frames.Move(clip.FrameCount - 1, 0);
        clip.Frames.Add(clip.Frames[0]).Metadata.Delay = TimeSpan.FromSeconds(1);
        clip.Apply(ops => ops.Grayscale());
        clip.Save(Path.Combine(output, "pan-gray.gif"));
        clip.Save(Path.Combine(output, "pan-gray.png"));

        using Raster<Rgba8> hues = Raster.Load<Rgba8>(SampleRunner.Image("duck.png"));
        hues.Apply(ops => ops.Fit(160, 160));
        for (int degrees = 45; degrees < 360; degrees += 45)
        {
            int angle = degrees;
            using Raster<Rgba8> frame = hues.CopyFrame(0);
            frame.Apply(ops => ops.HueRotate(angle));
            hues.Frames.Add(frame.Frames[0]);
        }

        foreach (Frame<Rgba8> frame in hues.Frames)
        {
            frame.Metadata.Delay = TimeSpan.FromMilliseconds(150);
        }

        using FileStream gif = File.Create(Path.Combine(output, "hues.gif"));
        hues.Save(gif, GifFormat.Info, new GifWriterOptions { Palette = new PaletteOptions { Dither = DitherMode.Ordered } });
    }
}
