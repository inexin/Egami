using System.Buffers.Binary;

namespace Egami.Formats.Psd;

/// <summary>
/// The adjustments of the specification's later kinds: exposure (expA), vibrance (vibA), posterize (post), threshold (thrs),
/// photo filter (phfl), channel mixer (mixr) and color balance (blnc); and the shared color helpers.
/// </summary>
internal abstract partial class PsdAdjustment
{
    /// <summary>The steps of the tables per-channel curves are looked up in.</summary>
    private const int TableSteps = 1024;

    /// <summary>
    /// Returns a color descriptor's color as straight sRGB-encoded red, green and blue, 0 to 1 (not clamped): RGB, gray, the
    /// plain CMYK formula, HSB, or Lab through D50.
    /// </summary>
    public static (double R, double G, double B) RgbOf(PsdDescriptor color)
    {
        switch (color.ClassId)
        {
            case "Grsc":
                {
                    double v = 1 - ((color.Number("Gry ") ?? 0) / 100);
                    return (v, v, v);
                }

            case "CMYC":
                {
                    double w = 1 - ((color.Number("Blck") ?? 0) / 100);
                    return ((1 - ((color.Number("Cyn ") ?? 0) / 100)) * w, (1 - ((color.Number("Mgnt") ?? 0) / 100)) * w, (1 - ((color.Number("Ylw ") ?? 0) / 100)) * w);
                }

            case "HSBC":
                return Hsb(color.Number("H   ") ?? 0, (color.Number("Strt") ?? 0) / 100, (color.Number("Brgh") ?? 0) / 100);
            case "LbCl":
                return PsdLab.ToSrgb(color.Number("Lmnc") ?? 0, color.Number("A   ") ?? 0, color.Number("B   ") ?? 0);
            default:
                return ((color.Number("Rd  ") ?? 0) / 255, (color.Number("Grn ") ?? 0) / 255, (color.Number("Bl  ") ?? color.Number("Bl ") ?? 0) / 255);
        }

        static (double, double, double) Hsb(double h, double s, double v)
        {
            double sector = ((h % 360) + 360) % 360 / 60;
            int i = (int)sector;
            double f = sector - i, p = v * (1 - s), q = v * (1 - (s * f)), t = v * (1 - (s * (1 - f)));
            return i switch
            {
                0 => (v, t, p),
                1 => (q, v, p),
                2 => (p, v, t),
                3 => (p, q, v),
                4 => (t, p, v),
                _ => (v, p, q),
            };
        }
    }

    /// <summary>The adjustments of the later kinds, or <see langword="null"/>.</summary>
    private static PsdAdjustment? OfMore(PsdLayerRecord record, EgamiContext context)
    {
        if (record.Blocks.TryGetValue("expA", out byte[]? data))
        {
            return Exposure.Read(data);
        }

        if (record.Blocks.TryGetValue("vibA", out data))
        {
            return PsdDescriptor.ReadVersioned(data) is PsdDescriptor d
                ? new Vibrance((float)(d.Number("vibrance") ?? 0) / 100, (float)(d.Number("Strt") ?? 0) / 100)
                : null;
        }

        if (record.Blocks.TryGetValue("post", out data))
        {
            return new Posterize(Math.Clamp((int)Short(data, 0), 2, 255));
        }

        if (record.Blocks.TryGetValue("thrs", out data))
        {
            return new Threshold(Math.Clamp((int)Short(data, 0), 1, 255));
        }

        if (record.Blocks.TryGetValue("phfl", out data))
        {
            return PhotoFilter.Read(data);
        }

        if (record.Blocks.TryGetValue("mixr", out data))
        {
            return ChannelMixer.Read(data);
        }

        if (record.Blocks.TryGetValue("blnc", out data))
        {
            return ColorBalance.Read(data);
        }

        return OfColors(record, context);
    }

    /// <summary>Returns the luminosity of a color by Photoshop's blend-mode weights.</summary>
    private static float Lum(float r, float g, float b) => (0.3f * r) + (0.59f * g) + (0.11f * b);

