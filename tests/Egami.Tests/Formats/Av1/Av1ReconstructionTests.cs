using System.Security.Cryptography;
using System.Text.Json;

using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Tests.Formats.Avif;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// The AV1 decoder's samples against dav1d 1.5.4's, stage by stage, for every intra AV1 item of the corpus (and with
/// EGAMI_CORPUS=avif the AVIF files that stay in .cache and the first frame of libaom's test vectors, dav1d's test data and the
/// Argon streams). Phase 2.9.2 tasks 6 and 7's gate is the reconstruction before the in-loop filters (dav1d with its in-loop
/// filters and film grain off: dav1d_no_filters_md5), for frames without superres; tasks 8 and 9's is the frame after
/// deblocking, CDEF, superres and loop restoration (dav1d without film grain: dav1d_no_grain_md5); task 10's is the output with
/// film grain (dav1d_md5). Items of several frames (layers) wait for task 16.
/// </summary>
public class Av1ReconstructionTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "avif", "manifest.json"))).RootElement.GetProperty("files").Clone());

    private static readonly Stage PreFilter = new("dav1d_no_filters_md5", false, false, h => h.FrameWidth == h.UpscaledWidth);
    private static readonly Stage Filtered = new("dav1d_no_grain_md5", true, false, _ => true);
    private static readonly Stage Output = new("dav1d_md5", true, true, _ => true);

    public static TheoryData<string> CorpusFiles() => [.. Manifest.Value.EnumerateObject().Select(p => p.Name)];

    public static TheoryData<string, string> Streams() => Av1HeaderTests.Streams();

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void PreFilterReconstructionEqualsDav1ds(string file) => CompareCorpusFile(file, PreFilter);

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void FilteredEqualsDav1ds(string file) => CompareCorpusFile(file, Filtered);

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void OutputWithFilmGrainEqualsDav1ds(string file) => CompareCorpusFile(file, Output);

    [Fact]
    public void ExtraOutputWithFilmGrainEqualsDav1ds() => CompareExtra(Output);

    [Theory]
    [MemberData(nameof(Streams))]
    public void StreamOutputWithFilmGrainEqualsDav1ds(string set, string summary) => CompareStreams(set, summary, Output);

    [Fact]
    public void ExtraPreFilterReconstructionEqualsDav1ds() => CompareExtra(PreFilter);

    [Fact]
    public void ExtraFilteredEqualsDav1ds() => CompareExtra(Filtered);

    [Theory]
    [MemberData(nameof(Streams))]
    public void StreamPreFilterReconstructionEqualsDav1ds(string set, string summary) => CompareStreams(set, summary, PreFilter);

    [Theory]
    [MemberData(nameof(Streams))]
    public void StreamFilteredEqualsDav1ds(string set, string summary) => CompareStreams(set, summary, Filtered);

    /// <summary>Task 11's gate: the output (with film grain) is the same whatever the number of threads tiles and filters run on.</summary>
    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void OutputIsTheSameAtEveryParallelism(string file)
    {
        if (!Manifest.Value.GetProperty(file).TryGetProperty("items", out JsonElement items))
        {
            return;
        }

        HeifFile heif;
        try
        {
            heif = HeifFile.Parse(File.ReadAllBytes(Path.Combine(Corpus, file)), "AVIF");
        }
        catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException)
        {
            return;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "av01" || !item.TryGetProperty(Output.Key, out _))
            {
                continue;
            }

            byte[] data = heif.ItemData(heif.Item(item.GetProperty("id").GetUInt32())!).ToArray();
            string? first = null;
            foreach (int parallelism in (int[])[1, 2, 3, 8])
            {
                string md5;
                try
                {
                    md5 = PlanesMd5(Av1Decoder.DecodeFirstFrame(data, "AVIF", parallelism: parallelism, cancellationToken: TestContext.Current.CancellationToken), grain: true);
                }
                catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException)
                {
                    break;
                }

                first ??= md5;
                Assert.True(first == md5, $"{file} item {item.GetProperty("id")} at parallelism {parallelism}");
            }
        }
    }

    private static void CompareCorpusFile(string file, Stage stage)
    {
        List<string> failures = [];
        CompareFile(Path.Combine(Corpus, file), Manifest.Value.GetProperty(file), file, stage, failures, out _);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void CompareExtra(Stage stage)
    {
        if (AvifReferenceTests.Cache() is not string cache || !File.Exists(Path.Combine(cache, "reference", "extra", "manifest.json")))
        {
            throw LocalCorpus.Skip("avif");
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", "extra", "manifest.json")));
        List<string> failures = [];
        int compared = 0;
        foreach (JsonProperty file in json.RootElement.GetProperty("files").EnumerateObject())
        {
            CompareFile(Path.Combine(cache, file.Name), file.Value, file.Name, stage, failures, out int count);
            compared += count;
        }

        Assert.True(failures.Count == 0, $"{failures.Count} items differ of {compared + failures.Count}:\n" + string.Join("\n", failures.Take(40)));
    }

    private static void CompareStreams(string set, string summary, Stage stage)
    {
        if (AvifReferenceTests.Cache() is not string cache || summary.Length == 0)
        {
            throw LocalCorpus.Skip("avif");
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(summary));
        int compared = 0;
        List<string> failures = [];
        foreach (JsonProperty stream in json.RootElement.GetProperty("streams").EnumerateObject())
        {
            if (!stream.Value.TryGetProperty(stage.Key, out JsonElement expected))
            {
                continue;
            }

            string relative = stream.Value.GetProperty("stream").GetString()!;
            if (relative.Contains("large_scale_tile", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Av1Frame frame;
                try
                {
                    frame = Av1Decoder.DecodeFirstFrame(Av1HeaderTests.Section5(Path.Combine(cache, relative), relative, 8 << 20), "AV1", filter: stage.Filter);
                }
                catch (CorruptDataException e) when (e.IsTruncation)
                {
                    frame = Av1Decoder.DecodeFirstFrame(Av1HeaderTests.Section5(Path.Combine(cache, relative), relative, Array.MaxLength), "AV1", filter: stage.Filter);
                }

                if (!frame.Header.ShowFrame || !stage.Covers(frame.Header))
                {
                    continue;
                }

                compared++;
                if (PlanesMd5(frame, stage.Grain) != expected.GetString())
                {
                    failures.Add($"{stream.Name}: differs");
                }
            }
            catch (UnsupportedFormatException)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failures.Add($"{stream.Name}: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
            }
        }

        Assert.True(failures.Count == 0, $"{set}: {failures.Count} of {compared + failures.Count} differ:\n" + string.Join('\n', failures.Take(60)));
        Assert.True(compared > 0, set);
    }

    private static void CompareFile(string path, JsonElement entry, string name, Stage stage, List<string> failures, out int compared)
    {
        compared = 0;
        if (!entry.TryGetProperty("items", out JsonElement items))
        {
            return;
        }

        HeifFile heif;
        try
        {
            heif = HeifFile.Parse(File.ReadAllBytes(path), "AVIF");
        }
        catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException)
        {
            return;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "av01" || !item.TryGetProperty(stage.Key, out JsonElement expected)
                || (item.TryGetProperty("frames", out JsonElement frames) && frames.GetInt32() != 1))
            {
                continue;
            }

            uint id = item.GetProperty("id").GetUInt32();
            string where = $"{name} item {id}";
            Av1Frame frame;
            try
            {
                frame = Av1Decoder.DecodeFirstFrame(heif.ItemData(heif.Item(id)!).ToArray(), "AVIF", filter: stage.Filter);
            }
            catch (UnsupportedFormatException)
            {
                continue;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failures.Add($"{where}: {e.GetType().Name}: {e.Message}");
                continue;
            }

            if (!stage.Covers(frame.Header))
            {
                continue;
            }

            compared++;
            string actual = PlanesMd5(frame, stage.Grain);
            if (actual != expected.GetString())
            {
                failures.Add($"{where}: {actual}, dav1d {expected.GetString()}");
            }
        }
    }

    /// <summary>The MD5 of a frame's planes (at the upscaled width) as dav1d writes them to y4m: Y, then U and V, 8-bit samples as bytes, else 16-bit little endian.</summary>
    internal static string PlanesMd5(Av1Frame frame, bool grain = false)
    {
        ushort[][] planes = grain ? Av1FilmGrainSynthesis.Apply(frame) : frame.Planes!;
        int width = frame.Header.UpscaledWidth, height = frame.Header.FrameHeight;
        bool wide = frame.Sequence.BitDepth > 8;
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        for (int plane = 0; plane < frame.Header.NumPlanes; plane++)
        {
            int ssx = plane > 0 ? frame.Sequence.SubsamplingX : 0, ssy = plane > 0 ? frame.Sequence.SubsamplingY : 0;
            int w = (width + ssx) >> ssx, h = (height + ssy) >> ssy;
            byte[] row = new byte[w * (wide ? 2 : 1)];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    ushort v = planes[plane][(y * frame.Strides[plane]) + x];
                    if (wide)
                    {
                        row[2 * x] = (byte)v;
                        row[(2 * x) + 1] = (byte)(v >> 8);
                    }
                    else
                    {
                        row[x] = (byte)v;
                    }
                }

                md5.AppendData(row);
            }
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>A decoding stage: the reference MD5 it equals, whether the in-loop filters run and film grain is added, and which frames it covers yet.</summary>
    private sealed record Stage(string Key, bool Filter, bool Grain, Func<Av1FrameHeader, bool> Covers);
}
