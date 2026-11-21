using Egami.Video.Codecs;
using Egami.Video.Codecs.Hevc.Encoder;

namespace Egami.Video.Hevc.Tests;

/// <summary>
/// The inter search's settings one at a time, each against the effort's own: every stream decodes to the encoder's
/// reconstruction, whichever way the search goes.
/// </summary>
public sealed class InterProfileTests
{
    public static TheoryData<string, int> Settings => new()
    {
        { "HalvesGate:false", 4 },
        { "TransformSplit:false", 4 },
        { "FullIntra:true", 4 },
        { "EarlySkip:false", 4 },
        { "SplitStatistics:false", 4 },
        { "IntraGate:false", 2 },
        { "RdoqKeptOnly:false", 4 },
        { "SquareSubpel:true", 1 },
        { "TransformDepth:0", 4 },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void StreamsDecodeToTheReconstruction(string setting, int effort)
    {
        string[] parts = setting.Split(':');
        System.Reflection.PropertyInfo property = typeof(HevcInterProfile).GetProperty(parts[0],
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        object value = property.PropertyType == typeof(bool) ? bool.Parse(parts[1]) : int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        HevcInterProfile.Adjust = profile =>
        {
            HevcInterProfile changed = profile with { };
            property.SetValue(changed, value);
            return changed;
        };
        try
        {
            VideoEncoderOptions options = new()
            {
                Width = 160,
                Height = 96,
                Effort = effort,
                KeyframeInterval = 8,
                Hevc = new HevcEncoderOptions { AdaptiveQuantization = false },
            };
            (List<EncodedPacket> packets, List<ushort[]> recon, VideoTrackInfo track) = EncoderTests.Encode(options, 6, i => EncoderTests.Picture(160, 96, i, 8));
            List<ushort[]> decoded = EncoderTests.Decode(track, packets);
            Assert.Equal(6, decoded.Count);
            for (int i = 0; i < decoded.Count; i++)
            {
                Assert.True(decoded[i].AsSpan().SequenceEqual(recon[i]), $"Frame {i} decodes differently from the encoder's reconstruction.");
            }

            EncoderTests.Dispose(packets);
        }
        finally
        {
            HevcInterProfile.Adjust = null;
        }
    }
}
