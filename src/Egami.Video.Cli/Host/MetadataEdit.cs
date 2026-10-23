using System.Text.Json;

namespace Egami.Video.Cli.Host;

/// <summary>
/// The edits of <c>metadata</c> (Build Plan task 8): tags, chapters, track fields and attachments from a JSON file in the
/// <c>metadata</c> dump's schema (docs/schemas/egami-video-metadata-1.json), then single tags set or removed and chapters added.
/// Keys missing from the JSON stay as they were, unless <see cref="Clear"/> drops them.
/// </summary>
internal sealed class MetadataEdit
{
    /// <summary>Gets or sets the loaded JSON document, or <see langword="null"/> without <c>--load</c>.</summary>
    public JsonDocument? Loaded { get; set; }

    /// <summary>Gets or sets the path of the loaded JSON, for messages.</summary>
    public string LoadedPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether what the JSON leaves out is dropped.</summary>
    public bool Clear { get; set; }

    /// <summary>Gets the tags to set, in order.</summary>
    public List<(string Key, string Value)> Set { get; } = [];

    /// <summary>Gets the tags to remove.</summary>
    public List<string> Remove { get; } = [];

    /// <summary>Gets the chapters to add.</summary>
    public List<ChapterInfo> Chapters { get; } = [];

    /// <summary>Gets a value indicating whether there is anything to change.</summary>
    public bool Any => Loaded is not null || Clear || Set.Count > 0 || Remove.Count > 0 || Chapters.Count > 0;

    /// <summary>Reads <c>--load</c>'s file, checking it is a metadata JSON of schema 1.</summary>
    public static JsonDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new UsageException($"--load '{path}' does not exist.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException error)
        {
            throw new UsageException($"--load '{path}' is not JSON: {error.Message}");
        }

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new UsageException($"--load '{path}' must hold an object, as 'egami-video metadata' writes.");
        }

