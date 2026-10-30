using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Psd;
using Egami.Pixels;
using Egami.Tests.Formats.Tiff;

namespace Egami.Tests.Formats.Psd;

/// <summary>
/// The PSD corpus (tools/make-psd-references.py): every file against the pixels it was written with, and against ImageMagick's
/// and Pillow's flattened image; layers against psd-tools; metadata against exiftool; damaged files in both recovery modes.
/// </summary>
public class PsdReaderTests(ITestOutputHelper output)
{
    internal static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "psd");
    internal static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "psd");

    private static readonly Lazy<JsonElement> Manifest = new(() =>
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json")));
        return document.RootElement.GetProperty("files").Clone();
    });

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Images).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesThePixelsWritten(string name)
    {
        if (!Manifest.Value.GetProperty(name).TryGetProperty("truth", out JsonElement layout))
        {
            return;
        }

        using Raster raster = Raster.Load(Path.Combine(Images, name), options: new ReaderOptions { Recovery = RecoveryMode.Strict });
        Assert.Equal(layout.GetString(), raster.GetType().GenericTypeArguments[0].Name);
        byte[] truth = Reference(name, "truth.raw")!;
        byte[] actual = TiffReference.Bytes(raster);
        Assert.Equal(truth.Length, actual.Length);
        double worst = TiffReference.MaxDifference(truth, actual, raster.Layout.ChannelType);
        double allowed = Manifest.Value.GetProperty(name).TryGetProperty("truthTolerance", out JsonElement tolerance) ? tolerance.GetDouble() : 0;
        Assert.True(worst <= allowed, $"{name} differs from the pixels written by {worst}.");
    }

    /// <summary>Files a reference tool reads differently, with the reason; Egami's own pixels are checked against the written ones.</summary>
    private static readonly Dictionary<string, string> KnownDifferences = new(StringComparer.Ordinal)
    {
        ["gray8-alpha.psd:magick"] = "ImageMagick divides the white out of RGB files only; it returns gray, CMYK and Lab ones flattened.",
        ["gray16-alpha.psb:magick"] = "As gray8-alpha.psd.",
        ["cmyk8-alpha.psd:magick"] = "As gray8-alpha.psd.",
        ["lab8-alpha.psd:magick"] = "As gray8-alpha.psd, and Lab as in lab8.psd.",
        ["lab8.psd:magick"] = "ImageMagick's raw RGBA of a Lab file holds the Lab values; Egami converts to sRGB, checked against the written pixels.",
        ["lab16.psd:magick"] = "As lab8.psd.",
        ["multichannel8.psd:magick"] = "ImageMagick reads the channels as CMYK inks; Egami loads the first as gray.",
        ["multichannel16.psd:magick"] = "As multichannel8.psd.",
        ["indexed-transparent.psd:magick"] = "ImageMagick ignores the transparent index (resource 1047).",
        ["no-composite.psd:magick"] = "ImageMagick flattens the layers itself; Egami returns the stored placeholder (design question 5).",
        ["layered-nocomposite.psd:magick"] = "As no-composite.psd.",
        ["layered-lab8.psd:magick"] = "As lab8.psd.",
        ["layered-lab8.psd:pillow"] = "As lab8.psd.",
        ["bitmap-raw.psd:pillow"] = "Pillow reads bitmap files inverted: 1 is black in PSD.",
        ["bitmap-rle.psd:pillow"] = "As bitmap-raw.psd.",
        ["gray8-alpha.psd:pillow"] = "Pillow returns the flattened image as stored, on white.",
        ["rgb8-alpha.psd:pillow"] = "As gray8-alpha.psd.",
        ["cmyk8-alpha.psd:pillow"] = "As gray8-alpha.psd.",
        ["lab8-alpha.psd:pillow"] = "As gray8-alpha.psd; and Pillow converts Lab to RGB numerically.",
        ["lab8.psd:pillow"] = "Pillow converts Lab to RGB numerically, not colorimetrically.",
        ["magick-rgba.psd:pillow"] = "ImageMagick writes the composite without flattening it onto white but with a negative layer count; Egami and ImageMagick divide the white out, Pillow does not.",
        ["rgb8-extra.psd:pillow"] = "Pillow takes the fourth channel for transparency; the positive layer count says it is an extra channel.",
        ["multichannel8.psd:pillow"] = "Pillow reads a multichannel file as something other than its first channel.",
        ["indexed-transparent.psd:pillow"] = "Pillow ignores the transparent index (resource 1047).",
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesImageMagick(string name)
    {
        if (KnownDifferences.ContainsKey(name + ":magick"))
        {
            return;
        }

        using Raster natural = Raster.Load(Path.Combine(Images, name));
        PsdDetails details = (PsdDetails)natural.Metadata.FormatDetails!;
        if (Reference(name, "magick.cmyk") is byte[] cmyk && natural.Layout.ChannelOrder == PixelChannelOrder.Cmyk)
        {
            byte[] actual = TiffReference.Bytes(natural);
            output.WriteLine($"{name}: CMYK peak {TiffReference.MaxDifference(cmyk, actual, natural.Layout.ChannelType)}");
            Assert.True(cmyk.AsSpan().SequenceEqual(actual), $"{name} differs from ImageMagick's inks.");
            return;
        }

        if (Reference(name, "magick.rgba") is byte[] rgba8)
        {
            using Raster<Rgba8> mine = Raster.Load<Rgba8>(Path.Combine(Images, name));
            (double peak, int count) = Peak8(rgba8, MemoryMarshal.AsBytes(Rows(mine)));
            output.WriteLine($"{name}: 8-bit peak {peak} in {count} samples");
            Assert.True(peak <= Tolerance(name, details), $"{name}: {peak} levels from ImageMagick.");
        }
        else if (Reference(name, "magick.rgba16") is byte[] rgba16)
        {
            using Raster<Rgba16> mine = Raster.Load<Rgba16>(Path.Combine(Images, name));
            (double peak, int count) = Peak16(MemoryMarshal.Cast<byte, ushort>(rgba16), MemoryMarshal.Cast<Rgba16, ushort>(Rows(mine)));
            output.WriteLine($"{name}: 16-bit peak {peak} in {count} samples");
            Assert.True(peak <= Tolerance(name, details), $"{name}: {peak} levels from ImageMagick.");
        }
        else if (Reference(name, "magick.f32") is byte[] f32)
        {
            using Raster<RgbaF32> mine = Raster.Load<RgbaF32>(Path.Combine(Images, name));
            ReadOnlySpan<float> theirs = MemoryMarshal.Cast<byte, float>(f32);
            ReadOnlySpan<float> ours = MemoryMarshal.Cast<RgbaF32, float>(Rows(mine));
            double worst = 0;
            for (int i = 0; i < ours.Length; i++)
            {
                if (i % 4 != 3 && theirs[i - (i % 4) + 3] == 0)
                {
                    continue;
                }

                // Dividing the white out of a stored float multiplies its rounding error by 1 / alpha; the bound grows with it.
                double alpha = theirs[i - (i % 4) + 3];
                double scale = i % 4 == 3 || alpha >= 1 ? 1 : 1 + (4 * Math.Pow(2, -24) / (1e-6 * alpha * Math.Max(1e-3, Math.Abs(theirs[i]))));
                worst = Math.Max(worst, Math.Abs(theirs[i] - ours[i]) / Math.Max(1e-3, Math.Abs(theirs[i])) / scale);
            }

            output.WriteLine($"{name}: float relative {worst}");
            Assert.True(worst <= 1e-6, $"{name}: {worst} relative from ImageMagick.");
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesPillow(string name)
    {
        if (Reference(name, "pillow.rgba") is not byte[] pillow || KnownDifferences.ContainsKey(name + ":pillow"))
        {
            return;
        }

        using Raster natural = Raster.Load(Path.Combine(Images, name));
        PsdDetails details = (PsdDetails)natural.Metadata.FormatDetails!;
        using Raster<Rgba8> mine = Raster.Load<Rgba8>(Path.Combine(Images, name));
        (double peak, int count) = Peak8(pillow, MemoryMarshal.AsBytes(Rows(mine)));
        output.WriteLine($"{name}: peak {peak} in {count} samples");
        Assert.True(peak <= Tolerance(name, details), $"{name}: {peak} levels from Pillow.");
    }

    /// <summary>Gets how far a reference may be: 0 for opaque files, 1 where the white is divided out of a transparent one.</summary>
    private static double Tolerance(string name, PsdDetails details) => details.ChannelCount > ColorChannels(details.ColorMode) ? 1 : 0;

    private static int ColorChannels(PsdColorMode mode) => mode switch
    {
        PsdColorMode.Rgb or PsdColorMode.Lab => 3,
        PsdColorMode.Cmyk => 4,
        _ => 1,
    };

    /// <summary>Returns the largest difference of 8-bit RGBA samples: alpha everywhere, color where alpha is not 0; and how many differ.</summary>
    private static (double Peak, int Count) Peak8(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int peak = 0;
        int count = 0;
        for (int i = 0; i < expected.Length; i += 4)
        {
            for (int c = 0; c < 4; c++)
            {
                if (c < 3 && expected[i + 3] == 0)
                {
                    continue;
                }

                int d = Math.Abs(expected[i + c] - actual[i + c]);
                peak = Math.Max(peak, d);
                count += d > 0 ? 1 : 0;
            }
        }

        return (peak, count);
    }

    private static (double Peak, int Count) Peak16(ReadOnlySpan<ushort> expected, ReadOnlySpan<ushort> actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int peak = 0;
        int count = 0;
        for (int i = 0; i < expected.Length; i += 4)
        {
            for (int c = 0; c < 4; c++)
            {
                if (c < 3 && expected[i + 3] == 0)
                {
                    continue;
                }

                int d = Math.Abs(expected[i + c] - actual[i + c]);
                peak = Math.Max(peak, d);
                count += d > 0 ? 1 : 0;
            }
        }

        return (peak, count);
    }

    internal static TPixel[] Rows<TPixel>(Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        TPixel[] all = new TPixel[raster.Width * raster.Height];
        for (int y = 0; y < raster.Height; y++)
        {
            raster.Row(y).CopyTo(all.AsSpan(y * raster.Width));
        }

        return all;
    }

    internal static byte[]? Reference(string name, string kind)
    {
        string path = Path.Combine(References, $"{name}.{kind}.gz");
        if (!File.Exists(path))
        {
            return null;
        }

        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream memory = new();
        gzip.CopyTo(memory);
        return memory.ToArray();
    }
}
