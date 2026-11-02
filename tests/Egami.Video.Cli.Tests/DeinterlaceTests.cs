using System.Text;

using Egami.Pixels;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// Video 1.2.0 Build Plan task 3: --deinterlace on encode, run, frames and the editing commands, on a woven Y4M clip whose
/// header says top field first. Without the option the frames pass as they were.
/// </summary>
public sealed class DeinterlaceTests
{
    private const int Width = 64;
    private const int Height = 48;
    private const int Frames = 6;

    [Fact]
    public void EncodeDeinterlacesAndKeepsTheRate()
    {
        using Tool tool = new();
        WriteWoven(tool.PathOf("woven.y4m"));
        Assert.Equal(0, tool.Run("encode", "woven.y4m", "-o", "plain.y4m").Code);
        ToolRun run = tool.Run("encode", "woven.y4m", "-o", "out.y4m", "--deinterlace");
        Assert.True(run.Code == 0, run.Error);

        List<byte[]> plain = Luma(tool.PathOf("plain.y4m"));
        List<byte[]> output = Luma(tool.PathOf("out.y4m"));
        Assert.Equal(Frames, output.Count);
        for (int n = 0; n < Frames; n++)
        {
            Assert.Equal(Woven(n), plain[n]);
            Assert.True(Psnr(output[n], Picture(2 * n)) > Psnr(Woven(n), Picture(2 * n)) + 6, $"frame {n}");
        }
    }

    [Fact]
    public void FieldRateDoublesTheFrames()
    {
        using Tool tool = new();
        WriteWoven(tool.PathOf("woven.y4m"));
        ToolRun run = tool.Run("encode", "woven.y4m", "-o", "out.y4m", "--deinterlace", "bob", "--deinterlace-rate", "field");
        Assert.True(run.Code == 0, run.Error);
        using VideoReader reader = VideoReader.Open(tool.PathOf("out.y4m"));
        Assert.Equal(new Rational(50, 1), reader.Track.FrameRate);
        Assert.Equal(2 * Frames, Luma(tool.PathOf("out.y4m")).Count);
    }

    [Fact]
    public void RunAndFramesDeinterlace()
    {
        using Tool tool = new();
        WriteWoven(tool.PathOf("woven.y4m"));
        ToolRun run = tool.Run("run", "woven.y4m", "--deinterlace", "--deinterlace-rate=field", "flip", "horizontal", "+", "save", "out.mkv", "--codec", "ffv1");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(2 * Frames, Luma(tool.PathOf("out.mkv")).Count);

        run = tool.Run("frames", "woven.y4m", "-o", "f/{frame:000}.png", "--deinterlace", "adaptive");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(Frames, FramesTests.Written(tool, "f").Length);
        run = tool.Run("frames", "woven.y4m", "-o", "one.png", "--at", "#2", "--deinterlace");
        Assert.True(run.Code == 0, run.Error);
        Assert.True(File.Exists(tool.PathOf("one.png")));

        Assert.Equal(2, tool.Run("encode", "woven.y4m", "-o", "bad.y4m", "--deinterlace", "fast").Code);
        Assert.Equal(2, tool.Run("encode", "woven.y4m", "-o", "bad.y4m", "--deinterlace-rate", "double").Code);
    }

    [Fact]
    public void EditCommandsDeinterlaceTheirClips()
    {
        using Tool tool = new();
        WriteWoven(tool.PathOf("woven.y4m"));
        Assert.Equal(0, tool.Run("encode", "woven.y4m", "-o", "woven.mkv", "--codec", "ffv1").Code);
        ToolRun run = tool.Run("fade", "woven.mkv", "--in", "0.04", "--deinterlace", "--deinterlace-frames", "all", "--save-edit", "edit.json", "-o", "faded.mkv", "--quality", "95");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("\"deinterlace\"", File.ReadAllText(tool.PathOf("edit.json")), StringComparison.Ordinal);
        List<byte[]> output = Luma(tool.PathOf("faded.mkv"));
        Assert.True(Psnr(output[3], Picture(6)) > Psnr(Woven(3), Picture(6)) + 3);

        // The edit file keeps the setting; the woven clip's FFV1 track does not say it is interlaced, hence --deinterlace-frames all.
        run = tool.Run("edit", "edit.json", "-o", "again.mkv", "--quality", "95");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(output[3], Luma(tool.PathOf("again.mkv"))[3]);
    }

    /// <summary>Progressive picture <paramref name="t"/> at the field rate: a bright bar moving 3 pixels a field over a ramp.</summary>
    private static byte[] Picture(int t)
    {
        byte[] luma = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                bool bar = x >= 4 + (3 * t) && x < 14 + (3 * t);
                luma[(y * Width) + x] = (byte)(bar ? 230 : 40 + (x * 2));
            }
        }

        return luma;
    }

    private static byte[] Woven(int n)
    {
        byte[] first = Picture(2 * n);
        byte[] second = Picture((2 * n) + 1);
        for (int y = 1; y < Height; y += 2)
        {
            second.AsSpan(y * Width, Width).CopyTo(first.AsSpan(y * Width, Width));
        }

        return first;
    }

    private static void WriteWoven(string path)
    {
        using FileStream file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W{Width} H{Height} F25:1 It A1:1 C420mpeg2\n"));
        byte[] chroma = new byte[Width * Height / 2];
        Array.Fill(chroma, (byte)128);
        for (int n = 0; n < Frames; n++)
        {
            file.Write("FRAME\n"u8);
            file.Write(Woven(n));
            file.Write(chroma);
        }
    }

    private static List<byte[]> Luma(string path)
    {
        List<byte[]> frames = [];
        using VideoReader reader = VideoReader.Open(path);
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                YuvPlane<byte> y = ((YuvRaster<byte>)frame.Raster).Y;
                byte[] luma = new byte[Width * Height];
                for (int row = 0; row < Height; row++)
                {
                    y.Row(row)[..Width].CopyTo(luma.AsSpan(row * Width, Width));
                }

                frames.Add(luma);
            }
        }

        return frames;
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }

        return sum == 0 ? 100 : 10 * Math.Log10(255.0 * 255 * a.Length / sum);
    }
}
