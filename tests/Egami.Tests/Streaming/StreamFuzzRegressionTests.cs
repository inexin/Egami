using Egami.Formats;
using Egami.Pixels;

namespace Egami.Tests.Streaming;

/// <summary>
/// Inputs the stream fuzz target found (tests/images/fuzz/stream): with <see cref="RecoveryMode.Strict"/>, decoding while the bytes
/// arrive must give the whole decode's pixels, or fail as it does. A BMP whose header claims a gigabyte of pixels failed the
/// memory limit only incrementally; a GIF application extension with an empty identifier was read before its sub-blocks had
/// arrived; a TGA whose extension area points into its pixels gave a different alpha when the file could not seek. Run-length
/// TGA files whose pixels reach into the footer, or whose extension area overruns the footer, were damage to one decode only.
/// </summary>
public class StreamFuzzRegressionTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "images", "fuzz", "stream");

    private static readonly ReaderOptions Strict = new() { Recovery = RecoveryMode.Strict };

    // The fuzz target's memory limit, which the BMP's claimed size is over.
    private static readonly EgamiContext Context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 256L * 1024 * 1024 } };

    public static TheoryData<string, int> Cases()
    {
        TheoryData<string, int> data = [];
        foreach (string file in Directory.GetFiles(Folder).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal))
        {
            foreach (int split in (int[])[1, 13, 4096])
            {
                data.Add(file, split);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void IncrementalDecodeMatchesWholeDecode(string file, int split)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Folder, file));
        (byte[]? whole, string? wholeError) = Whole(data);
        (byte[]? incremental, string? incrementalError) = Incremental(data, split);
        Assert.Equal(wholeError, incrementalError);
        Assert.Equal(whole, incremental);
    }

    private static (byte[]? Pixels, string? Error) Whole(byte[] data)
    {
        try
        {
            using RasterDecoder decoder = RasterDecoder.Open(data, Context, Strict);
            using Raster<Rgba8> band = Raster.Create<Rgba8>(decoder.Info.Width, 8, Context);
            List<byte> pixels = [];
            do
            {
                int rows;
                while ((rows = decoder.ReadRows(band)) > 0)
                {
                    Add(pixels, band, rows);
                }
            }
            while (decoder.NextFrame());

            return ([.. pixels], null);
        }
        catch (EgamiException e)
        {
            return (null, e.Message);
        }
    }

    private static (byte[]? Pixels, string? Error) Incremental(byte[] data, int split)
    {
        try
        {
            using RasterDecoder decoder = RasterDecoder.OpenIncremental(Context, Strict);
            Raster<Rgba8>? band = null;
            List<byte> pixels = [];
            try
            {
                int at = 0;
                while (true)
                {
                    bool ended = at >= data.Length;
                    if (ended)
                    {
                        decoder.EndOfInput();
                    }
                    else
                    {
                        int size = Math.Min(split, data.Length - at);
                        decoder.Append(data.AsSpan(at, size));
                        at += size;
                    }

                    if (decoder.HeaderRead)
                    {
                        band ??= Raster.Create<Rgba8>(decoder.Info.Width, 8, Context);
                        while (true)
                        {
                            int rows = decoder.ReadRows(band);
                            Add(pixels, band, rows);
                            if (decoder.Status == DecodeStatus.FrameEnded && decoder.NextFrame())
                            {
                                continue;
                            }

                            if (rows == 0 || decoder.Status != DecodeStatus.RowsAvailable)
                            {
                                break;
                            }
                        }
                    }

                    if (ended)
                    {
                        return ([.. pixels], null);
                    }
                }
            }
            finally
            {
                band?.Dispose();
            }
        }
        catch (EgamiException e)
        {
            return (null, e.Message);
        }
    }

    private static void Add(List<byte> pixels, Raster<Rgba8> band, int rows)
    {
        for (int y = 0; y < rows; y++)
        {
            pixels.AddRange(System.Runtime.InteropServices.MemoryMarshal.AsBytes(band.Row(y)));
        }
    }
}
