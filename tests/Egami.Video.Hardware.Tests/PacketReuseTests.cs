using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Hardware.Tests;

/// <summary>
/// The hardware decoders keep nothing of a packet after <see cref="IVideoDecoder.Send"/>: a caller such as
/// <see cref="VideoReader"/> disposes each packet once it is sent, and its pooled memory is reused, while the picture it
/// belongs to is only submitted when the next picture starts.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class PacketReuseTests
{
    [Theory]
    [InlineData("h264", "cabac-slices.h264")]
    [InlineData("h264", "cabac-b-spatial.h264")]
    [InlineData("hevc", "slices.h265")]
    [InlineData("hevc", "b.h265")]
    public void PacketsMayBeOverwrittenOnceSent(string folder, string clip)
    {
        bool hevc = folder == "hevc";
        if (hevc)
        {
            HevcDecoderTests.RequireDecoder();
        }
        else
        {
            H264DecoderTests.RequireDecoder();
        }

        byte[] stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "corpus", folder, clip));
        List<YuvVideoFrame> expected = hevc ? HevcDecoderTests.Decode(stream, clip, HardwareVideoCodecs.Hevc) : H264DecoderTests.Decode(stream, HardwareVideoCodecs.H264);
        List<YuvVideoFrame> actual = [];
        MediaContext context = MediaContext.Default.WithH264().WithHevc();
        using MemoryStream input = new(stream);
        using IDemuxer demuxer = hevc ? HevcFormat.Instance.OpenDemuxer(input, context, leaveOpen: true) : H264Format.Instance.OpenDemuxer(input, context, leaveOpen: true);
        using IVideoDecoder decoder = (hevc ? HardwareVideoCodecs.Hevc : HardwareVideoCodecs.H264).CreateDecoder(demuxer.Info.VideoTrack!, null, context);
        while (demuxer.ReadPacket() is { } packet)
        {
            decoder.Send(packet);
            packet.GetWritableData().Span.Fill(0xA5);
            packet.Dispose();
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                actual.Add(frame);
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            actual.Add(frame);
        }

        try
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                YuvRaster<byte> a = (YuvRaster<byte>)expected[i].Planes!, b = (YuvRaster<byte>)actual[i].Planes!;
                for (int y = 0; y < a.Height; y++)
                {
                    Assert.True(a.Y.Row(y).SequenceEqual(b.Y.Row(y)), $"{clip} frame {i} row {y}");
                }
            }
        }
        finally
        {
            expected.ForEach(f => f.Dispose());
            actual.ForEach(f => f.Dispose());
        }
    }
}
