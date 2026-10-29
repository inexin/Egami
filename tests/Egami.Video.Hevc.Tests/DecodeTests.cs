using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Hevc.Tests;

/// <summary>
/// Decodes the clips of tools/video/make-hevc-corpus.py and compares each frame with FFmpeg's decode of it (HM's for ctu16, whose
/// chroma FFmpeg decodes against the stream's own picture hashes), and every picture with the stream's MD5 picture hash.
/// </summary>
public sealed class DecodeTests
{
    public static TheoryData<string> Clips =>
    [
        "intra", "p", "b", "b-adapt", "open-gop", "weighted", "amp", "ctu16", "wpp", "slices", "no-sao", "tmvp-off", "lossless",
        "crop", "qp-high", "qp-low", "main10", "main12", "422-10", "444", "444-12", "gray", "hdr10",
    ];

    internal static string PathOf(string file) => Path.Combine(AppContext.BaseDirectory, "corpus", "hevc", file);

    [Theory]
    [MemberData(nameof(Clips))]
    public void RawStreamMatchesFfmpeg(string clip)
    {
        using FileStream stream = File.OpenRead(PathOf(clip + ".h265"));
        using IDemuxer demuxer = HevcFormat.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        Check(clip, demuxer, 1);
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void ThreadsGiveTheSameFrames(string clip)
    {
        using FileStream stream = File.OpenRead(PathOf(clip + ".h265"));
        using IDemuxer demuxer = HevcFormat.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        Check(clip, demuxer, 4);
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void PicturesMatchTheirHashes(string clip)
    {
        using Egami.Heic.HevcStreamDecoder decoder = new(new Egami.Heic.HevcDecoderOptions { CheckPictureHashes = true });
        decoder.Send(File.ReadAllBytes(PathOf(clip + ".h265")));
        decoder.Flush();
        int frames = 0;
        while (decoder.NextFrame is { } next)
        {
            using YuvRaster raster = next.BitDepth == 8
                ? YuvRaster.Create(next.Width, next.Height, next.Chroma, YuvColor.Bt709Limited)
                : YuvRaster.Create(next.Width, next.Height, next.Chroma, next.BitDepth, YuvColor.Bt709Limited);
            Assert.True(decoder.TryReceiveFrame(raster, out _));
            frames++;
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(clip + ".frames.json")));
        Assert.Equal(json.RootElement.GetProperty("md5").GetArrayLength(), frames);
    }

    [Theory]
    [InlineData("b")]
    [InlineData("main10")]
    [InlineData("hdr10")]
    public void Mp4MatchesFfmpeg(string clip)
    {
        using FileStream stream = File.OpenRead(PathOf(clip + ".mp4"));
        using IDemuxer demuxer = Mp4Format.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        Check(clip, demuxer, 1);
    }

    [Fact]
    public void MatroskaMatchesFfmpeg()
    {
        using FileStream stream = File.OpenRead(PathOf("p.mkv"));
        using IDemuxer demuxer = MatroskaFormat.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        Check("p", demuxer, 1);
    }

    [Fact]
    public void VideoReaderOpensRawAndMp4Files()
    {
        MediaContext context = MediaContext.Default.WithHevc();
        // The MP4's edit list, from FFmpeg's remux of the raw stream, shows 8 of its 10 frames, as ffprobe -count_frames reads it.
        foreach ((string file, int frames) in new[] { ("main10.h265", 10), ("main10.mp4", 8) })
        {
            using VideoReader reader = VideoReader.Open(PathOf(file), context: context);
            Assert.Equal(CodecIds.Hevc, reader.Track.CodecId);
            int count = 0;
            while (reader.ReadFrame() is { } frame)
            {
                using (frame)
                {
                    Assert.Equal((176, 144), (frame.Width, frame.Height));
                    count++;
                }
            }

            Assert.Equal(frames, count);
        }
    }

    [Fact]
    public void RawStreamTrackComesFromTheSps()
    {
        using FileStream stream = File.OpenRead(PathOf("422-10.h265"));
        using IDemuxer demuxer = HevcFormat.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        Assert.Equal(CodecIds.Hevc, track.CodecId);
        Assert.Equal((176, 144, 10, ChromaFormat.Yuv422), (track.Width, track.Height, track.BitDepth, track.ChromaFormat));
        Assert.Equal(new Rational(25, 1), track.FrameRate);
    }

    [Fact]
    public void FormatTellsHevcFromH264()
    {
        Assert.True(HevcFormat.Instance.IsMatch(File.ReadAllBytes(PathOf("p.h265")).AsSpan(0, 6)));
        Assert.False(HevcFormat.Instance.IsMatch([0, 0, 0, 1, 0x67, 0x64]));
        Assert.False(HevcFormat.Instance.IsMatch([0, 0, 0, 1, 0x40, 0x09]));
    }

    internal static List<YuvVideoFrame> Frames(string clip, int threads = 1)
    {
        using FileStream stream = File.OpenRead(PathOf(clip + ".h265"));
        using IDemuxer demuxer = HevcFormat.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        MediaContext context = MediaContext.Default.WithHevc() with { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        using IVideoDecoder decoder = HevcCodec.Instance.CreateDecoder(demuxer.Info.VideoTrack!, null, context);
        List<YuvVideoFrame> frames = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                decoder.Send(packet);
            }

            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                frames.Add(frame);
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static void Check(string clip, IDemuxer demuxer, int threads)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(clip + ".frames.json")));
        string[] expected = [.. json.RootElement.GetProperty("md5").EnumerateArray().Select(e => e.GetString()!)];
        int width = json.RootElement.GetProperty("width").GetInt32(), height = json.RootElement.GetProperty("height").GetInt32();
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        Assert.Equal((width, height), (track.Width, track.Height));
        MediaContext context = MediaContext.Default.WithHevc() with { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        using IVideoDecoder decoder = HevcCodec.Instance.CreateDecoder(track, null, context);
        List<string> actual = [];
        List<MediaTime> times = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                decoder.Send(packet);
            }

            Receive(decoder, actual, times);
        }

        decoder.Flush();
        Receive(decoder, actual, times);
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i] == actual[i], $"{clip}: frame {i} of {expected.Length} differs from FFmpeg's.");
        }

        for (int i = 1; i < times.Count; i++)
        {
            Assert.True(times[i] > times[i - 1], $"{clip}: frame {i} is not later than frame {i - 1}.");
        }
    }

    private static void Receive(IVideoDecoder decoder, List<string> hashes, List<MediaTime> times)
    {
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            using (frame)
            {
                hashes.Add(Md5(frame.Raster));
                times.Add(frame.Timestamp);
            }
        }
    }

    /// <summary>The MD5 of the planes one after the other, as FFmpeg's framemd5 gives it: a byte per sample at 8 bits, two above.</summary>
    internal static string Md5(YuvRaster raster)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        switch (raster)
        {
            case YuvRaster<byte> r8:
                foreach (YuvPlane<byte>? plane in new[] { r8.Y, r8.U, r8.V })
                {
                    for (int y = 0; plane is not null && y < plane.Height; y++)
                    {
                        md5.AppendData(plane.Row(y));
                    }
                }

                break;
            case YuvRaster<ushort> r16:
                foreach (YuvPlane<ushort>? plane in new[] { r16.Y, r16.U, r16.V })
                {
                    for (int y = 0; plane is not null && y < plane.Height; y++)
                    {
                        md5.AppendData(MemoryMarshal.AsBytes(plane.Row(y)));
                    }
                }

                break;
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }
}
