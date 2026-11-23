using System.Numerics;

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

    /// <summary>
    /// Writes 4:2:0 planes into <paramref name="target"/> as P010, 16-bit samples with 10 significant bits at the top, of
    /// <paramref name="codedWidth"/> by <paramref name="codedHeight"/>: <paramref name="planes"/> of bytes or of
    /// <see cref="ushort"/> at any depth up to 16 bits are shifted up to fill the word.
    /// </summary>
    internal static void WriteP010(YuvRaster planes, Span<byte> target, int codedWidth, int codedHeight)
    {
        Span<ushort> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(target[..(Size(codedWidth, codedHeight) * 2)]);
        if (planes is YuvRaster<byte> bytes)
        {
            WriteWords(bytes.Y, bytes.U!, bytes.V!, 8, words, codedWidth, codedHeight);
        }
        else
        {
            YuvRaster<ushort> shorts = (YuvRaster<ushort>)planes;
            WriteWords(shorts.Y, shorts.U!, shorts.V!, 16 - shorts.BitDepth, words, codedWidth, codedHeight);
        }
    }

    private static void WriteWords<T>(YuvPlane<T> y, YuvPlane<T> u, YuvPlane<T> v, int shift, Span<ushort> target, int codedWidth, int codedHeight)
        where T : unmanaged, IBinaryInteger<T>
    {
        int width = y.Width, height = y.Height;
        for (int row = 0; row < codedHeight; row++)
        {
            Span<ushort> line = target.Slice(row * codedWidth, codedWidth);
            ReadOnlySpan<T> source = y.Row(Math.Min(row, height - 1))[..width];
            for (int x = 0; x < width; x++)
            {
                line[x] = (ushort)(ushort.CreateTruncating(source[x]) << shift);
            }

            line[width..].Fill(line[width - 1]);
        }

        int chromaWidth = u.Width, chromaHeight = u.Height;
        Span<ushort> chroma = target[(codedWidth * codedHeight)..];
        for (int row = 0; row < codedHeight / 2; row++)
        {
            Span<ushort> line = chroma.Slice(row * codedWidth, codedWidth);
            ReadOnlySpan<T> cb = u.Row(Math.Min(row, chromaHeight - 1))[..chromaWidth];
            ReadOnlySpan<T> cr = v.Row(Math.Min(row, chromaHeight - 1))[..chromaWidth];
            for (int x = 0; x < codedWidth / 2; x++)
            {
                int sx = Math.Min(x, chromaWidth - 1);
                line[2 * x] = (ushort)(ushort.CreateTruncating(cb[sx]) << shift);
                line[(2 * x) + 1] = (ushort)(ushort.CreateTruncating(cr[sx]) << shift);
            }
        }
    }
}
