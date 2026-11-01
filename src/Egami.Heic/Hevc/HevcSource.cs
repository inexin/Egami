namespace Egami.Heic.Hevc;

/// <summary>
/// The samples a picture is encoded from: its planes (Y, Cb, Cr, or Y alone) at the coded size, padded to whole coding blocks by
/// repeating the last column and row.
/// </summary>
internal sealed class HevcSource
{
    /// <summary>Copies planes of the picture's size into planes of the coded size, padding to the right and below.</summary>
    /// <param name="settings">The picture's size and chroma format.</param>
    /// <param name="planes">The planes, each <paramref name="strides"/>[c] samples a row and as many rows as the component has.</param>
    /// <param name="strides">Each plane's row length.</param>
    internal HevcSource(HevcEncoderSettings settings, ushort[][] planes, int[] strides)
    {
        int components = settings.ChromaFormat == 0 ? 1 : 3;
        Planes = new ushort[components][];
        Widths = new int[components];
        Heights = new int[components];
        for (int c = 0; c < components; c++)
        {
            int sx = c == 0 ? 1 : settings.SubWidthC, sy = c == 0 ? 1 : settings.SubHeightC;
            int width = settings.CodedWidth / sx, height = settings.CodedHeight / sy;
            int sourceWidth = (settings.Width + sx - 1) / sx, sourceHeight = (settings.Height + sy - 1) / sy;
            ushort[] plane = new ushort[width * height];
            for (int y = 0; y < height; y++)
            {
                ReadOnlySpan<ushort> row = planes[c].AsSpan(Math.Min(y, sourceHeight - 1) * strides[c], sourceWidth);
                Span<ushort> target = plane.AsSpan(y * width, width);
                row.CopyTo(target);
                target[sourceWidth..].Fill(row[^1]);
            }

            (Planes[c], Widths[c], Heights[c]) = (plane, width, height);
        }
    }

    /// <summary>Gets the planes.</summary>
    internal ushort[][] Planes { get; }

    /// <summary>Gets each plane's width, its stride.</summary>
    internal int[] Widths { get; }

    /// <summary>Gets each plane's height.</summary>
    internal int[] Heights { get; }
}
