using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Pixels;

namespace Egami.Formats.Extended.Sgi;

/// <summary>
/// Writes SGI with the raster's channels: gray, gray and alpha, RGB or RGBA, in 16-bit samples when the raster has more than 8
/// bits a channel and 8-bit otherwise. The rows are run-length coded unless <see cref="SgiWriterOptions.Rle"/> is off. SGI stores
/// each channel as its own plane of bottom-up rows, so the rows wait in memory, coded, until the last one.
/// </summary>
internal sealed class SgiWriter : ExtendedWriter
{
    protected override FormatInfo Format => SgiFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        SgiWriterOptions sgi = start.Options as SgiWriterOptions ?? SgiWriterOptions.Default;
        start.ReportLosses("SGI", SaveKeeps.Alpha | SaveKeeps.Wide);
        PixelLayoutInfo layout = TPixel.Layout;
        bool wide = layout.BitsPerChannel > 8;
        int channels = layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray => 1,
            PixelChannelOrder.GrayAlpha => 2,
            _ => layout.HasAlpha ? 4 : 3,
        };
        return (channels, wide) switch
        {
            (1, false) => new Encoder<TPixel, Gray8>(output, start, sgi.Rle),
            (2, false) => new Encoder<TPixel, GrayAlpha8>(output, start, sgi.Rle),
            (3, false) => new Encoder<TPixel, Rgb8>(output, start, sgi.Rle),
            (4, false) => new Encoder<TPixel, Rgba8>(output, start, sgi.Rle),
            (1, true) => new Encoder<TPixel, Gray16>(output, start, sgi.Rle),
            (2, true) => new Encoder<TPixel, GrayAlpha16>(output, start, sgi.Rle),
            (3, true) => new Encoder<TPixel, Rgb16>(output, start, sgi.Rle),
            _ => new Encoder<TPixel, Rgba16>(output, start, sgi.Rle),
        };
    }

    private sealed class Encoder<TPixel, TFile> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
        where TFile : unmanaged, IPixelType<TFile>
    {
        private readonly Stream _output;
        private readonly TFile[] _row;
        private readonly byte[][] _planes;
        private readonly byte[] _scratch;
        private readonly int _width;
        private readonly int _height;
        private readonly int _channels;
        private readonly int _size;
        private readonly bool _rle;
        private readonly string _name;
        private int _y;

        public Encoder(Stream output, EncodeStart<TPixel> start, bool rle)
        {
            if (start.Width > ushort.MaxValue || start.Height > ushort.MaxValue)
            {
                throw new NotSupportedException($"SGI holds images up to 65,535 pixels on a side; this one is {start.Width}×{start.Height}.");
            }

            _output = output;
            _width = start.Width;
            _height = start.Height;
            _channels = TFile.Layout.ChannelCount;
            _size = TFile.Layout.BitsPerChannel / 8;
            _rle = rle;
            _row = new TFile[_width];
            _planes = new byte[_channels * _height][];
            _scratch = new byte[(_width * _size * 2) + (_size * 4)];
            _name = start.Metadata.FormatDetails is SgiDetails details ? details.Name : "";
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            PixelConvert.Convert<TPixel, TFile>(row, _row);
            int fileRow = _height - 1 - _y;
            for (int c = 0; c < _channels; c++)
            {
                int length = _size == 1
                    ? Plane(MemoryMarshal.AsBytes(_row.AsSpan()), c)
                    : Plane(MemoryMarshal.Cast<TFile, ushort>(_row.AsSpan()), c);
                _planes[(c * _height) + fileRow] = _scratch.AsSpan(0, length).ToArray();
            }

            _y++;
        }

        public override void Complete()
        {
            using OutputBuffer output = new(_output);
            Span<byte> header = output.GetSpan(SgiReader.HeaderLength)[..SgiReader.HeaderLength];
            header.Clear();
            BinaryPrimitives.WriteUInt16BigEndian(header, SgiReader.Magic);
            header[2] = (byte)(_rle ? 1 : 0);
            header[3] = (byte)_size;
            BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)(_channels == 1 ? 2 : 3));
            BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)_width);
            BinaryPrimitives.WriteUInt16BigEndian(header[8..], (ushort)_height);
            BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)_channels);
            BinaryPrimitives.WriteInt32BigEndian(header[12..], 0);
            BinaryPrimitives.WriteInt32BigEndian(header[16..], _size == 1 ? 255 : 65535);
            int nameLength = Encoding.ASCII.GetBytes(_name.AsSpan(0, Math.Min(_name.Length, 79)), header.Slice(24, 79));
            header[24 + nameLength] = 0;
            output.Advance(SgiReader.HeaderLength);
            if (_rle)
            {
                // The start table, then the length table, both indexed by row + channel × height; the rows follow in that order.
                long offset = SgiReader.HeaderLength + (_planes.Length * 8L);
                Span<byte> tables = output.GetSpan(_planes.Length * 8)[..(_planes.Length * 8)];
                for (int i = 0; i < _planes.Length; i++)
                {
                    int length = _planes[i]?.Length ?? 0;
                    BinaryPrimitives.WriteUInt32BigEndian(tables[(i * 4)..], (uint)offset);
                    BinaryPrimitives.WriteUInt32BigEndian(tables[((_planes.Length + i) * 4)..], (uint)length);
                    offset += length;
                }

                output.Advance(_planes.Length * 8);
            }

            foreach (byte[] plane in _planes)
            {
                output.Write(plane ?? []);
            }

            output.Flush();
        }

        /// <summary>Writes channel <paramref name="channel"/> of the row, coded or not, into the scratch row; returns its length.</summary>
        private int Plane<T>(ReadOnlySpan<T> samples, int channel)
            where T : unmanaged, IEquatable<T>
        {
            int width = _width;
            int step = _channels;
            if (!_rle)
            {
                for (int x = 0; x < width; x++)
                {
                    Put(x * _size, samples[(x * step) + channel]);
                }

                return width * _size;
            }

            int n = 0;
            int i = 0;
            while (i < width)
            {
                // Literal samples until three equal ones start, then the run of equal ones.
                int start = i;
                while (i < width - 2 && !(Equal(samples, i, i + 1, channel) && Equal(samples, i + 1, i + 2, channel)))
                {
                    i++;
                }

                if (i >= width - 2)
                {
                    i = width;
                }

                while (start < i)
                {
                    int count = Math.Min(126, i - start);
                    n = PutCount(n, 0x80 | count);
                    for (int k = 0; k < count; k++)
                    {
                        Put(n, samples[((start + k) * step) + channel]);
                        n += _size;
                    }

                    start += count;
                }

                if (i < width)
                {
                    T value = samples[(i * step) + channel];
                    int run = 1;
                    while (run < 126 && i + run < width && samples[((i + run) * step) + channel].Equals(value))
                    {
                        run++;
                    }

                    n = PutCount(n, run);
                    Put(n, value);
                    n += _size;
                    i += run;
                }
            }

            return PutCount(n, 0);
        }

        private bool Equal<T>(ReadOnlySpan<T> samples, int a, int b, int channel)
            where T : unmanaged, IEquatable<T>
        {
            return samples[(a * _channels) + channel].Equals(samples[(b * _channels) + channel]);
        }

        private int PutCount(int at, int count)
        {
            if (_size == 1)
            {
                _scratch[at] = (byte)count;
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(_scratch.AsSpan(at), (ushort)count);
            }

            return at + _size;
        }

        private void Put<T>(int at, T value)
            where T : unmanaged
        {
            if (_size == 1)
            {
                _scratch[at] = System.Runtime.CompilerServices.Unsafe.As<T, byte>(ref value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(_scratch.AsSpan(at), System.Runtime.CompilerServices.Unsafe.As<T, ushort>(ref value));
            }
        }
    }
}
