using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Vp9;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>VP9 through the codec, VideoReader and the containers (V3 task 9).</summary>
public class Vp9Tests
{
    public static TheoryData<string, string> ClipFiles
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach (string clip in Vp9Corpus.Clips().Where(c => c != "p1-440"))
            {
                foreach (string extension in (string[])[".ivf", ".webm", ".mkv"])
                {
                    if (File.Exists(Vp9Corpus.PathOf(clip + extension)))
                    {
                        data.Add(clip, extension);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ClipFiles))]
    public void FramesEqualLibvpx(string clip, string extension)
    {
        string path = Vp9Corpus.PathOf(clip + extension);
        Vp9Reference reference = Vp9Corpus.ReadReference(Vp9Corpus.PathOf(clip + ".ivf"));
        using VideoReader reader = VideoReader.Open(path, new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Equal(CodecIds.Vp9, reader.Track.CodecId);
        Assert.Equal(reference.Md5, Md5s(reader));

        // The design's cross-check: FFmpeg's own VP9 decoder gave libvpx's frames too (tools/video/make-vp9-corpus.py).
        Assert.True(reference.FfmpegAgrees);
    }

    [Theory]
    [MemberData(nameof(ClipFiles))]
    public void PacketsMatchFfprobe(string clip, string extension)
    {
        string path = Vp9Corpus.PathOf(clip + extension);
        Assert.SkipUnless(File.Exists(path + ".json"), "No ffprobe reference for this file.");
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        Corpus.AssertMatchesProbe(Corpus.ReadProbe(path), Corpus.ReadAll(demuxer));
    }

    [Fact]
    public void TestVectorsThroughVideoReaderEqualLibvpx()
    {
        IReadOnlyList<string> vectors = Vp9Corpus.Vectors;
        Assert.SkipWhen(vectors.Count == 0, "The VP9 test vectors are not fetched (python tools/video/make-vp9-corpus.py --vectors).");
        foreach (string vector in vectors.Where(v => !v.Contains("yuv440", StringComparison.Ordinal)))
        {
            Vp9Reference reference = Vp9Corpus.ReadReference(vector);
            if (reference.Errors.Count > 0)
            {
                // Streams libvpx rejects in part (intra-only frames before a keyframe) are the core tests' business.
                continue;
            }

            using VideoReader reader = VideoReader.Open(vector, new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
            List<string> ours = Md5s(reader);
            Assert.True(reference.Md5.SequenceEqual(ours), $"{Path.GetFileName(vector)}: first difference at {Vp9Corpus.FirstDifference(reference.Md5, ours)}");
        }
    }

    [Fact]
    public void SizeChangesComeOutAtTheirCodedSizeWithRenderSize()
    {
        string[] vectors = [.. Vp9Corpus.Vectors.Where(v => Path.GetFileName(v) is "vp90-2-05-resize.ivf" or "vp90-2-18-resize.ivf"
            || Path.GetFileName(v).StartsWith("vp90-2-21-resize_inter_320x", StringComparison.Ordinal))];
        Assert.SkipWhen(vectors.Length == 0, "The VP9 test vectors are not fetched (python tools/video/make-vp9-corpus.py --vectors).");
        foreach (string vector in vectors)
        {
            // libvpx's shown size and the render size of the frame decoded last in each packet.
            List<(int, int, Size?)> expected = [];
            int renderWidth = 0, renderHeight = 0;
            using (StreamReader dump = new(new GZipStream(File.OpenRead(Vp9Corpus.ReferencePath(vector, ".dump.gz")), CompressionMode.Decompress)))
            {
                while (dump.ReadLine() is { } line)
                {
                    if (line.StartsWith("render_width ", StringComparison.Ordinal))
                    {
                        renderWidth = int.Parse(line[13..], CultureInfo.InvariantCulture);
                    }
                    else if (line.StartsWith("render_height ", StringComparison.Ordinal))
                    {
                        renderHeight = int.Parse(line[14..], CultureInfo.InvariantCulture);
                    }
                    else if (line.StartsWith("shown ", StringComparison.Ordinal))
                    {
                        string[] size = line[6..].Split('x');
                        expected.Add((int.Parse(size[0], CultureInfo.InvariantCulture), int.Parse(size[1], CultureInfo.InvariantCulture),
                            new Size(renderWidth, renderHeight)));
                    }
                }
            }

            using VideoReader reader = VideoReader.Open(vector);
            List<(int, int, Size?)> actual = [];
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    actual.Add((frame.Width, frame.Height, frame.RenderSize));
                }
            }

            Assert.True(expected.SequenceEqual(actual), $"{Path.GetFileName(vector)}: first difference at {expected.Zip(actual).TakeWhile(p => p.First == p.Second).Count()}");
            Assert.True(actual.Select(a => (a.Item1, a.Item2)).Distinct().Count() > 1, $"{Path.GetFileName(vector)} keeps one size");
        }
    }

