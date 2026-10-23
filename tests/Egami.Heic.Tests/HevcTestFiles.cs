using System.IO.Compression;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Heic;
using Egami.Formats.Heif;
using Egami.Heic.Hevc;
using Egami.Nal;

namespace Egami.Heic.Tests;

/// <summary>The HEVC streams of Egami's HEIC corpus and, with EGAMI_CORPUS=heic, of the conformance suites in .cache/heic.</summary>
internal static class HevcTestFiles
{
    internal static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "heic");
    internal static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "heic");

    /// <summary>Gets the corpus manifest's files.</summary>
    internal static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json"))).RootElement.GetProperty("files").Clone());

    /// <summary>Gets HM's header traces of every corpus HEVC item, keyed "file#item".</summary>
    internal static readonly Lazy<JsonElement> HmHeaders = new(() => ReadGzipJson(Path.Combine(References, "hm-headers.json.gz")).GetProperty("items"));

    /// <summary>Gets the fetched .cache/heic folder when EGAMI_CORPUS names heic (or all), else null.</summary>
    internal static string? Cache()
    {
        string asked = Environment.GetEnvironmentVariable("EGAMI_CORPUS") ?? "";
        if (!asked.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Any(c => c.Trim() is "heic" or "all"))
        {
            return null;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "heic");
            if (Directory.Exists(Path.Combine(candidate, "reference")))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static JsonElement ReadGzipJson(string path)
    {
        using FileStream file = File.OpenRead(path);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using JsonDocument json = JsonDocument.Parse(gzip);
        return json.RootElement.Clone();
    }

    /// <summary>Gets an hvc1 item's NAL units: its hvcC parameter sets, then its data.</summary>
    internal static List<HevcNal> ItemNals(string file, uint itemId)
    {
        HeifFile heif = HeifFile.Parse(File.ReadAllBytes(Path.Combine(Corpus, file)), "HEIC");
        HeifItem item = heif.Item(itemId)!;
        HevcConfig config = HevcConfig.Read(heif, heif.Property(item, HeicFile.HvcC)!);
        List<HevcNal> nals = [.. config.NalUnits.Select(n => HevcNal.Parse(n.Span))];
        nals.AddRange(NalFraming.SplitLengthPrefixed(heif.ItemData(item), config.LengthSize).Select(n => HevcNal.Parse(n.Span)));
        return nals;
    }

    /// <summary>Gets an Annex B stream's NAL units up to the end of its first picture (as heiccorpus.py cuts it).</summary>
    internal static List<HevcNal> FirstAccessUnit(byte[] stream)
    {
        List<HevcNal> nals = [];
        bool seenSlice = false;
        foreach (ReadOnlyMemory<byte> unit in NalFraming.SplitAnnexB(stream))
        {
            HevcNal nal = HevcNal.Parse(unit.Span);
            if (seenSlice && ((nal.IsSlice && (nal.Rbsp[2] & 0x80) != 0) || nal.Type is 32 or 33 or 34 or 35 or 39 or (>= 41 and <= 44)))
            {
                break;
            }

            seenSlice |= nal.IsSlice;
            nals.Add(nal);
        }

        return nals;
    }
}
