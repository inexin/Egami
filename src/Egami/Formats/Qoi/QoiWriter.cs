using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Qoi;

/// <summary>
/// Writes QOI, written from the QOI specification 1.0. The encoder makes the reference encoder's choices in the same order (run,
/// index, diff, luma, RGB, RGBA), so the bytes match qoi.h for the same pixels. Only the first frame is written.
/// </summary>
internal sealed class QoiWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly QoiWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        QoiWriterOptions qoi = start.Options as QoiWriterOptions ?? QoiWriterOptions.Default;
        if (qoi.IncludeAlpha is null && TPixel.Layout.HasAlpha)
        {
            start.Refuse("QOI", "Choosing whether to write alpha", "Set QoiWriterOptions.IncludeAlpha.");
        }

        return new Encoder<TPixel>(output, start, qoi);
    }

    /// <summary>Codes rows one at a time, carrying the index, the previous pixel and the run across rows.</summary>
    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly StreamBufferWriter _writer;
        private readonly int _width;
        private readonly int _height;
        private readonly uint _opaque;
        private readonly uint[] _row;
        private readonly uint[] _index = new uint[64];
        private uint _previous = 0xFF00_0000;
        private int _run;
        private int _y;

        internal Encoder(Stream output, EncodeStart<TPixel> start, QoiWriterOptions qoi)
        {
            SaveLosses.Report(start, "QOI", SaveKeeps.Alpha);
            _width = start.Width;
            _height = start.Height;
            _row = ArrayPool<uint>.Shared.Rent(_width);
            bool alpha = qoi.IncludeAlpha ?? (TPixel.Layout.HasAlpha && !IsOpaque(start.Whole!, _row));
            _opaque = alpha ? 0u : 0xFF00_0000;
            _writer = new StreamBufferWriter(output);
            Span<byte> header = _writer.GetSpan(QoiReader.HeaderLength);
            "qoif"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)_width);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)_height);
            header[12] = (byte)(alpha ? 4 : 3);
            header[13] = (byte)(start.Metadata.ColorSpace == ColorSpaceHint.Linear || start.Metadata.GetColorProfile().Transfer?.IsLinear == true ? 1 : 0);
            _writer.Advance(QoiReader.HeaderLength);
        }

        public override void WriteRow(ReadOnlySpan<TPixel> source)
        {
            int width = _width;
            uint opaque = _opaque;
            Span<uint> pixels = _row.AsSpan(0, width);
            PixelConvert.Convert<TPixel, Rgba8>(source, MemoryMarshal.Cast<uint, Rgba8>(pixels));
            if (opaque != 0)
            {
                foreach (ref uint p in pixels)
                {
                    p |= opaque;
                }
            }

            // At most five bytes a pixel, plus the end marker after the last row.
            Span<byte> span = _writer.GetSpan((width * 5) + 8);
            ref byte dst = ref MemoryMarshal.GetReference(span);
            ref uint src = ref MemoryMarshal.GetReference(pixels);
            ref uint idx = ref MemoryMarshal.GetArrayDataReference(_index);
            uint previous = _previous;
            int run = _run;
            int n = 0;
            bool lastRow = _y == _height - 1;
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

            _writer.Advance(n);
            _previous = previous;
            _run = run;
            _y++;
        }

        public override void Complete()
        {
            _writer.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            _writer.Dispose();
            ArrayPool<uint>.Shared.Return(_row);
            base.Dispose(disposing);
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
