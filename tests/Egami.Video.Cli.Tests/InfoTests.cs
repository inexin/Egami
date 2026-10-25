using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Egami.Video.Cli.Commands;
using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// <c>info</c> (Build Plan tasks 4 and 17): every format, checked against the ffprobe dumps beside the corpus clips
/// (tools/video/make-corpus.py): track counts, codecs, sizes, packet counts and timestamps.
/// </summary>
public sealed class InfoTests
{
    public static TheoryData<string> CorpusClips()
    {
        TheoryData<string> clips = [];
        foreach (string json in Directory.GetFiles(Tool.Corpus, "*.json").Order(StringComparer.Ordinal))
        {
            string clip = json[..^5];
            if (File.Exists(clip))
            {
                clips.Add(Path.GetFileName(clip));
            }
        }

        return clips;
    }

    [Fact]
    public void TextSummary()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("info", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"));
        Assert.Equal(0, run.Code);
        Assert.Contains("AVI, 0:00:02.000", run.Text, StringComparison.Ordinal);
        Assert.Contains("#0 video (v:0): Motion JPEG 160x120 25 fps", run.Text, StringComparison.Ordinal);
        Assert.Contains("#1 audio (a:0): PCM 8000 Hz mono 16-bit", run.Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CorpusClips))]
    public void PacketsMatchFfprobe(string clip)
    {
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Tool.Corpus, clip + ".json")));
        using Tool tool = new();
        ToolRun run = tool.Run("info", Path.Combine(Tool.Corpus, clip), "--format", "ffprobe", "--packets");
        Assert.True(run.Code == 0, run.Error);
        using JsonDocument actual = JsonDocument.Parse(run.Text);

        // ffprobe lists Matroska cover art as a video stream (attached_pic); egami-video lists it as an attachment.
        JsonElement expectedStreams = JsonDocument.Parse(JsonSerializer.Serialize(expected.RootElement.GetProperty("streams").EnumerateArray()
            .Where(s => !s.TryGetProperty("attached_pic", out _)).ToList())).RootElement;
        JsonElement actualStreams = actual.RootElement.GetProperty("streams");
        Assert.Equal(expectedStreams.GetArrayLength(), actualStreams.GetArrayLength());
        for (int i = 0; i < expectedStreams.GetArrayLength(); i++)
        {
            JsonElement want = expectedStreams[i], got = actualStreams[i];
            Assert.Equal(want.GetProperty("codec_type").GetString(), got.GetProperty("codec_type").GetString());
            if (want.TryGetProperty("width", out JsonElement width))
            {
                Assert.Equal(width.GetInt32(), got.GetProperty("width").GetInt32());
                Assert.Equal(want.GetProperty("height").GetInt32(), got.GetProperty("height").GetInt32());
            }

            if (want.TryGetProperty("sample_rate", out JsonElement rate))
            {
                Assert.Equal(rate.GetString(), got.GetProperty("sample_rate").GetString());
                Assert.Equal(want.GetProperty("channels").GetInt32(), got.GetProperty("channels").GetInt32());
            }
        }

        if (!expected.RootElement.TryGetProperty("packets", out JsonElement expectedPackets))
        {
            return;
        }

        // Packet counts per stream equal ffprobe's; timestamps agree in seconds.
        List<JsonElement> actualPackets = [.. actual.RootElement.GetProperty("packets").EnumerateArray()];
        for (int stream = 0; stream < expectedStreams.GetArrayLength(); stream++)
        {
            double expectedBase = TimeBase(expectedStreams[stream]);
            double actualBase = TimeBase(actualStreams[stream]);
            List<double> want = [.. expectedPackets.EnumerateArray().Where(p => p.GetProperty("stream_index").GetInt32() == stream && p.TryGetProperty("pts", out _)).Select(p => p.GetProperty("pts").GetInt64() * expectedBase).Order()];
            List<double> got = [.. actualPackets.Where(p => p.GetProperty("stream_index").GetInt32() == stream).Select(p => p.GetProperty("pts").GetInt64() * actualBase).Order()];
            if (want.Count == 0)
            {
                continue;
            }

            // Laced Vorbis frames in a block with no duration carry no times of their own: ffprobe spaces them by a guessed
            // duration (0, 32, 64 ms, then the next block at 80), egami-video by each packet's own length (0, 0, 32 ms, the
            // first Vorbis packet giving no samples). The counts still agree.
            if (clip == "mkv-vorbis-xiph-laced.mkv" && expectedStreams[stream].GetProperty("codec_name").GetString() == "vorbis")
            {
                Assert.Equal(want.Count, got.Count);
                continue;
            }

            Assert.Equal(want.Count, got.Count);
            double tolerance = Math.Max(expectedBase, actualBase) + 1e-6;
            for (int i = 0; i < want.Count; i++)
            {
                Assert.True(Math.Abs(want[i] - got[i]) <= tolerance, $"{clip} stream {stream} packet {i}: ffprobe {want[i]}, egami-video {got[i]}");
            }
        }
    }

    [Fact]
    public void JsonAndXmlCarryTheSameFields()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-metadata.mkv");
        ToolRun json = tool.Run("info", clip, "--json", "--all");
        ToolRun xml = tool.Run("info", clip, "--format", "xml", "--all");
        Assert.Equal(0, json.Code);
        Assert.Equal(0, xml.Code);
        using JsonDocument document = JsonDocument.Parse(json.Text);
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        JsonElement file = document.RootElement.GetProperty("files")[0];
        XElement xfile = XDocument.Parse(xml.Text).Root!.Element("files")!.Element("file")!;
        foreach (JsonProperty property in file.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null)
            {
                continue;
            }

            Assert.True(xfile.Element(property.Name) is not null, $"XML lacks {property.Name}");
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                Assert.Equal(property.Value.ToString(), xfile.Element(property.Name)!.Value);
            }
        }

        Assert.Equal(file.GetProperty("tracks").GetArrayLength(), xfile.Element("tracks")!.Elements("track").Count());
        Assert.Equal(file.GetProperty("chapters").GetArrayLength(), xfile.Element("chapters")!.Elements("chapter").Count());
    }

    [Fact]
    public void CsvListsPackets()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("info", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"), "--csv", "--packets", "--track", "v");
        Assert.Equal(0, run.Code);
        string[] lines = run.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("path,track,kind,pts,ptsTime,dts,", lines[0], StringComparison.Ordinal);
        Assert.Equal(51, lines.Length);
    }

    [Fact]
    public void AudioFields()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("info", Path.Combine(Tool.Corpus, "audio", "flac-24-51.flac"), "--json");
        Assert.Equal(0, run.Code);
        using JsonDocument document = JsonDocument.Parse(run.Text);
        JsonElement track = document.RootElement.GetProperty("files")[0].GetProperty("tracks")[0];
        Assert.Equal("audio", track.GetProperty("kind").GetString());
        Assert.Equal(6, track.GetProperty("channels").GetInt32());
        Assert.Equal("5.1", track.GetProperty("layout").GetString());
        Assert.Equal(24, track.GetProperty("bitsPerSample").GetInt32());
        Assert.Equal("s32", track.GetProperty("sampleFormat").GetString());
    }

    [Theory]
    [InlineData(CodecIds.Mp3, "MP3", "mp3")]
    [InlineData(CodecIds.Mp2, "MP2", "mp2")]
    [InlineData(CodecIds.Mp1, "MP1", "mp1")]
    public void MpegAudioNames(string codecId, string name, string ffprobeName)
    {
        AudioTrackInfo track = new() { Number = 1, Kind = TrackKind.Audio, CodecId = codecId, TimeBase = new Rational(1, 44100), SampleRate = 44100, Channels = 2 };
        Assert.Equal(name, CodecNames.Name(codecId));
        Assert.Equal(ffprobeName, CodecNames.FfprobeName(track));
        Assert.Equal("fltp", InfoCommand.TrackSampleFormat(track));
    }

    [Fact]
    public void FramesDecodes()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("info", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"), "--frames", "--track", "v", "--json");
        Assert.Equal(0, run.Code);
        using JsonDocument document = JsonDocument.Parse(run.Text);
        JsonElement frames = document.RootElement.GetProperty("files")[0].GetProperty("frames");
        Assert.Equal(50, frames.GetArrayLength());
        Assert.Equal(160, frames[0].GetProperty("width").GetInt32());
    }

    [Fact]
    public void StandardInput()
    {
        using Tool tool = new();
        byte[] clip = File.ReadAllBytes(Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"));
        ToolRun run = tool.Run(["info", "-", "--all"], clip);
        Assert.True(run.Code == 0, run.Error);
        Assert.StartsWith("-: Matroska", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageErrors()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi");
        Assert.Equal(2, tool.Run("info", clip, "--format", "yaml").Code);
        Assert.Equal(2, tool.Run("info", clip, "--json", "--csv").Code);
        ToolRun missing = tool.Run("info", clip, "--track", "s");
        Assert.Equal(2, missing.Code);
        Assert.Contains("--track s matches no track", missing.Error, StringComparison.Ordinal);
        Assert.Equal(1, tool.Run("info", tool.PathOf("missing.mkv")).Code);
    }

    private static double TimeBase(JsonElement stream)
    {
        string[] parts = stream.GetProperty("time_base").GetString()!.Split('/');
        return double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
    }
}
