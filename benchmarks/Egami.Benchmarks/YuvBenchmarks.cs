using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// The planar YUV design's speed scenarios: 1080p 4:2:0 8-bit BT.709 limited to and from <see cref="Rgba8"/>, and 10-bit
/// BT.2020 limited to <see cref="Rgba16"/>, into rasters that already exist, as a video player reuses them.
/// </summary>
[MemoryDiagnoser]
public class YuvBenchmarks
{
    private YuvRaster<byte> _yuv8 = null!;
    private YuvRaster<ushort> _yuv10 = null!;
    private Raster<Rgba8> _rgba8 = null!;
    private Raster<Rgba16> _rgba16 = null!;

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads };
        _rgba8 = Raster.Create<Rgba8>(1920, 1080, context);
        _rgba16 = Raster.Create<Rgba16>(1920, 1080, context);
        Random random = new(1);
        for (int y = 0; y < 1080; y++)
        {
            Span<Rgba8> row = _rgba8.Row(y);
            for (int x = 0; x < 1920; x++)
            {
                // A smooth gradient with some noise, like video.
                row[x] = new Rgba8((byte)((x / 8) + random.Next(8)), (byte)((y / 5) + random.Next(8)), (byte)(((x + y) / 12) + random.Next(8)), 255);
            }
        }

        _yuv8 = YuvRaster.FromRaster(_rgba8, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
        _yuv10 = YuvRaster.Create(1920, 1080, ChromaFormat.Yuv420, 10, YuvColor.Bt2020Limited, context: context);
        foreach (YuvPlane<ushort> plane in new[] { _yuv10.Y, _yuv10.U!, _yuv10.V! })
        {
            for (int y = 0; y < plane.Height; y++)
            {
                Span<ushort> row = plane.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = (ushort)(64 + ((x + y) % 876) + random.Next(4));
                }
            }
        }

        random.NextBytes(MemoryMarshal.AsBytes(_rgba16.Row(0)));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _yuv8.Dispose();
        _yuv10.Dispose();
        _rgba8.Dispose();
        _rgba16.Dispose();
    }

    [Benchmark]
    public void Yuv420ToRgba8() => _yuv8.CopyTo(_rgba8);

    [Benchmark]
    public void Rgba8ToYuv420() => _yuv8.CopyFrom(_rgba8);

    [Benchmark]
    public void Yuv420TenBitToRgba16() => _yuv10.CopyTo(_rgba16);
}
