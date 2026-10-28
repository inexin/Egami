using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Pcx;

/// <summary>
/// Writes PCX version 5, run-length coded: gray images as one 8-bit plane with a gray palette, everything else as three 8-bit
/// planes (24-bit RGB). The resolution is kept, in dots per inch.
/// </summary>
internal sealed class PcxWriter : ExtendedWriter
{
    protected override FormatInfo Format => PcxFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        start.ReportLosses("PCX", SaveKeeps.Resolution);
        return IsGray<TPixel>() ? new Encoder<TPixel, Gray8>(output, start) : new Encoder<TPixel, Rgb8>(output, start);
    }

    private sealed class Encoder<TPixel, TFile> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
        where TFile : unmanaged, IPixelType<TFile>
    {
        private readonly OutputBuffer _output;
        private readonly TFile[] _row;
        private readonly byte[] _plane;
        private readonly int _planes;

        public Encoder(Stream output, EncodeStart<TPixel> start)
        {
            if (start.Width > ushort.MaxValue - 1 || start.Height > ushort.MaxValue + 1)
            {
                throw new NotSupportedException($"PCX holds images up to 65,534 pixels wide and 65,536 high; this one is {start.Width}×{start.Height}.");
            }

            _output = new OutputBuffer(output);
            _row = new TFile[start.Width];
            _planes = TFile.Layout.ChannelCount;
            int lineBytes = (start.Width + 1) & ~1;
            _plane = new byte[lineBytes];
            Span<byte> header = _output.GetSpan(PcxReader.HeaderLength)[..PcxReader.HeaderLength];
            header.Clear();
            header[0] = 0x0A;
            header[1] = 5;
            header[2] = 1;
            header[3] = 8;
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)(start.Width - 1));
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], (ushort)(start.Height - 1));
            (int hDpi, int vDpi) = Dpi(start.Metadata.Resolution);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)hDpi);
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)vDpi);
            header[65] = (byte)_planes;
            BinaryPrimitives.WriteUInt16LittleEndian(header[66..], (ushort)lineBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(header[68..], (ushort)(_planes == 1 ? 2 : 1));
            _output.Advance(PcxReader.HeaderLength);
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            PixelConvert.Convert<TPixel, TFile>(row, _row);
            ReadOnlySpan<byte> pixels = MemoryMarshal.AsBytes(_row.AsSpan());
            if (_planes == 1)
            {
                pixels.CopyTo(_plane);
                Encode(_plane);
                return;
            }

            ref byte source = ref MemoryMarshal.GetReference(pixels);
            ref byte plane = ref MemoryMarshal.GetArrayDataReference(_plane);
            int width = _row.Length;
            for (int p = 0; p < _planes; p++)
            {
                for (int x = 0, at = p; x < width; x++, at += _planes)
                {
                    Unsafe.Add(ref plane, x) = Unsafe.Add(ref source, at);
                }

                Encode(_plane);
            }
        }

        public override void Complete()
        {
            if (_planes == 1)
            {
                Span<byte> palette = _output.GetSpan(769)[..769];
                palette[0] = PcxReader.PaletteMarker;
                for (int i = 0; i < 256; i++)
                {
                    palette.Slice(1 + (i * 3), 3).Fill((byte)i);
                }

                _output.Advance(769);
            }

            _output.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            _output.Dispose();
            base.Dispose(disposing);
        }

        private static (int Horizontal, int Vertical) Dpi(Resolution? resolution)
        {
            if (resolution is not { Unit: not ResolutionUnit.None } r)
            {
                return (72, 72);
            }

            Resolution inch = r.ToPerInch();
            return ((int)Math.Clamp(Math.Round(inch.X), 1, ushort.MaxValue), (int)Math.Clamp(Math.Round(inch.Y), 1, ushort.MaxValue));
        }

        /// <summary>Codes one plane line: runs of up to 63 equal bytes, and single bytes with both top bits set as runs of one.</summary>
        private void Encode(ReadOnlySpan<byte> line)
        {
            Span<byte> span = _output.GetSpan(line.Length * 2);
            ref byte dst = ref MemoryMarshal.GetReference(span);
            ref byte src = ref MemoryMarshal.GetReference(line);
            int length = line.Length;
            int n = 0;
            int i = 0;
            while (i < length)
            {
                byte value = Unsafe.Add(ref src, i);
                int end = Math.Min(i + 63, length);
                int next = i + 1;
                while (next < end && Unsafe.Add(ref src, next) == value)
                {
                    next++;
                }

                int run = next - i;
                if (run > 1 || value >= 0xC0)
                {
                    Unsafe.Add(ref dst, n++) = (byte)(0xC0 | run);
                }

                Unsafe.Add(ref dst, n++) = value;
                i = next;
            }

            _output.Advance(n);
        }
    }
}
