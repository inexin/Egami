using BenchmarkDotNet.Attributes;

using Egami.Formats.Jbig2;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>JBIG2 decode of corpus pages (tests/images/jbig2): to Gray8 through Load, and the packed page alone.</summary>
[MemoryDiagnoser]
public class Jbig2Benchmarks
{
    private byte[] _file = [];

    [Params("text-page-generic.jb2", "text-page-symbol.jb2", "duck-dither-generic.jb2", "pdfbox-002.jb2")]
    public string File { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "tests", "images", "jbig2")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        string path = Path.Combine(root, "tests", "images", "jbig2", File);
        _file = System.IO.File.ReadAllBytes(System.IO.File.Exists(path) ? path : Path.Combine(root, ".cache", "jbig2-local", File));
    }

    [Benchmark(Baseline = true)]
    public int LoadGray8()
    {
        using Raster<Gray8> raster = Raster.Load<Gray8>(_file);
        return raster.Width;
    }

    [Benchmark]
    public int GenericRegionOnly()
    {
        if (!File.Contains("generic", StringComparison.Ordinal))
        {
            return 0;
        }

        // The region's data: after the file header (13), the page information segment (11 + 19) and the region header (11 + 18 + 8).
        int start = 13 + 30 + 11 + 26;
        int width = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(_file.AsSpan(13 + 30 + 11));
        int height = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(_file.AsSpan(13 + 30 + 15));
        Formats.MqDecoder mq = new();
        mq.Start(_file, start, _file.Length - start);
        Jbig2GenericParams p = new() { Width = width, Height = height, Template = 0, At = [3, -1, -3, -1, 2, -2, -2, -2] };
        return Jbig2Generic.DecodeArithmetic(p, mq, new byte[65536]).Width;
    }

    [Benchmark]
    public int PackedPage()
    {
        Jbig2Stream stream = Jbig2Stream.Parse(_file, _file.Length, embedded: false, 1 << 20);
        Jbig2Decoder decoder = new(stream, null, new Jbig2Budget(SafetyLimits.Default), strict: false);
        return decoder.DecodePage(decoder.PageSegments()[0], CancellationToken.None).Bitmap.Width;
    }
}
