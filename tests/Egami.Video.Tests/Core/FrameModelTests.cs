using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Containers;
using Egami.Video.Tests.Containers;

namespace Egami.Video.Tests.Core;

/// <summary>Task 15: pixel formats, color, HDR and variable frame rate carried through the frame and track model.</summary>
public class FrameModelTests
{
    private static readonly YuvColor Bt709Limited = new(YuvMatrix.Bt709, YuvRange.Limited, ChromaSiting.Left);

    public static TheoryData<ChromaFormat, int, bool> PlanarFormats
    {
        get
        {
            TheoryData<ChromaFormat, int, bool> data = [];
            foreach (ChromaFormat chroma in (ChromaFormat[])[ChromaFormat.Yuv420, ChromaFormat.Yuv422, ChromaFormat.Yuv444])
            {
                foreach (int depth in (int[])[8, 10, 12])
                {
                    data.Add(chroma, depth, false);
                    data.Add(chroma, depth, true);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(PlanarFormats))]
    public void PlanarFormatsRoundTripThroughFrames(ChromaFormat chroma, int depth, bool alpha)
    {
        using YuvRaster planes = depth == 8
            ? YuvRaster.Create(34, 18, chroma, Bt709Limited, alpha)
            : YuvRaster.Create(34, 18, chroma, depth, Bt709Limited, alpha);
        Fill(planes, depth);
        using YuvVideoFrame frame = new(Copy(planes, depth), MediaTime.Zero, color: new ColorCodePoints(1, 1, 1, false));
        Assert.Equal((chroma, depth, alpha), (frame.Raster.ChromaFormat, frame.Raster.BitDepth, frame.Raster.HasAlpha));
        AssertSame(planes, frame.Raster);

        // Through RGB at 16 bits and back stays within a level for 8-bit and a few for deeper planes.
        using Raster<Rgba16> rgb = frame.ToRaster<Rgba16>();
        Assert.Equal((34, 18), (rgb.Width, rgb.Height));
    }

    [Theory]
    [InlineData(YuvPackedFormat.Nv12)]
    [InlineData(YuvPackedFormat.Nv21)]
    [InlineData(YuvPackedFormat.P010)]
    [InlineData(YuvPackedFormat.P016)]
    [InlineData(YuvPackedFormat.Yuyv)]
    [InlineData(YuvPackedFormat.Uyvy)]
    public void PackedFormatsRoundTripThroughFrames(YuvPackedFormat format)
    {
        const int Width = 32, Height = 16;
        (int bytes, int depth) = format switch
        {
            YuvPackedFormat.Nv12 or YuvPackedFormat.Nv21 => (Width * Height * 3 / 2, 8),
            YuvPackedFormat.P010 => (Width * Height * 3, 10),
            YuvPackedFormat.P016 => (Width * Height * 3, 12),
            _ => (Width * Height * 2, 8),
        };
        byte[] input = new byte[bytes];
        Random random = new(7);
        random.NextBytes(input);
        if (depth > 8)
        {
            // Samples sit in the high bits of little-endian words: clear the bits below the depth.
            for (int i = 0; i < input.Length; i += 2)
            {
                ushort word = (ushort)(input[i] | (input[i + 1] << 8));
                word &= (ushort)(0xFFFF << (16 - depth));
                input[i] = (byte)word;
                input[i + 1] = (byte)(word >> 8);
            }
        }

        using YuvVideoFrame frame = YuvVideoFrame.FromPacked(format, input, Width, Height, Bt709Limited, MediaTime.Zero, bitDepth: depth);
        Assert.Equal(depth, frame.Raster.BitDepth);
        byte[] output = new byte[bytes];
        frame.CopyToPacked(format, output);
        Assert.Equal(input, output);
    }

    [Fact]
    public void MatroskaCarriesBitDepthChromaSitingRangeAlphaAndHdr()
    {
        HdrMetadata hdr = new()
        {
            MasteringPrimaries = new ColorPrimaries(new Chromaticity(0.708, 0.292), new Chromaticity(0.17, 0.797), new Chromaticity(0.131, 0.046), new Chromaticity(0.3127, 0.329)),
            MasteringMaxLuminance = 1000,
            MasteringMinLuminance = 0.0001,
            MaxContentLightLevel = 900,
            MaxFrameAverageLightLevel = 300,
        };
        VideoTrackInfo track = new()
        {
            Number = 1,
            Kind = TrackKind.Video,
            CodecId = CodecIds.Vp9,
            TimeBase = new Rational(1, 1000),
            Width = 64,
            Height = 32,
            Color = new ColorCodePoints(9, 16, 9, false),
            BitDepth = 10,
            ChromaFormat = ChromaFormat.Yuv422,
            ChromaSiting = ChromaSiting.TopLeft,
            HasAlpha = true,
            Hdr = hdr,
        };
        MemoryStream file = new();
        using (IMuxer muxer = MediaFile.CreateMuxer(file, "webm", [track], leaveOpen: true))
        {
            using EncodedPacket packet = new(1, new byte[] { 1, 2, 3 }, MediaTime.Zero) { IsKeyframe = true };
            muxer.WritePacket(packet);
        }

        file.Position = 0;
        using IDemuxer demuxer = MediaFile.OpenDemuxer(file);
        VideoTrackInfo read = (VideoTrackInfo)demuxer.Tracks[0];
        Assert.Equal(track.Color, read.Color);
        Assert.Equal((10, ChromaFormat.Yuv422, ChromaSiting.TopLeft, true), (read.BitDepth, read.ChromaFormat, read.ChromaSiting, read.HasAlpha));
        Assert.Equal(hdr, read.Hdr);
    }

    [Fact]
    public void LimitedRangeFromTheContainerReachesTheFramePlanes()
    {
        byte[] jpeg = Codecs.MotionJpegDecoderTests.Packets("avi-mjpeg-420-pcm.avi").First();
        VideoTrackInfo track = new()
        {
            Number = 1,
            Kind = TrackKind.Video,
            CodecId = CodecIds.MotionJpeg,
            TimeBase = new Rational(1, 25),
            Width = 160,
            Height = 120,
            Color = new ColorCodePoints(1, 1, 1, false),
            ChromaSiting = ChromaSiting.Center,
        };
        using Video.Codecs.IVideoDecoder decoder = Video.Codecs.VideoCodecs.CreateDecoder(track);
        using EncodedPacket packet = new(1, jpeg, MediaTime.Zero);
        decoder.Send(packet);
        decoder.Flush();
        Assert.True(decoder.TryReceive(out YuvVideoFrame? frame));
        using (frame)
        {
            Assert.Equal(new YuvColor(YuvMatrix.Bt709, YuvRange.Limited, ChromaSiting.Center), frame.Raster.Color);
            using YuvRaster jfif = YuvRaster.Load(jpeg);
            Codecs.MotionJpegDecoderTests.AssertSamePlanes(jfif, frame.Raster);
        }
    }

    [Fact]
    public void VariableFrameRateMatroskaRemuxKeepsEveryTimestamp()
    {
        long[] times = [0, 33, 50, 120, 121, 400, 1000, 1017];
        VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 1000), Width = 16, Height = 16 };
        string first = AviWriterTests.TempFile("vfr.mkv");
        using (IMuxer muxer = MediaFile.CreateMuxer(first, [track]))
        {
            foreach (long t in times)
            {
                using EncodedPacket packet = new(1, new byte[] { 0xFF, 0xD8, (byte)t, 0xFF, 0xD9 }, new MediaTime(t, new Rational(1, 1000))) { IsKeyframe = true };
                muxer.WritePacket(packet);
            }
        }

        string second = AviWriterTests.TempFile("vfr-remux.mkv");
        MediaFile.Remux(first, second, cancellationToken: TestContext.Current.CancellationToken);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(second);
        Assert.Equal(times, Corpus.ReadAll(demuxer).Select(p => p.Timestamp.Rescale(new Rational(1, 1000)).Ticks));
    }

