using Egami.Pixels;

namespace Egami.Video.Hardware.Encoding;

/// <summary>
/// Writes frames as NV12, the 8-bit 4:2:0 format video hardware takes: a luma plane, then one plane of interleaved Cb and Cr
/// samples. The picture is padded from its display size to the coded size by repeating its last row and column.
/// </summary>
internal static class Nv12Writer
{
    /// <summary>The bytes of an NV12 picture of the coded size.</summary>
    internal static int Size(int codedWidth, int codedHeight) => codedWidth * codedHeight * 3 / 2;

    /// <summary>
    /// Writes 8-bit 4:2:0 planes into <paramref name="target"/>, an NV12 picture of <paramref name="codedWidth"/> by
    /// <paramref name="codedHeight"/> whose rows are <paramref name="codedWidth"/> bytes apart.
    /// </summary>
    internal static void Write(YuvRaster<byte> planes, Span<byte> target, int codedWidth, int codedHeight)
    {
        int width = planes.Width, height = planes.Height;
        YuvPlane<byte> y = planes.Y;
        for (int row = 0; row < codedHeight; row++)
        {
            Span<byte> line = target.Slice(row * codedWidth, codedWidth);
            ReadOnlySpan<byte> source = y.Row(Math.Min(row, height - 1))[..width];
            source.CopyTo(line);
            line[width..].Fill(source[^1]);
        }

        YuvPlane<byte> u = planes.U!, v = planes.V!;
        int chromaWidth = u.Width, chromaHeight = u.Height;
        Span<byte> chroma = target[(codedWidth * codedHeight)..];
        for (int row = 0; row < codedHeight / 2; row++)
        {
            Span<byte> line = chroma.Slice(row * codedWidth, codedWidth);
            ReadOnlySpan<byte> cb = u.Row(Math.Min(row, chromaHeight - 1))[..chromaWidth];
            ReadOnlySpan<byte> cr = v.Row(Math.Min(row, chromaHeight - 1))[..chromaWidth];
            Interleave(cb, cr, line);
            for (int x = chromaWidth; x < codedWidth / 2; x++)
            {
                line[2 * x] = cb[^1];
                line[(2 * x) + 1] = cr[^1];
            }
        }
    }

    private static void Interleave(ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> target)
    {
        for (int x = 0; x < cb.Length; x++)
        {
            target[2 * x] = cb[x];
            target[(2 * x) + 1] = cr[x];
        }
    }
}
