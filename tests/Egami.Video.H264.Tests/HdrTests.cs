using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.H264.Tests;

/// <summary>HDR10+ in an ITU-T T.35 user data SEI message becomes the frame's <see cref="VideoFrame.Hdr10Plus"/>.</summary>
public sealed class HdrTests
{
    [Fact]
    public void Hdr10PlusSeiFillsTheFrame()
    {
        // The HDR10+ payload of the VP9 corpus clip, as WebM's BlockAdditional holds it, moved into a user data SEI message.
        Hdr10PlusMetadata expected;
        using (VideoReader webm = VideoReader.Open(Path.Combine(AppContext.BaseDirectory, "corpus", "hdr10plus.webm")))
        {
            using YuvVideoFrame source = webm.ReadFrame()!;
            expected = source.Hdr10Plus!;
        }

        using FileStream stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "corpus", "h264", "cabac-p.h264"));
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        using IVideoDecoder decoder = H264Codec.Instance.CreateDecoder(track, null, MediaContext.Default.WithH264());
        List<Hdr10PlusMetadata?> found = [];
        bool first = true;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                byte[] data = first ? [.. Sei(expected.Raw.Span), .. packet.Data.Span] : packet.Data.ToArray();
                first = false;
                using EncodedPacket sent = new(packet.TrackNumber, data, packet.Timestamp) { IsKeyframe = packet.IsKeyframe };
                decoder.Send(sent);
            }

            Receive(decoder, found);
        }

        decoder.Flush();
        Receive(decoder, found);
        Hdr10PlusMetadata meta = Assert.IsType<Hdr10PlusMetadata>(found[0]);
        Assert.Equal(expected.TargetDisplayMaxLuminance, meta.TargetDisplayMaxLuminance);
        Assert.Equal(expected.MaxScl, meta.MaxScl);
        Assert.Equal(expected.AverageMaxRgb, meta.AverageMaxRgb);
        Assert.Equal(expected.DistributionMaxRgb, meta.DistributionMaxRgb);
        Assert.Equal(expected.ToneCurve!.Anchors, meta.ToneCurve!.Anchors);
        Assert.All(found.Skip(1), Assert.Null);
    }

    private static void Receive(IVideoDecoder decoder, List<Hdr10PlusMetadata?> found)
    {
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            using (frame)
            {
                found.Add(frame.Hdr10Plus);
            }
        }
    }

    /// <summary>A start code and an SEI NAL unit holding one user_data_registered_itu_t_t35 message (payloadType 4).</summary>
    private static byte[] Sei(ReadOnlySpan<byte> t35)
    {
        List<byte> rbsp = [4];
        int size = t35.Length;
        for (; size >= 255; size -= 255)
        {
            rbsp.Add(255);
        }

        rbsp.Add((byte)size);
        rbsp.AddRange(t35);
        rbsp.Add(0x80);

        // Emulation prevention (7.4.1): a 3 after two zero bytes that come before a byte of 3 or less.
        List<byte> nal = [0, 0, 0, 1, 6];
        int zeros = 0;
        foreach (byte b in rbsp)
        {
            if (zeros >= 2 && b <= 3)
            {
                nal.Add(3);
                zeros = 0;
            }

            nal.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }

        return [.. nal];
    }
}
