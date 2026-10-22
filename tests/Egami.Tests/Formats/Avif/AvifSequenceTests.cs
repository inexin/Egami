using System.Security.Cryptography;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Avif;
using Egami.Formats.Heif;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Animated AVIF (2.9.5 task 9): every frame of the corpus's sequences, inter frames included, equals avifdec 1.4.2's
/// (reference/avif/sequences.json, from tools/make-av1-references.py sequences).
/// </summary>
public class AvifSequenceTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly Lazy<JsonElement> References = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "avif", "sequences.json"))).RootElement.Clone());

    public static TheoryData<string> Sequences() => [.. References.Value.EnumerateObject().Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(Sequences))]
    public void EveryFrameEqualsAvifdecs(string file)
    {
        string[] expected = [.. References.Value.GetProperty(file).GetProperty("frames").EnumerateArray().Select(e => e.GetString()!)];
        using MemoryStream stream = new(File.ReadAllBytes(Path.Combine(Corpus, file)));
        EgamiInput input = new(stream, EgamiContext.Default, TestContext.Current.CancellationToken);
        AvifSequence sequence = AvifSequence.Find(HeifFile.Read(input))!;
        AvifCodec codec = new(true);
        Assert.Equal(expected.Length, ((IHeifSequence)sequence).FrameCount);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i] == Md5(sequence.Decode(i, codec, input).Color), $"frame {i} differs from avifdec's");
        }

        // Out of order: each frame decodes from the key frame before it.
        for (int i = expected.Length - 1; i >= 0; i -= 2)
        {
            Assert.True(expected[i] == Md5(sequence.Decode(i, codec, input).Color), $"frame {i}, decoded out of order, differs from avifdec's");
        }
    }

    [Fact]
    public void SequenceWithInterFramesLoadsEveryFrame()
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, "libavif", "colors-animated-12bpc-keyframes-0-2-3.avif"));
        Assert.Equal(5, raster.FrameCount);
        Assert.Equal(0, raster.Metadata.LoopCount);
        Assert.Equal(TimeSpan.FromSeconds(1), raster.FrameMetadataAt(3).Delay);
        Assert.DoesNotContain(raster.Metadata.Warnings, w => w.Id == "Avif.SequenceFirstFrame");
    }

    /// <summary>The MD5 of a frame's planes as avifdec's y4m holds them: 8-bit samples as bytes, deeper ones 16-bit little-endian.</summary>
    private static string Md5(HeifPlanes planes)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        for (int p = 0; p < (planes.Monochrome ? 1 : 3); p++)
        {
            int w = p == 0 ? planes.Width : planes.ChromaWidth, h = p == 0 ? planes.Height : planes.ChromaHeight;
            byte[] row = new byte[w * (planes.BitDepth > 8 ? 2 : 1)];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    ushort v = planes.Samples[p][(y * planes.Strides[p]) + x];
                    if (planes.BitDepth > 8)
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
