using Egami.Formats;
using Egami.Pixels;
using Egami.Tests.Memory;

namespace Egami.Tests.Streaming;

/// <summary>Phase 2.2 task 6: BMP, TGA, PNM and QOI give Load's pixels on seekable and non-seekable streams, within WorkingBytes.</summary>
public class SimpleStreamingTests
{
    private static readonly string[] Folders = ["bmp/", "tga/", "pnm/", "qoi/"];

    public static TheoryData<string> Files() => [.. StreamingCorpus.Files.Where(f => Folders.Any(f.StartsWith))];

    [Theory]
    [MemberData(nameof(Files))]
    public void NonSeekableStreamsGiveLoadsPixels(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(new NonSeekableStream(data));
        foreach (Rect? region in (Rect?[])[null, new Rect(1, expected.Height / 3, Math.Max(1, expected.Width - 2), Math.Max(1, expected.Height / 2))])
        {
            ReaderOptions options = new() { Region = region };
            using RasterDecoder decoder = RasterDecoder.Open(new NonSeekableStream(data), options: options);
            using Raster<Rgba8>? frame = decoder.ReadFrame<Rgba8>();
            Assert.NotNull(frame);
            using Raster<Rgba8> cropped = region is Rect r ? expected.Derive(ops => ops.Crop(r), TestContext.Current.CancellationToken) : expected.Copy();
            StreamingCorpus.Equal(cropped, 0, frame, $"{file} {region}");
            Assert.True(decoder.IsMetadataComplete);
            if (region is null)
            {
                Assert.Equal(expected.Metadata.Warnings, decoder.Metadata.Warnings);
                Assert.Equal(expected.Metadata.Text, decoder.Metadata.Text);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void HeldBytesStayWithinWorkingBytes(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        long working = 0;
        long band = 0;
        RasterAccess access = RasterAccess.Buffered;
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using RasterDecoder decoder = RasterDecoder.Open(data, context);
            access = decoder.Info.Access;
            working = decoder.Info.WorkingBytes;
            using Raster<Rgba8> rows = Raster.Create<Rgba8>(decoder.Info.Width, 4, context);
            band = (long)rows.Width * 4 * 4;
            while (decoder.ReadRows(rows) > 0)
            {
            }
        });
        Assert.Equal(0, peak.LeftBytes);
        Assert.InRange(peak.PeakPixelBytes - band, 0, working);
    }

    [Fact]
    public void MostSimpleFilesStream()
    {
        int streamed = 0;
        int total = 0;
        foreach (string file in StreamingCorpus.Files.Where(f => Folders.Any(f.StartsWith)))
        {
            total++;
            streamed += Raster.Probe(StreamingCorpus.Read(file)).Access == RasterAccess.Streamed ? 1 : 0;
        }

        Assert.True(streamed * 2 > total, $"{streamed} of {total} stream");
    }

    /// <summary>A stream that cannot seek, like a network download.</summary>
    internal sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
