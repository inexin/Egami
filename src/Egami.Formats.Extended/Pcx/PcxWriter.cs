using System.Buffers.Binary;

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
            ReadOnlySpan<byte> pixels = System.Runtime.InteropServices.MemoryMarshal.AsBytes(_row.AsSpan());
            for (int p = 0; p < _planes; p++)
            {
                for (int x = 0; x < _row.Length; x++)
                {
                    _plane[x] = pixels[(x * _planes) + p];
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
            int n = 0;
            int i = 0;
            while (i < line.Length)
            {
                byte value = line[i];
                int run = 1;
                while (run < 63 && i + run < line.Length && line[i + run] == value)
                {
                    run++;
                }

                if (run > 1 || value >= 0xC0)
                {
                    span[n++] = (byte)(0xC0 | run);
                }

                span[n++] = value;
                i += run;
            }

            _output.Advance(n);
        }
    }
}
