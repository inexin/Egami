using System.Security.Cryptography;
using System.Text.Json;

using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Tests.Formats.Avif;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// Phase 2.9.2 tasks 6 and 7's gate: intra prediction, chroma from luma, palettes, intra block copy, dequantization and the
/// inverse transforms reconstruct every intra AV1 item of the corpus exactly as dav1d 1.5.4 does with its in-loop filters and
/// film grain off (the manifest's dav1d_no_filters_md5); with EGAMI_CORPUS=avif, also the AVIF files that stay in .cache.
/// Items with superres or several frames (layers) wait for task 9 and 16.
/// </summary>
public class Av1ReconstructionTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "avif", "manifest.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> CorpusFiles() => [.. Manifest.Value.EnumerateObject().Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void PreFilterReconstructionEqualsDav1ds(string file)
    {
        List<string> failures = [];
        CompareFile(Path.Combine(Corpus, file), Manifest.Value.GetProperty(file), file, failures, out _);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void ExtraPreFilterReconstructionEqualsDav1ds()
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
            CompareFile(Path.Combine(cache, file.Name), file.Value, file.Name, failures, out int count);
            compared += count;
        }

        Assert.True(failures.Count == 0, $"{failures.Count} items differ of {compared + failures.Count}:\n" + string.Join("\n", failures.Take(40)));
    }

    public static TheoryData<string, string> Streams() => Av1HeaderTests.Streams();

    /// <summary>
    /// The first frame of every stream of libaom's test vectors, dav1d's test data and the Argon streams that starts with a shown
    /// intra frame without superres equals dav1d's with its in-loop filters and film grain off.
    /// </summary>
    [Theory]
    [MemberData(nameof(Streams))]
    public void StreamPreFilterReconstructionEqualsDav1ds(string set, string summary)
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
            if (!stream.Value.TryGetProperty("dav1d_no_filters_md5", out JsonElement expected))
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
                    frame = Av1Decoder.DecodeFirstFrame(Av1HeaderTests.Section5(Path.Combine(cache, relative), relative, 8 << 20), "AV1");
                }
                catch (CorruptDataException e) when (e.IsTruncation)
                {
                    frame = Av1Decoder.DecodeFirstFrame(Av1HeaderTests.Section5(Path.Combine(cache, relative), relative, Array.MaxLength), "AV1");
                }

                if (!frame.Header.ShowFrame || frame.Header.FrameWidth != frame.Header.UpscaledWidth)
                {
                    continue;
                }

                compared++;
                string actual = PlanesMd5(frame);
                if (actual != expected.GetString())
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

    private static void CompareFile(string path, JsonElement entry, string name, List<string> failures, out int compared)
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
            if (item.GetProperty("type").GetString() != "av01" || !item.TryGetProperty("dav1d_no_filters_md5", out JsonElement expected)
                || (item.TryGetProperty("frames", out JsonElement frames) && frames.GetInt32() != 1))
            {
                continue;
            }

            uint id = item.GetProperty("id").GetUInt32();
            string where = $"{name} item {id}";
            Av1Frame frame;
            try
            {
                frame = Av1Decoder.DecodeFirstFrame(heif.ItemData(heif.Item(id)!).ToArray(), "AVIF");
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

            if (frame.Header.FrameWidth != frame.Header.UpscaledWidth)
            {
                continue;
            }

            compared++;
            string actual = PlanesMd5(frame);
            if (actual != expected.GetString())
            {
                failures.Add($"{where}: {actual}, dav1d {expected.GetString()}");
            }
        }
    }

    /// <summary>The MD5 of a frame's planes as dav1d writes them to y4m: Y, then U and V, 8-bit samples as bytes, else 16-bit little endian.</summary>
    internal static string PlanesMd5(Av1Frame frame)
    {
        int width = frame.Header.FrameWidth, height = frame.Header.FrameHeight;
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
                    ushort v = frame.Planes![plane][(y * frame.Strides[plane]) + x];
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
}
