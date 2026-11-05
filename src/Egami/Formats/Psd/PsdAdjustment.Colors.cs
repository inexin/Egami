using System.Numerics;

using Egami.Operations;

namespace Egami.Formats.Psd;

/// <summary>
/// The adjustments that remap colors: black and white (blwh), selective color (selc), gradient map (grdm) and color lookup
/// (clrL).
/// </summary>
internal abstract partial class PsdAdjustment
{
    /// <summary>The remapping adjustments, or <see langword="null"/>.</summary>
    private static PsdAdjustment? OfColors(PsdLayerRecord record, EgamiContext context)
    {
        if (record.Blocks.TryGetValue("blwh", out byte[]? data))
        {
            return PsdDescriptor.ReadVersioned(data) is PsdDescriptor d ? BlackWhite.Read(d) : null;
        }

        if (record.Blocks.TryGetValue("selc", out data))
        {
            return SelectiveColor.Read(data);
        }

        if (record.Blocks.TryGetValue("grdm", out data))
        {
            return GradientMap.Read(data);
        }

        if (record.Blocks.TryGetValue("clrL", out data))
        {
            return PsdDescriptor.ReadVersioned(data, 2) is PsdDescriptor d ? ColorLookup.Read(d, context) : null;
        }

        return null;
    }

    /// <summary>Sorts a color's channels: the index of the largest and the smallest, and the three values from largest down.</summary>
    private static (int Max, int Min, float Hi, float Mid, float Lo) Order(float r, float g, float b)
    {
        int max = r >= g ? (r >= b ? 0 : 2) : (g >= b ? 1 : 2);
        int min = r < g ? (r < b ? 0 : 2) : (g < b ? 1 : 2);
        if (min == max)
        {
            min = (max + 1) % 3;
        }

        float hi = max == 0 ? r : max == 1 ? g : b, lo = min == 0 ? r : min == 1 ? g : b;
        return (max, min, hi, r + g + b - hi - lo, lo);
    }

    /// <summary>
    /// Returns the hue class pair a color falls in: the primary (0 red, 2 green, 4 blue) of its largest channel and the secondary
    /// (1 yellow, 3 cyan, 5 magenta) it shares with its middle channel; the color holds largest − middle of the one and middle −
    /// smallest of the other.
    /// </summary>
    private static (int Primary, int Secondary) Classes(int max, int min)
    {
        int mid = 3 - max - min;
        int secondary = (max, mid) switch
        {
            (0, 1) or (1, 0) => 1,
            (1, 2) or (2, 1) => 3,
            _ => 5,
        };
        return (2 * max, secondary);
    }

    /// <summary>A no-op: a color lookup without a table.</summary>
    private sealed class NoChange : PsdAdjustment
    {
        public override void Apply(ref float r, ref float g, ref float b)
        {
        }
    }

    /// <summary>
    /// Black and white: the gray is the smallest channel, plus (largest − middle) times the weight of the color's primary and
    /// (middle − smallest) times the weight of its secondary; tinted, the tint color moved to that gray's luminosity.
    /// </summary>
    private sealed class BlackWhite(float[] weights, float[]? tint) : PsdAdjustment
    {
        public static BlackWhite Read(PsdDescriptor d)
        {
            float W(string key, double fallback) => (float)((d.Number(key) ?? fallback) / 100);
            float[] weights = [W("Rd  ", 40), W("Yllw", 60), W("Grn ", 40), W("Cyn ", 60), W("Bl  ", 20), W("Mgnt", 80)];
            float[]? tint = null;
            if ((d.Flag("useTint") ?? false) && d.Get<PsdDescriptor>("tintColor") is PsdDescriptor color)
            {
                (double r, double g, double b) = RgbOf(color);
                tint = [(float)Math.Clamp(r, 0, 1), (float)Math.Clamp(g, 0, 1), (float)Math.Clamp(b, 0, 1)];
            }

            return new BlackWhite(weights, tint);
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            (int max, int min, float hi, float mid, float lo) = Order(r, g, b);
            (int primary, int secondary) = Classes(max, min);
            float gray = Math.Clamp(lo + ((hi - mid) * weights[primary]) + ((mid - lo) * weights[secondary]), 0, 1);
            if (tint is null)
            {
                (r, g, b) = (gray, gray, gray);
                return;
            }

            (r, g, b) = (tint[0], tint[1], tint[2]);
            SetLum(ref r, ref g, ref b, gray);
        }
    }

    /// <summary>
    /// Selective color: nine color classes (reds to magentas, whites, neutrals, blacks), each with cyan, magenta, yellow and
    /// black changes (−100 to 100 %). A color's share of a hue class is as in black and white; of whites, how far its smallest
    /// channel is above half; of blacks, how far its largest is below half; of neutrals, the rest of its closeness to mid gray.
    /// Each ink (one minus its channel) changes by the shares times the changes, relative to the ink there or absolute; black
    /// darkens or lightens all three.
    /// </summary>
    private sealed class SelectiveColor(float[][] classes, bool absolute) : PsdAdjustment
    {
        public static SelectiveColor? Read(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4 + (10 * 8))
            {
                return null;
            }

