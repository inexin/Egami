using BenchmarkDotNet.Attributes;

using Egami.Formats;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>Decoding each scenario from the PNG design in its natural pixel type and as Rgba8, and probing it.</summary>
[MemoryDiagnoser]
public class PngDecodeBenchmarks
{
    private byte[] _file = [];

    [ParamsSource(nameof(Scenarios))]
    public string Scenario { get; set; } = string.Empty;

    public static IEnumerable<string> Scenarios => BenchImages.DecodeScenarios;

    [GlobalSetup]
    public void Setup()
    {
        _file = BenchImages.Png(Scenario);
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

/// <summary>Encoding the photo and the screenshot at default settings, and the photo at the fastest and smallest settings.</summary>
[MemoryDiagnoser]
public class PngEncodeBenchmarks
{
    private static readonly PngWriterOptions Fastest = new() { CompressionLevel = 1, Filter = PngFilterMode.Sub };
    private static readonly PngWriterOptions Best = new() { CompressionLevel = 9 };

    private Raster<Rgba8> _photo = null!;
    private Raster<Rgb8> _screenshot = null!;

    [GlobalSetup]
    public void Setup()
    {
        _photo = BenchImages.Photo(4000, 3000);
        _screenshot = BenchImages.Screenshot(1920, 1080);
        Console.WriteLine(
            $"// Output sizes: photo default {Encode(_photo, PngWriterOptions.Default):N0}, fastest {Encode(_photo, Fastest):N0}, " +
            $"best {Encode(_photo, Best):N0}; screenshot default {Encode(_screenshot, PngWriterOptions.Default):N0} bytes");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _photo.Dispose();
        _screenshot.Dispose();
    }

    [Benchmark]
    public long PhotoDefault()
    {
        return Encode(_photo, PngWriterOptions.Default);
    }

    [Benchmark]
    public long PhotoFastest()
    {
        return Encode(_photo, Fastest);
    }

    [Benchmark]
    public long PhotoBest()
    {
        return Encode(_photo, Best);
    }

    [Benchmark]
    public long ScreenshotDefault()
    {
        return Encode(_screenshot, PngWriterOptions.Default);
    }

    private static long Encode(Raster raster, WriterOptions options)
    {
        using CountingStream stream = new();
        raster.Save(stream, PngFormat.Info, options);
        return stream.Length;
    }

    /// <summary>Counts bytes without storing them, so the benchmark measures encoding rather than buffer growth.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _length += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _length += buffer.Length;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
    }
}
