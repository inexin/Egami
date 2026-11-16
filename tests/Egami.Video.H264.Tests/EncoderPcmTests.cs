using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>
/// A macroblock_layer takes at most 128 + RawMbBits, 3200 bits (A.3.1, A.3.3): one coded bigger, noise at QP 0, is written as
/// I_PCM, in CAVLC and CABAC, intra and inter pictures, and decodes to the encoder's reconstruction.
/// </summary>
[Collection(nameof(EncoderStatsRunsAlone))]
public sealed class EncoderPcmTests
{
    [Theory]
    [InlineData(H264Profile.High, H264EntropyCoder.Cavlc)]
    [InlineData(H264Profile.High, H264EntropyCoder.Cabac)]
    [InlineData(H264Profile.Main, H264EntropyCoder.Cavlc)]
    [InlineData(H264Profile.Main, H264EntropyCoder.Cabac)]
    public void OversizedMacroblocksBecomePcm(H264Profile profile, H264EntropyCoder entropy)
    {
        int pcm = 0;
        H264PictureEncoder.PcmWritten = _ => Interlocked.Increment(ref pcm);
        try
        {
            VideoEncoderOptions options = new()
            {
                Width = 64,
                Height = 64,
                Quantizer = 0,
                Effort = 4,
                KeyframeInterval = 4,
                H264 = new H264EncoderOptions
                {
                    Profile = profile,
                    Entropy = entropy,
                    AdaptiveQuantization = false,
                    MinQuantizer = 0,
                },
            };

            // Noise in the even frames, checks in the odd ones: I_PCM macroblocks next to coded ones, in I, P and B pictures.
            EncoderInterTests.Check(options, 6, (w, h, f) => EncoderCavlcLevelTests.Make(w, h, f, f % 2 == 0 ? 1 : 0));
        }
        finally
        {
            H264PictureEncoder.PcmWritten = null;
        }

        Assert.True(pcm > 0, "No macroblock was written as I_PCM.");
    }
}
