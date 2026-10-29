using System.Text.Json;

using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Hevc.Tests;

/// <summary>Damaged streams and a start away from the first IRAP picture, in each <see cref="DecodeErrorMode"/>.</summary>
public sealed class ErrorTests
{
    private static string[] Expected(string clip)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(DecodeTests.PathOf(clip + ".frames.json")));
        return [.. json.RootElement.GetProperty("md5").EnumerateArray().Select(e => e.GetString()!)];
    }

    private static List<EncodedPacket> Packets(string clip, out VideoTrackInfo track)
    {
        using FileStream stream = File.OpenRead(DecodeTests.PathOf(clip + ".h265"));
        using IDemuxer demuxer = HevcFormat.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
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
        MediaContext context = MediaContext.Default.WithHevc() with
        {
            Egami = EgamiContext.Default with { MaxParallelism = 1, Diagnostics = d => diagnostics?.Add(d.Id) },
        };
        using IVideoDecoder decoder = HevcCodec.Instance.CreateDecoder(track, new VideoDecoderOptions { Errors = errors }, context);
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

    /// <summary>The packet with its slice segment NAL units cut after their headers' first bytes, or after half their data.</summary>
    private static EncodedPacket CutSlices(EncodedPacket packet, bool half = false)
    {
        byte[] data = packet.Data.ToArray();
        List<byte> cut = [];
        for (int at = 0; at + 4 < data.Length;)
        {
            int next = data.AsSpan(at + 4).IndexOf((ReadOnlySpan<byte>)[0, 0, 0, 1]);
            int end = next < 0 ? data.Length : at + 4 + next;
            bool slice = ((data[at + 4] >> 1) & 63) < 32;
            cut.AddRange(data.AsSpan(at, !slice ? end - at : half ? (end - at) / 2 : Math.Min(8, end - at)).ToArray());
            at = end;
        }

        return new EncodedPacket(packet.TrackNumber, cut.ToArray(), packet.Timestamp) { IsKeyframe = packet.IsKeyframe };
    }

    [Fact]
    public void BrokenPictureThrowsInThrowMode()
    {
        List<EncodedPacket> packets = Packets("open-gop", out VideoTrackInfo track);
        packets[3] = CutSlices(packets[3]);
        Assert.ThrowsAny<CorruptDataException>(() => Decode(track, packets, DecodeErrorMode.Throw));
    }

    [Fact]
    public void SkipModeStartsAgainAtTheNextIrapPicture()
    {
        // open-gop has an IRAP picture every 8 frames; a broken picture in the first GOP drops frames up to the next one, whose
        // own frames and all after are FFmpeg's.
        string[] expected = Expected("open-gop");
        List<EncodedPacket> packets = Packets("open-gop", out VideoTrackInfo track);
        int next = packets.FindIndex(4, p => p.IsKeyframe);
        Assert.True(next > 4);
        packets[3] = CutSlices(packets[3]);
        List<string> diagnostics = [];
        List<string> frames = Decode(track, packets, DecodeErrorMode.Skip, diagnostics);
        Assert.Contains("Hevc.CorruptFrame", diagnostics);
        Assert.True(frames.Count < expected.Length);
        Assert.Equal(expected[^8..], frames[^8..]);
    }

    [Fact]
    public void ConcealModeKeepsEveryFrame()
    {
        // Slice data cut in half: the picture keeps what decoded and comes out. A broken slice header cannot be concealed.
        string[] expected = Expected("open-gop");
        List<EncodedPacket> packets = Packets("open-gop", out VideoTrackInfo track);
        packets[3] = CutSlices(packets[3], half: true);
        List<string> diagnostics = [];
        List<string> frames = Decode(track, packets, DecodeErrorMode.Conceal, diagnostics);
        Assert.Equal(expected.Length, frames.Count);
        Assert.NotEqual(expected, frames);
        Assert.Equal(expected[^8..], frames[^8..]);
    }

    [Fact]
    public void DecodingStartsAtTheFirstIrapPicture()
    {
        // Without the first IRAP picture (and so without the parameter sets x265 sends with each IRAP picture before it), the
        // pictures before the next IRAP picture give no frames.
        string[] expected = Expected("open-gop");
        List<EncodedPacket> packets = Packets("open-gop", out VideoTrackInfo track);
        int next = packets.FindIndex(1, p => p.IsKeyframe);
        List<string> frames = Decode(track, packets.Skip(1), DecodeErrorMode.Skip);
        int at = Array.IndexOf(expected, frames[0]);
        Assert.True(at >= next);
        Assert.Equal(expected[at..], frames);
    }
}
