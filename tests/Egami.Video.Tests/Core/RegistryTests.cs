using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Core;

/// <summary>A made-up container and codec pass through the registry, proving plug-ins need only the public interfaces.</summary>
public class RegistryTests
{
    private static readonly Rational Base = new(1, 25);

    private static MediaContext Context => MediaContext.Default.WithContainer(new FakeFormat()).WithCodec(new FakeCodec());

    private static byte[] FakeFile(params byte[] grays)
    {
        // "FAKE", then per packet: track byte, length (little-endian int), bytes.
        using MemoryStream file = new();
        file.Write("FAKE"u8);
        Span<byte> length = stackalloc byte[4];
        foreach (byte gray in grays)
        {
            file.WriteByte(1);
            BinaryPrimitives.WriteInt32LittleEndian(length, 1);
            file.Write(length);
            file.WriteByte(gray);
        }

        return file.ToArray();
    }

    [Fact]
    public void ProbeDetectsTheRegisteredContainer()
    {
        MediaInfo info = MediaFile.Probe(new MemoryStream(FakeFile(1, 2)), Context);
        Assert.Equal("Fake", info.FormatName);
        Assert.Equal("V_FAKE", Assert.Single(info.Tracks).CodecId);
        Assert.NotNull(info.VideoTrack);
    }

    [Fact]
    public void UnknownDataIsRejected() =>
        Assert.Throws<UnsupportedFormatException>(() => MediaFile.Probe(new MemoryStream("NOPE"u8.ToArray()), Context));

    [Fact]
    public void NonSeekableStreamsReplayTheDetectionBytes()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new NonSeekableStream(FakeFile(10, 20, 30)), Context);
        Assert.False(demuxer.CanSeek);
        List<byte> grays = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                grays.Add(packet.Data.Span[0]);
            }
        }

        Assert.Equal([10, 20, 30], grays);
    }

    [Fact]
    public void PacketsDecodeThroughTheRegisteredCodec()
    {
        MediaContext context = Context;
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(FakeFile(50, 100)), context);
        VideoTrackInfo track = (VideoTrackInfo)demuxer.Tracks[0];
        using IVideoDecoder<Rgb8> decoder = VideoCodecs.CreateDecoder<Rgb8>(track, context: context);
        List<(long Ticks, byte Red)> frames = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                decoder.Send(packet);
            }

            while (decoder.TryReceive(out VideoFrame<Rgb8>? frame))
            {
                using (frame)
                {
                    frames.Add((frame.Timestamp.Ticks, frame.Raster.GetPixel(1, 1).R));
                }
            }
        }

        Assert.Equal([(0L, (byte)50), (1L, (byte)100)], frames);
    }

    [Fact]
    public void MissingCodecsAreReported()
    {
        VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = "V_NONE", TimeBase = Base, Width = 1, Height = 1 };
        Assert.Throws<UnsupportedFormatException>(() => VideoCodecs.CreateDecoder(track, context: Context));
    }

    [Fact]
    public void WithContainerReplacesByName()
    {
        MediaContext context = Context.WithContainer(new FakeFormat());
        Assert.Single(context.Containers, f => f.Name == "Fake");
        Assert.Same(context.Containers[0], context.FindContainer(".fake"));
    }

    private sealed class FakeFormat : ContainerFormat
    {
        public override string Name => "Fake";

        public override IReadOnlyList<string> Extensions => [".fake"];

        public override int SignatureLength => 4;

        public override bool IsMatch(ReadOnlySpan<byte> header) => header.SequenceEqual("FAKE"u8);

        public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
        {
            stream.ReadExactly(new byte[4]);
            return new FakeDemuxer(stream);
        }
    }

    private sealed class FakeDemuxer(Stream stream) : IDemuxer
    {
        private long _next;

        public IReadOnlyList<TrackInfo> Tracks { get; } =
            [new VideoTrackInfo { Number = 1, Kind = TrackKind.Video, CodecId = "V_FAKE", TimeBase = Base, Width = 4, Height = 4 }];

        public MediaInfo Info => new() { FormatName = "Fake", Tracks = Tracks };

        public bool CanSeek => false;

        public EncodedPacket? ReadPacket()
        {
            int track = stream.ReadByte();
            if (track < 0)
            {
                return null;
            }

            byte[] length = new byte[4];
            stream.ReadExactly(length);
            EncodedPacket packet = EncodedPacket.Rent(track, BinaryPrimitives.ReadInt32LittleEndian(length), new MediaTime(_next++, Base));
            stream.ReadExactly(packet.GetWritableData().Span);
            packet.IsKeyframe = true;
            return packet;
        }

        public void Seek(MediaTime time, int? trackNumber = null) => throw new NotSupportedException();

        public void Dispose() => stream.Dispose();
    }

    private sealed class FakeCodec : VideoCodec
    {
        public override string Name => "Fake";

        public override string CodecId => "V_FAKE";

        public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context) =>
            new FakeDecoder(track, context);
    }

    private sealed class FakeDecoder(VideoTrackInfo track, MediaContext context) : IVideoDecoder
    {
        private readonly Queue<YuvVideoFrame> _ready = new();

        public void Send(EncodedPacket packet)
        {
            YuvRaster<byte> planes = YuvRaster.Create(track.Width, track.Height, ChromaFormat.Yuv400, new YuvColor(YuvMatrix.Bt601, YuvRange.Full, ChromaSiting.Center), context: context.Egami);
            for (int y = 0; y < planes.Height; y++)
            {
                planes.Y.Row(y).Fill(packet.Data.Span[0]);
            }

            _ready.Enqueue(new YuvVideoFrame(planes, packet.Timestamp));
        }

        public void Flush()
        {
        }

        public bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame) => _ready.TryDequeue(out frame);

        public void Reset()
        {
            while (_ready.TryDequeue(out YuvVideoFrame? frame))
            {
                frame.Dispose();
            }
        }

        public void Dispose() => Reset();
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
