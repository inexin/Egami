using System.Buffers;
using System.Buffers.Binary;
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
                int n = 0;
                bool lastRow = y == height - 1;
                for (int x = 0; x < width; x++)
                {
                    uint px = pixels[x];
                    if (px == previous)
                    {
                        run++;
                        if (run == 62 || (lastRow && x == width - 1))
                        {
                            span[n++] = (byte)(QoiReader.OpRun | (run - 1));
                            run = 0;
                        }

                        continue;
                    }

                    if (run > 0)
                    {
                        span[n++] = (byte)(QoiReader.OpRun | (run - 1));
                        run = 0;
                    }

                    int slot = QoiReader.Hash(px);
                    if (index[slot] == px)
                    {
                        span[n++] = (byte)(QoiReader.OpIndex | slot);
                    }
                    else
                    {
                        index[slot] = px;
                        if ((px >> 24) == (previous >> 24))
                        {
                            int vr = (sbyte)(byte)(px - previous);
                            int vg = (sbyte)(byte)((px >> 8) - (previous >> 8));
                            int vb = (sbyte)(byte)((px >> 16) - (previous >> 16));
                            int vgr = (sbyte)(vr - vg);
                            int vgb = (sbyte)(vb - vg);
                            if (vr is > -3 and < 2 && vg is > -3 and < 2 && vb is > -3 and < 2)
                            {
                                span[n++] = (byte)(QoiReader.OpDiff | ((vr + 2) << 4) | ((vg + 2) << 2) | (vb + 2));
                            }
                            else if (vgr is > -9 and < 8 && vg is > -33 and < 32 && vgb is > -9 and < 8)
                            {
                                span[n++] = (byte)(QoiReader.OpLuma | (vg + 32));
                                span[n++] = (byte)(((vgr + 8) << 4) | (vgb + 8));
                            }
                            else
                            {
                                span[n++] = QoiReader.OpRgb;
                                span[n++] = (byte)px;
                                span[n++] = (byte)(px >> 8);
                                span[n++] = (byte)(px >> 16);
                            }
                        }
                        else
                        {
                            span[n++] = QoiReader.OpRgba;
                            BinaryPrimitives.WriteUInt32LittleEndian(span[n..], px);
                            n += 4;
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