    /// <summary>Moves a color to luminosity <paramref name="l"/>, keeping its hue and bringing it back into 0..1 toward its gray.</summary>
    private static void SetLum(ref float r, ref float g, ref float b, float l)
    {
        float d = l - Lum(r, g, b);
        (r, g, b) = (r + d, g + d, b + d);
        l = Lum(r, g, b);
        float n = Math.Min(r, Math.Min(g, b)), x = Math.Max(r, Math.Max(g, b));
        if (n < 0 && l - n > 1e-6f)
        {
            (r, g, b) = (l + ((r - l) * l / (l - n)), l + ((g - l) * l / (l - n)), l + ((b - l) * l / (l - n)));
        }

        if (x > 1 && x - l > 1e-6f)
        {
            (r, g, b) = (l + ((r - l) * (1 - l) / (x - l)), l + ((g - l) * (1 - l) / (x - l)), l + ((b - l) * (1 - l) / (x - l)));
        }
    }

    /// <summary>Tabulates a function of 0..1 at <see cref="TableSteps"/> + 1 even points.</summary>
    private static float[] Tabulate(Func<double, double> f)
    {
        float[] table = new float[TableSteps + 1];
        for (int i = 0; i <= TableSteps; i++)
        {
            table[i] = (float)f((double)i / TableSteps);
        }

        return table;
    }

    /// <summary>Looks a value, clamped to 0..1, up in a table of <see cref="Tabulate"/>, between its points.</summary>
    private static float Lookup(float[] table, float v)
    {
        float x = Math.Clamp(v, 0, 1) * TableSteps;
        int i = Math.Min((int)x, TableSteps - 1);
        return table[i] + ((table[i + 1] - table[i]) * (x - i));
    }

    private static double ToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private static double FromLinear(double v) => v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;

    /// <summary>
    /// Exposure, in linear light: each channel times 2 to the exposure, plus the offset, raised to 1 / gamma. The block is a
    /// version and three big-endian floats.
    /// </summary>
    private sealed class Exposure(float[] table) : PsdAdjustment
    {
        public static Exposure? Read(ReadOnlySpan<byte> data)
        {
            if (data.Length < 14)
            {
                return null;
            }

            double exposure = BinaryPrimitives.ReadSingleBigEndian(data[2..]), offset = BinaryPrimitives.ReadSingleBigEndian(data[6..]);
            double gamma = BinaryPrimitives.ReadSingleBigEndian(data[10..]);
            if (!double.IsFinite(exposure) || !double.IsFinite(offset) || !double.IsFinite(gamma) || gamma <= 0)
            {
                return null;
            }

            double scale = Math.Pow(2, Math.Clamp(exposure, -20, 20));
            return new Exposure(Tabulate(v =>
            {
                double linear = (ToLinear(v) * scale) + offset;
                return linear <= 0 ? 0 : FromLinear(Math.Min(Math.Pow(linear, 1 / gamma), 1));
            }));
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            (r, g, b) = (Lookup(table, r), Lookup(table, g), Lookup(table, b));
        }
    }

    /// <summary>
    /// Vibrance and saturation: the color moves from its gray (its Rec. 601 luma) by the saturation, and further by the
    /// vibrance the less saturated it is.
    /// </summary>
    private sealed class Vibrance(float vibrance, float saturation) : PsdAdjustment
    {
        public override void Apply(ref float r, ref float g, ref float b)
        {
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            float s = max > 0 ? (max - min) / max : 0;
            float gray = (0.299f * r) + (0.587f * g) + (0.114f * b);
            float k = (1 + saturation) * (1 + (vibrance * (1 - s)));
            (r, g, b) = (Math.Clamp(gray + ((r - gray) * k), 0, 1), Math.Clamp(gray + ((g - gray) * k), 0, 1), Math.Clamp(gray + ((b - gray) * k), 0, 1));
        }
    }

    /// <summary>Posterize: each channel, on the 0..255 scale, falls into one of the levels, spread evenly from black to white.</summary>
    private sealed class Posterize(int levels) : PsdAdjustment
    {
        public override void Apply(ref float r, ref float g, ref float b)
        {
            (r, g, b) = (Level(r), Level(g), Level(b));
        }

        private float Level(float v)
        {
            int x = (int)MathF.Round(Math.Clamp(v, 0, 1) * 255);
            return Math.Min(x * levels / 256, levels - 1) / (float)(levels - 1);
        }
    }

