using System.IO.Compression;
using System.Text.Json;

namespace Egami.Tests.Yuv;

/// <summary>
/// The libjpeg-turbo raw-plane references in tests/reference/yuv (tools/make-yuv-references.py): what
/// <c>tj3DecompressToYUVPlanes8</c> gives for each corpus JPEG, and what <c>tj3CompressFromYUVPlanes8</c> makes of some of them.
/// </summary>
internal static class YuvJpegReferences
{
    internal static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "reference", "yuv");

    private static readonly Lazy<JsonDocument> Manifest = new(() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Folder, "manifest.json"))));

    /// <summary>Gets every decode reference: the JPEG, the scale (1, 2, 4 or 8), and the entry.</summary>
    internal static IEnumerable<Decode> Decodes()
    {
        foreach (JsonElement e in Manifest.Value.RootElement.GetProperty("files").EnumerateArray())
        {
            yield return new Decode(
                e.GetProperty("file").GetString()!,
                int.Parse(e.GetProperty("scale").GetString()!.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture),
                e.GetProperty("width").GetInt32(),
                e.GetProperty("height").GetInt32(),
                e.GetProperty("subsampling").GetString()!,
                [.. e.GetProperty("planes").EnumerateArray().Select(p => (p.GetProperty("width").GetInt32(), p.GetProperty("height").GetInt32()))],
                e.GetProperty("reference").GetString()!);
        }
    }

    /// <summary>Gets every encode reference.</summary>
    internal static IEnumerable<Encode> Encodes()
    {
        foreach (JsonElement e in Manifest.Value.RootElement.GetProperty("encodes").EnumerateArray())
        {
            yield return new Encode(
                e.GetProperty("file").GetString()!,
                e.GetProperty("source").GetString()!,
                e.GetProperty("width").GetInt32(),
                e.GetProperty("height").GetInt32(),
                e.GetProperty("subsampling").GetString()!,
                e.GetProperty("quality").GetInt32());
        }
    }

    /// <summary>Reads the planes of a .yuv.gz file: each plane's samples, row by row, packed.</summary>
    internal static byte[][] ReadPlanes(string file, IReadOnlyList<(int Width, int Height)> planes)
    {
        using GZipStream gzip = new(File.OpenRead(Path.Combine(Folder, file)), CompressionMode.Decompress);
        byte[][] result = new byte[planes.Count][];
        for (int i = 0; i < planes.Count; i++)
        {
            result[i] = new byte[planes[i].Width * planes[i].Height];
            gzip.ReadExactly(result[i]);
        }

        Assert.Equal(-1, gzip.ReadByte());
        return result;
    }

    internal sealed record Decode(string File, int Scale, int Width, int Height, string Subsampling, (int Width, int Height)[] Planes, string Reference)
    {
        public override string ToString() => Scale == 1 ? File : $"{File} 1/{Scale}";
    }

    internal sealed record Encode(string File, string Source, int Width, int Height, string Subsampling, int Quality);
}

/// <summary>Phase 2.1.4 task 1: every libjpeg-turbo plane reference loads and has the sizes its manifest gives.</summary>
public class YuvJpegReferenceFileTests
{
    [Fact]
    public void EveryReferenceLoads()
    {
        int count = 0;
        foreach (YuvJpegReferences.Decode decode in YuvJpegReferences.Decodes())
        {
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", decode.File)), decode.File);
            byte[][] planes = YuvJpegReferences.ReadPlanes(decode.Reference, decode.Planes);
            Assert.Equal(decode.Subsampling == "gray" ? 1 : 3, planes.Length);
            count++;
        }

        Assert.True(count > 40);
        foreach (YuvJpegReferences.Encode encode in YuvJpegReferences.Encodes())
        {
            Assert.True(File.Exists(Path.Combine(YuvJpegReferences.Folder, encode.File)), encode.File);
            Assert.Contains(YuvJpegReferences.Decodes(), d => d.Reference == encode.Source);
        }
    }
}
