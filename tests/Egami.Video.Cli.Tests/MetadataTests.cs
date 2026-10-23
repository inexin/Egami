using System.Text.Json;
using System.Text.Json.Nodes;

namespace Egami.Video.Cli.Tests;

/// <summary><c>metadata</c> (Build Plan task 8): tags, chapters, track fields and attachments as JSON, and back into a copy.</summary>
public sealed class MetadataTests
{
    private static readonly string Clip = Path.Combine(Tool.Corpus, "mkv-metadata.mkv");

    [Fact]
    public void DumpsTagsChaptersTracksAndAttachments()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("metadata", Clip);
        Assert.True(run.Code == 0, run.Error);
        JsonNode root = JsonNode.Parse(run.Text)!;
        Assert.Equal(1, (int)root["schema"]!);
        JsonNode file = root["files"]![0]!;
        Assert.Equal("Corpus clip", (string?)file["tags"]!["TITLE"]);
        Assert.Equal(["Opening", "Rest"], file["chapters"]!.AsArray().Select(c => (string?)c!["title"]));
        Assert.Equal(0.8, (double)file["chapters"]![0]!["end"]!);
        JsonNode audio = file["tracks"]![1]!;
        Assert.Equal("Tone", (string?)audio["name"]);
        Assert.Equal("nor", (string?)audio["language"]);
        Assert.False((bool)audio["default"]!);
        Assert.Equal("cover.jpg", (string?)file["attachments"]![0]!["name"]);
    }

    [Fact]
    public void DumpToAFileAndFromStandardInput()
    {
        using Tool tool = new();
        Assert.Equal(0, Ok(tool.Run("metadata", Clip, "-o", "meta.json")));
        Assert.Equal("Corpus clip", (string?)JsonNode.Parse(File.ReadAllText(tool.PathOf("meta.json")))!["files"]![0]!["tags"]!["TITLE"]);
        ToolRun piped = tool.Run(["metadata", "-", "--track", "a"], File.ReadAllBytes(Clip));
        Assert.True(piped.Code == 0, piped.Error);
        JsonNode file = JsonNode.Parse(piped.Text)!["files"]![0]!;
        Assert.Equal("audio", (string?)Assert.Single(file["tracks"]!.AsArray())!["kind"]);
    }

    [Fact]
    public void DumpThenLoadRoundTrips()
    {
        using Tool tool = new();
        Assert.Equal(0, Ok(tool.Run("metadata", Clip, "-o", "meta.json")));
        Assert.Equal(0, Ok(tool.Run("metadata", Clip, "--load", "meta.json", "--clear", "-o", "copy.mkv")));
        Assert.Equal(0, Ok(tool.Run("metadata", "copy.mkv", "-o", "again.json")));
        Assert.Equal(Comparable(tool.PathOf("meta.json")), Comparable(tool.PathOf("again.json")));
        Assert.Equal(RemuxTests.Packets(Clip), RemuxTests.Packets(tool.PathOf("copy.mkv")));
        TrackTests.FfprobeReads(tool.PathOf("copy.mkv"));
    }

    [Fact]
    public void LoadChangesOnlyWhatTheJsonNames()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("edit.json"), """
            {
              "tags": { "TITLE": "New title", "ARTIST": null, "YEAR": 2026 },
              "tracks": [ { "number": 2, "name": "Beep", "default": true, "tags": { "MOOD": "calm" } } ]
            }
            """);
        Assert.Equal(0, Ok(tool.Run("metadata", Clip, "--load", "edit.json", "-o", "out.mkv")));
        MediaInfo info = MediaFile.Probe(tool.PathOf("out.mkv"));
        Assert.Equal("New title", info.Tags["TITLE"]);
        Assert.False(info.Tags.ContainsKey("ARTIST"));
        Assert.Equal("2026", info.Tags["YEAR"]);
        Assert.Equal("Test clip", info.Tags["COMMENT"]);
        Assert.Equal(2, info.Chapters.Count);
        Assert.Single(info.Attachments);
        TrackInfo audio = info.Tracks[1];
        Assert.Equal("Beep", audio.Name);
        Assert.Equal("nor", audio.Language);
        Assert.True(audio.IsDefault);
        Assert.Equal("calm", audio.Tags["MOOD"]);
    }

    [Fact]
    public void ClearDropsWhatTheJsonLeavesOut()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("edit.json"), """{ "schema": 1, "tags": { "TITLE": "Only this" }, "chapters": [ { "start": "0:01", "title": "One" } ] }""");
        Assert.Equal(0, Ok(tool.Run("metadata", Clip, "--load", "edit.json", "--clear", "-o", "out.mkv")));
        MediaInfo info = MediaFile.Probe(tool.PathOf("out.mkv"));
        Assert.Equal("Only this", info.Tags["TITLE"]);
        Assert.False(info.Tags.ContainsKey("ARTIST"));
        ChapterInfo chapter = Assert.Single(info.Chapters);
        Assert.Equal(1, chapter.Start.Seconds, 3);
        Assert.Empty(info.Attachments);
        Assert.Null(info.Tracks[1].Name);
        Assert.Null(info.Tracks[1].Language);

        Assert.Equal(0, Ok(tool.Run("metadata", Clip, "--clear", "-o", "bare.mkv")));
        MediaInfo bare = MediaFile.Probe(tool.PathOf("bare.mkv"));
        Assert.False(bare.Tags.ContainsKey("TITLE"));
        Assert.Empty(bare.Chapters);
    }

    [Fact]
    public void SetRemoveAndChapter()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("metadata", Clip, "--set", "TITLE=Holiday", "--set", "GENRE=Test", "--remove", "artist", "--chapter", "1.5-1.9=Late", "--chapter", "0:00.400=Early", "-o", "out.mkv");
        Assert.True(run.Code == 0, run.Error);
        MediaInfo info = MediaFile.Probe(tool.PathOf("out.mkv"));
        Assert.Equal("Holiday", info.Tags["TITLE"]);
        Assert.Equal("Test", info.Tags["GENRE"]);
        Assert.False(info.Tags.ContainsKey("ARTIST"));
        Assert.Equal(["Opening", "Early", "Rest", "Late"], info.Chapters.Select(c => c.Title));
        Assert.Equal(1.9, info.Chapters[3].End!.Value.Seconds, 3);
        TrackTests.FfprobeReads(tool.PathOf("out.mkv"));
    }

    [Theory]
    [InlineData("audio/flac-tags-picture.flac", "out.flac")]
    [InlineData("audio/wav-tags.wav", "out.wav")]
    [InlineData("mp4/mp4-metadata.mp4", "out.mp4")]
    [InlineData("avi-tags.avi", "out.avi")]
    [InlineData("webm-vp8-opus.webm", "out.webm")]
    public void EditsOtherContainers(string clip, string output)
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, clip);
        Assert.Equal(0, Ok(tool.Run("metadata", source, "--set", "TITLE=Edited", "-o", output)));
        Assert.Equal("Edited", MediaFile.Probe(tool.PathOf(output)).Tags["TITLE"]);
        Assert.Equal(RemuxTests.Packets(source).Count, RemuxTests.Packets(tool.PathOf(output)).Count);
        TrackTests.FfprobeReads(tool.PathOf(output));
    }

    [Fact]
    public void WritesToStandardOutput()
    {
        using Tool tool = new();
        ToolRun run = tool.Run(["metadata", "-", "--set", "TITLE=Piped", "-o", "-", "--format", "mkv"], File.ReadAllBytes(Clip));
        Assert.True(run.Code == 0, run.Error);
        File.WriteAllBytes(tool.PathOf("piped.mkv"), run.Bytes);
        Assert.Equal("Piped", MediaFile.Probe(tool.PathOf("piped.mkv")).Tags["TITLE"]);
    }

    [Fact]
    public void UsageErrors()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("v2.json"), """{ "schema": 2 }""");
        File.WriteAllText(tool.PathOf("bad.json"), "{ nope");
        File.WriteAllText(tool.PathOf("attach.json"), """{ "attachments": [ { "name": "font.ttf" } ] }""");
        File.WriteAllText(tool.PathOf("tags.json"), """{ "tags": [ "TITLE" ] }""");
        Assert.Contains("KEY=value", Usage(tool.Run("metadata", Clip, "--set", "TITLE", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("is not a time", Usage(tool.Run("metadata", Clip, "--chapter", "soon=Intro", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("ends before it starts", Usage(tool.Run("metadata", Clip, "--chapter", "5-2=Intro", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("not frame numbers", Usage(tool.Run("metadata", Clip, "--chapter", "#3=Intro", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("does not exist", Usage(tool.Run("metadata", Clip, "--load", "missing.json", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("schema 2", Usage(tool.Run("metadata", Clip, "--load", "v2.json", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("is not JSON", Usage(tool.Run("metadata", Clip, "--load", "bad.json", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("embed --attach", Usage(tool.Run("metadata", Clip, "--load", "attach.json", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("must be an object", Usage(tool.Run("metadata", Clip, "--load", "tags.json", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("No output", Usage(tool.Run("metadata", Clip, "--set", "TITLE=x")), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(tool.Folder, "out*"));
    }

    [Fact]
    public void LoadPicksTheFileByPath()
    {
        using Tool tool = new();
        tool.Copy("mkv-metadata.mkv", "a.mkv");
        tool.Copy("webm-vp8-opus.webm", "b.webm");
        Assert.Equal(0, Ok(tool.Run("metadata", "a.mkv", "b.webm", "-o", "meta.json")));
        JsonNode root = JsonNode.Parse(File.ReadAllText(tool.PathOf("meta.json")))!;
        root["files"]![1]!["tags"]!["TITLE"] = "Second";
        File.WriteAllText(tool.PathOf("meta.json"), root.ToJsonString());
        Assert.Equal(0, Ok(tool.Run("metadata", "b.webm", "--load", "meta.json", "-o", "out.webm")));
        Assert.Equal("Second", MediaFile.Probe(tool.PathOf("out.webm")).Tags["TITLE"]);
        tool.Copy("avi-tags.avi", "c.avi");
        Assert.Contains("none is", Usage(tool.Run("metadata", "c.avi", "--load", "meta.json", "-o", "out.mkv")), StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledJobLeavesNoFile()
    {
        using Tool tool = new() { Cancellation = new CancellationToken(canceled: true) };
        Assert.Equal(130, tool.Run("metadata", Clip, "--set", "TITLE=x", "-o", "out.mkv").Code);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    /// <summary>A dump's tags (less the muxing app the writer sets), chapters, tracks and attachments, as text to compare.</summary>
    private static string Comparable(string json)
    {
        JsonNode file = JsonNode.Parse(File.ReadAllText(json))!["files"]![0]!;
        JsonObject tags = file["tags"]!.AsObject();
        tags.Remove("MUXINGAPP");
        tags.Remove("WRITINGAPP");
        JsonObject kept = new()
        {
            ["tags"] = tags.DeepClone(),
            ["chapters"] = file["chapters"]!.DeepClone(),
            ["tracks"] = file["tracks"]!.DeepClone(),
            ["attachments"] = file["attachments"]!.DeepClone(),
        };
        return kept.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static int Ok(ToolRun run)
    {
        Assert.True(run.Code == 0, run.Error);
        return run.Code;
    }

    private static string Usage(ToolRun run)
    {
        Assert.True(run.Code == 2, $"exit {run.Code}: {run.Error}");
        return run.Error;
    }
}
