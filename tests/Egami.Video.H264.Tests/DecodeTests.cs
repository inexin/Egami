using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.H264.Tests;

/// <summary>Decodes the clips of tools/video/make-h264-corpus.py and compares each frame with FFmpeg's decode of it.</summary>
public sealed class DecodeTests
{
    public static TheoryData<string> Clips =>
    [
        "cavlc-intra", "cavlc-p", "cavlc-slices", "cavlc-b", "cabac-p", "cabac-b-spatial", "cabac-b-temporal", "cabac-weighted",
        "cabac-slices", "high-8x8", "high-8x8-cavlc", "high-cqm", "high-crop", "high10", "high10-intra", "high422", "high422-cavlc",
        "high422-10", "gray", "open-gop", "intra-refresh", "constrained-intra", "many-refs", "slice-max", "weighted-cavlc",
        "deblock-offsets", "qp-low", "qp-high", "tiny-motion", "high10-qp-low", "high422-10-intra",

        // Interlaced: MBAFF frames from x264.
        "mbaff-fake", "mbaff-intra", "mbaff-intra-cavlc", "mbaff-p", "mbaff-p-cavlc", "mbaff-b-spatial", "mbaff-b-temporal",
        "mbaff-b-cavlc", "mbaff-weighted", "mbaff-bff", "mbaff-8x8", "mbaff-slices", "mbaff-high10", "mbaff-high422", "mbaff-pic-struct",
    ];

    public static TheoryData<string> InterlacedClips =>
    [
        "mbaff-fake", "mbaff-intra", "mbaff-p", "mbaff-b-spatial", "mbaff-bff", "mbaff-slices", "mbaff-pic-struct",
    ];

    private static string PathOf(string file) => Path.Combine(AppContext.BaseDirectory, "corpus", "h264", file);

    [Theory]
    [MemberData(nameof(Clips))]
    public void RawStreamMatchesFfmpeg(string clip)
    {
        using FileStream stream = File.OpenRead(PathOf(clip + ".h264"));
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        Check(clip, demuxer, 1);
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void ThreadsGiveTheSameFrames(string clip)
    {
        using FileStream stream = File.OpenRead(PathOf(clip + ".h264"));
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        Check(clip, demuxer, 4);
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void ScalarFormsMatchFfmpeg(string clip)
    {
        // The tests of this class run one at a time, so turning the vector forms off here does not reach the others.
        Codecs.H264.H264Inter.UseVectors = false;
        Codecs.H264.H264Deblock.UseVectors = false;
        try
        {
            RawStreamMatchesFfmpeg(clip);
        }
        finally
        {
            Codecs.H264.H264Inter.UseVectors = System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated;
            Codecs.H264.H264Deblock.UseVectors = Codecs.H264.H264Deblock.VectorsSupported;
        }
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void NarrowVectorFormsMatchFfmpeg(string clip)
    {
        // The 128-bit forms, which run where 256-bit vectors are not available.
        Codecs.H264.H264Inter.UseWideVectors = false;
        Codecs.H264.H264Deblock.UseWideVectors = false;
        try
        {
            RawStreamMatchesFfmpeg(clip);
        }
        finally
        {
            Codecs.H264.H264Inter.UseWideVectors = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
            Codecs.H264.H264Deblock.UseWideVectors = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        }
    }

    [Theory]
    [MemberData(nameof(InterlacedClips))]
    public void FieldOrderMatchesFfprobe(string clip)
    {
        // ffprobe's interlaced_frame, top_field_first and repeat_pict of each frame, in output order.
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(clip + ".frames.json")));
        string[] expected = [.. json.RootElement.GetProperty("fields").EnumerateArray().Select(e => string.Join(",", e.GetString()!.Split(',').Take(3)))];
        List<string> actual = [];
        foreach (YuvVideoFrame frame in Frames(File.ReadAllBytes(PathOf(clip + ".h264"))))
        {
            using (frame)
            {
                int interlaced = frame.FieldOrder == FieldOrder.Progressive ? 0 : 1, topFirst = frame.FieldOrder == FieldOrder.TopFieldFirst ? 1 : 0;
                actual.Add($"{interlaced},{topFirst},{frame.RepeatFields}");
            }
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PicStructRepeatsFields()
    {
        // mbaff-pic-struct's picture timing SEIs give pic_struct 3 in the first payload bits (no HRD delays); 5 (top, bottom,
        // top) shows each frame for three fields.
        byte[] data = File.ReadAllBytes(PathOf("mbaff-pic-struct.h264"));
        int patched = 0;
        for (int i = 0; i + 6 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && (data[i + 3] & 31) == 6 && data[i + 4] == 1 && (data[i + 6] >> 4) == 3)
            {
                data[i + 6] = (byte)((5 << 4) | (data[i + 6] & 15));
                patched++;
            }
        }

        Assert.Equal(8, patched);
        List<YuvVideoFrame> frames = Frames(data);
        Assert.All(frames, f => Assert.Equal(1, f.RepeatFields));
        Assert.All(frames, f => Assert.NotEqual(FieldOrder.Progressive, f.FieldOrder));
        frames.ForEach(f => f.Dispose());
    }

    private static List<YuvVideoFrame> Frames(byte[] stream)
    {
        using MemoryStream input = new(stream);
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(input, MediaContext.Default, leaveOpen: true);
        using IVideoDecoder decoder = H264Codec.Instance.CreateDecoder(demuxer.Info.VideoTrack!, null, MediaContext.Default.WithH264());
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

    [Theory]
    [InlineData("cabac-b-spatial")]
    [InlineData("high10")]
    public void Mp4MatchesFfmpeg(string clip)
    {
        using FileStream stream = File.OpenRead(PathOf(clip + ".mp4"));
        using IDemuxer demuxer = Mp4Format.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        Check(clip, demuxer, 1);
    }

    private static void Check(string clip, IDemuxer demuxer, int threads)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(clip + ".frames.json")));
        string[] expected = [.. json.RootElement.GetProperty("md5").EnumerateArray().Select(e => e.GetString()!)];
        int width = json.RootElement.GetProperty("width").GetInt32(), height = json.RootElement.GetProperty("height").GetInt32();
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        Assert.Equal((width, height), (track.Width, track.Height));
        MediaContext context = MediaContext.Default.WithH264() with { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        using IVideoDecoder decoder = H264Codec.Instance.CreateDecoder(track, null, context);
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

                if (r8.U is null)
                {
                    // FFmpeg gives a monochrome stream as 4:2:0 with mid-gray chroma.
                    byte[] gray = new byte[((r8.Width + 1) / 2) * ((r8.Height + 1) / 2) * 2];
                    Array.Fill(gray, (byte)128);
                    md5.AppendData(gray);
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
