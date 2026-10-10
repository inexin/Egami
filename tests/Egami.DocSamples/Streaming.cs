using Egami.Formats;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Streaming
{
    [Fact]
    public void DecodeRows()
    {
        using SampleFiles files = new();
        long sum = 0;

        #region decode-rows
        // A band of rows at a time: memory is the band plus the format's working set, whatever the image size.
        using RasterDecoder decoder = RasterDecoder.Open("egret.png");
        Console.WriteLine($"{decoder.Info.Width}×{decoder.Info.Height}, {decoder.Info.Access}, holding {decoder.Info.WorkingBytes:N0} bytes");
        using Raster<Gray8> band = Raster.Create<Gray8>(decoder.Info.Width, 64);
        int rows;
        while ((rows = decoder.ReadRows(band)) > 0)
        {
            for (int y = 0; y < rows; y++)
            {
                foreach (Gray8 pixel in band.Row(y))
                {
                    sum += pixel.L;
                }
            }
        }
        #endregion

        Assert.True(sum > 0);
    }

    [Fact]
    public void RegionAndReduction()
    {
        using SampleFiles files = new();

        #region region
        // Only a rectangle, at a quarter of its size or a little more.
        ReaderOptions options = new() { Region = new Rect(100, 200, 256, 256), MinimumSize = new Size(64, 64) };
        RasterInfo info = Raster.Probe("egret.png", options: options);   // Access and WorkingBytes before decoding
        using Raster tile = Raster.Load("egret.png", options: options);
        #endregion

        Assert.Equal((64, 64), (tile.Width, tile.Height));
        Assert.Equal(RasterAccess.Streamed, info.Access);
    }

    [Fact]
    public void LoadInto()
    {
        using SampleFiles files = new();

        #region load-into
        // Decode into a raster you already have: video frames and pooled images reuse their memory.
        using Raster<Rgb8> reused = Raster.Create<Rgb8>(480, 640);
        Raster.LoadInto("egret.png", reused);
        #endregion

        Assert.NotEqual(default, reused.GetPixel(10, 10));
    }

    [Fact]
    public void Frames()
    {
        using SampleFiles files = new();
        int count = 0;

        #region frames
        // An animation one composed frame at a time, into one reused canvas.
        using RasterDecoder gif = RasterDecoder.Open("pan.gif");
        using Raster<Rgba8> canvas = Raster.Create<Rgba8>(gif.Info.Width, gif.Info.Height);
        while (gif.ReadFrame(canvas) is Metadata.FrameMetadata frame)
        {
            Console.WriteLine($"frame {count++}, shown for {frame.Delay.TotalMilliseconds} ms");
        }
        #endregion

        Assert.True(count > 1);
    }
}
