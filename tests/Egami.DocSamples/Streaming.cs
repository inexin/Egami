using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Formats.Jpeg;
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
    public void Incremental()
    {
        using SampleFiles files = new();
        byte[] file = File.ReadAllBytes("egret.png");
        int rowsSoFar = 0;

        #region incremental
        // Decode while the bytes arrive, a chunk at a time, as from a download.
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        Raster<Rgb8>? band = null;
        foreach (ReadOnlyMemory<byte> chunk in file.Chunk(4096).Select(c => new ReadOnlyMemory<byte>(c)))
        {
            decoder.Append(chunk.Span);
            if (decoder.HeaderRead)
            {
                band ??= Raster.Create<Rgb8>(decoder.Info.Width, 16);
                int rows;
                while ((rows = decoder.ReadRows(band)) > 0)
                {
                    rowsSoFar += rows; // the rows finished so far; an interlaced file shows RenderPreview instead
                }
            }
        }

        decoder.EndOfInput();
        while (decoder.ReadRows(band!) is int rest and > 0)
        {
            rowsSoFar += rest;
        }

        band?.Dispose();
        #endregion

        Assert.Equal(640, rowsSoFar);
    }

    [Fact]
    public async Task Async()
    {
        using SampleFiles files = new();
        long sum = 0;

        #region async
        // On a server: read rows as the upload arrives, without holding the whole file.
        await using Stream upload = File.OpenRead("egret.png");
        using RasterDecoder decoder = await RasterDecoder.OpenAsync(upload, cancellationToken: TestContext.Current.CancellationToken);
        using Raster<Gray8> band = Raster.Create<Gray8>(decoder.Info.Width, 32);
        int rows;
        while ((rows = await decoder.ReadRowsAsync(band, TestContext.Current.CancellationToken)) > 0)
        {
            for (int y = 0; y < rows; y++)
            {
                sum += band.Row(y)[0].L;
            }
        }
        #endregion

        Assert.True(sum > 0);
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

    [Fact]
    public void EncodeRows()
    {
        using SampleFiles files = new();

        #region encode-rows
        // A huge image written a band at a time: only the band is ever in memory.
        const int Width = 8_000, Height = 6_000;
        using Raster<Rgb8> band = Raster.Create<Rgb8>(Width, 64);
        using RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>("big.png", Width, Height);
        for (int top = 0; top < Height; top += band.Height)
        {
            int rows = Math.Min(band.Height, Height - top);
            for (int y = 0; y < rows; y++)
            {
                band.Row(y).Fill(new Rgb8((byte)(top + y), 128, 200));
            }

            encoder.WriteRows(band, rows);
        }

        encoder.Complete(); // writes IEND; without it the file is deleted on dispose
        #endregion

        encoder.Dispose();
        RasterInfo info = Raster.Probe("big.png");
        Assert.Equal((Width, Height), (info.Width, info.Height));
    }

    [Fact]
    public void EncodeFrames()
    {
        using SampleFiles files = new();

        #region encode-frames
        // An animation written a frame at a time, each frame from one reused canvas.
        using Raster<Rgba8> canvas = Raster.Create<Rgba8>(120, 80);
        using FileStream file = File.Create("spinner.gif");
        using RasterEncoder<Rgba8> gif = RasterEncoder.Create<Rgba8>(file, GifFormat.Info, 120, 80);
        for (int i = 0; i < 12; i++)
        {
            for (int y = 0; y < canvas.Height; y++)
            {
                canvas.Row(y).Fill(new Rgba8(255, 255, 255, 255));
            }

            canvas.Row(40)[(i * 10)..((i * 10) + 10)].Fill(new Rgba8(200, 0, 0, 255));
            gif.WriteFrame(canvas, new Metadata.FrameMetadata { Delay = TimeSpan.FromMilliseconds(80) });
        }

        gif.Complete();
        #endregion

        gif.Dispose();
        file.Dispose();
        Assert.Equal(12, Raster.Probe("spinner.gif").FrameCount);
    }

    [Fact]
    public async Task EncodeAsync()
    {
        using SampleFiles files = new();
        using MemoryStream response = new();

        #region encode-async
        // On a server: send each band as it is encoded, without holding the file.
        using RasterDecoder source = RasterDecoder.Open("egret.png");
        using Raster<Rgb8> band = Raster.Create<Rgb8>(source.Info.Width, 32);
        using RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(response, JpegFormat.Info, source.Info.Width, source.Info.Height);
        int rows;
        while ((rows = source.ReadRows(band)) > 0)
        {
            await encoder.WriteRowsAsync(band, rows, TestContext.Current.CancellationToken);
        }

        await encoder.CompleteAsync(TestContext.Current.CancellationToken);
        #endregion

        Assert.True(response.Length > 0);
    }
}
