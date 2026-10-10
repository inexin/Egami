using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Formats.Jpeg;
using Egami.Formats.Pnm;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class PixelTypes
{
    [Fact]
    public void ChoosingAType()
    {
        using SampleFiles files = new();

        #region choose-type
        // Load straight into the type you will work in: the decoder converts row by row, with no full-size copy in between.
        using Raster<Rgb16> deep = Raster.Load<Rgb16>("duck.png");        // 16 bits a channel, no alpha
        using Raster<Alpha8> mask = Raster.Load<Alpha8>("duck.png");      // only the alpha channel
        using Raster<RgbaF16> half = Raster.Load<RgbaF16>("duck.png");    // half floats, room for HDR values

        // Or convert a raster you have; conversions between integer types are exact wherever the target can hold the value.
        using Raster<Bgr565> display = deep.As<Bgr565>();
        #endregion

        Assert.Equal(deep.Width, display.Width);
        Assert.True(mask.Row(0)[0].A >= 0 && (float)half.Row(0)[0].A <= 1f);
    }

    [Fact]
    public void CmykInks()
    {
        using SampleFiles files = new();

        #region cmyk-inks
        // A CMYK JPEG loads as RGB by the plain formula, as before; ask for Cmyk8 to get the file's ink amounts instead.
        using Raster<Cmyk8> inks = Raster.Load<Cmyk8>("duck-cmyk.jpg");
        Cmyk8 corner = inks.Row(0)[0];
        Console.WriteLine($"Cyan {corner.C}, magenta {corner.M}, yellow {corner.Y}, black {corner.K} (0 = no ink)");

        // Resizing filters the four inks, so black stays in the black channel; color adjustments go through RGB instead.
        inks.Apply(ops => ops.Fit(160, 160));
        #endregion

        Assert.Equal(JpegColorSpace.Cmyk, ((JpegDetails)Raster.Load("duck-cmyk.jpg").Metadata.FormatDetails!).ColorSpace);
        Assert.Equal(160, Math.Max(inks.Width, inks.Height));
    }

    [Fact]
    public void PremultipliedSurface()
    {
        using SampleFiles files = new();
        byte[] surfaceMemory = new byte[320 * 240 * 4];

        #region premultiplied-surface
        // A Windows or GPU surface is premultiplied BGRA: wrap its memory as PremulBgra8 and draw into it directly. Drawing and
        // resizing run on the stored values, with no multiply and divide per pixel; conversions still take straight colors.
        using Raster<PremulBgra8> surface = Raster.Wrap<PremulBgra8>(surfaceMemory, 320, 240, byteStride: 320 * 4);
        using Raster<Rgba8> logo = Raster.Load<Rgba8>("duck.png");
        logo.Apply(ops => ops.Fit(120, 120));
        surface.Apply(ops => ops.FillColor(new Rgba8(30, 60, 90, 255)).DrawImage(logo, new Point(20, 20), opacity: 0.8f));
        #endregion

        Assert.Equal(255, surface.Row(0)[0].A);
    }

    [Fact]
    public void HighDynamicRange()
    {
        using SampleFiles files = new();

        #region hdr
        // Half and float types keep values above 1 through conversions and geometry; filters and color adjustments clip to 0..1.
        using Raster<RgbaF16> light = Raster.Create<RgbaF16>(64, 64, RgbaF16.FromScaled(new Vector4(4f, 2f, 0.5f, 1f)));
        light.Apply(ops => ops.FlipHorizontal().Pad(80, 80, Rgba8.Black));

        // PFM keeps floats as they are: save as RGB floats, in linear light by convention.
        light.Save("light.pfm");
        using Raster<RgbF32> back = Raster.Load<RgbF32>("light.pfm");
        Console.WriteLine($"Red is still {back.Row(40)[40].R} after the round trip");
        #endregion

        Assert.Equal(PnmKind.FloatMap, ((PnmDetails)Raster.Load("light.pfm").Metadata.FormatDetails!).Kind);
        Assert.Equal(4f, back.Row(40)[40].R);
    }

    [Fact]
    public void PackedFramebuffer()
    {
        using SampleFiles files = new();
        ushort[] framebuffer = new ushort[240 * 135];

        #region packed-framebuffer
        // A 16-bit display framebuffer: wrap it as Bgr565 and draw a photo into it; each level rounds to the nearest of 32 or 64.
        using Raster<Bgr565> screen = Raster.Wrap<Bgr565>(MemoryMarshal.Cast<ushort, Bgr565>(framebuffer.AsSpan()).ToArray(), 240, 135);
        using Raster<Rgba8> photo = Raster.Load<Rgba8>("egret.png");
        photo.Apply(ops => ops.Cover(240, 135));
        screen.Apply(ops => ops.DrawImage(photo, new Point(0, 0)));
        Bgr565 pixel = screen.Row(60)[120];
        Console.WriteLine($"Levels: red {pixel.R} of 31, green {pixel.G} of 63, blue {pixel.B} of 31");
        #endregion

        Assert.InRange(pixel.G, 0, 63);
    }
}
