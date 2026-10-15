using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>Phase 2.9.3 task 11: straight alpha, CMYK, spot colors and single extra channels, against the raw conformance channels.</summary>
public class JxlExtraChannelTests
{
    private static string? Case(string name) => JxlReferenceTests.Cache() is string cache ? Path.Combine(cache, "conformance", "testcases", name, "input.jxl") : null;

    private static JxlPlanes Raw(string path)
    {
        using FileStream stream = File.OpenRead(path);
        EgamiInput input = new(stream, EgamiContext.Default, TestContext.Current.CancellationToken);
        try
        {
            return JxlImageDecoder.DecodeChannels(input)[0];
        }
        finally
        {
            input.Dispose();
        }
    }

    [Fact]
    public void PremultipliedAlphaLoadsStraight()
    {
        if (Case("alpha_premultiplied") is not string path)
        {
            return;
        }

        JxlPlanes raw = Raw(path);
        using Raster<RgbaF32> loaded = Raster.Load<RgbaF32>(path);
        Assert.True(((JxlDetails)loaded.Metadata.FormatDetails!).ExtraChannels[0].AlphaPremultiplied);
        int checkedPixels = 0;
        for (int y = 0; y < raw.Height; y += 7)
        {
            for (int x = 0; x < raw.Width; x += 7)
            {
                int i = (y * raw.Width) + x;
                float a = raw.Planes[3][i];
                if (a < 0.05f)
                {
                    continue;
                }

                RgbaF32 p = loaded.Row(y)[x];
                Assert.Equal(Math.Clamp(raw.Planes[0][i] / a, 0, 1), Math.Clamp(p.R, 0, 1), 0.002);
                Assert.Equal(a, p.A, 0.002);
                checkedPixels++;
            }
        }

        Assert.True(checkedPixels > 100);
    }

    [Fact]
    public void CmykLoadsAsInk()
    {
        if (Case("cmyk_layers") is not string path)
        {
            return;
        }

        JxlPlanes raw = Raw(path);
        using Raster loaded = Raster.Load(path);
        Assert.Equal(Cmyk8.Layout, loaded.Layout);
        Raster<Cmyk8> cmyk = (Raster<Cmyk8>)loaded;
        JxlDetails details = (JxlDetails)loaded.Metadata.FormatDetails!;
        int black = details.ExtraChannels.ToList().FindIndex(e => e.Kind == JxlExtraChannelKind.Black);
        for (int y = 0; y < raw.Height; y += 13)
        {
            for (int x = 0; x < raw.Width; x += 13)
            {
                int i = (y * raw.Width) + x;
                Cmyk8 p = cmyk.Row(y)[x];
                Assert.Equal(MathF.Round((1 - Math.Clamp(raw.Planes[0][i], 0, 1)) * 255), p.C);
                Assert.Equal(MathF.Round((1 - Math.Clamp(raw.Planes[3 + black][i], 0, 1)) * 255), p.K);
            }
        }
    }

    [Fact]
    public void SpotColorsMixInUnlessTurnedOff()
    {
        if (Case("spot") is not string path)
        {
            return;
        }

        JxlPlanes raw = Raw(path);
        using Raster<RgbaF32> rendered = Raster.Load<RgbaF32>(path);
        using Raster<RgbaF32> plain = Raster.Load<RgbaF32>(path, options: new JxlReaderOptions { RenderSpotColors = false });
        JxlDetails details = (JxlDetails)rendered.Metadata.FormatDetails!;
        int[] spots = [.. Enumerable.Range(0, details.ExtraChannels.Count).Where(e => details.ExtraChannels[e].Kind == JxlExtraChannelKind.SpotColor)];
        Assert.Equal(2, spots.Length);
        bool differs = false;
        for (int y = 0; y < raw.Height; y += 9)
        {
            for (int x = 0; x < raw.Width; x += 9)
            {
                int i = (y * raw.Width) + x;
                float r = raw.Planes[0][i];
                foreach (int e in spots)
                {
                    IReadOnlyList<float> s = details.ExtraChannels[e].SpotColor!;
                    float mix = s[3] * raw.Planes[3 + e][i];
                    r = (mix * s[0]) + ((1 - mix) * r);
                }

                Assert.Equal(raw.Planes[0][i], plain.Row(y)[x].R, 0.0001);
                Assert.Equal(r, rendered.Row(y)[x].R, 0.0001);
                differs |= Math.Abs(r - raw.Planes[0][i]) > 0.01;
            }
        }

        Assert.True(differs);
    }

    [Fact]
    public void SpotColorsRenderLikeDjxl()
    {
        if (Case("spot") is not string path)
        {
            return;
        }

        string rendered = Path.Combine(JxlReferenceTests.Cache()!, "reference", "conformance", "spot", "djxl-spot.npy");
        if (!File.Exists(rendered))
        {
            return;
        }

        JxlReferenceTests.Npy djxl = JxlReferenceTests.Npy.Read(rendered);
        using Raster<RgbaF32> egami = Raster.Load<RgbaF32>(path);
        int channels = djxl.Shape[3];
        Assert.Equal((djxl.Shape[1], djxl.Shape[2]), (egami.Height, egami.Width));
        double peak = 0;
        for (int y = 0; y < egami.Height; y++)
        {
            ReadOnlySpan<RgbaF32> row = egami.Row(y);
            for (int x = 0; x < egami.Width; x++)
            {
                int i = ((y * egami.Width) + x) * channels;
                peak = Math.Max(peak, Math.Abs(row[x].R - djxl.Data[i]));
                peak = Math.Max(peak, Math.Abs(row[x].G - djxl.Data[i + 1]));
                peak = Math.Max(peak, Math.Abs(row[x].B - djxl.Data[i + 2]));
            }
        }

        Assert.InRange(peak, 0, 1e-4);
    }

    [Fact]
    public void ExtraChannelReadsAlone()
    {
        if (Case("spot") is not string path)
        {
            return;
        }

        JxlPlanes raw = Raw(path);
        using Raster<GrayF32> spot = JxlImage.ReadExtraChannel(path, 1);
        Assert.Equal((raw.Width, raw.Height), (spot.Width, spot.Height));
        for (int y = 0; y < raw.Height; y += 11)
        {
            for (int x = 0; x < raw.Width; x += 11)
            {
                Assert.Equal(raw.Planes[4][(y * raw.Width) + x], spot.Row(y)[x].L);
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => JxlImage.ReadExtraChannel(path, 3));
    }
}
