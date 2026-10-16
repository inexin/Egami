using Egami.Formats;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Streaming;

/// <summary>Phase 2.2 task 9: asynchronous loads decode as the stream arrives, to Load's pixels, without holding the file.</summary>
public abstract class AsyncTests<TPart>
    where TPart : IPart
{
    private const int PartCount = 2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Files() => Parts.Take(StreamingCorpus.Asked, TPart.Index, PartCount, StreamingCorpus.NotAsked);

    [Theory]
    [MemberData(nameof(Files))]
    public async Task LoadAsyncEqualsLoad(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        using Raster<Rgba8> loaded = await Raster.LoadAsync<Rgba8>(new AsyncTests.TrickleStream(data, 333), cancellationToken: Ct);
        Assert.Equal(expected.FrameCount, loaded.FrameCount);
        for (int f = 0; f < expected.FrameCount; f++)
        {
            using Raster<Rgba8> frame = loaded.CopyFrame(f);
            StreamingCorpus.Equal(expected, f, frame, file);
        }

        Assert.Equal(expected.Metadata.Warnings, loaded.Metadata.Warnings);
        Assert.Equal(expected.Metadata.Text, loaded.Metadata.Text);
        Assert.Equal(expected.Metadata.FormatName, loaded.Metadata.FormatName);

        using Raster natural = Raster.Load(data);
        using Raster naturalAsync = await Raster.LoadAsync(new AsyncTests.TrickleStream(data, 4096), cancellationToken: Ct);
        Assert.Equal(natural.Layout, naturalAsync.Layout);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task ASeekableStreamEndsWhereLoadLeavesIt(string file)
    {
        byte[] data = [.. StreamingCorpus.Read(file), 1, 2, 3, 4, 5, 6, 7, 8];
        using MemoryStream sync = new(data);
        using (Raster.Load(sync))
        {
        }

        using MemoryStream async = new(data);
        using (await Raster.LoadAsync(async, cancellationToken: Ct))
        {
        }

        Assert.Equal(sync.Position, async.Position);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task OpenAsyncReadsRowsAndFrames(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        using RasterDecoder decoder = await RasterDecoder.OpenAsync(new AsyncTests.TrickleStream(data, 101), cancellationToken: Ct);
        using Raster<Rgba8> band = Raster.Create<Rgba8>(decoder.Info.Width, 5);
        for (int f = 0; f < expected.FrameCount; f++)
        {
            int y = 0;
            int rows;
            while ((rows = await decoder.ReadRowsAsync(band, Ct)) > 0)
            {
                for (int i = 0; i < rows; i++)
                {
                    Assert.True(expected.Frames[f].Row(y + i).SequenceEqual(band.Row(i)), $"{file} frame {f} row {y + i}");
                }

                y += rows;
            }

            Assert.Equal(expected.Height, y);
            Assert.Equal(f < expected.FrameCount - 1, await decoder.NextFrameAsync(Ct));
        }

        Assert.Equal(DecodeStatus.Completed, decoder.Status);
    }
}

/// <summary>The tests of <see cref="AsyncTests{TPart}"/> that run once rather than in parts.</summary>
public class AsyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALoadAsyncHoldsChunksNotTheFile()
    {
        using Raster<Rgb8> image = Raster.Create(2000, 1500, new Rgb8(10, 200, 30));
        for (int y = 0; y < image.Height; y++)
        {
            image.Row(y)[y % 2000] = new Rgb8(255, 0, 0);
        }

        using MemoryStream png = new();
        image.Save(png, Egami.Formats.Png.PngFormat.Info, new Egami.Formats.Png.PngWriterOptions { CompressionLevel = 0 });
        byte[] data = png.ToArray();
        Assert.True(data.Length > 5_000_000);
        using IncrementalLoad load = new(new MemoryStream(data), EgamiContext.Default, ReaderOptions.Default, Ct);
        using Raster<Rgb8> loaded = await load.ReadAsync<Rgb8>();
        Assert.InRange(load.Decoder.MostBytesHeld, 1, 256 * 1024);
        Assert.True(image.Row(777).SequenceEqual(loaded.Row(777)));
    }

    [Fact]
    public async Task ReadFrameAsyncGivesEveryFrame()
    {
        string file = StreamingCorpus.Files.First(f => f.StartsWith("gif/", StringComparison.Ordinal) && Raster.Probe(StreamingCorpus.Read(f)).FrameCount > 2);
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        using RasterDecoder decoder = await RasterDecoder.OpenAsync(new TrickleStream(data, 64), cancellationToken: Ct);
        using Raster<Rgba8> canvas = Raster.Create<Rgba8>(decoder.Info.Width, decoder.Info.Height);
        int frames = 0;
        while (await decoder.ReadFrameAsync(canvas, Ct) is not null)
        {
            StreamingCorpus.Equal(expected, frames++, canvas, file);
        }

        Assert.Equal(expected.FrameCount, frames);
    }

    /// <summary>A stream that hands out at most a few bytes per read and cannot seek, like a slow upload.</summary>
    internal sealed class TrickleStream(byte[] data, int most) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, most), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Part 1 of 2 of <see cref="AsyncTests{TPart}"/>.</summary>
public sealed class AsyncTestsPart0 : AsyncTests<Part0>;

/// <summary>Part 2 of 2 of <see cref="AsyncTests{TPart}"/>.</summary>
public sealed class AsyncTestsPart1 : AsyncTests<Part1>;
