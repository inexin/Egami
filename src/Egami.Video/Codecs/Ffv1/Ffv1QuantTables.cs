namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// The quantization tables Egami.Video's FFV1 encoder writes (RFC 9043 section 3.4): neighbour differences grouped into levels
/// whose bounds grow about twice as fast as the difference, scaled to the sample depth. The small context model quantizes the
/// three nearest differences into 11 levels each; the large one adds L and T at 3 levels, keeping the product of the level
/// counts under 32768 as decoders require.
/// </summary>
internal static class Ffv1QuantTables
{
    // The first difference of each level above 0, at 8 bits; the levels below are mirrored as negative ones.
    private static readonly int[] Near = [1, 2, 5, 11, 24];
    private static readonly int[] Far = [2];

    /// <summary>Builds one quantization table set for samples of <paramref name="bits"/> bits (one more for RGB).</summary>
    public static short[][] Build(int bits, bool large)
    {
        // Bounds measured on 1080p camera-like and synthetic clips against FFmpeg's sizes: 8 and 9 bits (RGB) as they are, twice
        // as wide at 10 bits, and so on.
        int factor = bits <= 9 ? 1 : Math.Min(1 << (bits - 9), 64);
        int[] near = Near, far = Far;
        int[][] bounds = large ? [near, near, near, far, far] : [near, near, near, [], []];
        short[][] tables = new short[Ffv1Parameters.ContextInputs][];
        int scale = 1;
        for (int j = 0; j < tables.Length; j++)
        {
            short[] table = new short[256];

            // Levels count up by one wherever the level changes, as a decoder rebuilds them from the run lengths.
            int levels = 1, previous = 0;
            for (int k = 0; k < 128; k++)
            {
                int level = 0;
                foreach (int bound in bounds[j])
                {
                    level += k >= Math.Min(bound * factor, 127) ? 1 : 0;
                }

                if (k > 0 && level != previous)
                {
                    levels++;
                }

                previous = level;
                table[k] = (short)(scale * (levels - 1));
            }

            for (int k = 1; k < 128; k++)
            {
                table[256 - k] = (short)-table[k];
            }

            table[128] = (short)-table[127];
            tables[j] = table;
            scale *= (2 * levels) - 1;
        }

        return tables;
    }
}
