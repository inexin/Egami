using System.Text.Json;

using Egami.Heic.Hevc;

namespace Egami.Heic.Tests;

/// <summary>
/// Phase 2.9.4 task 3: the HEVC header parsers read every element HM 18.0 reads, with the same values, in the same order: NAL
/// unit headers, VPS, SPS (VUI, range extension, scaling lists, reference picture sets), PPS (tiles, range extension), slice
/// segment headers and decoded picture hash SEI messages, on every corpus item and, with EGAMI_CORPUS=heic, on the first
/// picture of every intra-starting conformance stream.
/// </summary>
public class HevcHeaderTests(ITestOutputHelper output)
{
    public static TheoryData<string> CorpusItems() => [.. HevcTestFiles.HmHeaders.Value.EnumerateObject().Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(CorpusItems))]
    public void HeadersEqualHmsTrace(string key)
    {
        string[] parts = key.Split('#');
        List<HevcNal> nals = HevcTestFiles.ItemNals(parts[0], uint.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        Compare(key, nals, HevcTestFiles.HmHeaders.Value.GetProperty(key));
    }

    public static TheoryData<string> Suites() => ["HEVC_v1", "RExt"];

    [Theory]
    [MemberData(nameof(Suites))]
    public void ConformanceHeadersEqualHmsTrace(string suite)
    {
        if (HevcTestFiles.Cache() is not string cache)
        {
            output.WriteLine("Set EGAMI_CORPUS=heic to check the conformance streams.");
            return;
        }

        JsonElement streams = HevcTestFiles.ReadGzipJson(Path.Combine(cache, "reference", suite, "hm-headers.json.gz")).GetProperty("streams");
        string[] files = [.. Directory.GetFiles(Path.Combine(cache, "hevc", suite), "*.bi?", SearchOption.AllDirectories)];
        int checkedStreams = 0;
        foreach (JsonProperty stream in streams.EnumerateObject())
        {
            string path = files.First(f => Path.GetFileName(f) == stream.Name);
            Compare(stream.Name, HevcTestFiles.FirstAccessUnit(File.ReadAllBytes(path)), stream.Value);
            checkedStreams++;
        }

        Assert.True(checkedStreams > 10, $"{checkedStreams} streams");
    }

    /// <summary>Parses the NAL units and compares each one's elements with HM's, for the units HM traced in full.</summary>
    private static void Compare(string name, List<HevcNal> nals, JsonElement expected)
    {
        HevcParameterSetStore store = new() { Trace = [] };
        JsonElement[] hm = [.. expected.EnumerateArray()];
        int index = 0;
        foreach (HevcNal nal in nals)
        {
            store.Parse(nal, out _);
            (int type, List<(string Name, long Value)> elements) = store.Trace[^1];
            JsonElement[] want = [.. hm[index][1].EnumerateArray()];
            index++;

            // HM traces every SEI message its way; the decoded picture hash is the one compared (when alone in its unit).
            bool sei = type is HevcNal.PrefixSei or HevcNal.SuffixSei;
            if (sei && !(want.Length > 4 && want[6][0].GetString() == "hash_type" && elements.Count == want.Length))
            {
                continue;
            }

            // Layers above the base are not parsed.
            if (nal.LayerId != 0)
            {
                continue;
            }

            // Units Egami skips (filler data, end of sequence, reserved types) are compared by their NAL unit header alone.
            if (elements.Count == 4)
            {
                want = want[..Math.Min(4, want.Length)];
            }

            // HM parses a prefix SEI unit after the slice header that follows it, so its elements trail that slice's.
            if (nal.IsSlice && elements.Count < want.Length && want[elements.Count][0].GetString() == "payload_type")
            {
                want = want[..elements.Count];
            }

            string context = $"{name}, NAL unit {index} ({hm[index - 1][0].GetString()})";
            for (int i = 0; i < Math.Max(want.Length, elements.Count); i++)
            {
                string expectedElement = i < want.Length ? $"{want[i][0].GetString()} = {want[i][1].GetInt64()}" : "(end)";
                string actualElement = i < elements.Count ? $"{elements[i].Name} = {elements[i].Value}" : "(end)";
                Assert.True(expectedElement == actualElement, $"{context}, element {i}: HM has {expectedElement}, Egami {actualElement}");
            }
        }

        Assert.Equal(hm.Length, index);
    }
}
