using System.IO.Compression;
using System.Text.Json;

namespace Egami.Tests.Formats.Webp;

/// <summary>The WebP corpus made by tools/make-webp-references.py and libwebp's decodes of it.</summary>
internal static class WebpCorpus
{
    internal static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "webp");
    internal static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "webp");

    private static readonly Lazy<Dictionary<string, JsonElement>> Entries = new(() =>
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return json.RootElement.GetProperty("files").EnumerateArray().ToDictionary(e => e.GetProperty("file").GetString()!, e => e.Clone());
    });

    /// <summary>Gets every file, as "name.webp".</summary>
    internal static IEnumerable<string> Files => Entries.Value.Keys.Order(StringComparer.Ordinal);

    /// <summary>Gets the lossless stills (VP8L, simple or extended container).</summary>
    internal static IEnumerable<string> Lossless => Files.Where(f => !Animated(f) && Format(f) == 2);

    /// <summary>Gets the lossy stills (VP8, with or without ALPH).</summary>
    internal static IEnumerable<string> Lossy => Files.Where(f => !Animated(f) && Format(f) == 1);

    /// <summary>Gets the animations.</summary>
    internal static IEnumerable<string> Animations => Files.Where(Animated);

    internal static JsonElement Entry(string file) => Entries.Value[file];

    internal static JsonElement Info(string file) => Entry(file).GetProperty("info");

    internal static bool Animated(string file) => Info(file).GetProperty("animation").GetBoolean();

    /// <summary>Returns WebPGetFeatures's format: 1 lossy, 2 lossless, 0 mixed or animated.</summary>
    internal static int Format(string file) => Info(file).GetProperty("format").GetInt32();

    internal static (int Width, int Height) Size(string file)
    {
        JsonElement info = Info(file);
        return Animated(file)
            ? (info.GetProperty("canvasWidth").GetInt32(), info.GetProperty("canvasHeight").GetInt32())
            : (info.GetProperty("width").GetInt32(), info.GetProperty("height").GetInt32());
    }

    internal static byte[] Read(string file) => File.ReadAllBytes(Path.Combine(Images, file));

    /// <summary>Returns libwebp's RGBA decode (fancy upsampling for lossy files).</summary>
    internal static byte[] Rgba(string file) => Reference(file, "rgba")!;

    /// <summary>Returns libwebp's RGBA decode with replicated chroma, or <see langword="null"/> for files without one.</summary>
    internal static byte[]? NoFancy(string file) => Reference(file, "nofancy");

    /// <summary>Returns libwebp's planes of a lossy file: Y, then U and V at half size rounded up.</summary>
    internal static byte[] Yuv(string file) => Reference(file, "yuv")!;

    /// <summary>Returns every canvas libwebp's animation decoder composes, as raw RGBA.</summary>
    internal static byte[][] Frames(string file)
    {
        (int width, int height) = Size(file);
        byte[] all = Reference(file, "frames")!;
        int frameBytes = width * height * 4;
        return [.. Enumerable.Range(0, all.Length / frameBytes).Select(i => all.AsSpan(i * frameBytes, frameBytes).ToArray())];
    }

    /// <summary>Returns the timestamps (end of each frame, in ms) libwebp's animation decoder reports.</summary>
    internal static int[] Timestamps(string file) => [.. Entry(file).GetProperty("timestamps").EnumerateArray().Select(t => t.GetInt32())];

    private static byte[]? Reference(string file, string kind)
    {
        if (!Entry(file).TryGetProperty(kind, out JsonElement name))
        {
            return null;
        }

        using GZipStream gzip = new(File.OpenRead(Path.Combine(References, name.GetString()!)), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        return raw.ToArray();
    }
}

/// <summary>Every reference loads at the size the manifest gives.</summary>
public class WebpCorpusTests
{
    public static TheoryData<string> Files() => [.. WebpCorpus.Files];

    [Theory]
    [MemberData(nameof(Files))]
    public void ReferencesLoadAtTheRightSize(string file)
    {
        (int width, int height) = WebpCorpus.Size(file);
        Assert.True(File.Exists(Path.Combine(WebpCorpus.Images, file)));
        if (WebpCorpus.Animated(file))
        {
            byte[][] frames = WebpCorpus.Frames(file);
            Assert.Equal(WebpCorpus.Info(file).GetProperty("frames").GetInt32(), frames.Length);
            Assert.Equal(frames.Length, WebpCorpus.Timestamps(file).Length);
            return;
        }

        Assert.Equal(width * height * 4, WebpCorpus.Rgba(file).Length);
        if (WebpCorpus.Format(file) == 1)
        {
            int chroma = ((width + 1) / 2) * ((height + 1) / 2);
            Assert.Equal((width * height) + (2 * chroma), WebpCorpus.Yuv(file).Length);
            Assert.True(WebpCorpus.NoFancy(file) is null or { Length: > 0 });
        }
    }
}
