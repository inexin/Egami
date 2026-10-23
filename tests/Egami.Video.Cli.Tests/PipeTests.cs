using System.Diagnostics;

using Egami.Video.Codecs;

namespace Egami.Video.Cli.Tests;

/// <summary>Pipes (Build Plan task 11): Y4M and Matroska in on standard input as they arrive, clips and frames out on standard output.</summary>
public sealed class PipeTests
{
    [Fact]
    public void Y4mOnStandardInputMakesAClip()
    {
        using Tool tool = new();
        FramesTests.MakeClip(tool, "clip.y4m", 12, RawVideoCodec.Instance);
        byte[] y4m = File.ReadAllBytes(tool.PathOf("clip.y4m"));
        ToolRun run = tool.Run(["encode", "-", "-o", "out.mkv", "--codec", "raw"], y4m);
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(EncodeTests.Hashes(tool.PathOf("clip.y4m")), EncodeTests.Hashes(tool.PathOf("out.mkv")));
        Assert.Equal(new Rational(10, 1), EncodeTests.Video(tool.PathOf("out.mkv")).FrameRate);
    }

    [Fact]
    public void MatroskaOnStandardInputMakesAClip()
    {
        using Tool tool = new();
        FramesTests.MakeClip(tool, "clip.mkv", 6, VideoCodecs.MotionJpeg);
        ToolRun run = tool.Run(["encode", "-", "-o", "out.y4m"], File.ReadAllBytes(tool.PathOf("clip.mkv")));
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(EncodeTests.Hashes(tool.PathOf("clip.mkv")), EncodeTests.Hashes(tool.PathOf("out.y4m")));
    }

    [Theory]
    [InlineData("y4m")]
    [InlineData("mkv")]
    public void ClipsGoToStandardOutput(string format)
    {
        using Tool tool = new();
        FramesTests.MakeClip(tool, "clip.y4m", 8, RawVideoCodec.Instance);
        ToolRun run = tool.Run(["encode", "-", "-o", "-", "--format", format, "--codec", "raw"], File.ReadAllBytes(tool.PathOf("clip.y4m")));
        Assert.True(run.Code == 0, run.Error);
        File.WriteAllBytes(tool.PathOf($"piped.{format}"), run.Bytes);
        Assert.Equal(EncodeTests.Hashes(tool.PathOf("clip.y4m")), EncodeTests.Hashes(tool.PathOf($"piped.{format}")));
    }

    [Fact]
    public void ImagesToStandardOutput()
    {
        using Tool tool = new();
        EncodeTests.MakeImages(tool, "in", 4, 16, 16);
        ToolRun run = tool.Run("encode", "in/*.png", "-o", "-", "--format", "mkv");
        Assert.True(run.Code == 0, run.Error);
        File.WriteAllBytes(tool.PathOf("piped.mkv"), run.Bytes);
        Assert.Equal(4, EncodeTests.Hashes(tool.PathOf("piped.mkv")).Count);
    }

    [Fact]
    public void FramesRoundTripThroughPipes()
    {
        using Tool tool = new();
        EncodeTests.MakeImages(tool, "in", 5, 32, 24);
        ToolRun clip = tool.Run("encode", "in/*.png", "-o", "-", "--format", "mkv", "--quality", "90");
        Assert.True(clip.Code == 0, clip.Error);
        ToolRun frame = tool.Run(["frames", "-", "--at", "#3", "-o", "-", "--format", "png"], clip.Bytes);
        Assert.True(frame.Code == 0, frame.Error);
        File.WriteAllBytes(tool.PathOf("three.png"), frame.Bytes);
        Assert.True(EncodeTests.Psnr(tool, "in/003.png", "three.png", 1) > 35);
        using Raster image = Raster.Load(tool.PathOf("three.png"));
        Assert.Equal((32, 24), (image.Width, image.Height));
    }

    [Fact]
    public async Task FfmpegPipesY4mIn()
    {
        // ffmpeg -f yuv4mpegpipe - | egami-video encode - ...: run as two real processes when ffmpeg is on the PATH.
        string? ffmpeg = FindOnPath(OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (ffmpeg is null)
        {
            Assert.Skip("ffmpeg is not on the PATH.");
        }

        using Tool tool = new();
        string cli = Path.Combine(AppContext.BaseDirectory, "Egami.Video.Cli.dll");
        using Process source = Process.Start(new ProcessStartInfo(ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-frames:v", "15", "-pix_fmt", "yuv420p", "-f", "yuv4mpegpipe", "-"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        using Process encode = Process.Start(new ProcessStartInfo("dotnet", [cli, "encode", "-", "-o", "out.mkv", "--codec", "raw"])
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = tool.Folder,
        })!;
        Task<string> errors = encode.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await source.StandardOutput.BaseStream.CopyToAsync(encode.StandardInput.BaseStream, TestContext.Current.CancellationToken);
        encode.StandardInput.Close();
        await source.WaitForExitAsync(TestContext.Current.CancellationToken);
        await encode.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(encode.ExitCode == 0, await errors);
        Assert.Equal(15, EncodeTests.Hashes(tool.PathOf("out.mkv")).Count);
        Assert.Equal((64, 48), (EncodeTests.Video(tool.PathOf("out.mkv")).Width, EncodeTests.Video(tool.PathOf("out.mkv")).Height));
    }

    private static string? FindOnPath(string name)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