    [Theory]
    [InlineData("p0-altref.ivf", 8, ChromaFormat.Yuv420)]
    [InlineData("p2-12bit.ivf", 12, ChromaFormat.Yuv420)]
    [InlineData("p3-10bit-422.ivf", 10, ChromaFormat.Yuv422)]
    [InlineData("p1-444.ivf", 8, ChromaFormat.Yuv444)]
    public void IvfTrackTakesBitDepthAndSubsamplingFromTheFirstKeyframe(string file, int bitDepth, ChromaFormat chroma)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Vp9Corpus.PathOf(file));
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        Assert.Equal(bitDepth, track.BitDepth);
        Assert.Equal(chroma, track.ChromaFormat);
    }

    [Fact]
    public void FramesComeOutAtTheirBitDepthAndColor()
    {
        using VideoReader reader = VideoReader.Open(Vp9Corpus.PathOf("p2-10bit.webm"));
        YuvVideoFrame frame = reader.ReadFrames().First();
        using (frame)
        {
            YuvRaster<ushort> raster = Assert.IsType<YuvRaster<ushort>>(frame.Raster);
            Assert.Equal(10, raster.BitDepth);
            Assert.Equal(ChromaFormat.Yuv420, raster.ChromaFormat);
        }

        using VideoReader rgb = VideoReader.Open(Vp9Corpus.PathOf("p1-rgb.webm"));
        using YuvVideoFrame first = rgb.ReadFrames().First();
        Assert.Equal(ChromaFormat.Yuv444, first.Raster.ChromaFormat);
        Assert.Equal(0, first.Color!.Value.MatrixCoefficients);
        Assert.True(first.Color!.Value.FullRange);
    }

    [Fact]
    public void HiddenFramesGiveNoFrame()
    {
        // p0-altref.ivf keeps its alternate references in superframes; split them into packets of their own, as some IVF
        // writers do, and each hidden frame's packet gives no frame.
        List<EncodedPacket> packets = SplitSuperframes(Vp9Corpus.PathOf("p0-altref.ivf"), out VideoTrackInfo track);
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(track);
        int frames = 0, hidden = 0;
        foreach (EncodedPacket packet in packets)
        {
            using (packet)
            {
                bool lone = Vp9Superframe.IsLoneHiddenFrame(packet.Data.Span);
                decoder.Send(packet);
                bool got = false;
                while (decoder.TryReceive(out YuvVideoFrame? frame))
                {
                    frame.Dispose();
                    got = true;
                    frames++;
                }

                Assert.Equal(!lone, got);
                hidden += lone ? 1 : 0;
            }
        }

        Assert.True(hidden > 0);
        Assert.Equal(Vp9Corpus.ReadReference(Vp9Corpus.PathOf("p0-altref.ivf")).Md5.Count, frames);
    }

    [Fact]
    public void RemuxToWebMMergesHiddenFramesIntoSuperframes()
    {
        string path = Vp9Corpus.PathOf("p0-altref.ivf");
        List<byte[]> original = Vp9Corpus.Packets(path);
        List<EncodedPacket> split = SplitSuperframes(path, out VideoTrackInfo track);
        Assert.True(split.Count > original.Count);
        MemoryStream webm = new();
        using (IMuxer muxer = MediaFile.CreateMuxer(webm, "webm", [track], leaveOpen: true))
        {
            foreach (EncodedPacket packet in split)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        webm.Position = 0;
        using IDemuxer reread = MediaFile.OpenDemuxer(webm);
        List<byte[]> merged = [];
        while (reread.ReadPacket() is { } packet)
        {
            using (packet)
            {
                merged.Add(packet.Data.ToArray());
            }
        }

        // libvpx's encoder wrote the original superframes; the merge gives the same bytes, so vpxdec and FFmpeg read it alike.
        Assert.Equal(original.Count, merged.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.True(original[i].AsSpan().SequenceEqual(merged[i]), $"packet {i}");
        }
    }

    [Theory]
    [InlineData("p0-altref.ivf", "webm")]
    [InlineData("p0-altref.ivf", "mkv")]
    [InlineData("p2-10bit.webm", "ivf")]
    [InlineData("p3-12bit-444.webm", "mkv")]
    public void RemuxKeepsEveryFrame(string file, string format)
    {
        string path = Vp9Corpus.PathOf(file);
        byte[] written = Containers.AviWriterTests.Remux(path, format);
        using VideoReader a = VideoReader.Open(path);
        using VideoReader b = VideoReader.Open(new MemoryStream(written));
        Assert.Equal(Md5s(a), Md5s(b));
    }

    [Fact]
    public void IvfReadWriteReadIsByteIdentical()
    {
        string path = Vp9Corpus.PathOf("p2-12bit.ivf");
        byte[] written = Containers.AviWriterTests.Remux(path, "ivf");
        Assert.Equal(File.ReadAllBytes(path), written);
    }

    [Fact]
    public void KeyframeFlagMismatchIsReportedAndTheFrameWins()
    {
        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Vp9Corpus.PathOf("p0-keyframes.webm"));
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(demuxer.Info.VideoTrack!, context: context);
        List<string> md5 = [];
        for (int index = 0; demuxer.ReadPacket() is { } packet; index++)
        {
            using (packet)
            {
                if (index is 3 or 5)
                {
                    // Packet 3 is an inter frame marked key, packet 5 a keyframe marked inter.
                    packet.IsKeyframe = !packet.IsKeyframe;
                }

                decoder.Send(packet);
            }

            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    md5.Add(Md5(frame.Raster));
                }
            }
        }

        Assert.Equal(Vp9Corpus.ReadReference(Vp9Corpus.PathOf("p0-keyframes.ivf")).Md5, md5);
        Assert.Equal([3, 5], reported.Where(d => d.Id == "Vp9.KeyframeFlagMismatch").Select(d => d.Frame));
    }

    public static TheoryData<string> AlphaClips => [.. Vp9Corpus.Clips(alpha: true)];

    [Theory]
    [MemberData(nameof(AlphaClips))]
    public void AlphaPlanesEqualLibvpx(string clip)
    {
        string path = Vp9Corpus.PathOf(clip + ".webm");
        List<string[]> reference = ReadAlphaReference(path);
        using VideoReader reader = VideoReader.Open(path, new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.True(reader.Track.HasAlpha);
        List<string[]> ours = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                YuvRaster<byte> raster = Assert.IsType<YuvRaster<byte>>(frame.Raster);
                ours.Add([Vp8Tests.PlaneMd5(raster.Y), Vp8Tests.PlaneMd5(raster.U!), Vp8Tests.PlaneMd5(raster.V!), Vp8Tests.PlaneMd5(raster.A!)]);
            }
        }

        Assert.Equal(reference.Count, ours.Count);
        for (int i = 0; i < ours.Count; i++)
        {
            Assert.True(reference[i].SequenceEqual(ours[i]), $"frame {i}: y u v a {string.Join(' ', ours[i])}, libvpx {string.Join(' ', reference[i])}");
        }
    }

    [Fact]
    public void AlphaCanBeSkipped()
    {
        string path = Vp9Corpus.PathOf("alpha-ramp.webm");
        VideoReaderOptions options = new() { Decoder = new VideoDecoderOptions { DecodeAlpha = false } };
        using VideoReader reader = VideoReader.Open(path, options);
        List<string[]> reference = ReadAlphaReference(path);
        int count = 0;
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                Assert.False(frame.Raster.HasAlpha);
                Assert.Equal(reference[count][0], Vp8Tests.PlaneMd5(((YuvRaster<byte>)frame.Raster).Y));
                count++;
            }
        }

        Assert.Equal(reference.Count, count);
    }

    [Theory]
    [InlineData("alpha-ramp", "webm")]
    [InlineData("alpha-odd-75x45", "mkv")]
    public void AlphaRemuxKeepsTheAdditions(string clip, string format)
    {
        string path = Vp9Corpus.PathOf(clip + ".webm");
        byte[] written = Containers.AviWriterTests.Remux(path, format);
        using IDemuxer reread = MediaFile.OpenDemuxer(new MemoryStream(written));
        Assert.True(reread.Info.VideoTrack!.HasAlpha);
        using VideoReader a = VideoReader.Open(path);
        using VideoReader b = VideoReader.Open(new MemoryStream(written));
        Assert.Equal(AlphaMd5s(a), AlphaMd5s(b));
    }

    [Theory]
    [InlineData("p0-altref.webm", 9)]
    [InlineData("p0-resilient.webm", 9)]
    [InlineData("alpha-ramp.webm", 18)]
    public void FrameBuffersStayWithinNinePerStreamAndMemoryIsSteady(string clip, int most)
    {
        PooledPixelMemorySource memory = new();
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Memory = memory } };
        long[] peaks = new long[3];
        for (int pass = 0; pass < 3; pass++)
        {
            // The same clip runs three times, so a leak per frame or per keyframe shows as growth from pass to pass.
            using IDemuxer demuxer = MediaFile.OpenDemuxer(Vp9Corpus.PathOf(clip), context);
            using IVideoDecoder decoder = VideoCodecs.CreateDecoder(demuxer.Info.VideoTrack!, context: context);
            Vp9VideoDecoder vp9 = Assert.IsType<Vp9VideoDecoder>(decoder);
            for (int index = 0; demuxer.ReadPacket() is { } packet; index++)
            {
                using (packet)
                {
                    decoder.Send(packet);
                }

                while (decoder.TryReceive(out YuvVideoFrame? frame))
                {
                    frame.Dispose();
                }

                Assert.True(vp9.FrameBuffers <= most, $"{vp9.FrameBuffers} frame buffers at packet {index}");
                peaks[pass] = Math.Max(peaks[pass], memory.Usage.LiveBytes);
            }
        }

        Assert.Equal(peaks[0], peaks[2]);
        Assert.Equal(0, memory.Usage.LiveBytes);
    }

    [Fact]
    public void Hdr10CarriesColorAndStaticMetadata()
    {
        using VideoReader reader = VideoReader.Open(Vp9Corpus.PathOf("hdr10.mkv"));
        using YuvVideoFrame frame = reader.ReadFrames().First();
        Assert.Equal(new ColorCodePoints(9, 16, 9, false), frame.Color);
        Assert.Equal(frame.Color, frame.Raster.Metadata.CodePoints);
        HdrMetadata hdr = Assert.IsType<HdrMetadata>(frame.Hdr);
        Assert.Same(hdr, frame.Raster.Metadata.Hdr);
        Assert.Null(frame.Hdr10Plus);

        // ffprobe's mastering display and light levels for the same file.
        using JsonDocument side = JsonDocument.Parse(File.ReadAllText(Vp9Corpus.PathOf("hdr10.side_data.json")));
        JsonElement mastering = side.RootElement[0], light = side.RootElement[1];
        Assert.Equal(Rational(mastering, "max_luminance"), hdr.MasteringMaxLuminance!.Value, 3);
        Assert.Equal(Rational(mastering, "min_luminance"), hdr.MasteringMinLuminance!.Value, 4);
        ColorPrimaries primaries = hdr.MasteringPrimaries!;
        Assert.Equal(Rational(mastering, "red_x"), primaries.Red.X, 4);
        Assert.Equal(Rational(mastering, "green_y"), primaries.Green.Y, 4);
        Assert.Equal(Rational(mastering, "blue_y"), primaries.Blue.Y, 4);
        Assert.Equal(Rational(mastering, "white_point_x"), primaries.White.X, 4);
        Assert.Equal(light.GetProperty("max_content").GetInt32(), hdr.MaxContentLightLevel);
        Assert.Equal(light.GetProperty("max_average").GetInt32(), hdr.MaxFrameAverageLightLevel);
    }

    [Fact]
    public void Hdr10ConvertsToSdr()
    {
        using VideoReader reader = VideoReader.Open(Vp9Corpus.PathOf("hdr10.mkv"));
        using YuvVideoFrame frame = reader.ReadFrames().First();
        using Raster<Rgba8> coded = frame.ToRaster<Rgba8>();
        using Raster<Rgba8> sdr = frame.ToRaster<Rgba8>(ColorProfile.Srgb);
        Assert.Equal(ColorProfile.Srgb, sdr.Metadata.GetColorProfile());
        Assert.Equal((coded.Width, coded.Height), (sdr.Width, sdr.Height));

        // The same as tagging the RGB planes Rec. 2100 PQ with the static metadata and converting them through Egami.
        using Raster<Rgba16> rgb = frame.ToRaster<Rgba16>();
        rgb.Metadata.CodePoints = ColorCodePoints.Rec2100Pq;
        rgb.Metadata.Hdr = frame.Hdr;
        using Raster<Rgba8> expected = rgb.ConvertColor<Rgba8>(ColorProfile.Srgb);
        for (int y = 0; y < sdr.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(sdr.Row(y)), $"row {y}");
        }

        Assert.False(coded.Row(sdr.Height / 2).SequenceEqual(sdr.Row(sdr.Height / 2)));
        Assert.Equal(new ColorCodePoints(9, 16, 9, false), frame.Raster.Metadata.CodePoints);
    }

    [Fact]
    public void Hdr10PlusMatchesFfprobe()
    {
        using JsonDocument side = JsonDocument.Parse(File.ReadAllText(Vp9Corpus.PathOf("hdr10plus.side_data.json")));
        using VideoReader reader = VideoReader.Open(Vp9Corpus.PathOf("hdr10plus.webm"));
        int index = 0;
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                JsonElement expected = side.RootElement[index++].EnumerateArray()
                    .First(e => e.GetProperty("side_data_type").GetString()!.Contains("2094-40", StringComparison.Ordinal));
                Hdr10PlusMetadata meta = Assert.IsType<Hdr10PlusMetadata>(frame.Hdr10Plus);
                Assert.Equal(expected.GetProperty("application version").GetInt32(), meta.ApplicationVersion);
                Assert.Equal(Rational(expected, "targeted_system_display_maximum_luminance"), meta.TargetDisplayMaxLuminance, 6);

                // FFmpeg keeps light levels as a share of 10000 cd/m², the knee and anchors as shares of 1.
                JsonElement maxScl = expected.GetProperty("maxscl");
                for (int i = 0; i < 3; i++)
                {
                    Assert.Equal(Rational(maxScl[i]) * 10000, meta.MaxScl[i], 6);
                }

                Assert.Equal(Rational(expected, "average_maxrgb") * 10000, meta.AverageMaxRgb, 6);
                Assert.Equal(Rational(expected, "fraction_bright_pixels"), meta.FractionBrightPixels, 6);
                JsonElement percentages = expected.GetProperty("distribution_maxrgb_percentage");
                JsonElement percentiles = expected.GetProperty("distribution_maxrgb_percentile");
                Assert.Equal(percentages.GetArrayLength(), meta.DistributionMaxRgb.Count);
                for (int i = 0; i < meta.DistributionMaxRgb.Count; i++)
                {
                    Assert.Equal(percentages[i].GetInt32(), meta.DistributionMaxRgb[i].Percentage);
                    Assert.Equal(Rational(percentiles[i]) * 10000, meta.DistributionMaxRgb[i].Value, 6);
                }

                Hdr10PlusToneCurve curve = meta.ToneCurve!;
                Assert.Equal(Rational(expected, "knee_point_x"), curve.KneeX, 6);
                Assert.Equal(Rational(expected, "knee_point_y"), curve.KneeY, 6);
                JsonElement anchors = expected.GetProperty("bezier_curve_anchors");
                Assert.Equal(anchors.GetArrayLength(), curve.Anchors.Count);
                for (int i = 0; i < curve.Anchors.Count; i++)
                {
                    Assert.Equal(Rational(anchors[i]), curve.Anchors[i], 6);
                }
            }
        }

        Assert.Equal(side.RootElement.GetArrayLength(), index);
    }

    [Fact]
    public void Hdr10PlusSurvivesRemux()
    {
        string path = Vp9Corpus.PathOf("hdr10plus.webm");
        byte[] written = Containers.AviWriterTests.Remux(path, "webm");
        using VideoReader a = VideoReader.Open(path);
        using VideoReader b = VideoReader.Open(new MemoryStream(written));
        Assert.Equal(Hdr10PlusPayloads(a), Hdr10PlusPayloads(b));
    }

    private static List<byte[]> Hdr10PlusPayloads(VideoReader reader)
    {
        List<byte[]> payloads = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                payloads.Add(frame.Hdr10Plus!.Raw.ToArray());
            }
        }

        return payloads;
    }

    private static double Rational(JsonElement element, string name) => Rational(element.GetProperty(name));

    private static double Rational(JsonElement value)
    {
        string text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString()!;
        string[] parts = text.Split('/');
        double numerator = double.Parse(parts[0], CultureInfo.InvariantCulture);
        return parts.Length == 1 ? numerator : numerator / double.Parse(parts[1], CultureInfo.InvariantCulture);
    }

    private static List<string> AlphaMd5s(VideoReader reader)
    {
        List<string> md5 = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                YuvRaster<byte> raster = (YuvRaster<byte>)frame.Raster;
                md5.Add(Md5(raster) + Vp8Tests.PlaneMd5(raster.A!));
            }
        }

        return md5;
    }

    private static List<string[]> ReadAlphaReference(string path)
    {
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Vp9Corpus.ReferencePath(path, ".frames.json")));
        return [.. json.RootElement.GetProperty("md5").EnumerateArray().Select(f => ((string[])["y", "u", "v", "a"]).Select(p => f.GetProperty(p).GetString()!).ToArray())];
    }

    internal static List<string> Md5s(VideoReader reader)
    {
        List<string> md5 = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                md5.Add(Md5(frame.Raster));
            }
        }

        return md5;
    }

    /// <summary>The MD5 of a frame as libvpx's tests hash it: packed planes, 16-bit little-endian samples above 8 bits.</summary>
    internal static string Md5(YuvRaster raster)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        switch (raster)
        {
            case YuvRaster<byte> r8:
                foreach (YuvPlane<byte> plane in (YuvPlane<byte>[])[r8.Y, r8.U!, r8.V!])
                {
                    for (int y = 0; y < plane.Height; y++)
                    {
                        md5.AppendData(plane.Row(y));
                    }
                }

                break;
            case YuvRaster<ushort> r16:
                foreach (YuvPlane<ushort> plane in (YuvPlane<ushort>[])[r16.Y, r16.U!, r16.V!])
                {
                    for (int y = 0; y < plane.Height; y++)
                    {
                        md5.AppendData(MemoryMarshal.AsBytes(plane.Row(y)));
                    }
                }

                break;
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>The packets of a file with every superframe split into one packet per frame, at the superframe's time.</summary>
    internal static List<EncodedPacket> SplitSuperframes(string path, out VideoTrackInfo track)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        track = demuxer.Info.VideoTrack!;
        List<EncodedPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                byte[] data = packet.Data.ToArray();
                foreach (ReadOnlyMemory<byte> frame in Vp9Superframe.Split(data))
                {
                    packets.Add(new EncodedPacket(packet.TrackNumber, frame.ToArray(), packet.Timestamp)
                    {
                        IsKeyframe = FrameKind.IsKeyframe(CodecIds.Vp9, frame.Span) ?? false,
                        Duration = packet.Duration,
                    });
                }
            }
        }

        return packets;
    }
}
