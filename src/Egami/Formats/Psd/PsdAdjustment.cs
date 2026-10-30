using System.Buffers.Binary;

namespace Egami.Formats.Psd;

/// <summary>
/// An adjustment layer's color change, read from its block as the Photoshop File Format Specification lays it out: levels
/// (levl), curves (curv), hue/saturation (hue2), brightness/contrast (brit, with the CgEd descriptor newer files add) and invert
/// (nvrt). Each maps a straight color, channels 0 to 1, to the adjusted one. Values are on Photoshop's 0 to 255 scale whatever
/// the document's depth.
/// </summary>
internal abstract class PsdAdjustment
{
    /// <summary>Returns the adjustment of a layer record, or <see langword="null"/> when it has none Egami applies (or its data is damaged).</summary>
    public static PsdAdjustment? Of(PsdLayerRecord record, bool gray)
    {
        try
        {
            if (record.Has("nvrt"))
            {
                return new Invert();
            }

            if (record.Blocks.TryGetValue("levl", out byte[]? levels))
            {
                return Levels.Read(levels, gray);
            }

            if (record.Blocks.TryGetValue("curv", out byte[]? curves))
            {
                return Curves.Read(curves, gray);
            }

            if (record.Blocks.TryGetValue("hue2", out byte[]? hue))
            {
                return HueSaturation.Read(hue);
            }

            if (record.Blocks.TryGetValue("CgEd", out byte[]? modern) || record.Blocks.ContainsKey("brit"))
            {
                return BrightnessContrast.Read(modern, record.Blocks.GetValueOrDefault("brit"));
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // A block too short for its kind: the adjustment is left out.
        }

        return null;
    }

    /// <summary>Adjusts one straight color in place.</summary>
    public abstract void Apply(ref float r, ref float g, ref float b);

    private static int Short(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt16BigEndian(data[at..]);

    private sealed class Invert : PsdAdjustment
    {
        public override void Apply(ref float r, ref float g, ref float b)
        {
            (r, g, b) = (1 - r, 1 - g, 1 - b);
        }
    }

    /// <summary>
    /// Levels: per channel, then the composite: input black and white points, a gamma, output black and white points. The input
    /// range is stretched to 0..1, raised to 1/gamma and mapped onto the output range.
    /// </summary>
    private sealed class Levels(float[][] composite, float[][]?[] channels) : PsdAdjustment
    {
        public static Levels? Read(ReadOnlySpan<byte> data, bool gray)
        {
            if (data.Length < 2 + 10 || Short(data, 0) != 2)
            {
                return null;
            }

            float[][] records = new float[Math.Min(29, (data.Length - 2) / 10)][];
            for (int i = 0; i < records.Length; i++)
            {
                int at = 2 + (10 * i);
                records[i] = [Short(data, at), Short(data, at + 2), Short(data, at + 4), Short(data, at + 6), Short(data, at + 8) / 100f];
            }

            float[]?[] perChannel = new float[]?[3];
            for (int c = 0; c < 3 && !gray && c + 1 < records.Length; c++)
            {
                perChannel[c] = records[c + 1];
            }

            return new Levels([records[0]], [.. perChannel.Select(r => r is null ? null : new[] { r })]);
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            r = Level(composite[0], channels[0] is float[][] cr ? Level(cr[0], r) : r);
            g = Level(composite[0], channels[1] is float[][] cg ? Level(cg[0], g) : g);
            b = Level(composite[0], channels[2] is float[][] cb ? Level(cb[0], b) : b);
        }

        private static float Level(float[] l, float v)
        {
            float inLow = l[0], inHigh = l[1], outLow = l[2], outHigh = l[3], gamma = l[4];
            if (inLow == 0 && inHigh == 255 && outLow == 0 && outHigh == 255 && gamma == 1)
            {
                return v;
            }

            float x = inHigh > inLow ? Math.Clamp(((v * 255) - inLow) / (inHigh - inLow), 0, 1) : (v * 255 >= inHigh ? 1 : 0);
            if (gamma > 0 && gamma != 1)
            {
                x = MathF.Pow(x, 1 / gamma);
            }

            return (outLow + (x * (outHigh - outLow))) / 255;
        }
    }

    /// <summary>
    /// Curves: per channel, then the composite, each a smooth curve through its points (input, output on 0..255) by a natural
    /// cubic spline, flat beyond the first and last point, kept to 0..255; looked up in a table of 1024 steps.
    /// </summary>
    private sealed class Curves(float[]? composite, float[]?[] channels) : PsdAdjustment
    {
        public static Curves? Read(ReadOnlySpan<byte> data, bool gray)
        {
            if (data.Length < 7)
            {
                return null;
            }

            bool map = data[0] != 0;
            int version = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
            uint countMap = BinaryPrimitives.ReadUInt32BigEndian(data[3..]);
            if (map || version is not (1 or 4))
            {
                return null;
            }

            Dictionary<int, float[]> curves = [];
            int at = 7;
            List<int> ids = [];
            if (version == 1)
            {
                for (int bit = 0; bit < 32; bit++)
                {
                    if ((countMap & (1u << bit)) != 0)
                    {
                        ids.Add(bit);
                    }
                }
            }
            else
            {
                for (int i = 0; i < countMap; i++)
                {
                    ids.Add(i);
                }
            }

            foreach (int id in ids)
            {
                (float[] table, at) = ReadPoints(data, at);
                curves[id] = table;
            }

            // Version 1 is followed by "Crv " and the curves again with their channel ids, which win.
            if (version == 1 && at + 10 <= data.Length && data.Slice(at, 4).SequenceEqual("Crv "u8))
            {
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data[(at + 6)..]);
                at += 10;
                for (int i = 0; i < count && at + 2 <= data.Length; i++)
                {
                    int id = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                    (float[] table, at) = ReadPoints(data, at + 2);
                    curves[id] = table;
                }
            }

            float[]?[] perChannel = new float[]?[3];
            for (int c = 0; c < 3 && !gray; c++)
            {
                perChannel[c] = curves.GetValueOrDefault(c + 1);
            }

            return new Curves(curves.GetValueOrDefault(0), perChannel);
        }

        private static (float[] Table, int Next) ReadPoints(ReadOnlySpan<byte> data, int at)
        {
            int count = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            at += 2;
            List<(double In, double Out)> points = [];
            for (int i = 0; i < count; i++, at += 4)
            {
                points.Add((BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]), BinaryPrimitives.ReadUInt16BigEndian(data[at..])));
            }

            return (Table(points), at);
        }