    [Fact]
    public void AviWarnsOnceWhenItRoundsVariableFrameRate()
    {
        List<MediaWarning> warnings = [];
        MediaContext context = MediaContext.Default with { Warnings = warnings.Add };
        VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 1000), Width = 16, Height = 16, FrameRate = new Rational(25, 1) };
        using (IMuxer muxer = MediaFile.CreateMuxer(new MemoryStream(), "avi", [track], context))
        {
            foreach (long t in (long[])[0, 40, 95, 150, 170])
            {
                using EncodedPacket packet = new(1, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, new MediaTime(t, new Rational(1, 1000))) { IsKeyframe = true };
                muxer.WritePacket(packet);
            }
        }

        MediaWarning warning = Assert.Single(warnings);
        Assert.Equal(("avi-vfr", 1), (warning.Code, warning.TrackNumber));
    }

    private static void Fill(YuvRaster planes, int depth)
    {
        int max = (1 << depth) - 1;
        if (planes is YuvRaster<byte> b)
        {
            foreach (YuvPlane<byte>? plane in (YuvPlane<byte>?[])[b.Y, b.U, b.V, b.A])
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    Span<byte> row = plane.Row(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = (byte)(((x * 7) + (y * 13)) & max);
                    }
                }
            }
        }
        else if (planes is YuvRaster<ushort> w)
        {
            foreach (YuvPlane<ushort>? plane in (YuvPlane<ushort>?[])[w.Y, w.U, w.V, w.A])
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    Span<ushort> row = plane.Row(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = (ushort)(((x * 97) + (y * 131)) & max);
                    }
                }
            }
        }
    }

    private static YuvRaster Copy(YuvRaster source, int depth)
    {
        YuvRaster copy = depth == 8
            ? YuvRaster.Create(source.Width, source.Height, source.ChromaFormat, source.Color, source.HasAlpha)
            : YuvRaster.Create(source.Width, source.Height, source.ChromaFormat, depth, source.Color, source.HasAlpha);
        Fill(copy, depth);
        return copy;
    }

    private static void AssertSame(YuvRaster expected, YuvRaster actual)
    {
        Assert.Equal((expected.Width, expected.Height, expected.Color), (actual.Width, actual.Height, actual.Color));
        if (expected is YuvRaster<ushort> e && actual is YuvRaster<ushort> a)
        {
            for (int y = 0; y < e.Y.Height; y++)
            {
                Assert.True(e.Y.Row(y).SequenceEqual(a.Y.Row(y)));
            }
        }
    }
}