            float[][] classes = new float[9][];
            for (int i = 0; i < 9; i++)
            {
                int at = 4 + (8 * (i + 1));
                classes[i] = [Short(data, at) / 100f, Short(data, at + 2) / 100f, Short(data, at + 4) / 100f, Short(data, at + 6) / 100f];
            }

            return new SelectiveColor(classes, Short(data, 2) == 1);
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            (int max, int min, float hi, float mid, float lo) = Order(r, g, b);
            (int primary, int secondary) = Classes(max, min);
            Span<float> shares = stackalloc float[9];
            shares[primary] = hi - mid;
            shares[secondary] = mid - lo;
            shares[6] = Math.Max(0, (lo - 0.5f) * 2);
            shares[8] = Math.Max(0, (0.5f - hi) * 2);
            shares[7] = Math.Max(0, 1 - Math.Abs(hi - 0.5f) - Math.Abs(lo - 0.5f));
            Span<float> ink = [1 - Math.Clamp(r, 0, 1), 1 - Math.Clamp(g, 0, 1), 1 - Math.Clamp(b, 0, 1)];
            float black = 1 - hi;
            Span<float> change = stackalloc float[4];
            for (int k = 0; k < 9; k++)
            {
                if (shares[k] <= 0)
                {
                    continue;
                }

                for (int c = 0; c < 4; c++)
                {
                    float own = c < 3 ? ink[c] : black;
                    change[c] += shares[k] * classes[k][c] * (absolute ? 1 : own);
                }
            }

            for (int c = 0; c < 3; c++)
            {
                float v = 1 - Math.Clamp(ink[c] + change[c], 0, 1);
                ink[c] = change[3] >= 0 ? v * (1 - Math.Min(change[3], 1)) : v + ((1 - v) * Math.Min(-change[3], 1));
            }

            (r, g, b) = (ink[0], ink[1], ink[2]);
        }
    }

    /// <summary>
    /// Gradient map: the color's luminosity picks a place along the gradient (reversed when the map says so), whose color
    /// replaces it. Transparency stops are not used.
    /// </summary>
    private sealed class GradientMap(float[] table, bool reverse) : PsdAdjustment
    {
        public static GradientMap? Read(ReadOnlySpan<byte> data)
        {
            if (PsdGradient.ReadMap(data, out bool reverse) is not PsdGradient gradient || gradient.IsNoise)
            {
                return null;
            }

            float[] table = new float[(PsdGradient.Steps + 1) * 4];
            gradient.Table(table, 3, color =>
            {
                (double r, double g, double b) = RgbOf(color);
                return [(float)Math.Clamp(r, 0, 1), (float)Math.Clamp(g, 0, 1), (float)Math.Clamp(b, 0, 1), 1];
            }, true);
            return new GradientMap(table, reverse);
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            float t = Math.Clamp(Lum(r, g, b), 0, 1);
            float x = (reverse ? 1 - t : t) * PsdGradient.Steps;
            int i = Math.Min((int)x, PsdGradient.Steps - 1);
            float f = x - i;
            int a = 4 * i;
            r = table[a] + ((table[a + 4] - table[a]) * f);
            g = table[a + 1] + ((table[a + 5] - table[a + 1]) * f);
            b = table[a + 2] + ((table[a + 6] - table[a + 2]) * f);
        }
    }

    /// <summary>
    /// Color lookup: a 3D table from the CUBE file the layer keeps. Other table formats and the abstract-profile form are left
    /// out (and warned of); a lookup with no table changes nothing.
    /// </summary>
    private sealed class ColorLookup(Lut3D lut) : PsdAdjustment
    {
        public static PsdAdjustment? Read(PsdDescriptor d, EgamiContext context)
        {
            if (!d.Items.ContainsKey("lookupType"))
            {
                return new NoChange();
            }

            if (d.Get<string>("lookupType") != "3DLUT" || d.Get<string>("LUTFormat") != "LUTFormatCUBE" || d.Get<byte[]>("LUT3DFileData") is not byte[] cube)
            {
                return null;
            }

            try
            {
                using MemoryStream stream = new(cube, writable: false);
                return new ColorLookup(CubeFile.Read(stream, context));
            }
            catch (CorruptDataException)
            {
                return null;
            }
        }

        public override void Apply(ref float r, ref float g, ref float b)
        {
            Vector3 v = lut.Evaluate(new Vector3(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1)));
            (r, g, b) = (v.X, v.Y, v.Z);
        }
    }
}