    /// <summary>Threshold: white where the color's Rec. 601 luma, on the 0..255 scale, reaches the level, else black.</summary>
    private sealed class Threshold(int level) : PsdAdjustment
    {
        public override void Apply(ref float r, ref float g, ref float b)
        {
            float v = MathF.Round(((0.299f * r) + (0.587f * g) + (0.114f * b)) * 255) >= level ? 1 : 0;
            (r, g, b) = (v, v, v);
        }
    }

    /// <summary>
    /// Photo filter: the color multiplied by the filter's color, mixed in by the density, then, when luminosity is kept, moved
    /// back to its luminosity. Version 2 holds a color structure, the density in percent and the keep-luminosity flag.
    /// </summary>
    private sealed class PhotoFilter(float fr, float fg, float fb, float density, bool keep) : PsdAdjustment
    {
        public static PhotoFilter? Read(ReadOnlySpan<byte> data)
        {
            if (data.Length < 17 || Short(data, 0) != 2)
            {
                return null;
            }

            (double r, double g, double b) = RgbOf(PsdGradient.ColorOf(data.Slice(2, 10)));
            float density = Math.Clamp(BinaryPrimitives.ReadUInt32BigEndian(data[12..]) / 100f, 0, 1);
            return new PhotoFilter((float)Math.Clamp(r, 0, 1), (float)Math.Clamp(g, 0, 1), (float)Math.Clamp(b, 0, 1), density, data[16] != 0);
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            float l = Lum(r, g, b);
            r *= 1 - density + (density * fr);
            g *= 1 - density + (density * fg);
            b *= 1 - density + (density * fb);
            if (keep)
            {
                SetLum(ref r, ref g, ref b, l);
            }
        }
    }

    /// <summary>
    /// Channel mixer: each output channel a sum of the input channels by its row's percentages plus its constant; monochrome,
    /// the first row for all three. The block is a version, the monochrome flag and four rows of five shorts.
    /// </summary>
    private sealed class ChannelMixer(float[][] rows) : PsdAdjustment
    {
        public static ChannelMixer? Read(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4 + 30)
            {
                return null;
            }

            bool mono = Short(data, 2) != 0;
            float[][] rows = new float[3][];
            for (int i = 0; i < 3; i++)
            {
                int at = 4 + (10 * (mono ? 0 : i));
                rows[i] = [Short(data, at) / 100f, Short(data, at + 2) / 100f, Short(data, at + 4) / 100f, Short(data, at + 8) / 100f];
            }

            return new ChannelMixer(rows);
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            float ir = r, ig = g, ib = b;
            r = Row(rows[0], ir, ig, ib);
            g = Row(rows[1], ir, ig, ib);
            b = Row(rows[2], ir, ig, ib);
        }

        private static float Row(float[] row, float r, float g, float b) => Math.Clamp((row[0] * r) + (row[1] * g) + (row[2] * b) + row[3], 0, 1);
    }

    /// <summary>
    /// Color balance: for shadows, midtones and highlights, shifts along cyan–red, magenta–green and yellow–blue (−100 to 100),
    /// weighted by how much each tone range covers the channel's value; then, when luminosity is kept, back to its luminosity.
    /// </summary>
    private sealed class ColorBalance(float[] shifts, bool keep) : PsdAdjustment
    {
        public static ColorBalance? Read(ReadOnlySpan<byte> data)
        {
            if (data.Length < 19)
            {
                return null;
            }

            float[] shifts = new float[9];
            for (int i = 0; i < 9; i++)
            {
                shifts[i] = Math.Clamp(Short(data, 2 * i), -100, 100) / 100f;
            }

            return new ColorBalance(shifts, data[18] != 0);
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            float l = Lum(r, g, b);
            r = Channel(r, 0);
            g = Channel(g, 1);
            b = Channel(b, 2);
            if (keep)
            {
                SetLum(ref r, ref g, ref b, l);
            }
        }

        private float Channel(float v, int c)
        {
            float x = Math.Clamp(v, 0, 1);
            float shadows = (1 - x) * (1 - x), midtones = 2 * x * (1 - x), highlights = x * x;
            float shift = (shifts[c] * shadows) + (shifts[3 + c] * midtones) + (shifts[6 + c] * highlights);
            return Math.Clamp(x + (shift * 0.5f), 0, 1);
        }
    }
}
