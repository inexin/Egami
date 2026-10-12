using Egami.Formats;
using Egami.Pixels;

namespace Egami.Tests.Formats;

public sealed class FormatPipelineTests : IDisposable
{
    private static readonly EgamiContext Context = TestFormat.Context;
    private readonly string _directory = Directory.CreateTempSubdirectory("egami-tests-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void StreamRoundTrip()
    {
        using Raster<Rgba8> original = Sample();
        using MemoryStream stream = new();
        original.Save(stream, TestFormat.Info);
        stream.Position = 0;

        using Raster loaded = Raster.Load(stream, Context);
        Raster<Rgba8> typed = Assert.IsType<Raster<Rgba8>>(loaded);
        AssertSame(original, typed);
        Assert.Equal("EGTF", loaded.Metadata.FormatName);
    }

    [Fact]
    public void GenericLoadConvertsWhileDecoding()
    {
        using Raster<Rgba8> original = Sample();
        byte[] bytes = Encode(original);

        using Raster<Bgra8> bgra = Raster.Load<Bgra8>(bytes, Context);
        Assert.Equal(new Bgra8(0, 0, 255, 255), bgra.GetPixel(0, 0));

        using Raster<Gray8> gray = Raster.Load<Gray8>(new MemoryStream(bytes), Context);
        Assert.Equal(127, gray.GetPixel(0, 0).L);
    }

    [Fact]
    public void NaturalPixelTypeFollowsTheFile()
    {
        using Raster<Gray8> original = Raster.Create(3, 2, new Gray8(77));
        using Raster loaded = Raster.Load(Encode(original), Context);
        Assert.IsType<Raster<Gray8>>(loaded);
        Assert.Equal(Gray8.Layout, loaded.Layout);
    }

    [Fact]
    public void FileRoundTripChoosesTheFormatByExtension()
    {
        string path = Path.Combine(_directory, "image.EGT");
        using Raster<Rgba8> original = Sample();
        original.Save(path);

        using Raster loaded = Raster.Load(path, Context);
        AssertSame(original, Assert.IsType<Raster<Rgba8>>(loaded));
        using Raster<Rgba16> wide = Raster.Load<Rgba16>(path, Context);
        Assert.Equal(new Rgba16(65535, 0, 0, 65535), wide.GetPixel(0, 0));
        Assert.Equal(5, Raster.Probe(path, Context).Width);
    }

    [Fact]
    public async Task AsyncRoundTrip()
    {
        using Raster<Rgba8> original = Sample();
        using MemoryStream stream = new();
        await original.SaveAsync(stream, "egtf", cancellationToken: TestContext.Current.CancellationToken);
        stream.Position = 0;

        using Raster loaded = await Raster.LoadAsync(stream, Context, cancellationToken: TestContext.Current.CancellationToken);
        AssertSame(original, Assert.IsType<Raster<Rgba8>>(loaded));

        string path = Path.Combine(_directory, "async.egtf");
        await original.SaveAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        using Raster<Gray8> gray = await Raster.LoadAsync<Gray8>(path, Context, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(127, gray.GetPixel(0, 0).L);
        using Raster fromPath = await Raster.LoadAsync(path, Context, cancellationToken: TestContext.Current.CancellationToken);
        using Raster<Rgba8> typed = await Raster.LoadAsync<Rgba8>(new MemoryStream(File.ReadAllBytes(path)), Context, cancellationToken: TestContext.Current.CancellationToken);
        AssertSame(original, typed);

        RasterInfo info = await Raster.ProbeAsync(path, Context, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, info.Height);
        stream.Position = 0;
        Assert.Equal(5, (await Raster.ProbeAsync(stream, Context, cancellationToken: TestContext.Current.CancellationToken)).Width);
    }

    [Fact]
    public void ProbeReadsOnlyTheHeader()
    {
        CountingSource memory = new();
        EgamiContext context = Context with { Memory = memory };
        using Raster<Rgba8> original = Sample();
        byte[] bytes = Encode(original);

        RasterInfo info = Raster.Probe(bytes, context);
        Assert.Same(TestFormat.Info, info.Format);
        Assert.Equal(5, info.Width);
        Assert.Equal(4, info.Height);
        Assert.Equal(Rgba8.Layout, info.Layout);
        Assert.Equal(80, info.EstimatedBytes);
        Assert.Equal(0, memory.Allocations);

        using MemoryStream stream = new(bytes);
        Raster.Probe(stream, context);
        Assert.Equal(13, stream.Position);
    }

    [Fact]
    public void SeekableStreamEndsJustPastTheImage()
    {
        using Raster<Rgba8> original = Sample();
        using MemoryStream stream = new();
        original.Save(stream, TestFormat.Info);
        long end = stream.Position;
        stream.Write("trailing data"u8);
        stream.Position = 0;

        using (Raster.Load(stream, Context))
        {
            Assert.Equal(end, stream.Position);
        }

        stream.Position = 0;
        Assert.True(stream.CanRead, "The stream must be left open.");
    }

    [Fact]
    public void NonSeekableStreamsWork()
    {
        using Raster<Rgba8> original = Sample();
        using NonSeekableStream stream = new(Encode(original));
        using Raster loaded = Raster.Load(stream, Context);
        AssertSame(original, Assert.IsType<Raster<Rgba8>>(loaded));
    }

    [Fact]
    public void UnknownDataThrows()
    {
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Load("NOPE...."u8.ToArray(), Context));
        Assert.Contains("EGTF", e.Message, StringComparison.Ordinal);
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(Array.Empty<byte>(), Context));
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load("EGTF"u8.ToArray(), EgamiContext.Default));
    }

    [Fact]
    public void SaveWithUnknownFormatThrows()
    {
        using Raster<Rgba8> raster = Sample();
        using MemoryStream stream = new();
        Assert.Throws<UnsupportedFormatException>(() => raster.Save(stream, "xyz"));
        Assert.Throws<UnsupportedFormatException>(() => raster.Save(Path.Combine(_directory, "no-extension")));
        Assert.Throws<UnsupportedFormatException>(() => raster.Save(stream, TestFormat.Info with { Writer = null }));
    }

    [Fact]
    public void FormatWithoutReaderIsReported()
    {
        EgamiContext context = EgamiContext.Default.WithFormat(TestFormat.Info with { Reader = null });
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Load("EGTF"u8.ToArray(), context));
        Assert.Contains("no reader", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedDataThrowsCorruptDataWithTheOffset()
    {
        using Raster<Rgba8> original = Sample();
        byte[] bytes = Encode(original);
        CorruptDataException e = Assert.Throws<CorruptDataException>(() => Raster.Load(bytes.AsMemory(0, bytes.Length - 1), Context));
        Assert.Equal("EGTF", e.FormatName);
        Assert.Equal(13 + (3 * 20), e.Offset);
    }

    [Fact]
    public void LimitsAreCheckedBeforeAllocating()
    {
        CountingSource memory = new();
        EgamiContext context = Context with { Memory = memory, Limits = SafetyLimits.Default with { MaxWidth = 4 } };
        using Raster<Rgba8> original = Sample();

        LimitExceededException e = Assert.Throws<LimitExceededException>(() => Raster.Load(Encode(original), context));
        Assert.Equal("MaxWidth", e.LimitName);
        Assert.Equal(0, memory.Allocations);

        context = Context with { Memory = memory, Limits = SafetyLimits.Default with { MaxTotalBytes = 79 } };
        Assert.Equal("MaxTotalBytes", Assert.Throws<LimitExceededException>(() => Raster.Load(Encode(original), context)).LimitName);
        Assert.Equal(0, memory.Allocations);
    }

    [Fact]
    public async Task AsyncLoadCountsTheBufferAgainstTheMemoryLimit()
    {
        using Raster<Rgba8> original = Sample();
        EgamiContext context = Context with { Limits = SafetyLimits.Default with { MaxTotalBytes = 50 } };
        await Assert.ThrowsAsync<LimitExceededException>(() => Raster.LoadAsync(new MemoryStream(Encode(original)), context, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellationStopsLoading()
    {
        using Raster<Rgba8> original = Sample();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Raster.LoadAsync(new MemoryStream(Encode(original)), Context, cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.SaveAsync(new MemoryStream(), TestFormat.Info, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void FailedSaveDeletesThePartialFile()
    {
        string path = Path.Combine(_directory, "broken.egtf");
        EgamiContext context = EgamiContext.Default.WithFormat(TestFormat.Info with { Writer = new ThrowingWriter() });
        using Raster<Rgba8> raster = Raster.Create(2, 2, Rgba8.Red, context);
        Assert.Throws<InvalidOperationException>(() => raster.Save(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WithFormatReplacesByNameAndAppendsOtherwise()
    {
        FormatInfo other = TestFormat.Info with { Name = "Other", Matches = _ => false };
        EgamiContext context = EgamiContext.Default.WithFormat(TestFormat.Info).WithFormat(other);
        string[] builtIn = ["PNG", "JPEG", "GIF", "WebP", "BMP", "QOI", "PNM", "TIFF", "JBIG2", "JPEG 2000", "ICO", "CUR", "PSD", "Radiance", "OpenEXR", "TGA"];
        Assert.Equal([.. builtIn, "EGTF", "Other"], context.Formats.Select(f => f.Name));

        FormatInfo replacement = TestFormat.Info with { Name = "egtf", Extensions = ["x"] };
        context = context.WithFormat(replacement);
        Assert.Equal([.. builtIn, "egtf", "Other"], context.Formats.Select(f => f.Name));
        Assert.Equal(builtIn, EgamiContext.Default.Formats.Select(f => f.Name));
    }

    [Fact]
    public void EgamiInputHelpersReadExactly()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
        EgamiInput input = new(new NonSeekableStream(data), EgamiContext.Default, CancellationToken.None);
        Assert.Equal([1, 2, 3], input.Peek(3).ToArray());
        Assert.Equal(0, input.Position);
        Assert.Equal(0x0102, input.ReadUInt16BigEndian());
        Assert.Equal(0x0403, input.ReadUInt16LittleEndian());
        Assert.Equal(0x05060708u, input.ReadUInt32BigEndian());
        Assert.Equal(0x0C0B0A09u, input.ReadUInt32LittleEndian());
        Assert.Equal(12, input.Position);
        Assert.Equal(13, input.ReadByte());
        Assert.Equal(0, input.Read(new byte[4]));
        Assert.True(input.Peek(10).IsEmpty);
        Assert.Throws<CorruptDataException>(() => input.ReadByte());
        Assert.Throws<CorruptDataException>(() => input.Skip(1));
    }

    private static Raster<Rgba8> Sample()
    {
        Raster<Rgba8> raster = Raster.Create(5, 4, Rgba8.Red, Context);
        raster.SetPixel(4, 3, new Rgba8(1, 2, 3, 4));
        raster.SetPixel(2, 1, Rgba8.Lime);
        return raster;
    }

    private static byte[] Encode(Raster raster)
    {
        using MemoryStream stream = new();
        raster.Save(stream, TestFormat.Info);
        return stream.ToArray();
    }

    private static void AssertSame(Raster<Rgba8> expected, Raster<Rgba8> actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.Equal(expected.Row(y).ToArray(), actual.Row(y).ToArray());
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;

        public override long Position { get => base.Position; set => throw new NotSupportedException(); }

        public override long Seek(long offset, SeekOrigin loc)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ThrowingWriter : IFormatWriter
    {
        public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            output.Write("EGTF partial"u8);
            throw new InvalidOperationException("Simulated encoder failure.");
        }
    }

    private sealed class CountingSource : IPixelMemorySource
    {
        private int _allocations;

        public int MaxBlockBytes => PooledPixelMemorySource.Shared.MaxBlockBytes;

        public int Allocations => _allocations;

        public System.Buffers.IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            Interlocked.Increment(ref _allocations);
            return PooledPixelMemorySource.Shared.Allocate(byteLength, clear);
        }
    }
}
