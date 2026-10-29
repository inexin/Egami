using System.Security.Cryptography;
using System.Text.Json;

using Egami.Pixels;

namespace Egami.Heic.Tests;

/// <summary>
/// Phase 2.9.10 task 23: <see cref="HevcStreamDecoder"/>, through its public API only, decodes Kvazaar's coding of a moving
/// pattern (P and B pictures, reordered) to HM's frames, however the stream is cut, and again after a reset.
/// </summary>
public class HevcStreamDecoderTests
{
    private static readonly string Stream = Path.Combine(AppContext.BaseDirectory, "images", "heic", "video", "hevc-inter-kvazaar.h265");

    private static readonly string[] Expected = [.. JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "heic", "hevc-inter-kvazaar.json")))
        .RootElement.GetProperty("frames").EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public void DecodesAWholeStreamToHmFrames()
    {
        using HevcStreamDecoder decoder = new();
        List<string> frames = [];
        decoder.Send(File.ReadAllBytes(Stream));
        Receive(decoder, frames);
        decoder.Flush();
        Receive(decoder, frames);
        Assert.Equal(Expected, frames);
    }

    [Fact]
    public void DecodesNalUnitByNalUnitAndAfterAReset()
    {
        using HevcStreamDecoder decoder = new(new HevcDecoderOptions { CheckPictureHashes = true });
        for (int pass = 0; pass < 2; pass++)
        {
            List<string> frames = [];
            foreach (byte[] unit in Units(File.ReadAllBytes(Stream)))
            {
                decoder.Send(unit);
                Receive(decoder, frames);
            }

            decoder.Flush();
            Receive(decoder, frames);
            Assert.Equal(Expected, frames);
            decoder.Reset();
        }
    }

    [Fact]
    public void GivesTheFramesInfo()
    {
        using HevcStreamDecoder decoder = new();
        decoder.Send(File.ReadAllBytes(Stream));
        decoder.Flush();
        HevcFrameInfo? next = decoder.NextFrame;
        Assert.NotNull(next);
        Assert.Equal(96, next.Value.Width);
        Assert.Equal(64, next.Value.Height);
        Assert.Equal(8, next.Value.BitDepth);
        Assert.Equal(ChromaFormat.Yuv420, next.Value.Chroma);
        Assert.Equal(HevcPictureKind.Idr, next.Value.Kind);
        Assert.True(next.Value.IsIrap);
        Assert.Equal(0, next.Value.Poc);
        YuvRaster<byte> wrong = YuvRaster.Create(64, 64, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
        Assert.Throws<ArgumentException>(() => decoder.TryReceiveFrame(wrong, out _));
    }

    private static void Receive(HevcStreamDecoder decoder, List<string> frames)
    {
        while (decoder.NextFrame is { } info)
        {
            YuvRaster<byte> raster = YuvRaster.Create(info.Width, info.Height, info.Chroma, YuvColor.Bt709Limited);
            Assert.True(decoder.TryReceiveFrame(raster, out _));
            using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            foreach (YuvPlane<byte>? plane in new[] { raster.Y, raster.U, raster.V })
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    md5.AppendData(plane.Row(y));
                }
            }

#pragma warning disable CA1308 // MD5s are written in lower case
            frames.Add(Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant());
#pragma warning restore CA1308
        }
    }

    /// <summary>The stream's NAL units, each with its start code.</summary>
    internal static IEnumerable<byte[]> Units(byte[] stream)
    {
        List<int> starts = [];
        for (int i = 0; i + 3 <= stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                starts.Add(i);
                i += 2;
            }
        }

        for (int k = 0; k < starts.Count; k++)
        {
            int end = k + 1 < starts.Count ? starts[k + 1] : stream.Length;
            yield return stream[starts[k]..end];
        }
    }
}

/// <summary>Phase 2.9.10 task 25: what decoding allocates once the decoder runs (EGAMI_ALLOCATION_TESTS=1).</summary>
public class HevcAllocationTests
{
    [Trait("Category", "Allocation")]
    [Fact]
    public void SteadyStateAllocations()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        byte[] stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "heic", "video", "hevc-inter-kvazaar.h265"));
        using HevcStreamDecoder decoder = new();
        Egami.YuvRaster<byte> raster = Egami.YuvRaster.Create(96, 64, Egami.Pixels.ChromaFormat.Yuv420, Egami.Pixels.YuvColor.Bt709Limited);
        List<byte[]> units = [.. HevcStreamDecoderTests.Units(stream)];
        void Run()
        {
            // The stream again and again: each pass starts at an IDR picture, as a player's stream would at a key frame.
            foreach (byte[] unit in units)
            {
                decoder.Send(unit);
                while (decoder.TryReceiveFrame(raster, out _))
                {
                }
            }
        }

        Run();
        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        long perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / 12;
        TestContext.Current.TestOutputHelper?.WriteLine($"{perFrame} bytes a frame");
        // Pictures, their maps and the SAO copy are used again; what is left (about 6 KB) is per NAL unit and slice: each unit's
        // RBSP, its slice header and lists, whatever the picture size.
        Assert.True(perFrame < 8192, $"{perFrame} bytes a frame");
    }
}

/// <summary>Inputs the hevcstream fuzz target found: each must fail as broken data, never crash.</summary>
public class HevcFuzzRegressionTests
{
    public static TheoryData<string> Files() => [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "images", "fuzz", "hevc-regressions")).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    /// <summary>entry-point-past-data: a slice's entry point past its data started the CABAC engine outside the array.</summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void DamagedStreamsFailCleanly(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "fuzz", "hevc-regressions", file));
        using HevcStreamDecoder decoder = new();
        try
        {
            decoder.Send(data);
            decoder.Flush();
        }
        catch (EgamiException)
        {
        }
    }
}
