namespace Egami.Video.Cli.Tests;

/// <summary>
/// FFV1 in egami-video (the V6 note on the Video hub's V1.1 row): the codec name in encode, run and ffmpeg mode, its expert
/// options, and the verify command over <c>Ffv1Codec.Verify</c>.
/// </summary>
public sealed class Ffv1Tests
{
    [Fact]
    public void EncodeWritesLosslessFfv1()
    {
        using Tool tool = new();
        EncodeTests.MakeImages(tool, "in", 4, 64, 48);
        ToolRun run = tool.Run("encode", "in/*.png", "--codec", "ffv1", "--chroma", "444", "-o", "clip.mkv");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(CodecIds.Ffv1, EncodeTests.Video(tool.PathOf("clip.mkv")).CodecId);
        Assert.Contains("FFV1 64x48", tool.Run("info", "clip.mkv").Text, StringComparison.Ordinal);

        ToolRun verify = tool.Run("verify", "clip.mkv");
        Assert.True(verify.Code == 0, verify.Error);
        Assert.Contains("clip.mkv: intact, 4 frames", verify.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeTakesTheExpertOptions()
    {
        using Tool tool = new();
        EncodeTests.MakeImages(tool, "in", 6, 64, 48);
        ToolRun run = tool.Run("encode", "in/*.png", "--codec", "ffv1", "--slices", "4", "--coder", "golomb", "--context", "large", "--keyframe-interval", "3", "--no-slice-crc", "-o", "clip.mkv");
        Assert.True(run.Code == 0, run.Error);

        using IDemuxer demuxer = MediaFile.OpenDemuxer(tool.PathOf("clip.mkv"));
        int keyframes = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            keyframes += packet.IsKeyframe ? 1 : 0;
            packet.Dispose();
        }

        Assert.Equal(2, keyframes);
        Assert.Contains("no slice CRCs to check", tool.Run("verify", "clip.mkv").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ffv1OptionsNeedFfv1()
    {
        using Tool tool = new();
        EncodeTests.MakeImages(tool, "in", 2, 32, 32);
        ToolRun run = tool.Run("encode", "in/*.png", "--slices", "4", "-o", "clip.mkv");
        Assert.Equal(2, run.Code);
        Assert.Contains("--codec ffv1", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RunKeepsFfv1AndTakesItsOptions()
    {
        using Tool tool = new();
        string clip = tool.Copy(Path.Combine("ffv1", "v3-420.mkv"));
        ToolRun run = tool.Run("run", clip, "flip", "horizontal", "--slices", "1", "-o", "out.mkv");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(CodecIds.Ffv1, EncodeTests.Video(tool.PathOf("out.mkv")).CodecId);
        Assert.Equal(0, tool.Run("verify", "out.mkv").Code);
    }

    [Fact]
    public void VerifyFindsDamage()
    {
        using Tool tool = new();
        string clip = tool.Copy(Path.Combine("ffv1", "v3-420.mkv"));
        byte[] data = File.ReadAllBytes(tool.PathOf(clip));
        data[data.Length * 2 / 3] ^= 0x5A;
        File.WriteAllBytes(tool.PathOf("damaged.mkv"), data);

        ToolRun run = tool.Run("verify", "damaged.mkv", clip, "--json");
        Assert.Equal(1, run.Code);
        Assert.Contains("\"intact\": false", run.Text, StringComparison.Ordinal);
        Assert.Contains("\"intact\": true", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyRefusesOtherCodecs()
    {
        using Tool tool = new();
        string clip = tool.Copy("webm-vp8-opus.webm");
        Assert.Equal(1, tool.Run("verify", clip).Code);
    }

    [Theory]
    [InlineData("run in.mkv -o out.mkv --codec ffv1 --coder range-default --context large --slices 4 --no-slice-crc --keyframe-interval 1", "-i in.mkv -c:v ffv1 -level 3 -coder 1 -context 1 -slices 4 -slicecrc 0 -g 1 out.mkv")]
    [InlineData("encode img{frame:000}.png -o out.mkv --rate 10 --codec ffv1 --keyframe-interval 5", "-framerate 10 -i img%03d.png -c:v ffv1 -g 5 out.mkv")]
    public void FfmpegModeMapsFfv1Options(string expected, string line)
    {
        using Tool tool = new();
        ToolRun run = tool.Run(["ffmpeg", .. line.Split(' '), "--explain"]);
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal("egami-video " + expected, run.Text.Trim());
    }

    [Theory]
    [InlineData("-i in.mkv -c:v ffv1 -level 1 out.mkv", "-level 1")]
    [InlineData("-i in.mkv -c:v mjpeg -slices 4 out.mkv", "-slices")]
    [InlineData("-i in.mkv -c copy -g 10 out.mkv", "-g")]
    public void FfmpegModeRefusesWhatItCannotMap(string line, string option)
    {
        using Tool tool = new();
        ToolRun run = tool.Run(["ffmpeg", .. line.Split(' '), "--explain"]);
        Assert.Equal(2, run.Code);
        Assert.Contains(option, run.Error, StringComparison.Ordinal);
    }
}