        /// <summary>Tabulates the natural cubic spline through the points at 1024 inputs from 0 to 255.</summary>
        private static float[] Table(List<(double In, double Out)> points)
        {
            points = [.. points.OrderBy(p => p.In).DistinctBy(p => p.In)];
            float[] table = new float[1024];
            if (points.Count == 0)
            {
                for (int i = 0; i < table.Length; i++)
                {
                    table[i] = i / 1023f;
                }

                return table;
            }

            int n = points.Count;
            double[] second = new double[n];
            if (n > 2)
            {
                // The tridiagonal system of a natural spline: second derivatives 0 at both ends.
                double[] u = new double[n];
                for (int i = 1; i < n - 1; i++)
                {
                    double sig = (points[i].In - points[i - 1].In) / (points[i + 1].In - points[i - 1].In);
                    double p = (sig * second[i - 1]) + 2;
                    second[i] = (sig - 1) / p;
                    double slope = ((points[i + 1].Out - points[i].Out) / (points[i + 1].In - points[i].In)) - ((points[i].Out - points[i - 1].Out) / (points[i].In - points[i - 1].In));
                    u[i] = ((6 * slope / (points[i + 1].In - points[i - 1].In)) - (sig * u[i - 1])) / p;
                }

                for (int i = n - 2; i >= 0; i--)
                {
                    second[i] = (second[i] * second[i + 1]) + u[i];
                }
            }

            for (int t = 0; t < table.Length; t++)
            {
                double x = t * 255.0 / 1023;
                double y;
                if (n == 1 || x <= points[0].In)
                {
                    y = points[0].Out;
                }
                else if (x >= points[^1].In)
                {
                    y = points[^1].Out;
                }
                else
                {
                    int k = 1;
                    while (points[k].In < x)
                    {
                        k++;
                    }

                    double h = points[k].In - points[k - 1].In;
                    double a = (points[k].In - x) / h, b = (x - points[k - 1].In) / h;
                    y = (a * points[k - 1].Out) + (b * points[k].Out) + ((((a * a * a) - a) * second[k - 1]) + (((b * b * b) - b) * second[k])) * h * h / 6;
                }

                table[t] = (float)(Math.Clamp(y, 0, 255) / 255);
            }

            return table;
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            r = Look(composite, Look(channels[0], r));
            g = Look(composite, Look(channels[1], g));
            b = Look(composite, Look(channels[2], b));
        }

