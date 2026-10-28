using System.Security.Cryptography;
using System.Text.Json;

using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Tests.Codecs;

/// <summary>The FFV1 decoder against FFmpeg's: every frame of tests/video/corpus/ffv1 (tools/video/make-ffv1-references.py).</summary>
public class Ffv1Tests
{
    public static TheoryData<string> ClipNames =>
        [.. Directory.GetFiles(Path.Combine(Corpus.Directory, "ffv1"), "*.frames.json").Select(f => Path.GetFileName(f)[..^".frames.json".Length]).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(ClipNames))]
    public void ClipFramesEqualFfmpeg(string clip)
    {
        (string file, int depth, List<string> expected) = ReadReference(clip);
        using VideoReader reader = VideoReader.Open(PathOf(file));
        List<string> ours = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                ours.Add(Md5(frame.Raster, depth));
            }
        }

        Assert.Equal(expected, ours);
    }

    [Fact]
    public void TracksAreFfv1()
    {
        Assert.Same(Ffv1Codec.Instance, VideoCodecs.Ffv1);
        Assert.Contains(Ffv1Codec.Instance, MediaContext.Default.Codecs);
        using VideoReader reader = VideoReader.Open(PathOf("v3-420.mkv"));
        Assert.Equal(CodecIds.Ffv1, reader.Track.CodecId);
    }

    internal static string PathOf(string file) => Path.Combine(Corpus.Directory, "ffv1", file);

    internal static (string File, int Depth, List<string> Md5) ReadReference(string clip)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(clip + ".frames.json")));
        JsonElement root = json.RootElement;
        return (root.GetProperty("file").GetString()!, root.GetProperty("bitDepth").GetInt32(), [.. root.GetProperty("md5").EnumerateArray().Select(e => e.GetString()!)]);
    }

    /// <summary>The MD5 of a frame as the reference script hashes it: Y, U, V, alpha at the source depth, LE 16-bit above 8 bits.</summary>
    internal static string Md5(YuvRaster picture, int depth)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        if (picture is YuvRaster<byte> bytes)
        {
            foreach (YuvPlane<byte>? plane in new[] { bytes.Y, bytes.U, bytes.V, bytes.A })
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    md5.AppendData(plane.Row(y)[..plane.Width]);
                }
            }
        }
        else
        {
            var words = (YuvRaster<ushort>)picture;
            int shift = picture.BitDepth - depth;
            foreach (YuvPlane<ushort>? plane in new[] { words.Y, words.U, words.V, words.A })
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    Span<ushort> row = plane.Row(y)[..plane.Width];
                    byte[] le = new byte[row.Length * 2];
                    for (int x = 0; x < row.Length; x++)
                    {
                        int v = row[x] >> shift;
                        le[2 * x] = (byte)v;
                        le[(2 * x) + 1] = (byte)(v >> 8);
                    }

                    md5.AppendData(le);
                }
            }
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }
}
