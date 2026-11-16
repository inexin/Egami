using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>
/// The CAVLC rate count against the bits written: every coded macroblock counts as many bits as it writes, intra 4x4, 8x8 and
/// 16x16 and inter alike (the intra modes' prev_intra_pred_mode_flag and rem_intra_pred_mode were once left out of the count).
/// </summary>
[Collection(nameof(EncoderStatsRunsAlone))]
public sealed class EncoderCavlcParityTests
{
    [Theory]
    [InlineData(H264Profile.High, 4)]
    [InlineData(H264Profile.High, 6)]
    [InlineData(H264Profile.Main, 2)]
    public void CountedBitsAreTheBitsWritten(H264Profile profile, int effort)
    {
        List<(H264MbKind Kind, long Counted, long Written)> macroblocks = [];
        H264PictureEncoder.CavlcParity = (kind, counted, written) =>
        {
            lock (macroblocks)
            {
                macroblocks.Add((kind, counted, written));
            }
        };
        try
        {
            VideoEncoderOptions options = new()
            {
                Width = 80,
                Height = 64,
                Effort = effort,
                KeyframeInterval = 4,
                H264 = new H264EncoderOptions { Profile = profile, Entropy = H264EntropyCoder.Cavlc, AdaptiveQuantization = true },
            };
            EncoderInterTests.Check(options, 9, EncoderInterTests.Moving);
        }
        finally
        {
            H264PictureEncoder.CavlcParity = null;
        }

        Assert.All(macroblocks, m => Assert.True(m.Counted == m.Written, $"{m.Kind}: counted {m.Counted} bits, wrote {m.Written}"));

        // The intra modes are written only in I_NxN macroblocks (4x4, and 8x8 in High).
        Assert.Contains(macroblocks, m => m.Kind == (profile == H264Profile.High ? H264MbKind.I8x8 : H264MbKind.I4x4));
        Assert.Contains(macroblocks, m => m.Kind == H264MbKind.Inter);
    }
}
