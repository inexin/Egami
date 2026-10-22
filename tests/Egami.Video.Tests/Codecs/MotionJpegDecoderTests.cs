using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.MotionJpeg;
using Egami.Video.Containers;
using Egami.Video.Tests.Containers;

namespace Egami.Video.Tests.Codecs;

public class MotionJpegDecoderTests
{
    public static TheoryData<string> MotionJpegClips => ["avi-mjpeg-420-pcm.avi", "avi-mjpeg-422-mp3-ntsc.avi", "avi-mjpeg-444-noaudio.avi", "mkv-mjpeg-pcm.mkv", "mkv-mjpeg-vfw.mkv"];

    [Theory]
    [MemberData(nameof(MotionJpegClips))]
    public void FramesMatchEgamisOwnDecode(string clip)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Corpus.PathOf(clip));
        VideoTrackInfo track = (VideoTrackInfo)demuxer.Tracks[0];
        Assert.True(MotionJpegCodec.Instance.CanDecode(track));
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(track);
        int frames = 0;
        foreach ((EncodedPacket packet, YuvVideoFrame frame) in DecodeAll(demuxer, decoder, track.Number))
        {
            using (packet)
            using (frame)
            {
                using YuvRaster expected = YuvRaster.Load(packet.Data);
                AssertSamePlanes(expected, frame.Raster);
                Assert.Equal(packet.Timestamp, frame.Timestamp);
                Assert.Equal(MotionJpegDecoder.ColorFor(track), frame.Color);
                Assert.Equal(5, frame.Color!.Value.MatrixCoefficients);
                frames++;
            }
        }

        Assert.True(frames >= 20);
    }

    [Fact]
    public void FramesWithoutDhtGetTheStandardTables()
    {
        // FFmpeg wrote this clip with -huffman default, so its tables are the Annex K ones and removing them changes nothing.
        foreach (byte[] jpeg in Packets("avi-mjpeg-420-pcm.avi").Take(5))
        {
            byte[] stripped = StripSegments(jpeg, 0xC4);
            Assert.True(stripped.Length < jpeg.Length);
            using YuvRaster expected = YuvRaster.Load(jpeg);
            using YuvRaster actual = DecodeOne(stripped);
            AssertSamePlanes(expected, actual);
        }
    }

    [Fact]
    public void JunkAroundTheImageIsIgnored()
    {
        byte[] jpeg = Packets("avi-mjpeg-444-noaudio.avi").First();
        byte[] padded = [0, 0, 0x12, 0x34, 0xFF, .. jpeg, 0, 0, 0, 0xFF, 0xFF];
        using YuvRaster expected = YuvRaster.Load(jpeg);
        using YuvRaster actual = DecodeOne(padded);
        AssertSamePlanes(expected, actual);
    }

    [Theory]
    [InlineData(1, ChromaSubsampling.Subsample420)]
    [InlineData(2, ChromaSubsampling.Subsample422)]
    [InlineData(0, ChromaSubsampling.Subsample420)]
    public void InterlacedFieldPairsAreWoven(int polarity, ChromaSubsampling subsampling)
    {
        // Two half-height field images, the first carrying an AVI1 APP0 with the field order.
        using Raster<Rgb8> source = Raster.Create<Rgb8>(64, 48);
        for (int y = 0; y < source.Height; y++)
        {
            Span<Rgb8> row = source.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgb8((byte)(x * 4), (byte)(y * 5), (byte)((x + y) * 2));
            }
        }

        byte[] even = EncodeRows(source, 0, subsampling), odd = EncodeRows(source, 1, subsampling);
        byte[] first = polarity == 2 ? odd : even, second = polarity == 2 ? even : odd;
        byte[] avi1 = [0xFF, 0xE0, 0x00, 0x10, (byte)'A', (byte)'V', (byte)'I', (byte)'1', (byte)polarity, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] packet = [.. first[..2], .. avi1, .. first[2..], .. second];

        using YuvRaster woven = DecodeOne(packet, height: 48);
        using YuvRaster top = YuvRaster.Load(even);
        using YuvRaster bottom = YuvRaster.Load(odd);
        Assert.Equal((64, 48), (woven.Width, woven.Height));
        YuvRaster<byte> w = (YuvRaster<byte>)woven, t = (YuvRaster<byte>)top, b = (YuvRaster<byte>)bottom;
        for (int y = 0; y < 48; y++)
        {
            YuvPlane<byte> field = (y & 1) == 0 ? t.Y : b.Y;
            Assert.True(field.Row(y / 2).SequenceEqual(w.Y.Row(y)), $"row {y}");
        }

        for (int y = 0; y < w.U!.Height; y++)
        {
            YuvPlane<byte> field = (y & 1) == 0 ? t.U! : b.U!;
            Assert.True(field.Row(y / 2).SequenceEqual(w.U.Row(y)), $"chroma row {y}");
        }
    }

    [Fact]
    public void ParallelDecodeKeepsFramesInOrder()
    {
        List<byte[]> packets = [.. Packets("avi-mjpeg-422-mp3-ntsc.avi")];
        List<long> serial = DecodeTimes(packets, 1), parallel = DecodeTimes(packets, 4);
        Assert.Equal(serial, parallel);
        Assert.Equal(serial.Order(), serial);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void ScaledDecodeMatchesEgamisScaledDecode(int denominator)
    {
        byte[] jpeg = Packets("avi-mjpeg-420-pcm.avi").First();
        VideoTrackInfo track = Track(160, 120);
        using IVideoDecoder decoder = MotionJpegCodec.Instance.CreateDecoder(track, new VideoDecoderOptions { ScaleDenominator = denominator }, MediaContext.Default);
        using EncodedPacket packet = new(1, jpeg, MediaTime.Zero);
        decoder.Send(packet);
        decoder.Flush();
        Assert.True(decoder.TryReceive(out YuvVideoFrame? frame));
        using (frame)
        {
            Assert.Equal((160 / denominator, 120 / denominator), (frame.Width, frame.Height));
            using YuvRaster expected = YuvRaster.Load(jpeg, options: new ReaderOptions { MinimumSize = new Size(160 / denominator, 120 / denominator) });
            AssertSamePlanes(expected, frame.Raster);
        }
    }

    [Fact]
    public void ContainerColorOverridesJfif()
    {
        Egami.Colors.ColorCodePoints bt709 = new(1, 1, 1, false);
        VideoTrackInfo track = Track(160, 120) with { Color = bt709 };
        using IVideoDecoder decoder = MotionJpegCodec.Instance.CreateDecoder(track, null, MediaContext.Default);
        using EncodedPacket packet = new(1, Packets("avi-mjpeg-420-pcm.avi").First(), MediaTime.Zero);
        decoder.Send(packet);
        decoder.Flush();
        Assert.True(decoder.TryReceive(out YuvVideoFrame? frame));
        using (frame)
        {
            Assert.Equal(bt709, frame.Color);
        }
    }

    [Fact]
    public void PacketsWithoutAnImageAreCorrupt()
    {
        using IVideoDecoder decoder = MotionJpegCodec.Instance.CreateDecoder(Track(16, 16), null, MediaContext.Default with { Egami = EgamiContext.Default with { MaxParallelism = 1 } });
        using EncodedPacket packet = new(1, new byte[] { 1, 2, 3 }, MediaTime.Zero);
        Assert.Throws<CorruptDataException>(() =>
        {
            decoder.Send(packet);
            decoder.Flush();
            decoder.TryReceive(out _);
        });
    }

    internal static IEnumerable<(EncodedPacket Packet, YuvVideoFrame Frame)> DecodeAll(IDemuxer demuxer, IVideoDecoder decoder, int track)
    {
        // The decoder may hold frames back, so pair frames with packets by order.
        Queue<EncodedPacket> sent = new();
        while (demuxer.ReadPacket() is { } packet)
        {
            if (packet.TrackNumber != track)
            {
                packet.Dispose();
                continue;
            }

            decoder.Send(packet);
            sent.Enqueue(packet);
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                yield return (sent.Dequeue(), frame);
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            yield return (sent.Dequeue(), frame);
        }
    }

    internal static IEnumerable<byte[]> Packets(string clip)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Corpus.PathOf(clip));
        int track = demuxer.Tracks[0].Number;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == track)
                {
                    yield return packet.Data.ToArray();
                }
            }
        }
    }

    internal static void AssertSamePlanes(YuvRaster expected, YuvRaster actual)
    {
        Assert.Equal((expected.Width, expected.Height, expected.ChromaFormat), (actual.Width, actual.Height, actual.ChromaFormat));
        YuvRaster<byte> e = (YuvRaster<byte>)expected, a = (YuvRaster<byte>)actual;
        foreach ((YuvPlane<byte>? ep, YuvPlane<byte>? ap) in ((YuvPlane<byte>?, YuvPlane<byte>?)[])[(e.Y, a.Y), (e.U, a.U), (e.V, a.V)])
        {
            Assert.Equal(ep is null, ap is null);
            if (ep is null)
            {
                continue;
            }

            for (int y = 0; y < ep.Height; y++)
            {
                Assert.True(ep.Row(y).SequenceEqual(ap!.Row(y)), $"row {y} differs");
            }
        }
    }

    private static VideoTrackInfo Track(int width, int height) =>
        new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), Width = width, Height = height };

    private static YuvRaster DecodeOne(byte[] packet, int height = 120)
    {
        using IVideoDecoder decoder = MotionJpegCodec.Instance.CreateDecoder(Track(64, height), null, MediaContext.Default);
        using EncodedPacket p = new(1, packet, MediaTime.Zero);
        decoder.Send(p);
        decoder.Flush();
        Assert.True(decoder.TryReceive(out YuvVideoFrame? frame));
        YuvRaster planes = frame.Raster;
        return planes;
    }

    private static List<long> DecodeTimes(List<byte[]> packets, int parallelism)
    {
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { MaxParallelism = parallelism } };
        using IVideoDecoder decoder = MotionJpegCodec.Instance.CreateDecoder(Track(176, 144), null, context);
        List<long> times = [];
        for (int i = 0; i < packets.Count; i++)
        {
            using EncodedPacket packet = new(1, packets[i], new MediaTime(i, new Rational(1, 25)));
            decoder.Send(packet);
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    times.Add(frame.Timestamp.Ticks);
                }
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            using (frame)
            {
                times.Add(frame.Timestamp.Ticks);
            }
        }

        Assert.Equal(packets.Count, times.Count);
        return times;
    }

    private static byte[] StripSegments(byte[] jpeg, byte marker)
    {
        List<byte> output = [jpeg[0], jpeg[1]];
        int position = 2;
        while (position + 4 <= jpeg.Length && jpeg[position] == 0xFF && jpeg[position + 1] != 0xDA)
        {
            int length = (jpeg[position + 2] << 8) | jpeg[position + 3];
            if (jpeg[position + 1] != marker)
            {
                output.AddRange(jpeg.AsSpan(position, 2 + length));
            }

            position += 2 + length;
        }

        output.AddRange(jpeg.AsSpan(position));
        return [.. output];
    }

    private static byte[] EncodeRows(Raster<Rgb8> source, int parity, ChromaSubsampling subsampling)
    {
        using Raster<Rgb8> field = Raster.Create<Rgb8>(source.Width, source.Height / 2);
        for (int y = 0; y < field.Height; y++)
        {
            source.Row((y * 2) + parity).CopyTo(field.Row(y));
        }

        using MemoryStream output = new();
        field.Save(output, JpegFormat.Info, new JpegWriterOptions { Quality = 90, Subsampling = subsampling });
        return output.ToArray();
    }
}
