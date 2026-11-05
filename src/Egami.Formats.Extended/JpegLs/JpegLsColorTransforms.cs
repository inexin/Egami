namespace Egami.Formats.Extended.JpegLs;

/// <summary>
/// HP's reversible colour transforms, signalled by an APP8 "mrfx" segment: integer differences taken modulo the sample range
/// and offset by half of it, so they stay within the sample's bits. HP1 codes (R − G, G, B − G), HP2 (R − G, G, B − (R + G) / 2)
/// and HP3 (G + (B′ + R′) / 4, B′ = B − G, R′ = R − G). They apply to interleaved scans only: a file written with interleave
/// none keeps its components as they are, as CharLS writes and reads it. Matched to CharLS's output as a black box.
/// </summary>
internal static class JpegLsColorTransforms
{
    /// <summary>Turns coded triples back into RGB, in place, for <paramref name="width"/> pixels of <paramref name="channels"/> samples.</summary>
    public static void Inverse(JpegLsColorTransform transform, Span<ushort> samples, int channels, int width, int bits)
    {
        int range = 1 << bits;
        int mask = range - 1;
        int half = range >> 1;
        for (int x = 0, at = 0; x < width; x++, at += channels)
        {
            int v1 = samples[at], v2 = samples[at + 1], v3 = samples[at + 2];
            int r, g, b;
            switch (transform)
            {
                case JpegLsColorTransform.Hp1:
                    g = v2;
                    r = (v1 + g - half) & mask;
                    b = (v3 + g - half) & mask;
                    break;
                case JpegLsColorTransform.Hp2:
                    g = v2;
                    r = (v1 + g - half) & mask;
                    b = (v3 + ((r + g) >> 1) - half) & mask;
                    break;
                default:
                    g = (v1 - ((v2 + v3 - range) >> 2)) & mask;
                    r = (v3 + g - half) & mask;
                    b = (v2 + g - half) & mask;
                    break;
            }

            samples[at] = (ushort)r;
            samples[at + 1] = (ushort)g;
            samples[at + 2] = (ushort)b;
        }
    }

    /// <summary>Codes RGB triples with <paramref name="transform"/>, in place; the inverse of <see cref="Inverse"/>.</summary>
    public static void Forward(JpegLsColorTransform transform, Span<ushort> samples, int channels, int width, int bits)
    {
        int range = 1 << bits;
        int mask = range - 1;
        int half = range >> 1;
        for (int x = 0, at = 0; x < width; x++, at += channels)
        {
            int r = samples[at], g = samples[at + 1], b = samples[at + 2];
            int v1, v2, v3;
            switch (transform)
            {
                case JpegLsColorTransform.Hp1:
                    v1 = (r - g + half) & mask;
                    v2 = g;
                    v3 = (b - g + half) & mask;
                    break;
                case JpegLsColorTransform.Hp2:
                    v1 = (r - g + half) & mask;
                    v2 = g;
                    v3 = (b - ((r + g) >> 1) + half) & mask;
                    break;
                default:
                    v2 = (b - g + half) & mask;
                    v3 = (r - g + half) & mask;
                    v1 = (g + ((v2 + v3 - range) >> 2)) & mask;
                    break;
            }

            samples[at] = (ushort)v1;
            samples[at + 1] = (ushort)v2;
            samples[at + 2] = (ushort)v3;
        }
    }
}
