using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Tests.Containers;

namespace Egami.Video.Tests;

/// <summary>
/// Inputs AFL++ found crashing (tests/video/fuzz-regressions): each must now be read or rejected with an EgamiException, along the
/// same path the fuzz harness takes.
/// </summary>
public class FuzzRegressionTests
{
    public static TheoryData<string> Inputs => [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fuzz-regressions"), "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "fuzz-regressions"), f))];

    [Theory]
    [MemberData(nameof(Inputs))]
    public void ReadsOrRejectsCleanly(string input)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fuzz-regressions", input));
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { MaxParallelism = 1 } };
        if (input.StartsWith("mjpeg", StringComparison.Ordinal))
        {
            VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), Width = 64, Height = 64 };
            try
            {
                using IVideoDecoder decoder = MotionJpegCodec.Instance.CreateDecoder(track, null, context);
                using EncodedPacket packet = new(1, data, MediaTime.Zero);
                decoder.Send(packet);
                decoder.Flush();
                while (decoder.TryReceive(out YuvVideoFrame? frame))
                {
                    frame.Dispose();
                }
            }
            catch (EgamiException)
            {
            }

            return;
        }

        if (input.StartsWith("vp9raw", StringComparison.Ordinal))
        {
            DecodeVp9Packets(data, context);
            return;
        }

        if (input.StartsWith("vp9", StringComparison.Ordinal))
        {
            // Broken VP9 frames are reported, never thrown, in Skip and Conceal.
            foreach (DecodeErrorMode mode in (DecodeErrorMode[])[DecodeErrorMode.Skip, DecodeErrorMode.Conceal])
            {
                try
                {
                    using VideoReader reader = VideoReader.Open(new MemoryStream(data), new VideoReaderOptions { Errors = mode }, context);
                    while (reader.ReadFrame() is { } frame)
                    {
                        frame.Dispose();
                    }
                }
                catch (EgamiException e) when (e is not CorruptDataException { FormatName: "VP9" })
                {
                }
            }

            return;
        }

        try
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(data), context);
            Drain(demuxer);
            demuxer.Seek(MediaTime.FromSeconds(0.5));
            Drain(demuxer);
            demuxer.Seek(MediaTime.Zero);
            Drain(demuxer);
        }
        catch (EgamiException)
        {
        }

        try
        {
            using IDemuxer streaming = MediaFile.OpenDemuxer(new NonSeekableStream(data), context);
            Drain(streaming);
        }
        catch (EgamiException)
        {
        }
    }

    /// <summary>The vp9raw harness's input: a CodecPrivate after its length byte, then packets after 2-byte little-endian lengths, concealed.</summary>
    private static void DecodeVp9Packets(byte[] data, MediaContext context)
    {
        int privateLength = Math.Min(data[0], data.Length - 1);
        VideoTrackInfo track = new()
        {
            Number = 1,
            Kind = TrackKind.Video,
            CodecId = CodecIds.Vp9,
            TimeBase = new Rational(1, 30),
            Width = 64,
            Height = 64,
            CodecPrivate = data.AsSpan(1, privateLength).ToArray(),
        };
        using IVideoDecoder decoder = VideoCodecs.Vp9.CreateDecoder(track, new VideoDecoderOptions { Errors = DecodeErrorMode.Conceal }, context);
        for (int at = 1 + privateLength, i = 0; at + 2 <= data.Length; i++)
        {
            int length = Math.Min(data[at] | (data[at + 1] << 8), data.Length - at - 2);
            using EncodedPacket packet = new(1, data.AsSpan(at + 2, length).ToArray(), new MediaTime(i, track.TimeBase)) { IsKeyframe = i == 0 };
            decoder.Send(packet);
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                frame.Dispose();
            }

            at += 2 + length;
        }
    }

    private static void Drain(IDemuxer demuxer)
    {
        for (int i = 0; i < 1000 && demuxer.ReadPacket() is { } packet; i++)
        {
            packet.Dispose();
        }
    }
}
