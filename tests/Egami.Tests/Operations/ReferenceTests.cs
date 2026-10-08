using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>
/// Compares resize, rotation, blur and sharpen with ImageMagick outputs made by tools/make-op-references.py. Each case runs the
/// matching Egami chain on the same input and requires a PSNR of at least 45 dB, over premultiplied RGB and alpha.
/// </summary>
public partial class ReferenceTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "ops");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "ops");

    public static TheoryData<string> Cases()
    {
        return [.. File.ReadLines(Path.Combine(References, "manifest.txt")).Where(l => !l.StartsWith('#')).Select(l => l.Split(' ')[0])];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesImageMagick(string name)
    {
        string line = File.ReadLines(Path.Combine(References, "manifest.txt")).Single(l => l.StartsWith(name + " ", StringComparison.Ordinal));
        string[] parts = line.Split(' ', 4);
        int width = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int height = int.Parse(parts[2], CultureInfo.InvariantCulture);
        string args = parts[3];
        string input = name[..name.IndexOf('.', StringComparison.Ordinal)];
        string operation = name[(input.Length + 1)..];

        using Raster<Rgba8> source = Raster.Load<Rgba8>(Path.Combine(Images, input + ".png"));
        using Raster<Rgba8> result = source.Derive(ops => Build(ops, operation, args, width, height), TestContext.Current.CancellationToken);
        Assert.Equal((width, height), (result.Width, result.Height));

        byte[] expected;
        using (GZipStream gzip = new(File.OpenRead(Path.Combine(References, name + ".rgba8.gz")), CompressionMode.Decompress))
        using (MemoryStream buffer = new())
        {
            gzip.CopyTo(buffer);
            expected = buffer.ToArray();
        }

        // Rotation references hold premultiplied color; see tools/make-op-references.py.
        double psnr = Psnr(MemoryMarshal.Cast<byte, Rgba8>(expected), result, premultipliedReference: operation.StartsWith("rotate", StringComparison.Ordinal));
        Assert.True(psnr >= 45, $"{name}: PSNR {psnr:F2} dB");
    }

    /// <summary>Builds the Egami chain for a case, taking the crop rectangle from the ImageMagick arguments when there is one.</summary>
    private static void Build(RasterOps ops, string operation, string args, int width, int height)
    {
        Match crop = CropPattern().Match(args);
        if (crop.Success)
        {
            ops.Crop(Int(crop, 3), Int(crop, 4), Int(crop, 1), Int(crop, 2));
        }

        switch (operation)
        {
            case "down-bicubic-linear":
                ops.Resize(width, height, ResampleFilter.Bicubic, ColorSpace.Linear);
                break;
            case string s when s.StartsWith("down-", StringComparison.Ordinal) || s.StartsWith("up-", StringComparison.Ordinal):
                ops.Resize(width, height, Enum.Parse<ResampleFilter>(s[(s.IndexOf('-', StringComparison.Ordinal) + 1)..], ignoreCase: true));
                break;
            case "rotate-15":
                ops.Rotate(15);
                break;
            case "rotate-n33":
                ops.Rotate(-33);
                break;
            case "blur-2":
                ops.Blur(2);
                break;
            case "blur-25":
                ops.Blur(25);
                break;
            case "sharpen":
                ops.Sharpen(1, 1);
                break;
            default:
                throw new ArgumentException($"No Egami chain for {operation}.");
        }
    }

    private static double Psnr(ReadOnlySpan<Rgba8> expected, Raster<Rgba8> actual, bool premultipliedReference)
    {
        Assert.Equal(actual.Width * actual.Height, expected.Length);
        double sum = 0;
        for (int y = 0; y < actual.Height; y++)
        {
            ReadOnlySpan<Rgba8> row = actual.Row(y);
            ReadOnlySpan<Rgba8> reference = expected.Slice(y * actual.Width, actual.Width);
            for (int x = 0; x < row.Length; x++)
            {
                // Premultiplied, so the color of pixels that are nearly invisible counts for little.
                Rgba8 a = row[x], e = reference[x];
                int ea = premultipliedReference ? 255 : e.A;
                sum += (Sq((a.R * a.A) - (e.R * ea)) / 65025.0) + (Sq((a.G * a.A) - (e.G * ea)) / 65025.0)
                    + (Sq((a.B * a.A) - (e.B * ea)) / 65025.0) + Sq(a.A - e.A);
            }
        }

        double mse = sum / (4.0 * expected.Length);
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);

        static double Sq(double v) => v * v;
    }

    private static int Int(Match match, int group)
    {
        return int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"-crop (\d+)x(\d+)\+(\d+)\+(\d+)")]
    private static partial Regex CropPattern();
}
