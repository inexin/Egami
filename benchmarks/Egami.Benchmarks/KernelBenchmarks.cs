using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Formats;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>Bulk pixel conversion of one megapixel; the Scalar variant shows what SIMD gains.</summary>
public class PixelConvertBenchmarks
{
    private const int Pixels = 1024 * 1024;
    private readonly byte[] _source = new byte[Pixels * 16];
    private readonly byte[] _destination = new byte[Pixels * 16];

    [GlobalSetup]
    public void Setup()
    {
        new Random(1).NextBytes(_source);
    }

    [Benchmark]
    public void Rgba8ToBgra8()
    {
        PixelConvert.Convert(Source<Rgba8>(), Destination<Bgra8>());
    }

    [Benchmark]
    public void Rgba8ToBgra8Scalar()
    {
        PixelConvert.ConvertScalar(Source<Rgba8>(), Destination<Bgra8>());
    }

    [Benchmark]
    public void Rgb8ToRgba8()
    {
        PixelConvert.Convert(Source<Rgb8>(), Destination<Rgba8>());
    }

    [Benchmark]
    public void Rgba16ToRgba8()
    {
        PixelConvert.Convert(Source<Rgba16>(), Destination<Rgba8>());
    }

    [Benchmark]
    public void Rgb16ToRgb8()
    {
        PixelConvert.Convert(Source<Rgb16>(), Destination<Rgb8>());
    }

    [Benchmark]
    public void Rgb8ToRgb16()
    {
        PixelConvert.Convert(Source<Rgb8>(), Destination<Rgb16>());
    }

    [Benchmark]
    public void GrayAlpha16ToGrayAlpha8()
    {
        PixelConvert.Convert(Source<GrayAlpha16>(), Destination<GrayAlpha8>());
    }

    [Benchmark]
    public void Rgba8ToArgb8()
    {
        PixelConvert.Convert(Source<Rgba8>(), Destination<Argb8>());
    }

    [Benchmark]
    public void Abgr8ToBgra8()
    {
        PixelConvert.Convert(Source<Abgr8>(), Destination<Bgra8>());
    }

    [Benchmark]
    public void RgbaF32ToRgbaF16()
    {
        PixelConvert.Convert(Source<RgbaF32>(), Destination<RgbaF16>());
    }

    [Benchmark]
    public void RgbaF16ToRgbaF32()
    {
        PixelConvert.Convert(Source<RgbaF16>(), Destination<RgbaF32>());
    }

    [Benchmark]
    public void Bgr565ToRgba8()
    {
        PixelConvert.Convert(Source<Bgr565>(), Destination<Rgba8>());
    }

    [Benchmark]
    public void Rgba8ToBgr565()
    {
        PixelConvert.Convert(Source<Rgba8>(), Destination<Bgr565>());
    }

    [Benchmark]
    public void Rgba8ToRgb10A2()
    {
        PixelConvert.Convert(Source<Rgba8>(), Destination<Rgb10A2>());
    }

    [Benchmark]
    public void Rgb16ToRgba16()
    {
        PixelConvert.Convert(Source<Rgb16>(), Destination<Rgba16>());
    }

    [Benchmark]
    public void Gray8ToRgba8()
    {
        PixelConvert.Convert(Source<Gray8>(), Destination<Rgba8>());
    }

    [Benchmark]
    public void Rgba8ToGray8()
    {
        PixelConvert.Convert(Source<Rgba8>(), Destination<Gray8>());
    }

    [Benchmark]
    public void Rgba8ToRgbaF32()
    {
        PixelConvert.Convert(Source<Rgba8>(), Destination<RgbaF32>());
    }

    private ReadOnlySpan<T> Source<T>()
        where T : unmanaged
    {
        return MemoryMarshal.Cast<byte, T>(_source.AsSpan())[..Pixels];
    }

    private Span<T> Destination<T>()
        where T : unmanaged
    {
        return MemoryMarshal.Cast<byte, T>(_destination.AsSpan())[..Pixels];
    }
}

/// <summary>CRC-32 over 1 MB, hardware path against the table path.</summary>
public class Crc32Benchmarks
{
    private readonly byte[] _data = new byte[1024 * 1024];

    [GlobalSetup]
    public void Setup()
    {
        new Random(2).NextBytes(_data);
    }

    [Benchmark]
    public uint Hardware()
    {
        return Crc32.UpdateState(~0u, _data, useHardware: true);
    }

    [Benchmark(Baseline = true)]
    public uint Table()
    {
        return Crc32.UpdateState(~0u, _data, useHardware: false);
    }
}

/// <summary>Unfiltering and filtering one 4000-pixel RGBA row with each filter; the Scalar variants show what SIMD gains.</summary>
public class PngFilterBenchmarks
{
    private const int Stride = 4;
    private readonly byte[] _previous = new byte[4000 * Stride];
    private readonly byte[] _row = new byte[4000 * Stride];
    private readonly byte[] _output = new byte[4000 * Stride];

    [Params(PngFilter.Sub, PngFilter.Up, PngFilter.Average, PngFilter.Paeth)]
    public byte Filter { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(3);
        random.NextBytes(_previous);
        random.NextBytes(_row);
    }

    [Benchmark]
    public void Unfilter()
    {
        PngFilterSimd.Unfilter(Filter, _row, _previous, Stride);
    }

    [Benchmark]
    public void UnfilterScalar()
    {
        PngFilter.Unfilter(Filter, _row, _previous, Stride);
    }

    [Benchmark]
    public void FilterRow()
    {
        PngFilterEncoder.Filter(Filter, _row, _previous, Stride, _output);
    }

    [Benchmark]
    public void FilterRowScalar()
    {
        PngFilterEncoder.FilterScalar(Filter, _row, _previous, Stride, _output);
    }
}

/// <summary>The 16-bit linear-light luma on its own, from 8-bit and from float channels, a megapixel at a time.</summary>
public class Luma16Benchmarks
{
    private const int Pixels = 1024 * 1024;
    private readonly byte[] _bytes = new byte[Pixels * 3];
    private readonly float[] _floats = new float[Pixels * 3];

    [GlobalSetup]
    public void Setup()
    {
        new Random(1).NextBytes(_bytes);
        for (int i = 0; i < _floats.Length; i++)
        {
            _floats[i] = _bytes[i] / 255.5f;
        }
    }

    [Benchmark]
    public int Of8()
    {
        int sum = 0;
        for (int i = 0; i < _bytes.Length; i += 3)
        {
            sum += Luma16.Of8(_bytes[i], _bytes[i + 1], _bytes[i + 2]);
        }

        return sum;
    }

    [Benchmark]
    public int OfScaled()
    {
        int sum = 0;
        for (int i = 0; i < _floats.Length; i += 3)
        {
            sum += Luma16.OfScaled(_floats[i], _floats[i + 1], _floats[i + 2]);
        }

        return sum;
    }
}
