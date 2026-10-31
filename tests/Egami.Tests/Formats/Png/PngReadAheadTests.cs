using System.Text.RegularExpressions;

using Egami.Formats;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Tests.Formats.Png;

/// <summary>
/// Step 2.4.8: a PNG of a megabyte or more inflates on another thread, ahead of the rows. Clean, cut short, damaged and
/// truncated image data must decode to the same pixels, warnings and errors as on one thread, where nothing reads ahead. Only the
/// byte offset an error names may differ: the input has been read further by the time a row turns out to be broken.
/// </summary>
public class PngReadAheadTests
{
    private const int Width = 700;
    private const int Height = 500;

    private static readonly EgamiContext OneThread = EgamiContext.Default with { MaxParallelism = 1 };
    private static readonly EgamiContext Threads = EgamiContext.Default with { MaxParallelism = 8 };

    public static TheoryData<string, bool> Cases()
    {
        TheoryData<string, bool> data = [];
        foreach (string kind in (string[])["clean", "short", "damaged", "adler", "truncated"])
        {
            data.Add(kind, false);
            data.Add(kind, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodesAsOneThreadDoes(string kind, bool interlaced)
    {
        byte[] file = File(kind, interlaced);
        Assert.True((long)((Width * 4) + 1) * Height >= 1 << 20);

        (Rgba8[]? one, string[] oneWarnings, string? oneError) = Decode(file, OneThread, RecoveryMode.Lenient);
        (Rgba8[]? many, string[] manyWarnings, string? manyError) = Decode(file, Threads, RecoveryMode.Lenient);
        Assert.Equal(oneError, manyError);
        Assert.Equal(oneWarnings, manyWarnings);
        Assert.Equal(one, many);
        Assert.Equal(kind == "clean", oneWarnings.Length == 0 && oneError is null);

        (_, _, string? strictOne) = Decode(file, OneThread, RecoveryMode.Strict);
        (_, _, string? strictMany) = Decode(file, Threads, RecoveryMode.Strict);
        Assert.Equal(strictOne, strictMany);
        Assert.Equal(kind == "clean", strictOne is null);
    }

    [Fact]
    public void StopsCleanlyWhenTheReaderGivesUpEarly()
    {
        // A strict decode of data damaged near the start stops long before the worker has inflated everything it could.
        byte[] file = File("damaged", interlaced: false, at: 0.05);
        for (int i = 0; i < 20; i++)
        {
            Assert.Throws<CorruptDataException>(() => Raster.Load(file, Threads, new ReaderOptions { Recovery = RecoveryMode.Strict }));
        }
    }

    private static (Rgba8[]? Pixels, string[] Warnings, string? Error) Decode(byte[] file, EgamiContext context, RecoveryMode recovery)
    {
        try
        {
            using Raster<Rgba8> raster = Raster.Load<Rgba8>(file, context, new ReaderOptions { Recovery = recovery });
            Rgba8[] pixels = new Rgba8[raster.Width * raster.Height];
            raster.CopyPixelsTo(pixels);
            return (pixels, [.. raster.Metadata.Warnings.Select(w => w.Message)], null);
        }
        catch (CorruptDataException e)
        {
            return (null, [], Regex.Replace(e.Message, "at byte [0-9]+", "at byte n"));
        }
    }

    private static byte[] File(string kind, bool interlaced, double at = 0.6)
    {
        PngBuilder builder = PngReaderTests.Build(PngColorType.Rgba, 8, interlaced, false, Width, Height, seed: 31);
        builder.IdatChunks = 7;
        if (kind == "short")
        {
            byte[] raw = builder.RawImageData();
            builder.EditCompressed = _ => PngBuilder.Compress(raw[..(int)(raw.Length * at)]);
        }
        else if (kind == "damaged")
        {
            builder.EditCompressed = data =>
            {
                data.AsSpan((int)(data.Length * at), 64).Fill(0xFF);
                return data;
            };
        }
        else if (kind == "adler")
        {
            builder.EditCompressed = data =>
            {
                data[^1] ^= 0xFF;
                return data;
            };
        }

        byte[] file = builder.Build();
        return kind == "truncated" ? file[..(int)(file.Length * at)] : file;
    }
}
