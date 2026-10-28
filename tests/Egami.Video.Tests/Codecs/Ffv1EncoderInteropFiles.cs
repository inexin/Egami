using System.Text.Json;

using Egami.Video.Codecs;

namespace Egami.Video.Tests.Codecs;

/// <summary>
/// Writes Matroska files from the FFV1 encoder, each with the frame hashes FFmpeg's decode must give, for
/// tools/video/check-ffv1-encoder.py. Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class Ffv1EncoderInteropFiles
{
    [Fact]
    public void WriteFiles()
    {
        string? root = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipWhen(root is null, "EGAMI_INTEROP_DIR is not set.");
        string folder = Path.Combine(root!, "ffv1");
        Directory.CreateDirectory(folder);
        foreach (string clip in Ffv1Tests.ClipNames)
        {
            (_, int depth, List<string> expected) = Ffv1Tests.ReadReference(clip);
            List<(string Name, Ffv1EncoderOptions Options, int Interval)> variants =
            [
                ("tab", new Ffv1EncoderOptions { BitDepth = depth }, 1),
                ("def-small", new Ffv1EncoderOptions { BitDepth = depth, Coder = Ffv1Coder.RangeDefaultTable, ContextModel = Ffv1ContextModel.Small, Slices = 6 }, 1),
                ("gop", new Ffv1EncoderOptions { BitDepth = depth, Slices = 4, SliceCrc = false }, 3),
            ];
            if (depth == 8)
            {
                variants.Add(("rice", new Ffv1EncoderOptions { BitDepth = depth, Coder = Ffv1Coder.GolombRice, Slices = 4 }, 1));
                variants.Add(("rice-gop", new Ffv1EncoderOptions { BitDepth = depth, Coder = Ffv1Coder.GolombRice, ContextModel = Ffv1ContextModel.Small }, 2));
            }

            variants.Add(("avi", new Ffv1EncoderOptions { BitDepth = depth, Slices = 9 }, 1));
            foreach ((string name, Ffv1EncoderOptions options, int interval) in variants)
            {
                string container = name == "avi" ? "avi" : "mkv";
                string file = Path.Combine(folder, $"{clip}.{name}.{container}");
                File.WriteAllBytes(file, Ffv1EncoderTests.Reencode(Ffv1Tests.PathOf(Ffv1Tests.ReadReference(clip).File), options, interval, container: container));
                File.WriteAllText(Path.ChangeExtension(file, ".json"), JsonSerializer.Serialize(new { clip, md5 = expected }));
            }
        }
    }
}
