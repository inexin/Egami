using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Sgi;

/// <summary>
/// Reads SGI, written from Paul Haeberli's "The SGI Image File Format" (version 1.00): plain and run-length coded files of 1 to 4
/// channels of 8-bit or 16-bit samples; further channels are skipped. The planes are stored bottom-up and the coded rows may lie
/// anywhere in the file, so the reader reads the file whole and decodes a row of every channel as it is asked for.
/// </summary>
internal sealed class SgiReader : ExtendedReader
{
    internal const int HeaderLength = 512;
    internal const ushort Magic = 474;

    /// <summary>Returns whether the data starts with the SGI magic number, a storage of 0 or 1 and 1 or 2 bytes per sample.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(header) == Magic && header[2] <= 1 && header[3] is 1 or 2;
    }

    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input);
        return Describe(SgiFormat.Info, header.Width, header.Height, header.Layout, RasterAccess.Buffered, header.RawBytes);
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input);
        input.CheckFrameSize(header.Width, header.Height);
        long tables = header.Rle ? (long)header.Height * header.Planes * 8 : 0;
        long max = header.Rle ? tables + (header.RawBytes * 2) + ((long)header.Height * header.Planes * 4) + 65536 : header.RawBytes;
        byte[] data = InputData.ReadToEnd(input, max, out int length);
        return (header.Channels, header.Bytes) switch
        {
            (1, 1) => new Rows<Gray8>(input, options, header, data, length),
            (2, 1) => new Rows<GrayAlpha8>(input, options, header, data, length),
            (3, 1) => new Rows<Rgb8>(input, options, header, data, length),
            (4, 1) => new Rows<Rgba8>(input, options, header, data, length),
            (1, _) => new Rows<Gray16>(input, options, header, data, length),
            (2, _) => new Rows<GrayAlpha16>(input, options, header, data, length),
            (3, _) => new Rows<Rgb16>(input, options, header, data, length),
            _ => new Rows<Rgba16>(input, options, header, data, length),
        };
    }

    private static Header ReadHeader(EgamiInput input)
    {
        byte[] bytes = new byte[HeaderLength];
        input.ReadExactly(bytes);
        if (!Matches(bytes))
        {
            throw input.Corrupt(0, "The SGI signature is wrong.");
        }

        int dimension = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        int width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6));
        int height = dimension == 1 ? 1 : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(8));
        int planes = dimension < 3 ? 1 : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10));
        if (dimension is < 1 or > 3)
        {
            throw input.Corrupt(4, $"Dimension {dimension} is not defined; it must be 1, 2 or 3.");
        }

        if (width == 0 || height == 0 || planes == 0)
        {
            throw input.Corrupt(6, $"The image size {width}×{height}×{planes} is invalid.");
        }

        int nameLength = bytes.AsSpan(24, 80).IndexOf((byte)0);
        string name = Encoding.ASCII.GetString(bytes, 24, nameLength < 0 ? 79 : nameLength);
        int channels = Math.Min(planes, 4);
        int sampleBytes = bytes[3];
        PixelLayoutInfo layout = (channels, sampleBytes) switch
        {
            (1, 1) => Gray8.Layout,
            (2, 1) => GrayAlpha8.Layout,
            (3, 1) => Rgb8.Layout,
            (4, 1) => Rgba8.Layout,
            (1, _) => Gray16.Layout,
            (2, _) => GrayAlpha16.Layout,
            (3, _) => Rgb16.Layout,
            _ => Rgba16.Layout,
        };
        return new Header(width, height, planes, channels, sampleBytes, bytes[2] == 1, name, layout);
    }

    /// <summary>What the header says.</summary>
    private sealed record Header(int Width, int Height, int Planes, int Channels, int Bytes, bool Rle, string Name, PixelLayoutInfo Layout)
    {
        /// <summary>Gets the bytes of all planes uncoded.</summary>
        public long RawBytes => (long)Width * Height * Planes * Bytes;
    }

    /// <summary>The rows of the file read whole; row y of the image is row height − 1 − y of each plane.</summary>
    private sealed class Rows<TNatural> : ExtendedRows<TNatural, TNatural>
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        private readonly Header _header;
        private readonly byte[] _data;
        private readonly int _length;
        private bool _damageNoted;
        private bool _missingNoted;

        public Rows(EgamiInput input, ReaderOptions options, Header header, byte[] data, int length)
            : base(input, options, SgiFormat.Info, "Sgi", header.Width, header.Height)
        {
            _header = header;
            _data = data;
            _length = length;
            Metadata.FormatDetails = new SgiDetails { Name = header.Name, Rle = header.Rle, BytesPerChannel = header.Bytes, Channels = header.Channels };
            if (header.Planes > 4)
            {
                Warn("ChannelsSkipped", $"The file has {header.Planes} channels; the first 4 were read.");
            }
        }

        public override RasterAccess Access => RasterAccess.Buffered;

        public override long WorkingBytes => base.WorkingBytes + _data.Length;

        protected override bool DecodeRow(int y, Span<TNatural> row)
        {
            int fileRow = _header.Height - 1 - y;
            bool complete = true;
            for (int c = 0; c < _header.Channels; c++)
            {
                complete &= _header.Bytes == 1
                    ? Plane(MemoryMarshal.AsBytes(row), c, fileRow)
                    : Plane(MemoryMarshal.Cast<TNatural, ushort>(row), c, fileRow);
            }

            if (!complete)
            {
                // The rows are not stored in the image's order, so a short file loses rows here and there, not all after one.
                if (Strict)
                {
                    throw Input.Corrupt(HeaderLength + _length, "The data ends unexpectedly.");
                }

                if (!_missingNoted)
                {
                    _missingNoted = true;
                    Warn("RowsMissing", "The file ends before the data of some rows; they are left transparent black.");
                }
            }

            return true;
        }

        /// <summary>Decodes one plane's row into every <see cref="Header.Channels"/>th sample of <paramref name="samples"/>.</summary>
        private bool Plane<T>(Span<T> samples, int channel, int fileRow)
            where T : unmanaged
        {
            int width = _header.Width;
            int step = _header.Channels;
            int size = _header.Bytes;
            if (!_header.Rle)
            {
                long at = (((long)channel * _header.Height) + fileRow) * width * size;
                for (int x = 0; x < width; x++)
                {
                    if (at + size > _length)
                    {
                        Clear(samples, channel, x);
                        return false;
                    }

                    samples[(x * step) + channel] = Sample<T>(at);
                    at += size;
                }

                return true;
            }

            int index = fileRow + (channel * _header.Height);
            if ((index * 4L) + 4 > _length)
            {
                Clear(samples, channel, 0);
                return false;
            }

            long start = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(index * 4)) - (long)HeaderLength;
            long pos = start;
            int x0 = 0;
            while (true)
            {
                if (pos < 0 || pos + size > _length)
                {
                    Clear(samples, channel, x0);
                    if (start >= _length)
                    {
                        return false;
                    }

                    NoteDamage(fileRow, channel);
                    return true;
                }

                int count = size == 1 ? _data[pos] : BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)pos));
                pos += size;
                bool literal = (count & 0x80) != 0;
                count &= 0x7F;
                if (count == 0)
                {
                    break;
                }

                if (x0 + count > width || pos + ((literal ? count : 1) * size) > _length)
                {
                    Clear(samples, channel, x0);
                    NoteDamage(fileRow, channel);
                    return true;
                }

                if (literal)
                {
                    for (int i = 0; i < count; i++)
                    {
                        samples[((x0 + i) * step) + channel] = Sample<T>(pos);
                        pos += size;
                    }
                }
                else
                {
                    T value = Sample<T>(pos);
                    pos += size;
                    for (int i = 0; i < count; i++)
                    {
                        samples[((x0 + i) * step) + channel] = value;
                    }
                }

                x0 += count;
            }

            if (x0 < width)
            {
                Clear(samples, channel, x0);
                NoteDamage(fileRow, channel);
            }

            return true;
        }

        private T Sample<T>(long at)
            where T : unmanaged
        {
            if (typeof(T) == typeof(byte))
            {
                byte b = _data[at];
                return System.Runtime.CompilerServices.Unsafe.As<byte, T>(ref b);
            }

            ushort s = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan((int)at));
            return System.Runtime.CompilerServices.Unsafe.As<ushort, T>(ref s);
        }

        private void Clear<T>(Span<T> samples, int channel, int from)
            where T : unmanaged
        {
            for (int x = from; x < _header.Width; x++)
            {
                samples[(x * _header.Channels) + channel] = default;
            }
        }

        private void NoteDamage(int fileRow, int channel)
        {
            if (Strict)
            {
                throw Input.Corrupt(HeaderLength, $"The coded row {fileRow} of channel {channel} is damaged.");
            }

            if (!_damageNoted)
            {
                _damageNoted = true;
                Warn("RowDamaged", $"The coded row {fileRow} of channel {channel} is damaged; what could not be decoded is left black.");
            }
        }
    }
}
