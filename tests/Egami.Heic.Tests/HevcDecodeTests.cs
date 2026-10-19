using System.Security.Cryptography;
using System.Text.Json;

using Egami.Heic.Hevc;

namespace Egami.Heic.Tests;

/// <summary>
/// Phase 2.9.4 tasks 4 to 8: every HEVC item of the corpus decodes bit-exact to HM 18.0's output (the planes inside the
/// conformance window, as HM writes them), and its decoded picture hash SEI message, when it has one, matches; the output is the
/// same on several threads.
/// </summary>
public class HevcDecodeTests
{
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(HevcTestFiles.References, "manifest.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> Items()
    {
        TheoryData<string> items = [];
        foreach (JsonProperty file in Manifest.Value.EnumerateObject())
        {
            if (!file.Value.TryGetProperty("items", out JsonElement list))
            {
                continue;
            }

            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() == "hvc1" && item.TryGetProperty("hm_md5", out _))
                {
                    items.Add($"{file.Name}#{item.GetProperty("id").GetInt32()}");
                }
            }
        }

        return items;
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void DecodesBitExactToHm(string key)
    {
        string[] parts = key.Split('#');
        uint id = uint.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        JsonElement item = Manifest.Value.GetProperty(parts[0]).GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetUInt32() == id);
        HevcPicture picture = HevcDecoder.Decode(HevcTestFiles.ItemNals(parts[0], id), SafetyLimits.Default, out HevcPictureHash? hash, 1, TestContext.Current.CancellationToken);
        Assert.Equal(item.GetProperty("hm_md5").GetString(), Md5(picture));
        Assert.Equal(Md5(picture), Md5(HevcDecoder.Decode(HevcTestFiles.ItemNals(parts[0], id), SafetyLimits.Default, out _, Threads, TestContext.Current.CancellationToken)));
        if (hash is not null)
        {
            Assert.True(HevcDecoder.Matches(picture, hash), "decoded picture hash");
        }
    }

    /// <summary>The thread count the decoder is also run with: its output must not change (task 8).</summary>
    private const int Threads = 5;

    public static TheoryData<string> Suites() => ["HEVC_v1", "RExt", "nokia"];

    /// <summary>
    /// With EGAMI_CORPUS=heic: the first picture of every conformance stream that starts with an intra picture equals HM's, and
    /// its hash; streams outside 2.9.4 (16-bit, High Throughput, layered, inter first pictures) must say so.
    /// </summary>
    [Theory]
    [MemberData(nameof(Suites))]
    public void ConformanceFirstPicturesDecodeBitExact(string suite)
    {
        if (HevcTestFiles.Cache() is not string cache)
        {
            return;
        }

        using JsonDocument summary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", suite, "summary.json")));
        string root = suite == "nokia" ? Path.Combine(cache, "nokia", "bitstreams") : Path.Combine(cache, "hevc", suite);
        string[] files = [.. Directory.GetFiles(root, "*", SearchOption.AllDirectories)];
        List<string> failures = [];
        int decoded = 0, unsupported = 0;
        foreach (JsonProperty stream in summary.RootElement.GetProperty("streams").EnumerateObject())
        {
            if (!stream.Value.TryGetProperty("first_md5", out JsonElement expected))
            {
                continue;
            }

            // HM writes pictures in output order; when a later picture comes out first, its first frame is not the first decoded.
            int[] pocs = [.. stream.Value.GetProperty("slices").EnumerateArray().Select(p => int.Parse(p.GetString()!.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture))];
            if (pocs.Skip(1).Any(p => p < pocs[0]))
            {
                continue;
            }

            string path = files.First(f => Path.GetFileName(f) == stream.Name);
            try
            {
                List<HevcNal> nals = HevcTestFiles.FirstAccessUnit(File.ReadAllBytes(path));
                HevcPicture picture = HevcDecoder.Decode(nals, SafetyLimits.Default, out HevcPictureHash? hash, 1, TestContext.Current.CancellationToken);
                string md5 = Md5(picture);
                if (Md5(HevcDecoder.Decode(nals, SafetyLimits.Default, out _, Threads, TestContext.Current.CancellationToken)) != md5)
                {
                    failures.Add($"{stream.Name}: differs on {Threads} threads");
                }

                // The extended precision streams continue with a 16-bit sequence, so HM writes its whole output, the first frame
                // too, at another depth; their own checksums judge the first picture.
                bool hmOutputAtAnotherDepth = stream.Name.StartsWith("EXTPREC_", StringComparison.Ordinal) && hash is not null;
                if (md5 != expected.GetString() && !hmOutputAtAnotherDepth)
                {
                    string hashState = hash is null ? "no hash" : HevcDecoder.Matches(picture, hash) ? "hash matches" : "hash differs";
                    failures.Add($"{stream.Name}: MD5 {md5}, HM {expected.GetString()} ({hashState})");
                }
                else if (hash is not null && !HevcDecoder.Matches(picture, hash))
                {
                    failures.Add($"{stream.Name}: hash mismatch");
                }

                decoded++;
            }
            catch (UnsupportedFormatException)
            {
                unsupported++;
            }
            catch (Exception e) when (e is CorruptDataException or IndexOutOfRangeException or InvalidOperationException or ArgumentException)
            {
                failures.Add($"{stream.Name}: {e.GetType().Name} {e.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{decoded} decoded, {unsupported} unsupported, {failures.Count} failed:\n" + string.Join("\n", failures));
    }

    public static TheoryData<string> ParallelItems()
    {
        TheoryData<string> items = [];
        foreach (string key in Items().Select(row => row.Data))
        {
            if (key.StartsWith("hm-tiles", StringComparison.Ordinal) || key.StartsWith("hm-wpp", StringComparison.Ordinal)
                || key.StartsWith("hm-dependent-slices", StringComparison.Ordinal) || key.StartsWith("hm-slices", StringComparison.Ordinal))
            {
                items.Add(key);
            }
        }

        return items;
    }

    /// <summary>
    /// Damaged tiles, wavefronts and slices fail or decode the same way on one thread and on several: no hang, and only
    /// <see cref="CorruptDataException"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(ParallelItems))]
    public void DamagedStreamsDecodeTheSameOnSeveralThreads(string key)
    {
        string[] parts = key.Split('#');
        List<HevcNal> nals = HevcTestFiles.ItemNals(parts[0], uint.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        int slice = nals.FindLastIndex(n => n.IsSlice);
        foreach (double at in (double[])[0.2, 0.5, 0.8])
        {
            byte[] damaged = (byte[])nals[slice].Rbsp.Clone();
            damaged[(int)(damaged.Length * at)] ^= 0x55;
            List<HevcNal> copy = [.. nals];
            copy[slice] = HevcNal.Parse(damaged);
            Assert.Equal(Outcome(copy, 1), Outcome(copy, Threads));
        }

        static string Outcome(List<HevcNal> nals, int threads)
        {
            try
            {
                return Md5(HevcDecoder.Decode(nals, SafetyLimits.Default, out _, threads, TestContext.Current.CancellationToken));
            }
            catch (CorruptDataException e)
            {
                return e.GetType().Name;
            }
        }
    }

    /// <summary>The MD5 of the planes inside the conformance window, one byte a sample up to 8 bits and two (little-endian) above.</summary>
    internal static string Md5(HevcPicture picture)
    {
        HevcSps sps = picture.Sps;
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        for (int c = 0; c < picture.Planes.Length; c++)
        {
            int sx = c == 0 ? 1 : sps.SubWidthC, sy = c == 0 ? 1 : sps.SubHeightC;
            int left = sps.Conformance.Left / sx, top = sps.Conformance.Top / sy;
            int width = sps.OutputWidth / sx, height = sps.OutputHeight / sy;
            int bytes = (c == 0 ? sps.BitDepthLuma : sps.BitDepthChroma) > 8 || Math.Max(sps.BitDepthLuma, sps.BitDepthChroma) > 8 ? 2 : 1;
            byte[] row = new byte[width * bytes];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    ushort v = picture.Planes[c][((top + y) * picture.Widths[c]) + left + x];
                    row[x * bytes] = (byte)v;
                    if (bytes == 2)
                    {
                        row[(x * 2) + 1] = (byte)(v >> 8);
                    }
                }

                md5.AppendData(row);
            }
        }

#pragma warning disable CA1308 // MD5s are written in lower case
        return Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
#pragma warning restore CA1308
    }
}
