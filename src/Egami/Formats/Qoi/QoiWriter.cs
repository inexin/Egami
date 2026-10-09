using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Qoi;

/// <summary>
/// Writes QOI, written from the QOI specification 1.0. The encoder makes the reference encoder's choices in the same order (run,
/// index, diff, luma, RGB, RGBA), so the bytes match qoi.h for the same pixels. Only the first frame is written.
/// </summary>
internal sealed class QoiWriter : IFormatWriter
{
    internal static readonly QoiWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        ArgumentNullException.ThrowIfNull(output);
        QoiWriterOptions qoi = options as QoiWriterOptions ?? QoiWriterOptions.Default;
        int width = raster.Width;
        int height = raster.Height;
        uint[] row = ArrayPool<uint>.Shared.Rent(width);
        try
        {
            bool alpha = qoi.IncludeAlpha ?? (TPixel.Layout.HasAlpha && !IsOpaque(raster, row));
            uint opaque = alpha ? 0u : 0xFF00_0000;
            using StreamBufferWriter writer = new(output);
            Span<byte> header = writer.GetSpan(QoiReader.HeaderLength);
            "qoif"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)height);
            header[12] = (byte)(alpha ? 4 : 3);
            header[13] = (byte)(raster.Metadata.ColorSpace == ColorSpaceHint.Linear ? 1 : 0);
            writer.Advance(QoiReader.HeaderLength);

            Span<uint> index = stackalloc uint[64];
            index.Clear();
            ref uint idx = ref MemoryMarshal.GetReference(index);
            uint previous = 0xFF00_0000;
            int run = 0;
            for (int y = 0; y < height; y++)
            {
                Span<uint> pixels = row.AsSpan(0, width);
                PixelConvert.Convert<TPixel, Rgba8>(raster.Row(y), MemoryMarshal.Cast<uint, Rgba8>(pixels));
                if (opaque != 0)
                {
                    foreach (ref uint p in pixels)
                    {
                        p |= opaque;
                    }
                }

                // At most five bytes a pixel, plus the end marker after the last row.
                Span<byte> span = writer.GetSpan((width * 5) + 8);
                ref byte dst = ref MemoryMarshal.GetReference(span);
                ref uint src = ref MemoryMarshal.GetReference(pixels);
                int n = 0;
                bool lastRow = y == height - 1;
                for (int x = 0; x < width; x++)
                {
                    uint px = Unsafe.Add(ref src, x);
                    if (px == previous)
                    {
                        run++;
                        if (run == 62 || (lastRow && x == width - 1))
                        {
                            Unsafe.Add(ref dst, n++) = (byte)(QoiReader.OpRun | (run - 1));
                            run = 0;
                        }

                        continue;
                    }

                    if (run > 0)
                    {
                        Unsafe.Add(ref dst, n++) = (byte)(QoiReader.OpRun | (run - 1));
                        run = 0;
                    }

                    int slot = QoiReader.Hash(px);
                    ref uint entry = ref Unsafe.Add(ref idx, slot);
                    if (entry == px)
                    {
                        Unsafe.Add(ref dst, n++) = (byte)(QoiReader.OpIndex | slot);
                    }
                    else
                    {
                        entry = px;
                        if ((px >> 24) == (previous >> 24))
                        {
                            int vr = (sbyte)(byte)(px - previous);
                            int vg = (sbyte)(byte)((px >> 8) - (previous >> 8));
                            int vb = (sbyte)(byte)((px >> 16) - (previous >> 16));
                            int vgr = (sbyte)(vr - vg);
                            int vgb = (sbyte)(vb - vg);
                            if ((uint)(vr + 2) < 4 && (uint)(vg + 2) < 4 && (uint)(vb + 2) < 4)
                            {
                                Unsafe.Add(ref dst, n++) = (byte)(QoiReader.OpDiff | ((vr + 2) << 4) | ((vg + 2) << 2) | (vb + 2));
                            }
                            else if ((uint)(vgr + 8) < 16 && (uint)(vg + 32) < 64 && (uint)(vgb + 8) < 16)
                            {
                                Unsafe.Add(ref dst, n) = (byte)(QoiReader.OpLuma | (vg + 32));
                                Unsafe.Add(ref dst, n + 1) = (byte)(((vgr + 8) << 4) | (vgb + 8));
                                n += 2;
                            }
                            else
                            {
                                // The opcode byte, then red, green and blue: the pixel's low three bytes shifted up one.
                                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, n), QoiReader.OpRgb | (px << 8));
                                n += 4;
                            }
                        }
                        else
                        {
                            Unsafe.Add(ref dst, n) = QoiReader.OpRgba;
                            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, n + 1), px);
                            n += 5;
                        }
                    }

                    previous = px;
                }

                if (lastRow)
                {
                    QoiReader.EndMarker.CopyTo(span[n..]);
                    n += QoiReader.EndMarker.Length;
                }

                writer.Advance(n);
            }

            writer.Flush();
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(row);
        }
    }

    private static bool IsOpaque<TPixel>(Raster<TPixel> raster, uint[] row)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int y = 0; y < raster.Height; y++)
        {
            Span<uint> pixels = row.AsSpan(0, raster.Width);
            PixelConvert.Convert<TPixel, Rgba8>(raster.Row(y), MemoryMarshal.Cast<uint, Rgba8>(pixels));
            foreach (uint p in pixels)
            {
                if (p < 0xFF00_0000)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
