using System.IO.Compression;
using System.Text.Json;

using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>
/// AutoOrient against jpegtran's lossless turns of the same file (JPEG design, Testing), the planner's resize-first order, and the
/// EXIF changes every geometry operation makes.
/// </summary>
public class AutoOrientTests(ITestOutputHelper output)
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jpeg");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jpeg");

    public static TheoryData<int> Orientations() => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Orientations))]
    public void EveryOrientationMatchesJpegtran(int orientation)
    {
        string file = $"exif-orientation-{orientation}.jpg";
        using Raster<Rgb8> raster = Raster.Load<Rgb8>(File.ReadAllBytes(Path.Combine(Images, file)));
        using Raster<Rgb8> stored = raster.Copy();
        Assert.Equal((ImageOrientation)orientation, raster.Metadata.Exif!.Orientation);
        raster.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
        bool swaps = orientation >= 5;
        Assert.Equal(swaps ? (240, 320) : (320, 240), (raster.Width, raster.Height));
        Assert.Equal(ImageOrientation.Normal, raster.Metadata.Exif.Orientation);

        // Every pixel moved exactly where the orientation says.
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                (int sx, int sy) = Stored(orientation, x, y, 320, 240);
                Assert.Equal(stored.GetPixel(sx, sy), raster.GetPixel(x, y));
            }
        }

        // Against jpegtran's lossless turn, decoded: jpegtran turns the DCT blocks, and decoding is not symmetric under turns
        // (the IDCT's rounding and the chroma filter's bias favor one direction), so the two can differ by a few levels; they
        // must be as close as decode conformance asks.
        JsonElement entry = Entry(file);
        string reference = entry.TryGetProperty("upright", out JsonElement upright) ? upright.GetString()! : entry.GetProperty("reference").GetString()!;
        byte[] expected = Gunzip(reference);
        byte[] actual = new byte[expected.Length];
        raster.CopyPixelsTo(actual.AsSpan());
        int max = expected.Zip(actual, (a, b) => Math.Abs(a - b)).Max();
        double psnr = Psnr(expected, actual);
        output.WriteLine($"{file}: {psnr:F1} dB, max difference {max}");
        Assert.True(psnr >= 50 && max <= 3, $"{psnr:F1} dB, max {max}");

        // A second AutoOrient changes nothing.
        raster.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
        byte[] again = new byte[expected.Length];
        raster.CopyPixelsTo(again.AsSpan());
        Assert.Equal(actual, again);
    }

    /// <summary>Where upright pixel (x, y) is in an image stored with the orientation, of stored size width × height.</summary>
    private static (int X, int Y) Stored(int orientation, int x, int y, int width, int height) => orientation switch
    {
        2 => (width - 1 - x, y),
        3 => (width - 1 - x, height - 1 - y),
        4 => (x, height - 1 - y),
        5 => (y, x),
        6 => (y, height - 1 - x),
        7 => (width - 1 - y, height - 1 - x),
        8 => (width - 1 - y, x),
        _ => (x, y),
    };

    [Theory]
    [MemberData(nameof(Orientations))]
    public void ResizingFirstMatchesTurningFirst(int orientation)
    {
        using Raster<Rgb8> source = Raster.Load<Rgb8>(File.ReadAllBytes(Path.Combine(Images, $"exif-orientation-{orientation}.jpg")));
        foreach ((string name, Action<RasterOps> resize) in ((string, Action<RasterOps>)[])[
            ("fit", ops => ops.Fit(100, 90)),
            ("stretch", ops => ops.Resize(77, 51)),
            ("fill top", ops => ops.Fill(90, 60, Anchor.Top)),
            ("fill bottom right", ops => ops.Fill(50, 70, Anchor.BottomRight))])
        {
            using Raster<Rgb8> planned = source.Derive(ops => { ops.AutoOrient(); resize(ops); }, TestContext.Current.CancellationToken);
            using Raster<Rgb8> turned = source.Derive(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
            using Raster<Rgb8> separate = turned.Derive(resize, TestContext.Current.CancellationToken);
            Assert.Equal((separate.Width, separate.Height), (planned.Width, planned.Height));
            double psnr = Psnr(separate, planned);
            output.WriteLine($"{orientation} {name}: {psnr:F1} dB");
            Assert.True(psnr > 40, $"{orientation} {name}: {psnr:F1} dB");
            Assert.Equal(ImageOrientation.Normal, planned.Metadata.Exif!.Orientation);
        }
    }

    [Fact]
    public void WithoutExifNothingHappens()
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(30, 20, new Rgb8(1, 2, 3));
        raster.Apply(ops => ops.AutoOrient().Fit(15, 15), TestContext.Current.CancellationToken);
        Assert.Equal((15, 10), (raster.Width, raster.Height));
        Assert.Null(raster.Metadata.Exif);
    }

    [Fact]
    public void GeometryOperationsKeepExifInStep()
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(40, 30);
        ExifData exif = new();
        exif.Orientation = ImageOrientation.Rotate90;
        exif.Set(ExifKey.PixelXDimension, (ushort)40);
        exif.Set(ExifKey.PixelYDimension, 30u);
        raster.Metadata.Exif = exif;
        raster.Metadata.Resolution = new Resolution(300, 150, ResolutionUnit.PerInch);
        raster.Metadata.Exif = WithThumbnail(exif);
        Assert.False(raster.Metadata.Exif.Thumbnail.IsEmpty);

        // Derive leaves the source's metadata alone.
        using Raster<Rgb8> cropped = raster.Derive(ops => ops.Crop(0, 0, 20, 10), TestContext.Current.CancellationToken);
        Assert.Equal((20L, 10L), Dimensions(cropped));
        Assert.True(cropped.Metadata.Exif!.Thumbnail.IsEmpty);
        Assert.Equal(ImageOrientation.Rotate90, cropped.Metadata.Exif.Orientation);
        Assert.Equal((40L, 30L), Dimensions(raster));
        Assert.False(raster.Metadata.Exif.Thumbnail.IsEmpty);

        using Raster<Rgb8> padded = raster.Derive(ops => ops.Pad(50, 50), TestContext.Current.CancellationToken);
        Assert.Equal((50L, 50L), Dimensions(padded));
        Assert.False(padded.Metadata.Exif!.Thumbnail.IsEmpty);

        using Raster<Rgb8> resized = raster.Derive(ops => ops.Resize(80, 60), TestContext.Current.CancellationToken);
        Assert.Equal((80L, 60L), Dimensions(resized));
        Assert.True(resized.Metadata.Exif!.Thumbnail.IsEmpty);
        Assert.True(resized.Metadata.Exif.TryGet(ExifKey.PixelXDimension, out ExifEntry? x) && x.Type == ExifType.Short);

        using Raster<Rgb8> flipped = raster.Derive(ops => ops.Flip(FlipMode.Horizontal), TestContext.Current.CancellationToken);
        Assert.Equal(ImageOrientation.Normal, flipped.Metadata.Exif!.Orientation);

        using Raster<Rgb8> quarter = raster.Derive(ops => ops.Rotate(90), TestContext.Current.CancellationToken);
        Assert.Equal(ImageOrientation.Normal, quarter.Metadata.Exif!.Orientation);
        Assert.Equal((30L, 40L), Dimensions(quarter));
        Assert.Equal(new Resolution(150, 300, ResolutionUnit.PerInch), quarter.Metadata.Resolution);

        using Raster<Rgb8> angled = raster.Derive(ops => ops.Rotate(30), TestContext.Current.CancellationToken);
        Assert.Equal(ImageOrientation.Normal, angled.Metadata.Exif!.Orientation);
        Assert.Equal(((long)angled.Width, (long)angled.Height), Dimensions(angled));

        // AutoOrient by 6 turns clockwise, swaps the size and density, and leaves the tag Normal.
        raster.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
        Assert.Equal((30, 40), (raster.Width, raster.Height));
        Assert.Equal((30L, 40L), Dimensions(raster));
        Assert.Equal(new Resolution(150, 300, ResolutionUnit.PerInch), raster.Metadata.Resolution);
        Assert.Equal(ImageOrientation.Normal, raster.Metadata.Exif!.Orientation);
        Assert.Equal(ImageOrientation.Normal, ExifData.Parse(raster.Metadata.Exif.ToBytes().Span).Orientation);
    }

    [Fact]
    public void TransposeAndTransverseAreMirrorsAcrossTheDiagonals()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(37, 21);
        raster.ForEachRow((row, y) =>
        {
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Rgba8((byte)x, (byte)y, 7, 255);
            }
        });
        foreach ((ImageOrientation orientation, Func<int, int, (int, int)> source) in ((ImageOrientation, Func<int, int, (int, int)>)[])[
            (ImageOrientation.Transpose, (x, y) => (y, x)),
            (ImageOrientation.Transverse, (x, y) => (36 - y, 20 - x))])
        {
            ExifData exif = new() { Orientation = orientation };
            raster.Metadata.Exif = exif;
            using Raster<Rgba8> turned = raster.Derive(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
            Assert.Equal((21, 37), (turned.Width, turned.Height));
            for (int y = 0; y < turned.Height; y++)
            {
                for (int x = 0; x < turned.Width; x++)
                {
                    (int sx, int sy) = source(x, y);
                    Assert.Equal(raster.GetPixel(sx, sy), turned.GetPixel(x, y));
                }
            }
        }
    }

    private static (long Width, long Height) Dimensions(Raster raster)
    {
        ExifData exif = raster.Metadata.Exif!;
        return (exif.TryGet(ExifKey.PixelXDimension, out ExifEntry? x) ? x.AsInteger() : -1, exif.TryGet(ExifKey.PixelYDimension, out ExifEntry? y) ? y.AsInteger() : -1);
    }

    /// <summary>The block with an IFD1 holding a small "thumbnail", built by hand since thumbnails are read but not set.</summary>
    private static ExifData WithThumbnail(ExifData exif)
    {
        byte[] block = exif.ToBytes().ToArray();
        int count = BitConverter.ToUInt16(block, 8);
        int link = 10 + (12 * count);
        byte[] thumbnail = [0xFF, 0xD8, 0xFF, 0xD9];
        int ifd1 = block.Length;
        byte[] ifd = new byte[2 + 24 + 4];
        BitConverter.TryWriteBytes(ifd.AsSpan(0), (ushort)2);
        Entry(ifd.AsSpan(2), 0x0201, (uint)(ifd1 + ifd.Length));
        Entry(ifd.AsSpan(14), 0x0202, (uint)thumbnail.Length);
        byte[] grown = [.. block, .. ifd, .. thumbnail];
        BitConverter.TryWriteBytes(grown.AsSpan(link), (uint)ifd1);
        return ExifData.Parse(grown);

        static void Entry(Span<byte> slot, ushort tag, uint value)
        {
            BitConverter.TryWriteBytes(slot, tag);
            BitConverter.TryWriteBytes(slot[2..], (ushort)4);
            BitConverter.TryWriteBytes(slot[4..], 1u);
            BitConverter.TryWriteBytes(slot[8..], value);
        }
    }

    private static double Psnr(Raster<Rgb8> a, Raster<Rgb8> b)
    {
        byte[] x = new byte[a.Width * a.Height * 3];
        byte[] y = new byte[x.Length];
        a.CopyPixelsTo(x.AsSpan());
        b.CopyPixelsTo(y.AsSpan());
        return Psnr(x, y);
    }

    private static double Psnr(byte[] x, byte[] y)
    {
        double sum = 0;
        for (int i = 0; i < x.Length; i++)
        {
            sum += (x[i] - y[i]) * (x[i] - y[i]);
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / (sum / x.Length));
    }

    private static byte[] Gunzip(string name)
    {
        using GZipStream gzip = new(File.OpenRead(Path.Combine(References, name)), CompressionMode.Decompress);
        using MemoryStream buffer = new();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static JsonElement Entry(string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return json.RootElement.GetProperty("files").EnumerateArray().Single(e => e.GetProperty("file").GetString() == file).Clone();
    }
}
