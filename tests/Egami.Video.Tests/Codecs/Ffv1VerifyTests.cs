using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>Checking FFV1 files' CRCs without decoding them (V6 task 10).</summary>
public class Ffv1VerifyTests
{
    [Theory]
    [InlineData("v3-420.mkv", 3, 12)]
    [InlineData("v3-slices-24.mkv", 1, 24)]
    [InlineData("v3-420-avi.avi", 2, 12)]
    public void AnIntactFileChecksEverySlice(string file, int frames, int slices)
    {
        Ffv1VerifyResult result = Ffv1Codec.Verify(Ffv1Tests.PathOf(file), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsIntact);
        Assert.True(result.HasSliceCrcs);
        Assert.True(result.ConfigurationCrcOk);
        Assert.Equal(frames, result.Frames);
        Assert.Equal(slices, result.Slices);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void FilesWithoutCrcsSayWhatCouldNotBeChecked()
    {
        Ffv1VerifyResult v1 = Ffv1Codec.Verify(Ffv1Tests.PathOf("v1-gop.mkv"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(v1.HasSliceCrcs);
        Assert.Null(v1.ConfigurationCrcOk);
        Assert.Equal(6, v1.Frames);
        Assert.Equal(0, v1.Slices);
        Assert.True(v1.IsIntact);

        Ffv1VerifyResult noCrc = Ffv1Codec.Verify(Ffv1Tests.PathOf("v3-nocrc.mkv"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(noCrc.HasSliceCrcs);
        Assert.True(noCrc.ConfigurationCrcOk);
    }

    [Fact]
    public void DamagedSlicesAndRecordsAreListed()
    {
        MemoryStream output = new();
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(Ffv1Tests.PathOf("v3-420.mkv")))
        {
            byte[] record = demuxer.Info.VideoTrack!.CodecPrivate.ToArray();
            record[^1] ^= 1;
            VideoTrackInfo track = demuxer.Info.VideoTrack with { CodecPrivate = record };
            using IMuxer muxer = MediaFile.CreateMuxer(output, "mkv", [track], leaveOpen: true);
            for (int i = 0; demuxer.ReadPacket() is { } packet; i++)
            {
                using (packet)
                {
                    byte[] data = packet.Data.ToArray();
                    if (i == 1)
                    {
                        data[data.Length / 2] ^= 0x10;
                    }

                    using EncodedPacket copy = new(track.Number, data, packet.Timestamp) { IsKeyframe = packet.IsKeyframe };
                    muxer.WritePacket(copy);
                }
            }
        }

        output.Position = 0;
        Ffv1VerifyResult result = Ffv1Codec.Verify(output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsIntact);
        Assert.False(result.ConfigurationCrcOk);
        Ffv1VerifyError error = Assert.Single(result.Errors);
        Assert.Equal(1, error.Frame);
        Assert.InRange(error.Slice, 0, 3);
    }

    [Fact]
    public void AFileWithoutFfv1IsRefused() =>
        Assert.Throws<UnsupportedFormatException>(() => Ffv1Codec.Verify(Corpus.PathOf("mkv-mjpeg-pcm.mkv"), cancellationToken: TestContext.Current.CancellationToken));
}
