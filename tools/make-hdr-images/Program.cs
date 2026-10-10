// Usage, from the repo root: dotnet run --project tools/make-hdr-images -- tests/images/hdr tests/images/ops/duck.png <comparison folder>
using Egami;
using Egami.Colors;
using Egami.Pixels;

// HDR test images for Phase 2.1.2 task 9: a ramp pattern and the duck lifted to HDR, as PQ and HLG 16-bit PNGs (Rec. 2020,
// cICP), and a comparison of their sRGB conversions with BT.2390, Reinhard and Clip.
string outDir = args[0], duckPath = args[1], compareDir = args[2];
Directory.CreateDirectory(outDir);
Directory.CreateDirectory(compareDir);

// Rows of ramps from 0 to 4000 cd/m² (linear in PQ signal) for gray and saturated Rec. 2020 colors.
(float R, float G, float B)[] hues = [(1, 1, 1), (1, 0, 0), (0, 1, 0), (0, 0, 1), (0, 1, 1), (1, 0, 1), (1, 1, 0), (1, 0.45f, 0.1f)];
using Raster<RgbaF32> pattern = Raster.Create<RgbaF32>(1024, hues.Length * 48);
for (int y = 0; y < pattern.Height; y++)
{
    (float r, float g, float b) = hues[y / 48];
    for (int x = 0; x < pattern.Width; x++)
    {
        double peak = TransferFunction.Pq.ToLinear(x / 1023.0 * TransferFunction.Pq.FromLinear(4000 / 203.0));
        pattern.SetPixel(x, y, new RgbaF32((float)(r * peak), (float)(g * peak), (float)(b * peak), 1));
    }
}

// The duck, its highlights stretched up to about 2,000 cd/m².
using Raster<RgbaF32> duck = Raster.Load<RgbaF32>(duckPath);
duck.Apply(o => o.Fit(320, 320));
duck.Apply(o => o.ConvertColor(ColorProfile.Create(ColorPrimaries.Rec2020, TransferFunction.Linear, "Linear Rec. 2020")));
for (int y = 0; y < duck.Height; y++)
{
    for (int x = 0; x < duck.Width; x++)
    {
        RgbaF32 p = duck.GetPixel(x, y);
        float max = MathF.Max(p.R, MathF.Max(p.G, p.B));
        float gain = 1 + (9 * max * max * max * max);
        duck.SetPixel(x, y, new RgbaF32(p.R * gain, p.G * gain, p.B * gain, 1));
    }
}

ColorProfile linear2020 = ColorProfile.Create(ColorPrimaries.Rec2020, TransferFunction.Linear, "Linear Rec. 2020");
foreach ((string name, Raster<RgbaF32> source) in new[] { ("pattern", pattern), ("duck", duck) })
{
    source.Metadata.IccProfile = default;
    source.Metadata.CodePoints = null;
    source.Metadata.Hdr = null;
    source.Metadata.IccProfile = linear2020.ToIcc();
    foreach ((string curve, ColorProfile hdr) in new[] { ("pq", ColorProfile.Rec2100Pq), ("hlg", ColorProfile.Rec2100Hlg) })
    {
        using Raster<Rgb16> encoded = source.ConvertColor<Rgb16>(hdr, new ColorTransformOptions { ToneMapping = ToneMapping.Clip });
        encoded.Metadata.Hdr = new HdrMetadata { MasteringPrimaries = ColorPrimaries.Rec2020, MasteringMaxLuminance = name == "pattern" ? 4000 : 2030, MasteringMinLuminance = 0.005, MaxContentLightLevel = name == "pattern" ? 4000 : 2030 };
        string path = Path.Combine(outDir, $"{name}-{curve}.png");
        encoded.Save(path);

        // The comparison: BT.2390, Reinhard and Clip, top to bottom.
        using Raster<Rgb16> loaded = Raster.Load<Rgb16>(path);
        int h = loaded.Height;
        using Raster<Rgb8> sheet = Raster.Create<Rgb8>(loaded.Width, h * 3);
        int row = 0;
        foreach (ToneMapping method in new[] { ToneMapping.Bt2390, ToneMapping.Reinhard, ToneMapping.Clip })
        {
            using Raster<Rgb8> sdr = loaded.ConvertColor<Rgb8>(ColorProfile.Srgb, new ColorTransformOptions { ToneMapping = method });
            for (int y = 0; y < h; y++)
            {
                sdr.Row(y).CopyTo(sheet.Row((row * h) + y));
            }

            row++;
        }

        sheet.Save(Path.Combine(compareDir, $"{name}-{curve}-to-srgb.png"));
    }
}

Console.WriteLine("Wrote HDR test images to " + outDir);
