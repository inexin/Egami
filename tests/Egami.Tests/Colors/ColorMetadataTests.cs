using System.Buffers.Binary;
using System.Text.Json;

using Egami.Colors;
using Egami.Formats.Bmp;
using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.1.2 task 7: CodePoints, Primaries and Hdr from the readers, and GetColorProfile's order.</summary>
public class ColorMetadataTests
{
    private static readonly string Suite = Path.Combine(AppContext.BaseDirectory, "images", "pngsuite");

    public static TheoryData<string> PillowFiles()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reference", "pngsuite", "color-tags.json")));
        return [.. json.RootElement.GetProperty("files").EnumerateObject().Select(p => p.Name)];
    }

    [Theory]
    [MemberData(nameof(PillowFiles))]
    public void PngTagsMatchPillow(string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reference", "pngsuite", "color-tags.json")));
        JsonElement pillow = json.RootElement.GetProperty("files").GetProperty(file);
        using Raster raster = Raster.Load(Path.Combine(Suite, file));
        RasterMetadata metadata = raster.Metadata;
        if (pillow.TryGetProperty("chromaticity", out JsonElement c))
        {
            double[] v = [.. c.EnumerateArray().Select(e => e.GetDouble())];
            ColorPrimaries p = metadata.Primaries!;
            Assert.Equal(v, [p.White.X, p.White.Y, p.Red.X, p.Red.Y, p.Green.X, p.Green.Y, p.Blue.X, p.Blue.Y], new Close(1e-5));
        }
        else
        {
            Assert.Null(metadata.Primaries);
        }

        // Pillow gives the file's gamma (0.45455); Egami keeps its reciprocal, the decoding exponent, where 1.0 did.
        if (pillow.TryGetProperty("gamma", out JsonElement g) && metadata.Gamma is double gamma)
        {
            Assert.Equal(1, gamma * g.GetDouble(), 6);
        }
    }

    [Fact]
    public void PngHdrChunksAreRead()
    {
        using Raster<Rgb16> raster = Raster.Create<Rgb16>(2, 2);
        PngDetails details = new() { CodingIndependentCodePoints = new byte[] { 9, 16, 0, 1 }, ColorType = PngColorType.Rgb, BitDepth = 16 };
        byte[] mdcv = new byte[24];
        ushort[] xy = [35400, 14600, 8500, 39850, 6550, 2300, 15635, 16450];   // BT.2020 primaries and D65, in 0.00002
        for (int i = 0; i < 8; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(mdcv.AsSpan(i * 2), xy[i]);
        }

        BinaryPrimitives.WriteUInt32BigEndian(mdcv.AsSpan(16), 10_000_000);   // 1000 cd/m²
        BinaryPrimitives.WriteUInt32BigEndian(mdcv.AsSpan(20), 50);           // 0.005 cd/m²
        byte[] clli = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(clli, 8_000_000);
        BinaryPrimitives.WriteUInt32BigEndian(clli.AsSpan(4), 4_000_000);
        details.OtherChunks.Add(new PngChunk("mDCV", mdcv, PngChunkPosition.BeforePalette));
        details.OtherChunks.Add(new PngChunk("cLLI", clli, PngChunkPosition.BeforePalette));
        raster.Metadata.FormatDetails = details;
        raster.Metadata.FormatName = "PNG";   // mDCV and cLLI are not safe to copy into another format
        using MemoryStream file = new();
        raster.Save(file, "png");

        using Raster back = Raster.Load(file.ToArray());
        Assert.Equal(new ColorCodePoints(9, 16, 0, true), back.Metadata.CodePoints);
        Assert.Same(ColorProfile.Rec2100Pq, back.Metadata.GetColorProfile());
        HdrMetadata hdr = back.Metadata.Hdr!;
        Assert.True(hdr.MasteringPrimaries!.IsCloseTo(ColorPrimaries.Rec2020));
        Assert.Equal(1000, hdr.MasteringMaxLuminance);
        Assert.Equal(0.005, hdr.MasteringMinLuminance!.Value, 9);
        Assert.Equal(800, hdr.MaxContentLightLevel);
        Assert.Equal(400, hdr.MaxFrameAverageLightLevel);
        Assert.Equal(ColorSpaceHint.Unknown, back.Metadata.ColorSpace);   // cICP 9/16 has no 1.0 hint, as before
    }

    [Fact]
    public void ProfileComesFromTheFileInOrder()
    {
        // Gamma alone; 45455 means sRGB, with no primaries or sRGB's.
        RasterMetadata m = new() { ColorSpace = ColorSpaceHint.Gamma, Gamma = 100_000.0 / 45_455 };
        Assert.Same(ColorProfile.Srgb, m.GetColorProfile());
        m.Primaries = ColorPrimaries.Rec709;
        Assert.Same(ColorProfile.Srgb, m.GetColorProfile());
        m.Primaries = ColorPrimaries.DisplayP3;
        ColorProfile p3Gamma = m.GetColorProfile();
        Assert.True(p3Gamma.Primaries!.IsCloseTo(ColorPrimaries.DisplayP3) && p3Gamma.Transfer!.Equals(TransferFunction.Gamma(100_000.0 / 45_455)));
        m.Gamma = 1.8;
        Assert.True(m.GetColorProfile().Transfer!.Equals(TransferFunction.Gamma(1.8)));

        // Primaries with no curve take sRGB's; linear keeps them.
        RasterMetadata primaries = new() { Primaries = ColorPrimaries.Rec2020 };
        Assert.True(primaries.GetColorProfile().IsEquivalentTo(ColorProfile.Create(ColorPrimaries.Rec2020, TransferFunction.Srgb)));
        primaries.ColorSpace = ColorSpaceHint.Linear;
        Assert.True(primaries.GetColorProfile().IsEquivalentTo(ColorProfile.Create(ColorPrimaries.Rec2020, TransferFunction.Linear)));

        // The ICC profile beats the tags; code points beat the ICC profile; a damaged profile counts as none.
        RasterMetadata icc = new() { ColorSpace = ColorSpaceHint.Srgb, IccProfile = ColorProfile.DisplayP3.ToIcc() };
        Assert.True(icc.GetColorProfile().IsEquivalentTo(ColorProfile.DisplayP3));
        icc.CodePoints = ColorCodePoints.Rec2100Hlg;
        Assert.Same(ColorProfile.Rec2100Hlg, icc.GetColorProfile());
        icc.CodePoints = new ColorCodePoints(2, 2, 0, true);   // unspecified: says nothing
        Assert.True(icc.GetColorProfile().IsEquivalentTo(ColorProfile.DisplayP3));
        icc.IccProfile = new byte[] { 1, 2, 3 };
        Assert.Same(ColorProfile.Srgb, icc.GetColorProfile());

        // A camera set to Adobe RGB marks it in EXIF.
        ExifData exif = new();
        exif.Set(ExifKey.ColorSpace, (ushort)0xFFFF);
        exif.Set(new ExifKey(0x0001, ExifIfd.Interoperability), "R03");
        Assert.Same(ColorProfile.AdobeRgb, new RasterMetadata { Exif = exif }.GetColorProfile());
        Assert.Same(ColorProfile.Srgb, new RasterMetadata().GetColorProfile());
    }

    [Fact]
    public void BmpCalibratedEndpointsAreRead()
    {
        // A 1×1, 24-bit BMP with a version 4 header, LCS_CALIBRATED_RGB, Adobe RGB's endpoints and gamma 2.2.
        byte[] bmp = new byte[14 + 108 + 4];
        "BM"u8.CopyTo(bmp);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 14 + 108);
        Span<byte> info = bmp.AsSpan(14);
        BinaryPrimitives.WriteUInt32LittleEndian(info, 108);
        BinaryPrimitives.WriteInt32LittleEndian(info[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(info[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(info[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(info[14..], 24);
        Matrix3d m = ColorProfile.AdobeRgb.ToXyz();
        for (int c = 0; c < 3; c++)
        {
            for (int r = 0; r < 3; r++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(info[(60 + (c * 12) + (r * 4))..], (int)Math.Round(m[r, c] * (1 << 30)));
            }

            BinaryPrimitives.WriteUInt32LittleEndian(info[(96 + (c * 4))..], (uint)(2.2 * 65536));
        }

        using Raster raster = Raster.Load(bmp);
        Assert.True(raster.Metadata.Primaries!.IsCloseTo(ColorPrimaries.AdobeRgb));
        Assert.Null(raster.Metadata.Gamma);   // 1.0's value is unchanged
        ColorProfile profile = raster.Metadata.GetColorProfile();
        Assert.True(profile.Primaries!.IsCloseTo(ColorPrimaries.AdobeRgb) && Math.Abs(profile.Transfer!.ToLinear(0.5) - Math.Pow(0.5, 2.2)) < 1e-4);
        Assert.IsType<BmpDetails>(raster.Metadata.FormatDetails);
    }

    [Fact]
    public void ConversionRecordsCodePoints()
    {
        using Raster<RgbaF32> raster = Raster.Create<RgbaF32>(2, 2, new RgbaF32(0.5f, 0.5f, 0.5f, 1));
        raster.Apply(ops => ops.ConvertColor(ColorProfile.Rec2100Pq), TestContext.Current.CancellationToken);
        Assert.Equal(ColorCodePoints.Rec2100Pq, raster.Metadata.CodePoints);
        Assert.Same(ColorProfile.Rec2100Pq, raster.Metadata.GetColorProfile());
        raster.Apply(ops => ops.ConvertColor(ColorProfile.Srgb), TestContext.Current.CancellationToken);
        Assert.Null(raster.Metadata.CodePoints);
        Assert.Equal(0.5f, raster.GetPixel(0, 0).R, 4);
    }

    private sealed class Close(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;

        public int GetHashCode(double obj) => 0;
    }
}