        private static float Look(float[]? table, float v)
        {
            if (table is null)
            {
                return v;
            }

            float x = Math.Clamp(v, 0, 1) * 1023;
            int i = Math.Min((int)x, 1022);
            return table[i] + ((table[i + 1] - table[i]) * (x - i));
        }
    }

    /// <summary>
    /// Hue/saturation: the master hue (degrees), saturation and lightness (−100 to 100) plus six hue ranges (reds to magentas),
    /// each weighted by where the pixel's hue falls in its ramps; or, colorized, one hue and saturation for every pixel. Hue and
    /// saturation change in HSL; lightness mixes toward white or black.
    /// </summary>
    private sealed class HueSaturation(bool colorize, int[] colorization, int[] master, int[][] ranges, int[][] settings) : PsdAdjustment
    {
        public static HueSaturation? Read(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4 + 12 + (6 * 14) || Short(data, 0) != 2)
            {
                return null;
            }

            int[] colorization = [Short(data, 4), Short(data, 6), Short(data, 8)];
            int[] master = [Short(data, 10), Short(data, 12), Short(data, 14)];
            int[][] ranges = new int[6][], settings = new int[6][];
            for (int i = 0; i < 6; i++)
            {
                int at = 16 + (14 * i);
                ranges[i] = [Short(data, at), Short(data, at + 2), Short(data, at + 4), Short(data, at + 6)];
                settings[i] = [Short(data, at + 8), Short(data, at + 10), Short(data, at + 12)];
            }

            return new HueSaturation(data[2] != 0, colorization, master, ranges, settings);
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            (float h, float s, float l) = ToHsl(r, g, b);
            float dh, ds, dl;
            if (colorize)
            {
                h = colorization[0];
                s = colorization[1] / 100f;
                dl = colorization[2];
                (r, g, b) = FromHsl(h, s, l);
            }
            else
            {
                (dh, ds, dl) = (master[0], master[1], master[2]);
                for (int i = 0; i < 6; i++)
                {
                    float w = Weight(ranges[i], h);
                    dh += w * settings[i][0];
                    ds += w * settings[i][1];
                    dl += w * settings[i][2];
                }

                if (dh != 0 || ds != 0)
                {
                    h = (((h + dh) % 360) + 360) % 360;
                    s = ds >= 0 ? s + ((1 - s) * s * ds / 100) : s * (1 + (ds / 100));
                    (r, g, b) = FromHsl(h, Math.Clamp(s, 0, 1), l);
                }
            }

            if (dl > 0)
            {
                (r, g, b) = (r + ((1 - r) * dl / 100), g + ((1 - g) * dl / 100), b + ((1 - b) * dl / 100));
            }
            else if (dl < 0)
            {
                (r, g, b) = (r * (1 + (dl / 100)), g * (1 + (dl / 100)), b * (1 + (dl / 100)));
            }
        }

        /// <summary>Returns how much a hue range covers a hue: 1 between its sustain points, ramping to 0 at its outer points, wrapping round 360.</summary>
        private static float Weight(int[] range, float hue)
        {
            float b0 = range[0], b1 = range[1], e1 = range[2], e0 = range[3];
            float best = 0;
            for (int turn = -1; turn <= 1; turn++)
            {
                float x = hue + (360 * turn);
                float w = x < b0 || x > e0 ? 0 : x < b1 ? (x - b0) / Math.Max(b1 - b0, 1e-6f) : x <= e1 ? 1 : (e0 - x) / Math.Max(e0 - e1, 1e-6f);
                best = Math.Max(best, w);
            }

            return best;
        }

        private static (float H, float S, float L) ToHsl(float r, float g, float b)
        {
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            float l = (max + min) / 2;
            if (max == min)
            {
                return (0, 0, l);
            }

            float d = max - min;
            float s = l > 0.5f ? d / (2 - max - min) : d / (max + min);
            float h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
            return (h * 60, s, l);
        }

        private static (float R, float G, float B) FromHsl(float h, float s, float l)
        {
            float q = l < 0.5f ? l * (1 + s) : l + s - (l * s);
            float p = (2 * l) - q;
            return (Channel(p, q, (h / 360) + (1f / 3)), Channel(p, q, h / 360), Channel(p, q, (h / 360) - (1f / 3)));

            static float Channel(float p, float q, float t)
            {
                t = ((t % 1) + 1) % 1;
                return t < 1f / 6 ? p + ((q - p) * 6 * t) : t < 0.5f ? q : t < 2f / 3 ? p + ((q - p) * ((2f / 3) - t) * 6) : p;
            }
        }
    }

    /// <summary>
    /// Brightness/contrast. The legacy form (brit, or CgEd's useLegacy): brightness shifts every value, contrast scales the
    /// distance from the mean. The current form is approximated: brightness bends values with a gamma curve that keeps black and
    /// white, contrast as legacy about the mean of 127.5.
    /// </summary>
    private sealed class BrightnessContrast(float brightness, float contrast, float mean, bool legacy) : PsdAdjustment
    {
        public static BrightnessContrast? Read(byte[]? modern, byte[]? old)
        {
            if (modern is not null && PsdDescriptor.ReadVersioned(modern) is PsdDescriptor d)
            {
                return new BrightnessContrast(
                    (float)(d.Number("Brgh") ?? 0), (float)(d.Number("Cntr") ?? 0), (float)(d.Number("means") ?? 127), d.Flag("useLegacy") ?? false);
            }

            if (old is { Length: >= 6 })
            {
                return new BrightnessContrast(Short(old, 0), Short(old, 2), Short(old, 4), true);
            }

            return null;
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            r = Channel(r);
            g = Channel(g);
            b = Channel(b);
        }

        private float Channel(float v)
        {
            if (legacy)
            {
                v += brightness / 255;
                float factor = contrast >= 0 ? 1 / Math.Max(1 - (contrast / 100), 1e-3f) : 1 + (contrast / 100);
                float m = mean / 255;
                return Math.Clamp(((v - m) * factor) + m, 0, 1);
            }

            v = Math.Clamp(v, 0, 1);
            if (brightness != 0)
            {
                v = MathF.Pow(v, MathF.Pow(2, -brightness / 75));
            }

            float k = contrast >= 0 ? 1 / Math.Max(1 - (contrast / 101), 1e-3f) : 1 + (contrast / 100);
            return Math.Clamp(((v - 0.5f) * k) + 0.5f, 0, 1);
        }
    }
}
