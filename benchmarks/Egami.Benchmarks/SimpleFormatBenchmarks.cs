using BenchmarkDotNet.Attributes;

using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Formats.Pnm;
using Egami.Formats.Qoi;
using Egami.Formats.Tga;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>Decoding each Phase 1.5 scenario in its natural pixel type and as Rgba8, and probing it.</summary>
[MemoryDiagnoser]
public class SimpleDecodeBenchmarks
{
    private byte[] _file = [];

    [ParamsSource(nameof(Scenarios))]
    public string Scenario { get; set; } = string.Empty;

    public static IEnumerable<string> Scenarios => SimpleFiles.DecodeScenarios;

    [GlobalSetup]
    public void Setup()
    {
        _file = SimpleFiles.File(Scenario);
        Console.WriteLine($"// {Scenario}: {_file.Length:N0} bytes");
    }

    [Benchmark]
    public int Decode()
    {
        using Raster raster = Raster.Load(new MemoryStream(_file, writable: false));
        return raster.Width;
    }

    [Benchmark]
    public int DecodeToRgba8()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(new MemoryStream(_file, writable: false));
        return raster.Width;
    }

    [Benchmark]
    public int Probe()
    {
        return Raster.Probe(new MemoryStream(_file, writable: false)).Width;
    }
}

/// <summary>Encoding the Phase 1.5 scenarios: the photo as BMP, TGA, QOI and PNM, and the translucent screenshot as QOI.</summary>
[MemoryDiagnoser]
public class SimpleEncodeBenchmarks
{
    private Raster<Rgba8> _photo = null!;
    private Raster<Rgb8> _photoRgb = null!;
    private Raster<Gray16> _gray16 = null!;
    private Raster<Rgba8> _screenshot = null!;
    private Raster<Rgb8> _small = null!;

    [GlobalSetup]
    public void Setup()
    {
        _photo = SimpleFiles.Photo();
        _photoRgb = _photo.As<Rgb8>();
        _gray16 = _photo.As<Gray16>();
        _screenshot = SimpleFiles.TranslucentScreenshot();
        using Raster<Rgba8> small = BenchImages.Photo(512, 512);
        _small = small.As<Rgb8>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _photo.Dispose();
        _photoRgb.Dispose();
        _gray16.Dispose();
        _screenshot.Dispose();
        _small.Dispose();
    }

    [Benchmark]
    public long Bmp24() => Write(_photoRgb, BmpFormat.Info, BmpWriterOptions.Default);

    [Benchmark]
    public long Bmp32() => Write(_photo, BmpFormat.Info, new BmpWriterOptions { PixelFormat = BmpPixelFormat.Bgra32 });

    [Benchmark]
    public long Tga32Rle() => Write(_photo, TgaFormat.Info, new TgaWriterOptions { PixelFormat = TgaPixelFormat.Bgra32 });

    [Benchmark]
    public long Tga32Raw() => Write(_photo, TgaFormat.Info, new TgaWriterOptions { PixelFormat = TgaPixelFormat.Bgra32, Rle = false });

    [Benchmark]
    public long QoiPhoto() => Write(_photoRgb, QoiFormat.Info, QoiWriterOptions.Default);

    [Benchmark]
    public long QoiScreenshot() => Write(_screenshot, QoiFormat.Info, QoiWriterOptions.Default);

    [Benchmark]
    public long PnmP6() => Write(_photoRgb, PnmFormat.Info, PnmWriterOptions.Default);

    [Benchmark]
    public long PnmP5Wide() => Write(_gray16, PnmFormat.Info, PnmWriterOptions.Default);

    [Benchmark]
    public long PnmP3() => Write(_small, PnmFormat.Info, new PnmWriterOptions { PlainText = true });

    private static long Write<TPixel>(Raster<TPixel> raster, FormatInfo format, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        CountingStream stream = new();
        raster.Save(stream, format, options);
        return stream.Length;
    }

    /// <summary>A stream that only counts what is written, so encoding is measured without copying into a growing buffer.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _length += count;

        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    }
}
