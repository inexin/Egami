using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.1.2 task 8: color tags written from the generic metadata, OmitSrgbProfile, and QOI's linear flag.</summary>
public class ColorWritingTests
{
    [Fact]
    public void PngWritesTagsFromAnyFormat()
    {
        // A raster that did not come from a PNG: its code points, primaries and HDR levels still reach the file.
        using Raster<Rgb16> raster = Raster.Create<Rgb16>(3, 2, new Rgb16(1000, 20000, 40000));
        raster.Metadata.CodePoints = ColorCodePoints.Rec2100Hlg;
        raster.Metadata.Primaries = ColorPrimaries.Rec2020;
        raster.Metadata.Hdr = new HdrMetadata
        {
            MasteringPrimaries = ColorPrimaries.DisplayP3,
            MasteringMaxLuminance = 1000,
            MasteringMinLuminance = 0.0001,
            MaxContentLightLevel = 650,
            MaxFrameAverageLightLevel = 120,
        };
        using MemoryStream file = new();
        raster.Save(file, "png");
        using Raster back = Raster.Load(file.ToArray());
        Assert.Equal(ColorCodePoints.Rec2100Hlg, back.Metadata.CodePoints);
        Assert.True(back.Metadata.Primaries!.IsCloseTo(ColorPrimaries.Rec2020, 1e-5));
        Assert.True(back.Metadata.Hdr!.MasteringPrimaries!.IsCloseTo(ColorPrimaries.DisplayP3, 2e-5));
        Assert.Equal((1000.0, 0.0001, 650.0, 120.0), (back.Metadata.Hdr.MasteringMaxLuminance!.Value, back.Metadata.Hdr.MasteringMinLuminance!.Value, back.Metadata.Hdr.MaxContentLightLevel!.Value, back.Metadata.Hdr.MaxFrameAverageLightLevel!.Value));
        Assert.Same(ColorProfile.Rec2100Hlg, back.Metadata.GetColorProfile());

        // Written again, each color chunk appears once: kept chunks are not doubled by the generic ones.
        using MemoryStream again = new();
        back.Save(again, "png");
        string text = System.Text.Encoding.Latin1.GetString(again.ToArray());
        foreach (string chunk in new[] { "cICP", "cHRM", "mDCV", "cLLI" })
        {
            Assert.Equal(1, text.Split(chunk).Length - 1);
        }

        PngDetails details = (PngDetails)back.Metadata.FormatDetails!;
        Assert.Equal(2, details.OtherChunks.Count(c => c.Type is "mDCV" or "cLLI"));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("bmp")]
    [InlineData("gif")]
    public void OmitSrgbProfileDropsOnlySrgb(string format)
    {
        foreach ((ColorProfile profile, bool dropped) in new[] { (ColorProfile.Srgb, true), (ColorProfile.FromIcc(IccReferences.Bytes("compact/sRGB-v2-magic.icc")), true), (ColorProfile.DisplayP3, false) })
        {
            using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4, new Rgba8(10, 120, 230, 255));
            raster.Metadata.IccProfile = profile.ToIcc();
            using MemoryStream kept = new(), omitted = new();
            raster.Save(kept, format);
            raster.Save(omitted, format, new WriterOptions { OmitSrgbProfile = true });
            using Raster a = Raster.Load(kept.ToArray()), b = Raster.Load(omitted.ToArray());
            Assert.False(a.Metadata.IccProfile.IsEmpty);
            Assert.Equal(dropped, b.Metadata.IccProfile.IsEmpty);
            Assert.True(b.Metadata.GetColorProfile().IsEquivalentTo(profile));
            if (dropped && format == "png")
            {
                Assert.Equal(ColorSpaceHint.Srgb, b.Metadata.ColorSpace);   // the sRGB chunk in its place
            }
        }
    }

    [Fact]
    public void QoiMarksLinearProfilesLinear()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(2, 2);
        raster.Metadata.IccProfile = ColorProfile.LinearSrgb.ToIcc();
        using MemoryStream file = new();
        raster.Save(file, "qoi");
        Assert.Equal(1, file.ToArray()[13]);
        using Raster back = Raster.Load(file.ToArray());
        Assert.Equal(ColorSpaceHint.Linear, back.Metadata.ColorSpace);
    }
}
