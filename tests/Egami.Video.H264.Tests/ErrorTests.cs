using System.Text.Json;

using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.H264.Tests;

/// <summary>Damaged streams, a start away from the first IDR picture, and seeking in a raw stream.</summary>
public sealed class ErrorTests
{
    private static string PathOf(string file) => Path.Combine(AppContext.BaseDirectory, "corpus", "h264", file);

    private static string[] Expected(string clip)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(clip + ".frames.json")));
        return [.. json.RootElement.GetProperty("md5").EnumerateArray().Select(e => e.GetString()!)];
    }

    private static List<EncodedPacket> Packets(string clip, out VideoTrackInfo track)
    {
        using FileStream stream = File.OpenRead(PathOf(clip + ".h264"));
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        track = demuxer.Info.VideoTrack!;
        List<EncodedPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            packets.Add(packet);
        }

        return packets;
    }

    private static List<string> Decode(VideoTrackInfo track, IEnumerable<EncodedPacket> packets, DecodeErrorMode errors, List<string>? diagnostics = null)
    {
        MediaContext context = MediaContext.Default.WithH264() with
        {
            Egami = EgamiContext.Default with { MaxParallelism = 1, Diagnostics = d => diagnostics?.Add(d.Id) },
        };
        using IVideoDecoder decoder = H264Codec.Instance.CreateDecoder(track, new VideoDecoderOptions { Errors = errors }, context);
        List<string> hashes = [];
        foreach (EncodedPacket packet in packets)
        {
            decoder.Send(packet);
            Receive();
        }

        decoder.Flush();
        Receive();
        return hashes;

        void Receive()
        {
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    hashes.Add(DecodeTests.Md5(frame.Raster));
                }
            }
        }
    }

    [Fact]
    public void DecodingStartsAtTheNextIdrPicture()
    {
        // cavlc-slices has IDR pictures at frames 0 and 8 and no B-frames; without the first, output starts at frame 8.
        string[] expected = Expected("cavlc-slices");
        List<EncodedPacket> packets = Packets("cavlc-slices", out VideoTrackInfo track);
        Assert.True(packets[8].IsKeyframe);
        Assert.False(packets[3].IsKeyframe);
        // The parameter sets come only with the first picture, as x264 writes them, so they lead the cut stream on their own.
        byte[] first = packets[0].Data.ToArray();
        List<byte> header = [];
        for (int at = 0; at + 4 < first.Length;)
        {
            int next = first.AsSpan(at + 4).IndexOf((ReadOnlySpan<byte>)[0, 0, 0, 1]);
            int end = next < 0 ? first.Length : at + 4 + next;
            if ((first[at + 4] & 31) is 7 or 8)
            {
                header.AddRange(first.AsSpan(at, end - at).ToArray());
            }

            at = end;
        }

        EncodedPacket parameterSets = new(1, header.ToArray(), packets[0].Timestamp);
        List<string> frames = Decode(track, [parameterSets, .. packets.Skip(3)], DecodeErrorMode.Throw);
        Assert.Equal(expected[8..], frames);
    }

    [Fact]
    public void SeekGoesBackToTheIdrPictureBefore()
    {
        string[] expected = Expected("cavlc-slices");
        using FileStream stream = File.OpenRead(PathOf("cavlc-slices.h264"));
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        demuxer.Seek(new MediaTime(9, track.TimeBase));
        List<EncodedPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            packets.Add(packet);
        }

        Assert.Equal(2, packets.Count);
        Assert.True(packets[0].IsKeyframe);
        Assert.Equal(new MediaTime(8, track.TimeBase), packets[0].Timestamp);
        Assert.Equal(expected[8..], Decode(track, packets, DecodeErrorMode.Throw));

        demuxer.Seek(MediaTime.Zero);
        Assert.True(demuxer.ReadPacket() is { IsKeyframe: true } first && first.Timestamp == MediaTime.Zero);
    }

    [Fact]
    public void RawStreamTimesAndTrackComeFromTheStream()
    {
        using FileStream stream = File.OpenRead(PathOf("high-crop.h264"));
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        Assert.Equal(CodecIds.Avc, track.CodecId);
        Assert.Equal((170, 100), (track.Width, track.Height));
        Assert.Equal(8, track.BitDepth);
        Assert.Equal(new Rational(25, 1), track.FrameRate);

        using FileStream again = File.OpenRead(PathOf("high-crop.h264"));
        using IDemuxer timed = new H264Format { FrameRate = new Rational(30000, 1001) }.OpenDemuxer(again, MediaContext.Default, leaveOpen: true);
        Assert.Equal(new Rational(30000, 1001), timed.Info.VideoTrack!.FrameRate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264Format { FrameRate = new Rational(0, 1) });
    }

    [Fact]
    public void DetectionTellsH264FromOtherStreams()
    {
        Assert.True(H264Format.Instance.IsMatch(File.ReadAllBytes(PathOf("cavlc-intra.h264")).AsSpan(0, 8)));
        Assert.True(H264Format.Instance.IsMatch([0, 0, 1, 0x09, 0xF0]));
        Assert.False(H264Format.Instance.IsMatch([0, 0, 0, 1, 0x40, 1]));
        Assert.False(H264Format.Instance.IsMatch([0, 0, 0, 1, 0x26, 1]));
        Assert.False(H264Format.Instance.IsMatch("RIFF"u8));
    }

    [Theory]
    [InlineData("cabac-b-spatial")]
    [InlineData("cavlc-b")]
    [InlineData("high10")]
    public void BrokenSlicesAreReportedConcealedOrSkipped(string clip)
    {
        string[] expected = Expected(clip);
        List<EncodedPacket> packets = Packets(clip, out VideoTrackInfo track);

        // Packet 4 loses the second half of its slice data.
        EncodedPacket broken = packets[4];
        byte[] data = broken.Data.ToArray();
        Array.Fill(data, (byte)0xFF, data.Length / 2, data.Length - (data.Length / 2));
        packets[4] = new EncodedPacket(broken.TrackNumber, data, broken.Timestamp) { IsKeyframe = broken.IsKeyframe };

        List<string> diagnostics = [];
        List<string> concealed = Decode(track, packets, DecodeErrorMode.Conceal, diagnostics);
        Assert.Equal(expected.Length, concealed.Count);
        Assert.Contains("H264.CorruptSlice", diagnostics);
        Assert.Equal(expected[0], concealed[0]);

        List<string> skipped = Decode(track, packets, DecodeErrorMode.Skip);
        Assert.True(skipped.Count < expected.Length);
        Assert.All(skipped, hash => Assert.Contains(hash, expected.Concat(concealed)));

        Assert.Throws<CorruptDataException>(() => Decode(track, packets, DecodeErrorMode.Throw));
    }
}
