using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats.Pnm;
using Egami.Pixels;

namespace Egami.Tests.Formats;

/// <summary>
/// Phase 2.1.1 references from tools/make-float-cmyk-references.py: PFM read and written bit for bit as ImageMagick and Netpbm
/// do, and CMYK JPEG inks identical to libjpeg-turbo's TJPF_CMYK decode.
/// </summary>
public class FloatAndCmykReferenceTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference");

    public static TheoryData<string> PfmFiles() =>
        [.. Directory.GetFiles(Path.Combine(Images, "pfm"), "*.pfm").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal)];

    /// <summary>
    /// ImageMagick multiplies samples by a scale other than 1, while Netpbm's pfmtopam divides by it (rgb-big.pfm, scale 2.5:
    /// 0.25 becomes 26 of 255). As the tools disagree, Egami keeps the stored floats and reports the scale in PnmDetails.Scale;
    /// for the usual scale of 1, which both tools write, it reads exactly what ImageMagick reads.
    /// </summary>
    [Theory]
    [MemberData(nameof(PfmFiles))]
    public void EveryPfmReadsAsImageMagickReadsIt(string name)
    {
        string path = Path.Combine(Images, "pfm", name + ".pfm");
        float[] magick = Floats(Gunzip(Path.Combine(References, "pfm", name + ".magick.f32.gz")));
        float scale = (float)((PnmDetails)Raster.Load(path).Metadata.FormatDetails!).Scale!.Value;
        float[] ours = LoadFloats(path);
        if (scale == 1f)
        {
            Assert.Equal(magick, ours);
            return;
        }

        // ImageMagick's own multiply is not exact (8.90625 comes out 8.90625095), so a scaled file is compared within 1e-6.
        Assert.Equal(magick.Length, ours.Length);
        for (int i = 0; i < ours.Length; i++)
        {
            Assert.True(Math.Abs(magick[i] - (ours[i] * scale)) <= 1e-6 * Math.Max(1, Math.Abs(magick[i])), $"sample {i}: {ours[i]} × {scale} against {magick[i]}");
        }
    }

    [Theory]
    [InlineData("magick-rgb")]
    [InlineData("magick-gray")]
    public void ImageMagickFilesHoldTheirSourceFloatsAndEgamiWritesTheSameBytes(string name)
    {
        JsonElement source = Source(name);
        float[] floats = [.. source.GetProperty("floats").EnumerateArray().Select(e => (float)e.GetDouble())];
        string path = Path.Combine(Images, "pfm", name + ".pfm");
        Assert.Equal(floats, LoadFloats(path));

        using Raster raster = Raster.Load(path);
        using MemoryStream written = new();
        raster.Save(written, PnmFormat.Info);
        Assert.Equal(File.ReadAllBytes(path), written.ToArray());
    }

    /// <summary>
    /// pamtopfm writes sample × (1 / maxval) as floats, with "-1.000000" for the scale where Egami and ImageMagick write "-1.0"; the
    /// floats after the header are compared byte for byte.
    /// </summary>
    [Theory]
    [InlineData("netpbm-rgb")]
    [InlineData("netpbm-gray")]
    public void NetpbmFilesHoldSampleOverMaxvalAndEgamiWritesTheSameFloats(string name)
    {
        JsonElement source = Source(name);
        int max = source.GetProperty("maxval").GetInt32();
        float[] expected = [.. source.GetProperty("samples").EnumerateArray().Select(e => e.GetInt32() * (1f / max))];
        string path = Path.Combine(Images, "pfm", name + ".pfm");
        Assert.Equal(expected, LoadFloats(path));

        using Raster raster = Raster.Load(path);
        using MemoryStream written = new();
        raster.Save(written, PnmFormat.Info);
        byte[] theirs = File.ReadAllBytes(path), ours = written.ToArray();
        Assert.Equal(theirs[HeaderLength(theirs)..], ours[HeaderLength(ours)..]);
    }

    /// <summary>TurboJPEG returns Adobe CMYK as stored, inverted; Egami's inks are 255 minus those samples, for CMYK and YCCK alike.</summary>
    [Theory]
    [InlineData("duck-cmyk")]
    [InlineData("duck-ycck")]
    public void CmykJpegInksMatchLibjpegTurbo(string name)
    {
        byte[] turbo = Gunzip(Path.Combine(References, "jpeg", name + ".turbo-cmyk.gz"));
        using Raster<Cmyk8> inks = Raster.Load<Cmyk8>(Path.Combine(Images, "jpeg", name + ".jpg"));
        byte[] ours = new byte[inks.Width * inks.Height * 4];
        for (int y = 0; y < inks.Height; y++)
        {
            MemoryMarshal.AsBytes(inks.Row(y)).CopyTo(ours.AsSpan(y * inks.Width * 4));
        }

        Assert.Equal(turbo.Length, ours.Length);
        int differ = 0;
        for (int i = 0; i < ours.Length; i++)
        {
            differ += ours[i] == 255 - turbo[i] ? 0 : 1;
        }

        Assert.Equal(0, differ);
    }

    private static float[] LoadFloats(string path)
    {
        using Raster raster = Raster.Load(path);
        int channels = raster is Raster<RgbF32> ? 3 : 1;
        float[] values = new float[raster.Width * raster.Height * channels];
        for (int y = 0; y < raster.Height; y++)
        {
            Span<float> target = values.AsSpan(y * raster.Width * channels, raster.Width * channels);
            if (raster is Raster<RgbF32> color)
            {
                MemoryMarshal.Cast<RgbF32, float>(color.Row(y)).CopyTo(target);
            }
            else
            {
                MemoryMarshal.Cast<GrayF32, float>(((Raster<GrayF32>)raster).Row(y)).CopyTo(target);
            }
        }

        return values;
    }

    private static JsonElement Source(string name)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "pfm", "pfm-source.json")));
        return document.RootElement.GetProperty(name).Clone();
    }

    private static float[] Floats(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();

    private static byte[] Gunzip(string path)
    {
        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream memory = new();
        gzip.CopyTo(memory);
        return memory.ToArray();
    }

    private static int HeaderLength(byte[] file)
    {
        int lines = 0;
        for (int i = 0; i < file.Length; i++)
        {
            if (file[i] == '\n' && ++lines == 3)
            {
                return i + 1;
            }
        }

        throw new InvalidDataException();
    }
}
