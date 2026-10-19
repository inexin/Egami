namespace Egami.Formats.Heif;

/// <summary>
/// A decoded HEIF image item as a codec gives it: luma, then chroma unless monochrome, as samples of <see cref="BitDepth"/> bits
/// in rows of <see cref="Strides"/> samples. The codec-neutral image layer (grid, alpha, clap, color conversion) works on these.
/// </summary>
/// <param name="Width">The width in luma samples.</param>
/// <param name="Height">The height in luma samples.</param>
/// <param name="BitDepth">The bits a sample has: 8, 10 or 12.</param>
/// <param name="SubsamplingX">1 when chroma has half the luma columns.</param>
/// <param name="SubsamplingY">1 when chroma has half the luma rows.</param>
/// <param name="Monochrome">Whether there is luma only.</param>
/// <param name="Samples">The planes: Y, then U and V unless monochrome.</param>
/// <param name="Strides">Each plane's row length in samples.</param>
internal sealed record HeifPlanes(int Width, int Height, int BitDepth, int SubsamplingX, int SubsamplingY, bool Monochrome, ushort[][] Samples, int[] Strides)
{
    /// <summary>Gets the chroma format.</summary>
    internal Pixels.ChromaFormat Chroma => Monochrome ? Pixels.ChromaFormat.Yuv400
        : SubsamplingX == 0 ? Pixels.ChromaFormat.Yuv444
        : SubsamplingY == 0 ? Pixels.ChromaFormat.Yuv422
        : Pixels.ChromaFormat.Yuv420;

    /// <summary>Gets the width of a chroma plane.</summary>
    internal int ChromaWidth => (Width + SubsamplingX) >> SubsamplingX;

    /// <summary>Gets the height of a chroma plane.</summary>
    internal int ChromaHeight => (Height + SubsamplingY) >> SubsamplingY;

    /// <summary>Gets the luma plane alone, as an alpha image is used.</summary>
    internal HeifPlanes LumaOnly() => Monochrome ? this : this with { Monochrome = true, Samples = [Samples[0]], Strides = [Strides[0]] };

    /// <summary>
    /// Builds a grid image (HEIF 6.6.2.3.2) from its cells, row by row: every cell the same size and format, together covering
    /// the output size, the last row and column reaching into it, and even sizes where chroma is subsampled (MIAF 7.3.11.4.2), as
    /// libavif checks them. A null cell was not needed (outside a Region) and is left zero.
    /// </summary>
    internal static HeifPlanes Grid(IReadOnlyList<HeifPlanes?> cells, int rows, int columns, int width, int height, EgamiInput input)
    {
        HeifPlanes first = cells.First(c => c is not null)!;
        int cw = first.Width, ch = first.Height;
        foreach (HeifPlanes? cell in cells)
        {
            if (cell is not null && (cell.Width != cw || cell.Height != ch || cell.BitDepth != first.BitDepth || cell.Monochrome != first.Monochrome
                || cell.SubsamplingX != first.SubsamplingX || cell.SubsamplingY != first.SubsamplingY))
            {
                throw input.Corrupt(-1, "The grid's cells differ in size, depth or chroma format.");
            }
        }

        if ((long)cw * columns < width || (long)ch * rows < height)
        {
            throw input.Corrupt(-1, $"The grid's {columns} x {rows} cells of {cw} x {ch} do not cover its {width} x {height} output.");
        }

        if ((long)cw * (columns - 1) >= width || (long)ch * (rows - 1) >= height)
        {
            throw input.Corrupt(-1, $"The grid's last column or row of {cw} x {ch} cells lies outside its {width} x {height} output.");
        }

        int ssx = first.Monochrome ? 0 : first.SubsamplingX, ssy = first.Monochrome ? 0 : first.SubsamplingY;
        if ((ssx != 0 && ((width | cw) & 1) != 0) || (ssy != 0 && ((height | ch) & 1) != 0))
        {
            throw input.Corrupt(-1, "The grid's output or cell size is odd where its chroma is subsampled.");
        }

        int planes = first.Samples.Length;
        ushort[][] samples = new ushort[planes][];
        int[] strides = new int[planes];
        for (int p = 0; p < planes; p++)
        {
            strides[p] = p == 0 ? width : (width + ssx) >> ssx;
            samples[p] = new ushort[strides[p] * (p == 0 ? height : (height + ssy) >> ssy)];
        }

        for (int t = 0; t < cells.Count; t++)
        {
            if (cells[t] is not HeifPlanes cell)
            {
                continue;
            }

            int x0 = cw * (t % columns), y0 = ch * (t / columns);
            int w = Math.Min(cw, width - x0), h = Math.Min(ch, height - y0);
            for (int p = 0; p < planes; p++)
            {
                int sx = p == 0 ? 0 : ssx, sy = p == 0 ? 0 : ssy;
                int px = x0 >> sx, py = y0 >> sy, pw = (w + sx) >> sx, ph = (h + sy) >> sy;
                for (int y = 0; y < ph; y++)
                {
                    cell.Samples[p].AsSpan(y * cell.Strides[p], pw).CopyTo(samples[p].AsSpan(((py + y) * strides[p]) + px));
                }
            }
        }

        return first with { Width = width, Height = height, Samples = samples, Strides = strides };
    }
}

/// <summary>An overlay image's description (HEIF 6.6.2.3.3): the canvas's RGBA fill (16 bits each), its size, and each layer's offset.</summary>
/// <param name="Fill">The canvas fill: red, green, blue and alpha from 0 to 65535.</param>
/// <param name="Width">The canvas width.</param>
/// <param name="Height">The canvas height.</param>
/// <param name="Offsets">Where each layer's top-left corner goes, which may be outside the canvas.</param>
internal sealed record HeifOverlay(ushort[] Fill, int Width, int Height, (int X, int Y)[] Offsets);

/// <summary>The codec seam of the HEIF image layer: decodes one coded image item (AV1 for AVIF, HEVC for HEIC) into planes.</summary>
internal interface IHeifCodec
{
    /// <summary>Gets the color the stream of the last item decoded gives, for an item with no nclx property.</summary>
    Colors.ColorCodePoints CodePoints { get; }

    /// <summary>Gets whether several items may decode at once on this instance, as a grid's cells do.</summary>
    bool Concurrent { get; }

    /// <summary>
    /// Decodes a coded item of <paramref name="file"/> into planes, with film grain or any other output process the codec defines
    /// applied, on up to <paramref name="parallelism"/> threads.
    /// </summary>
    HeifPlanes Decode(HeifImage file, HeifItem item, EgamiInput input, int parallelism);
}
