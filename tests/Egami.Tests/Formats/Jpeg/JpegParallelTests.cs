using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Parallel decoding (the restart-interval split of sequential scans and the split last pass of buffered files) gives the pixels
/// and warnings one thread gives, at every core count, for clean and damaged files alike.
/// </summary>
public class JpegParallelTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jpeg");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Files()
    {
        TheoryData<string> data = [];
        foreach (string manifest in (string[])["manifest.json", Path.Combine("depth", "manifest.json")])
        {
            string folder = Path.GetDirectoryName(manifest)!;
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reference", "jpeg", manifest)));
            foreach (JsonElement e in json.RootElement.GetProperty("files").EnumerateArray())
            {
                if (!e.TryGetProperty("unsupported", out _) && !(e.TryGetProperty("lossless", out JsonElement l) && l.GetBoolean()))
                {
                    data.Add(Path.Combine(folder, e.GetProperty("file").GetString()!));
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryCoreCountDecodesTheSame(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, file));
        (byte[] serial, IReadOnlyList<string> warnings) = Decode(data, 1, ReaderOptions.Default);
        foreach (int cores in (int[])[2, 3, 4, 8])
        {
            (byte[] parallel, IReadOnlyList<string> parallelWarnings) = Decode(data, cores, ReaderOptions.Default);
            Assert.Equal(serial, parallel);
            Assert.Equal(warnings, parallelWarnings);
        }
    }

    [Theory]
    [InlineData("duck-restart-rows.jpg")]
    [InlineData("duck-restart-blocks.jpg")]
    [InlineData("depth/arith-444-restart.jpg")]
    public void DamagedIntervalsDecodeAsOneThreadDoes(string file)
    {
        byte[] clean = File.ReadAllBytes(Path.Combine(Images, file));
        int scan = ScanStart(clean);
        Random random = new(file.Length);
        for (int trial = 0; trial < 12; trial++)
        {
            byte[] data = (byte[])clean.Clone();
            switch (trial % 4)
            {
                case 0:
                    // A byte flipped inside the data, the markers intact.
                    data[scan + random.Next(data.Length - scan - 2)] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    // The file cut short.
                    data = data[..(scan + random.Next(data.Length - scan))];
                    break;
                case 2:
                    // A restart marker renumbered.
                    int marker = FindMarker(data, scan, random);
                    if (marker > 0)
                    {
                        data[marker] = (byte)(0xD0 + ((data[marker] + 3) & 7));
                    }

                    break;
                default:
                    // A restart marker removed.
                    int at = FindMarker(data, scan, random);
                    if (at > 0)
                    {
                        data = [.. data[..(at - 1)], .. data[(at + 1)..]];
                    }

                    break;
            }

            foreach (RecoveryMode mode in (RecoveryMode[])[RecoveryMode.Lenient, RecoveryMode.Strict])
            {
                ReaderOptions options = new() { Recovery = mode };
                Exception? serialError = Record.Exception(() => Decode(data, 1, options));
                foreach (int cores in (int[])[2, 4])
                {
                    if (serialError is not null)
                    {
                        Assert.Equal(serialError.GetType(), Record.Exception(() => Decode(data, cores, options))?.GetType());
                        continue;
                    }

                    (byte[] serial, IReadOnlyList<string> warnings) = Decode(data, 1, options);
                    (byte[] parallel, IReadOnlyList<string> parallelWarnings) = Decode(data, cores, options);
                    Assert.Equal(serial, parallel);
                    Assert.Equal(warnings, parallelWarnings);
                }
            }
        }
    }

    [Fact]
    public void IntervalsAreFoundOnlyInOrder()
    {
        // Data, RST0, data with a stuffed byte, fill bytes then RST1, data, then EOI.
        byte[] scan = [1, 2, 0xFF, 0xD0, 3, 0xFF, 0x00, 4, 0xFF, 0xFF, 0xD1, 5, 0xFF, 0xD9];
        Assert.Equal([0, 4, 11], JpegDecoder<Rgb8>.FindIntervals(scan, 3)!);
        Assert.Null(JpegDecoder<Rgb8>.FindIntervals(scan, 2));
        Assert.Null(JpegDecoder<Rgb8>.FindIntervals(scan, 4));

        byte[] swapped = [1, 0xFF, 0xD1, 2, 0xFF, 0xD0, 3, 0xFF, 0xD9];
        Assert.Null(JpegDecoder<Rgb8>.FindIntervals(swapped, 3));

        // The numbers wrap after RST7.
        List<byte> many = [];
        for (int i = 0; i < 10; i++)
        {
            many.AddRange([7, 0xFF, (byte)(0xD0 + (i & 7))]);
        }

        many.Add(9);
        int[] starts = JpegDecoder<Rgb8>.FindIntervals([.. many], 11)!;
        Assert.Equal(Enumerable.Range(0, 11).Select(i => 3 * i), starts);

        // A file that ends inside the data without any marker after the scan.
        Assert.Equal([0, 4], JpegDecoder<Rgb8>.FindIntervals([1, 2, 0xFF, 0xD0, 3], 2)!);
        Assert.Null(JpegDecoder<Rgb8>.FindIntervals([1, 2, 0xFF], 1));
    }

    private static (byte[] Pixels, IReadOnlyList<string> Warnings) Decode(byte[] data, int cores, ReaderOptions options)
    {
        using MemoryStream stream = new(data);
        EgamiInput input = new(stream, EgamiContext.Default with { MaxParallelism = cores }, Ct);
        JpegHeader header = JpegSegmentReader.ReadHeader(input, options);
        using Raster<Rgba16> raster = JpegReader.Decode<Rgba16>(input, header, options, forcePipeline: cores > 1);
        byte[] pixels = new byte[raster.Width * raster.Height * 8];
        for (int y = 0; y < raster.Height; y++)
        {
            MemoryMarshal.AsBytes(raster.Row(y)).CopyTo(pixels.AsSpan(y * raster.Width * 8));
        }

        return (pixels, [.. raster.Metadata.Warnings]);
    }

    private static int ScanStart(byte[] data)
    {
        int at = 2;
        while (data[at + 1] != 0xDA)
        {
            at += 2 + ((data[at + 2] << 8) | data[at + 3]);
        }

        return at + 2 + ((data[at + 2] << 8) | data[at + 3]);
    }

    /// <summary>Returns the position of the number byte of a random restart marker after <paramref name="from"/>, or 0 if none.</summary>
    private static int FindMarker(byte[] data, int from, Random random)
    {
        List<int> markers = [];
        for (int i = from; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] is >= 0xD0 and <= 0xD7)
            {
                markers.Add(i + 1);
            }
        }

        return markers.Count == 0 ? 0 : markers[random.Next(markers.Count)];
    }
}