        if (root.TryGetProperty("schema", out JsonElement schema) && !(schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int version) && version == 1))
        {
            throw new UsageException($"--load '{path}' has schema {schema.GetRawText()}; egami-video reads schema 1.");
        }

        return document;
    }

    /// <summary>Parses a <c>--chapter</c> value: <c>start[-end]=title</c>.</summary>
    public static ChapterInfo ParseChapter(string text)
    {
        int equals = text.IndexOf('=', StringComparison.Ordinal);
        string times = equals < 0 ? text : text[..equals];
        string? title = equals < 0 ? null : text[(equals + 1)..];
        int dash = times.IndexOf('-', StringComparison.Ordinal);
        TimeValue start = TimeValue.Parse(dash < 0 ? times : times[..dash], "--chapter");
        TimeValue? end = dash < 0 ? null : TimeValue.Parse(times[(dash + 1)..], "--chapter");
        if (start.IsFrame || end is { IsFrame: true })
        {
            throw new UsageException($"--chapter '{text}' takes times, not frame numbers: start[-end]=title, such as 1:30-2:45=Intro.");
        }

        if (end is { } e && e.Seconds <= start.Seconds)
        {
            throw new UsageException($"--chapter '{text}' ends before it starts.");
        }

        return new ChapterInfo(MediaTime.FromSeconds(start.Seconds), end is { } stop ? MediaTime.FromSeconds(stop.Seconds) : null, string.IsNullOrEmpty(title) ? null : title);
    }

    /// <summary>The input's tracks and metadata with the edits applied.</summary>
    public (List<TrackInfo> Tracks, MediaMetadata Metadata) Apply(MediaInfo info, IReadOnlyList<TrackInfo> tracks, InputFile input)
    {
        JsonElement? entry = Loaded is null ? null : Entry(Loaded.RootElement, input);
        Dictionary<string, string> tags = Clear ? new(StringComparer.Ordinal) : new(info.Tags, StringComparer.Ordinal);
        List<ChapterInfo> chapters = Clear ? [] : [.. info.Chapters];
        List<AttachmentInfo> attachments = Clear ? [] : [.. info.Attachments];
        List<TrackInfo> result = [.. tracks.Select(t => Clear ? t with { Language = null, Name = null, Tags = new Dictionary<string, string>() } : t)];
        if (entry is { } file)
        {
            if (file.TryGetProperty("tags", out JsonElement tagList))
            {
                ApplyTags(tags, tagList, "tags");
            }

            if (file.TryGetProperty("chapters", out JsonElement chapterList))
            {
                chapters = [.. Items(chapterList, "chapters").Select(ReadChapter)];
            }

            if (file.TryGetProperty("attachments", out JsonElement attachmentList))
            {
                attachments = [];
                foreach (JsonElement item in Items(attachmentList, "attachments"))
                {
                    string name = Text(item, "name") ?? throw Wrong("an attachment has no name");
                    AttachmentInfo found = info.Attachments.FirstOrDefault(a => a.Name == name)
                        ?? throw Wrong($"the attachment '{name}' is not in {input.Display}; the JSON can keep, describe or drop attachments, not add them (embed --attach adds)");
                    if (item.TryGetProperty("description", out JsonElement description))
                    {
                        found = found with { Description = description.ValueKind == JsonValueKind.Null ? null : description.GetString() };
                    }

                    attachments.Add(found);
                }
            }

            if (file.TryGetProperty("tracks", out JsonElement trackList))
            {
                int position = 0;
                foreach (JsonElement item in Items(trackList, "tracks"))
                {
                    int at = item.TryGetProperty("number", out JsonElement number) && number.TryGetInt32(out int n) ? result.FindIndex(t => t.Number == n)
                        : item.TryGetProperty("index", out JsonElement index) && index.TryGetInt32(out int i) ? result.FindIndex(t => IndexOf(info.Tracks, t) == i)
                        : position < result.Count ? position : -1;
                    position++;
                    if (at >= 0)
                    {
                        result[at] = ApplyTrack(result[at], item);
                    }
                }
            }
        }

        foreach (string key in Remove)
        {
            foreach (string name in tags.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                tags.Remove(name);
            }
        }

        foreach ((string key, string value) in Set)
        {
            foreach (string name in tags.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) && k != key).ToList())
            {
                tags.Remove(name);
            }

            tags[key] = value;
        }

        chapters.AddRange(Chapters);
        chapters.Sort((a, b) => a.Start.Seconds.CompareTo(b.Start.Seconds));
        return (result, new MediaMetadata { Tags = tags, Chapters = chapters, Attachments = attachments });
    }

    private JsonElement Entry(JsonElement root, InputFile input)
    {
        if (!root.TryGetProperty("files", out JsonElement files))
        {
            return root;
        }

        List<JsonElement> list = [.. Items(files, "files")];
        if (list.Count == 1)
        {
            return list[0];
        }

        foreach (JsonElement file in list)
        {
            if (Text(file, "path") is { } path && (path == input.Display || (input.Path is { } full && Path.GetFileName(path) == Path.GetFileName(full))))
            {
                return file;
            }
        }

        throw new UsageException($"--load '{LoadedPath}' has {list.Count} files and none is {input.Display}; give a JSON with one file, or one whose path names it.");
    }

    private TrackInfo ApplyTrack(TrackInfo track, JsonElement item)
    {
        if (item.TryGetProperty("language", out JsonElement language))
        {
            track = track with { Language = TextOrNull(language, "language") };
        }

        if (item.TryGetProperty("name", out JsonElement name))
        {
            track = track with { Name = TextOrNull(name, "name") };
        }

        if (item.TryGetProperty("default", out JsonElement isDefault))
        {
            track = isDefault.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? track with { IsDefault = isDefault.GetBoolean() }
                : throw Wrong("a track's default must be true or false");
        }

        if (item.TryGetProperty("tags", out JsonElement tagList))
        {
            Dictionary<string, string> tags = new(track.Tags, StringComparer.Ordinal);
            ApplyTags(tags, tagList, "a track's tags");
            track = track with { Tags = tags };
        }

        return track;
    }

    private void ApplyTags(Dictionary<string, string> tags, JsonElement list, string what)
    {
        if (list.ValueKind != JsonValueKind.Object)
        {
            throw Wrong($"{what} must be an object of names and values");
        }

        foreach (JsonProperty tag in list.EnumerateObject())
        {
            switch (tag.Value.ValueKind)
            {
                case JsonValueKind.Null:
                    tags.Remove(tag.Name);
                    break;
                case JsonValueKind.String:
                    tags[tag.Name] = tag.Value.GetString()!;
                    break;
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    tags[tag.Name] = tag.Value.GetRawText();
                    break;
                default:
                    throw Wrong($"the tag '{tag.Name}' must be text, a number or null");
            }
        }
    }

    private ChapterInfo ReadChapter(JsonElement item)
    {
        MediaTime start = Time(item, "start") ?? throw Wrong("a chapter has no start");
        return new ChapterInfo(start, Time(item, "end"), Text(item, "title"), Text(item, "language"));
    }

    private MediaTime? Time(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => MediaTime.FromSeconds(value.GetDouble()),
            JsonValueKind.String => TimeValue.Parse(value.GetString()!, $"--load chapter {name}") is { IsFrame: false } time
                ? MediaTime.FromSeconds(time.Seconds)
                : throw Wrong($"a chapter's {name} must be seconds or [HH:]MM:SS, not a frame number"),
            _ => throw Wrong($"a chapter's {name} must be seconds"),
        };
    }

    private string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) ? TextOrNull(value, name) : null;

    private string? TextOrNull(JsonElement value, string name) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => throw Wrong($"'{name}' must be text or null"),
    };

    private JsonElement.ArrayEnumerator Items(JsonElement list, string name) =>
        list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : throw Wrong($"'{name}' must be a list");

    private UsageException Wrong(string what) => new($"--load '{LoadedPath}': {what}.");

    private static int IndexOf(IReadOnlyList<TrackInfo> tracks, TrackInfo track)
    {
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Number == track.Number)
            {
                return i;
            }
        }

        return -1;
    }

}
