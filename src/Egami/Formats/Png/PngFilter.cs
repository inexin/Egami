namespace Egami.Formats.Png;

/// <summary>The five PNG filter types (section 9.2) and their scalar reconstruction.</summary>
internal static class PngFilter
{
    internal const byte None = 0;
    internal const byte Sub = 1;
    internal const byte Up = 2;
    internal const byte Average = 3;
    internal const byte Paeth = 4;

    /// <summary>
    /// Reverses the filter on one scanline in place.
    /// </summary>
    /// <param name="filter">The filter-type byte, 0 to 4.</param>
    /// <param name="row">The filtered bytes of this row, replaced by the reconstructed bytes.</param>
    /// <param name="previous">The reconstructed previous row of the same pass, all zero for the first row.</param>
    /// <param name="stride">Bytes per complete pixel, at least 1.</param>
    internal static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> previous, int stride)
    {
        switch (filter)
        {
            case None:
                break;
            case Sub:
                for (int i = stride; i < row.Length; i++)
                {
                    row[i] += row[i - stride];
                }

                break;
            case Up:
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] += previous[i];
                }

                break;
            case Average:
                for (int i = 0; i < stride && i < row.Length; i++)
                {
                    row[i] += (byte)(previous[i] >> 1);
                }

                for (int i = stride; i < row.Length; i++)
                {
                    row[i] += (byte)((row[i - stride] + previous[i]) >> 1);
                }

                break;
            case Paeth:
                for (int i = 0; i < stride && i < row.Length; i++)
                {
                    row[i] += previous[i];
                }

                for (int i = stride; i < row.Length; i++)
                {
                    row[i] += Predict(row[i - stride], previous[i], previous[i - stride]);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(filter), filter, "Filter types are 0 to 4.");
        }
    }

    /// <summary>The Paeth predictor: whichever of left, above and upper-left is closest to left + above - upper-left, ties in that order.</summary>
    internal static byte Predict(byte left, byte above, byte upperLeft)
    {
        int p = left + above - upperLeft;
        int pa = Math.Abs(p - left);
        int pb = Math.Abs(p - above);
        int pc = Math.Abs(p - upperLeft);
        return pa <= pb && pa <= pc ? left : pb <= pc ? above : upperLeft;
    }
}
