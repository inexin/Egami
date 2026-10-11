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
    private YuvRaster<byte> _jpegPlanes = null!;
    private byte[] _nv12 = [];

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
        _jpegPlanes = YuvRaster.FromRaster(_rgba8, ChromaFormat.Yuv420, YuvColor.Jpeg);
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
        _nv12 = new byte[1920 * 1080 * 3 / 2];
        random.NextBytes(_nv12);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _yuv8.Dispose();
        _jpegPlanes.Dispose();
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

    /// <summary>Phase 2.1.4 task 9: JPEG straight from 4:2:0 BT.601 full-range planes, with no conversion or downsampling.</summary>
    [Benchmark]
    public void SaveJpegFromPlanes() => _jpegPlanes.Save(Stream.Null, Formats.Jpeg.JpegFormat.Info);

    /// <summary>JPEG from BT.709 limited planes, which are converted first.</summary>
    [Benchmark]
    public void SaveJpegFromVideoPlanes() => _yuv8.Save(Stream.Null, Formats.Jpeg.JpegFormat.Info);

    /// <summary>The same picture saved from RGB, for comparison.</summary>
    [Benchmark]
    public void SaveJpegFromRgba8() => _rgba8.Save(Stream.Null, Formats.Jpeg.JpegFormat.Info);

    /// <summary>Phase 2.1.4 task 10: scaling 4:2:0 planes to 720p, as video scales before converting.</summary>
    [Benchmark]
    public void ResizePlanesTo720p()
    {
        using YuvRaster<byte> small = _yuv8.Resize(1280, 720);
    }

    /// <summary>The same scale on Rgba8, for comparison.</summary>
    [Benchmark]
    public void ResizeRgba8To720p()
    {
        using Raster small = _rgba8.Derive(ops => ops.Resize(1280, 720));
    }

    /// <summary>Phase 2.1.4 task 7: a decoder's NV12 frame into planes (single-threaded by design: one pass over memory).</summary>
    [Benchmark]
    public void Nv12ToPlanes() => _yuv8.CopyFromPacked(YuvPackedFormat.Nv12, _nv12);
}
