using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Exr;
using Egami.Pixels;

namespace Egami.Tests.Formats.Exr;

/// <summary>
/// OpenEXR decoding (Phase 2.9.1 tasks 4 to 7): every channel of every part of tests/images/exr equals OpenEXR 3.5.2's decode
/// bit for bit (DWAA and DWAB within one half-float step), and the RGBA Egami loads equals RgbaInputFile's for every layer.
/// When .cache/exr-ref exists (tools/make-hdr-references.py exr), the openexr-images set is checked the same way.
/// </summary>
public class ExrDecodeTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "exr");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "exr");

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Images, "*.exr").Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    public static TheoryData<string> CachedFiles()
    {
        string root = RepoRoot();
        string refs = Path.Combine(root, ".cache", "exr-ref");
        TheoryData<string> data = [];
        if (Directory.Exists(refs))
        {
            foreach (string file in Directory.GetFiles(refs, "*.ref").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(file));
            }
        }
        else
        {
            data.Add(string.Empty);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ChannelsEqualOpenExr(string name)
    {
        CheckChannels(File.ReadAllBytes(Path.Combine(Images, name)), ExrReference.Load(Path.Combine(References, name + ".ref.gz")), name);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void RgbaEqualsRgbaInputFile(string name)
    {
        CheckRgba(File.ReadAllBytes(Path.Combine(Images, name)), ExrReference.Load(Path.Combine(References, name + ".ref.gz")), name);
    }

    [Theory]
    [MemberData(nameof(CachedFiles))]
    public void OpenExrImagesEqualOpenExr(string name)
    {
        if (name.Length == 0)
        {
            Assert.Skip("No .cache/exr-ref: run tools/make-hdr-references.py exr.");
        }

        string root = RepoRoot();
        string image = Path.Combine(root, ".cache", "openexr-images", name[..^4].Replace("__", "/", StringComparison.Ordinal));
        ExrReference reference = ExrReference.Load(Path.Combine(root, ".cache", "exr-ref", name));
        byte[] file = File.ReadAllBytes(image);
        if (reference.Parts.All(p => p.GetProperty("type").GetString()!.StartsWith("deep", StringComparison.Ordinal)))
        {
            Assert.Throws<UnsupportedFormatException>(() => Raster.Load(file).Dispose());
            return;
        }

        CheckChannels(file, reference, name);
        CheckRgba(file, reference, name);
    }

    private static void CheckChannels(byte[] file, ExrReference reference, string name)
    {
        int index = 0;
        foreach (JsonElement part in reference.Parts)
        {
            if (part.GetProperty("type").GetString()!.StartsWith("deep", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            bool lossy = part.GetProperty("compression").GetInt32() is 8 or 9;
            foreach (JsonElement channel in part.GetProperty("channels").EnumerateArray())
            {
                string channelName = channel.GetProperty("name").GetString()!;
                using Raster<GrayF32> raster = ExrImage.ReadChannel(new MemoryStream(file), channelName, new ExrReaderOptions { Part = index });
                Assert.Equal((channel.GetProperty("width").GetInt32(), channel.GetProperty("height").GetInt32()), (raster.Width, raster.Height));
                float[] expected = reference.Channel(channel);
                bool half = channel.GetProperty("type").GetInt32() == 1;
                for (int y = 0; y < raster.Height; y++)
                {
                    ReadOnlySpan<float> row = MemoryMarshal.Cast<GrayF32, float>(raster.Row(y));
                    for (int x = 0; x < row.Length; x++)
                    {
                        float want = expected[(y * raster.Width) + x];
                        if (BitConverter.SingleToInt32Bits(row[x]) != BitConverter.SingleToInt32Bits(want) && !(float.IsNaN(row[x]) && float.IsNaN(want)))
                        {
                            Assert.True(lossy && half && HalfSteps(row[x], want) <= 1, $"{name} part {index} channel {channelName} ({x}, {y}): {row[x]} for {want}");
                        }
                    }
                }
            }

            index++;
        }
    }

    private static void CheckRgba(byte[] file, ExrReference reference, string name)
    {
        JsonElement part = reference.Parts.First();
        if (!part.TryGetProperty("rgba", out JsonElement entries))
        {
            return;
        }

        bool lossy = part.GetProperty("compression").GetInt32() is 8 or 9;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string layer = entry.GetProperty("layer").GetString()!;
            int channels = entry.GetProperty("channels").GetInt32();
            using Raster raster = Raster.Load(file, options: new ExrReaderOptions { Layer = layer.Length == 0 ? null : layer });
            if (layer.Length == 0 && ((ExrDetails)raster.Metadata.FormatDetails!).Layer.Length > 0)
            {
                // No default layer: Egami falls back to a named one, which RgbaInputFile gives as all black.
                continue;
            }

            using Raster<RgbaF32> rgba = Raster.Load<RgbaF32>(file, options: new ExrReaderOptions { Layer = layer.Length == 0 ? null : layer });
            ushort[] expected = reference.Rgba(entry, rgba.Width * rgba.Height);
            for (int y = 0; y < rgba.Height; y++)
            {
                Span<RgbaF32> row = rgba.Row(y);
                for (int x = 0; x < rgba.Width; x++)
                {
                    int i = ((y * rgba.Width) + x) * 4;
                    float[] mine = [row[x].R, row[x].G, row[x].B, row[x].A];
                    for (int c = 0; c < 4; c++)
                    {
                        ushort want = expected[i + c];
                        ushort got = BitConverter.HalfToUInt16Bits((Half)mine[c]);
                        if (got != want && !(Half.IsNaN(BitConverter.UInt16BitsToHalf(got)) && Half.IsNaN(BitConverter.UInt16BitsToHalf(want))))
                        {
                            float w = (float)BitConverter.UInt16BitsToHalf(want);
                            Assert.True(lossy && HalfSteps(mine[c], w) <= 1, $"{name} layer '{layer}' ({x}, {y}) channel {c}: {mine[c]} for {w} (channels {channels})");
                        }
                    }
                }
            }
        }
    }

    /// <summary>Counts the half values between two floats that are halves.</summary>
    private static int HalfSteps(float a, float b)
    {
        static int Ordered(float v)
        {
            int bits = BitConverter.HalfToUInt16Bits((Half)v);
            return (bits & 0x8000) != 0 ? -(bits & 0x7FFF) : bits;
        }

        return Math.Abs(Ordered(a) - Ordered(b));
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Egami.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
